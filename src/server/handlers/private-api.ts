import express from "express";
import hs from "hivesigner";
import axios, { AxiosRequestConfig } from "axios";
import { cryptoUtils, Signature, Client } from '@hiveio/dhive'

import { announcements } from "./announcements";
import {
    apiRequest,
    getPromotedEntries,
    ChainBalanceResponse,
    parseBalanceProvider,
    fetchChainzBalance,
    fetchBlockstreamBalance,
    ensureAuthKeyInPath,
} from "../helper";

import { pipe } from "../util";
import { cache } from '../cache';
import { ACTIVE_PROPOSAL_META, bots } from "./constants";

import bs58check from "bs58check";

const client = new Client([
    "https://api.hive.blog",
    "https://techcoderx.com",
    "https://api.deathwing.me",
    "https://rpc.mahdiyari.info",
    "https://hive-api.arcange.eu",
    "https://api.openhive.network",
    "https://hiveapi.actifit.io",
    "https://hive-api.3speak.tv",
    "https://api.syncad.com",
    "https://api.c0ff33a.uk"
], {
    timeout: 2000,
    failoverThreshold: 2,
    consoleOnFailover: false
});

interface DecodedToken {
    signed_message: {
        type: string;
        app: string;
    };
    authors: string[];
    timestamp: number;
    signatures: string[];
}

let hivesignerAccountCache: any | null = null;
let hivesignerCacheTime = 0;
const HIVE_SIGNER_CACHE_TTL = 24 * 60 * 60 * 1000; // 24 hours

const validateCode = async (req: express.Request): Promise<string | false> => {
    const { code } = req.body as { code?: unknown };

    if (typeof code !== "string") {
        if (code !== undefined && code !== null) {
            console.warn("validateCode(): received non-string code payload");
        }

        return false;
    }

    const trimmedCode = code.trim();

    if (!trimmedCode) {
        return false;
    }

    try {
        const buffer = Buffer.from(trimmedCode, "base64");

        if (buffer.length === 0) {
            return false;
        }

        const decoded = JSON.parse(buffer.toString("utf-8")) as DecodedToken;
        const { signed_message, authors, timestamp, signatures } = decoded;

        if (
            typeof signed_message !== "object" ||
            !Array.isArray(authors) ||
            typeof authors[0] !== "string" ||
            typeof timestamp !== "number" ||
            !Array.isArray(signatures) ||
            typeof signatures[0] !== "string"
        ) {
            console.warn("Invalid token structure", decoded);
            return false;
        }

        const author = authors[0];
        const signature = signatures[0];
        const currentTime = Date.now();

        // Optional: reject tokens older than 30 days
        /*
        const now = Math.floor(currentTime / 1000);
        const maxAgeSeconds = 30 * 24 * 60 * 60;
        if (now - timestamp > maxAgeSeconds) {
            console.warn("Token expired", author, code);
            return false;
        }
        */

        const rawMessage = JSON.stringify({ signed_message, authors, timestamp });
        const digest = cryptoUtils.sha256(rawMessage);
        const recoveredPubKey = Signature.fromString(signature).recover(digest).toString();


        const [account] = await client.database.getAccounts([author]);
        if (!account) {
            console.error("Fetching account error");
            return false;
        }

        const postingPubKeys = account.posting.key_auths.map(([key]) => key);
        if (postingPubKeys.includes(recoveredPubKey)) {
            return author;
        }

        // Use cached hivesigner account, refresh every 24h
        if (!hivesignerAccountCache || currentTime - hivesignerCacheTime > HIVE_SIGNER_CACHE_TTL) {
            try {
                const [hivesignerAccount] = await client.database.getAccounts(["hivesigner"]);
                hivesignerAccountCache = hivesignerAccount;
                hivesignerCacheTime = currentTime;
            } catch (err) {
                console.error("Failed to fetch hivesigner account", err);
                return false;
            }
        }

        const hsPostingKeys = hivesignerAccountCache?.posting?.key_auths?.map(([key]: [string, number]) => key) || [];
        if (hsPostingKeys.includes(recoveredPubKey)) {
            return author;
        }

        console.warn("Posting key mismatch", recoveredPubKey, postingPubKeys, hsPostingKeys);
        return false;
    } catch (err) {
        console.error("Token validation error", err);
        return false;
    }

    /*
    // Fallback: validate using Hivesigner /me endpoint
    try {
        return await new hs.Client({ accessToken: code }).me().then((r: { name: string }) => r.name);
    } catch (e) {
        return false;
    }
    */
};

export const receivedVesting = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;
    pipe(apiRequest(`delegatee_vesting_shares/${username}`, "GET"), res);
};

export const receivedRC = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;
    pipe(apiRequest(`delegatee_rc/${username}`, "GET"), res);
};

export const rewardedCommunities = async (req: express.Request, res: express.Response) => {
    pipe(apiRequest(`rewarded-communities`, "GET"), res);
};

const CHAIN_PARAM_REGEX = /^[a-z0-9-]+$/i;

const CHAINSTACK_API_BASE = "https://api.chainstack.com/v1";
const CHAINSTACK_NODES_ENDPOINT = `${CHAINSTACK_API_BASE}/nodes`;
const CHAINSTACK_NODE_CACHE_TTL_MS = 300 * 60 * 1000; // 5 hours

type BroadcastRequestBody = {
    signedPayload?: unknown;
    rawTransaction?: unknown;
    payload?: unknown;
};

interface ChainstackNodeDetails {
    https_endpoint?: string;
    wss_endpoint?: string;
    beacon_endpoint?: string;
    toncenter_api_v2?: string;
    toncenter_api_v3?: string;
    solidity_http_api_endpoint?: string;
    auth_username?: string;
    auth_password?: string;
    auth_key?: string;
}

interface ChainstackNode {
    id: string;
    name: string;
    network: string;
    provider: string;
    status: string;
    details: ChainstackNodeDetails;
}

interface ChainstackNodeResponse {
    results?: ChainstackNode[];
}

interface AxiosAuthConfig {
    username: string;
    password: string;
}

interface AxiosNodeConfig {
    headers: Record<string, string>;
    auth?: AxiosAuthConfig;
}

interface ChainBroadcastResponse {
    chain: string;
    txId?: string;
    raw: unknown;
    nodeId: string;
    provider: "chainstack";
}

interface ChainHandler {
    validateAddress?: (address: string) => boolean;
    selectNode: (nodes: ChainstackNode[]) => ChainstackNode | null;
    fetchBalance: (node: ChainstackNode, address: string) => Promise<ChainBalanceResponse>;
}

interface ChainBroadcastHandler {
    normalizePayload?: (payload: unknown) => unknown;
    selectNode: (nodes: ChainstackNode[]) => ChainstackNode | null;
    broadcast: (node: ChainstackNode, payload: unknown) => Promise<ChainBroadcastResponse>;
}

let cachedNodes: ChainstackNode[] | null = null;
let nodesCacheExpiry = 0;

const buildNodeAxiosConfig = (
    node: ChainstackNode,
    opts?: { noAuthHeaders?: boolean }
): AxiosNodeConfig => {
    const headers: Record<string, string> = {
        "Content-Type": "application/json",
    };

    let auth: AxiosAuthConfig | undefined;

    if (!opts?.noAuthHeaders) {
        if (node.details?.auth_key) {
            headers["x-api-key"] = node.details.auth_key;
        }
        const hasBasicAuth = node.details?.auth_username && node.details?.auth_password;
        if (hasBasicAuth) {
            auth = {
                username: node.details!.auth_username as string,
                password: node.details!.auth_password as string,
            };
        }
    }

    return { headers, auth };
};


const fetchChainstackNodes = async (): Promise<ChainstackNode[]> => {
    const apiKey = process.env.CHAINSTACK_API_KEY;

    if (!apiKey) {
        throw new Error("Chainstack API key is not configured");
    }

    const now = Date.now();

    if (cachedNodes && now < nodesCacheExpiry) {
        return cachedNodes;
    }

    try {
        const response = await axios.get<ChainstackNodeResponse>(CHAINSTACK_NODES_ENDPOINT, {
            headers: {
                Authorization: `Bearer ${apiKey}`,
            },
        });

        const nodes = (response.data?.results || []).filter((node) => node.status === "running");

        cachedNodes = nodes;
        nodesCacheExpiry = now + CHAINSTACK_NODE_CACHE_TTL_MS;

        return nodes;
    } catch (err) {
        console.error("Failed to fetch Chainstack nodes", err);
        throw new Error("Unable to fetch Chainstack nodes");
    }
};

const extractSignedPayload = (body: BroadcastRequestBody | undefined): unknown => {
    if (!body || typeof body !== "object") return undefined;

    const { signedPayload, rawTransaction, payload } = body;

    if (signedPayload !== undefined) return signedPayload;
    if (rawTransaction !== undefined) return rawTransaction;
    return payload;
};

const endpointIncludes = (node: ChainstackNode, needle: string) => {
    const loweredNeedle = needle.toLowerCase();
    const candidates = [
        node.details?.https_endpoint,
        node.details?.wss_endpoint,
        node.details?.beacon_endpoint,
    ].filter(Boolean) as string[];

    return candidates.some((candidate) => candidate.toLowerCase().includes(loweredNeedle));
};

const ensureHttpsEndpoint = (node: ChainstackNode): string => {
    const endpoint = node.details?.https_endpoint;

    if (!endpoint) {
        throw new Error(`Node ${node.id} does not expose an HTTPS endpoint`);
    }

    return endpoint;
};

const coercePayloadBuffer = (value: unknown): Buffer | null => {
    if (typeof value === "string") {
        return null;
    }

    if (Buffer.isBuffer(value)) {
        return value;
    }

    if (value instanceof Uint8Array) {
        return Buffer.from(value);
    }

    if (value instanceof ArrayBuffer) {
        return Buffer.from(new Uint8Array(value));
    }

    if (Array.isArray(value) && value.every((entry) => Number.isInteger(entry) && entry >= 0 && entry <= 255)) {
        return Buffer.from(value as number[]);
    }

    return null;
};

const normalizeHexPayload = (value: unknown): string => {
    const trimmed = typeof value === "string" ? value.trim() : "";
    const hexFromString = trimmed.startsWith("0x") ? trimmed.slice(2) : trimmed;

    const buffer = coercePayloadBuffer(value);
    const hexFromBuffer = buffer ? buffer.toString("hex") : "";

    const normalized = hexFromString || hexFromBuffer;

    if (!normalized) {
        throw new Error("Signed payload must be a hex-encoded string or byte array");
    }

    if (!/^[a-fA-F0-9]+$/.test(normalized) || normalized.length % 2 !== 0) {
        throw new Error("Signed payload must be a hex-encoded string");
    }

    return `0x${normalized}`;
};

const normalizeBase64Payload = (value: unknown): string => {
    const trimmed = typeof value === "string" ? value.trim() : "";
    const buffer = coercePayloadBuffer(value);

    if (!trimmed && !buffer) {
        throw new Error("Signed payload must be a base64 string or byte array");
    }

    if (buffer) {
        if (buffer.length === 0) {
            throw new Error("Signed payload must not be empty");
        }
        return buffer.toString("base64");
    }

    try {
        const buf = Buffer.from(trimmed, "base64");
        if (buf.length === 0) {
            throw new Error("Empty payload");
        }
    } catch (err) {
        throw new Error("Signed payload must be valid base64");
    }

    return trimmed;
};

const stripLeadingZeros = (value: string): string => {
    const stripped = value.replace(/^0+/, "");
    return stripped === "" ? "0" : stripped;
};

const multiplyDecimalString = (value: string, multiplier: number): string => {
    const normalized = stripLeadingZeros(value);

    if (multiplier === 0 || normalized === "0") {
        return "0";
    }

    if (!Number.isInteger(multiplier) || multiplier < 0) {
        throw new Error("Invalid multiplier for decimal conversion");
    }

    let carry = 0;
    let result = "";

    for (let index = normalized.length - 1; index >= 0; index -= 1) {
        const digit = normalized.charCodeAt(index) - 48;

        if (digit < 0 || digit > 9) {
            throw new Error("Invalid decimal value during multiplication");
        }

        const product = digit * multiplier + carry;
        const remainder = product % 10;

        result = remainder.toString() + result;
        carry = Math.floor(product / 10);
    }

    while (carry > 0) {
        result = (carry % 10).toString() + result;
        carry = Math.floor(carry / 10);
    }

    return stripLeadingZeros(result);
};

const addSmallIntToDecimalString = (value: string, addend: number): string => {
    const normalized = stripLeadingZeros(value);

    if (addend === 0) {
        return normalized;
    }

    if (!Number.isInteger(addend) || addend < 0) {
        throw new Error("Invalid addend for decimal conversion");
    }

    let carry = addend;
    let result = "";

    for (let index = normalized.length - 1; index >= 0; index -= 1) {
        const digit = normalized.charCodeAt(index) - 48;

        if (digit < 0 || digit > 9) {
            throw new Error("Invalid decimal value during addition");
        }

        const sum = digit + carry;
        result = (sum % 10).toString() + result;
        carry = Math.floor(sum / 10);
    }

    while (carry > 0) {
        result = (carry % 10).toString() + result;
        carry = Math.floor(carry / 10);
    }

    return stripLeadingZeros(result);
};

const hexToDecimalString = (value: string): string => {
    const normalized = stripLeadingZeros(value.toLowerCase());

    if (normalized === "0") {
        return "0";
    }

    let result = "0";

    for (const char of normalized) {
        const digit = parseInt(char, 16);

        if (Number.isNaN(digit)) {
            throw new Error("Invalid hexadecimal balance response");
        }

        result = multiplyDecimalString(result, 16);

        if (digit !== 0) {
            result = addSmallIntToDecimalString(result, digit);
        }
    }

    return stripLeadingZeros(result);
};

const parseHexBalance = (value: unknown): string => {
    if (typeof value !== "string") {
        throw new Error("Invalid hexadecimal balance response");
    }

    try {
        const normalized = value.startsWith("0x") ? value.slice(2) : value;

        if (normalized === "") {
            return "0";
        }

        return hexToDecimalString(normalized);
    } catch (err) {
        throw new Error("Failed to parse hexadecimal balance");
    }
};

const fetchEvmBalance = async (chain: string, node: ChainstackNode, address: string): Promise<ChainBalanceResponse> => {
    const endpoint = ensureHttpsEndpoint(node);
    const config = buildNodeAxiosConfig(node);

    const payload = {
        jsonrpc: "2.0",
        id: "balance",
        method: "eth_getBalance",
        params: [address, "latest"],
    };

    const response = await axios.post(endpoint, payload, config);
    const data = response.data;

    if (data?.error) {
        throw new Error(data.error?.message || "EVM balance request failed");
    }

    const balance = parseHexBalance(data?.result);

    return {
        chain,
        balance,
        unit: "wei",
        raw: data,
        nodeId: node.id,
        provider: "chainstack",
    };
};

const broadcastEvmTransaction = async (
    chain: string,
    node: ChainstackNode,
    signedPayload: string,
): Promise<ChainBroadcastResponse> => {
    const endpoint = ensureHttpsEndpoint(node);
    const config = buildNodeAxiosConfig(node);

    const payload = {
        jsonrpc: "2.0",
        id: "broadcast",
        method: "eth_sendRawTransaction",
        params: [signedPayload],
    };

    const response = await axios.post(endpoint, payload, config);
    const data = response.data;

    if (data?.error) {
        throw new Error(data.error?.message || "EVM broadcast failed");
    }

    return {
        chain,
        txId: data?.result,
        raw: data,
        nodeId: node.id,
        provider: "chainstack",
    };
};

const fetchSolanaBalance = async (node: ChainstackNode, address: string): Promise<ChainBalanceResponse> => {
    const endpoint = ensureHttpsEndpoint(node);
    const config = buildNodeAxiosConfig(node);

    const payload = {
        jsonrpc: "2.0",
        id: "balance",
        method: "getBalance",
        params: [address, { commitment: "finalized" }],
    };

    const response = await axios.post(endpoint, payload, config);
    const data = response.data;

    if (data?.error) {
        throw new Error(data.error?.message || "Solana balance request failed");
    }

    const value = data?.result?.value;

    if (typeof value !== "number") {
        throw new Error("Invalid Solana balance response");
    }

    return {
        chain: "sol",
        balance: value.toString(),
        unit: "lamports",
        raw: data,
        nodeId: node.id,
        provider: "chainstack",
    };
};

const broadcastSolanaTransaction = async (
    node: ChainstackNode,
    signedPayload: string,
): Promise<ChainBroadcastResponse> => {
    const endpoint = ensureHttpsEndpoint(node);
    const config = buildNodeAxiosConfig(node);

    const payload = {
        jsonrpc: "2.0",
        id: "broadcast",
        method: "sendTransaction",
        params: [signedPayload, { encoding: "base64" }],
    };

    const response = await axios.post(endpoint, payload, config);
    const data = response.data;

    if (data?.error) {
        throw new Error(data.error?.message || "Solana broadcast failed");
    }

    return {
        chain: "sol",
        txId: data?.result,
        raw: data,
        nodeId: node.id,
        provider: "chainstack",
    };
};

const fetchTronBalance = async (node: ChainstackNode, address: string): Promise<ChainBalanceResponse> => {
    const endpoint = ensureHttpsEndpoint(node);
    const config = buildNodeAxiosConfig(node);

    let normalizedAddress = address;

    if (!address.startsWith("0x")) {
        try {
            const decoded = bs58check.decode(address);
            normalizedAddress = `0x${Buffer.from(decoded).toString("hex")}`;
        } catch (err) {
            throw new Error("Invalid Tron address provided");
        }
    }

    const payload = {
        jsonrpc: "2.0",
        id: "balance",
        method: "eth_getBalance",
        params: [normalizedAddress, "latest"],
    };

    const response = await axios.post(endpoint, payload, config);
    const data = response.data;

    if (data?.error) {
        throw new Error(data.error?.message || "Tron balance request failed");
    }

    const balance = parseHexBalance(data?.result);

    return {
        chain: "tron",
        balance,
        unit: "sun",
        raw: data,
        nodeId: node.id,
        provider: "chainstack",
    };
};

const broadcastTronTransaction = async (
    node: ChainstackNode,
    signedPayload: string,
): Promise<ChainBroadcastResponse> => broadcastEvmTransaction("tron", node, signedPayload);

const requestToncenterBalance = async (
    baseEndpoint: string,
    address: string,
    headers: Record<string, string>,
    auth?: AxiosAuthConfig,
    apiKey?: string,
) => {
    const sanitizedBase = baseEndpoint.replace(/\/+$/, "");
    const baseLower = sanitizedBase.toLowerCase();
    const restBase = baseLower.endsWith("/jsonrpc")
        ? sanitizedBase.slice(0, -"/jsonrpc".length)
        : sanitizedBase;

    const baseConfig: AxiosRequestConfig = {
        headers,
        auth,
    };

    const queryParams = {
        address,
        ...(apiKey ? { api_key: apiKey } : {}),
    };

    const attempts: Array<() => Promise<any>> = [];

    const buildGetConfig = (extraParams?: Record<string, string>): AxiosRequestConfig => ({
        ...baseConfig,
        params: {
            ...queryParams,
            ...(extraParams || {}),
        },
    });

    if (restBase) {
        attempts.push(() => axios.get(`${restBase}/getAddressBalance`, buildGetConfig()));
        attempts.push(() => axios.get(restBase, buildGetConfig({ method: "getAddressBalance" })));
    }

    const postConfig: AxiosRequestConfig = {
        ...baseConfig,
        headers: {
            ...headers,
            "Content-Type": "application/json",
        },
    };

    if (apiKey) {
        postConfig.params = { api_key: apiKey };
    }

    const postEndpoints = new Set<string>();
    postEndpoints.add(sanitizedBase);

    if (!baseLower.endsWith("/jsonrpc")) {
        postEndpoints.add(`${sanitizedBase}/jsonRPC`);
    } else if (restBase) {
        postEndpoints.add(restBase);
    }

    for (const endpoint of Array.from(postEndpoints).reverse()) {
        attempts.unshift(() =>
            axios.post(
                endpoint,
                {
                    id: "balance",
                    jsonrpc: "2.0",
                    method: "getAddressBalance",
                    params: [{ address }],
                },
                postConfig,
            ),
        );
    }

    let lastError: unknown = null;

    for (const attempt of attempts) {
        try {
            const response = await attempt();
            return response.data;
        } catch (error) {
            if (
                axios.isAxiosError(error) &&
                (error.response?.status === 404 || error.response?.status === 405)
            ) {
                lastError = error;
                continue;
            }

            throw error;
        }
    }

    if (lastError) {
        throw lastError;
    }

    throw new Error("Unable to fetch TON balance from Toncenter endpoint");
};

const extractTonBalanceValue = (value: unknown, seen: Set<unknown> = new Set()): string | null => {
    if (typeof value === "string") return value;
    if (typeof value === "number") return Number.isFinite(value) ? String(value) : null;
    if (Array.isArray(value)) {
        for (const entry of value) {
            const got = extractTonBalanceValue(entry, seen);
            if (got !== null) return got;
        }
        return null;
    }
    if (value && typeof value === "object") {
        if (seen.has(value)) return null;
        seen.add(value);
        const obj = value as Record<string, unknown>;
        // common toncenter / tonapi fields
        for (const k of ["balance", "result", "address_balance", "account_balance", "available_balance"]) {
            if (k in obj) {
                const got = extractTonBalanceValue(obj[k], seen);
                if (got !== null) return got;
            }
        }
        // nested address forms
        if (obj.address && typeof obj.address === "object") {
            const got = extractTonBalanceValue((obj.address as any).balance, seen);
            if (got !== null) return got;
        }
    }
    return null;
};

const ensureTonAuthKeyInEndpoint = (endpoint: string, authKey?: string): string => {
    if (!authKey) {
        return endpoint;
    }

    try {
        const url = new URL(endpoint);

        if (!url.hostname.endsWith(".chainstack.com")) {
            return endpoint;
        }

        const segments = url.pathname.split("/").filter(Boolean);

        if (segments[0] === authKey) {
            return endpoint;
        }

        url.pathname = `/${[authKey, ...segments].join("/")}`;

        return url.toString();
    } catch (err) {
        return endpoint;
    }
};

const TON_TIMEOUT_MS = 10000;

const fetchTonBalance = async (node: ChainstackNode, address: string): Promise<ChainBalanceResponse> => {
    // Build candidate endpoints (v3 first), inject path auth key if needed
    const endpointCandidates = [node.details?.toncenter_api_v3, node.details?.toncenter_api_v2]
        .filter((e): e is string => Boolean(e))
        .map((e) => ensureAuthKeyInPath(e, node.details?.auth_key));

    if (endpointCandidates.length === 0) {
        throw new Error("TON node does not provide a Toncenter endpoint");
    }

    // Common headers (avoid only-header auth; toncenter prefers api_key query)
    const headers: Record<string, string> = {
        Accept: "application/json, text/plain;q=0.9, */*;q=0.8",
        "Content-Type": "application/json",
        "User-Agent": "EcencyBalanceBot/1.0 (+https://ecency.com)",
    };

    // Basic auth if present
    const auth =
        node.details?.auth_username && node.details?.auth_password
            ? {
                username: node.details.auth_username as string,
                password: node.details.auth_password as string,
            }
            : undefined;

    const apiKey = node.details?.auth_key; // chainstack toncenter often wants this as ?api_key=

    const doJsonRpc = async (base: string) => {
        // v3: many endpoints expose /jsonrpc; support both base and /jsonrpc
        const postTargets = new Set<string>();
        const baseSanitized = base.replace(/\/+$/, "");
        const lower = baseSanitized.toLowerCase();
        if (lower.endsWith("/jsonrpc")) {
            postTargets.add(baseSanitized);
        } else {
            postTargets.add(`${baseSanitized}/jsonrpc`);
            postTargets.add(baseSanitized); // some setups accept JSON-RPC at root
        }

        const body = {
            id: "balance",
            jsonrpc: "2.0",
            method: "getAddressBalance",
            params: [{ address }],
        };

        for (const url of postTargets) {
            try {
                const cfg: AxiosRequestConfig = {
                    headers,
                    auth,
                    timeout: TON_TIMEOUT_MS,
                    params: apiKey ? { api_key: apiKey } : undefined,
                    validateStatus: (s) => s >= 200 && s < 500,
                };
                const { data, status } = await axios.post(url, body, cfg);

                // reject HTML/text
                if (typeof data === "string" && /<[^>]+>/.test(data)) {
                    throw new Error(`HTML from toncenter (JSON-RPC) status=${status}`);
                }

                if (data?.error) {
                    // common errors: invalid address, not initialized
                    const msg = data.error?.message || data.error;
                    throw new Error(String(msg || "toncenter JSON-RPC error"));
                }

                const bal = extractTonBalanceValue(data?.result ?? data);
                if (bal === null) throw new Error("Invalid TON balance response (JSON-RPC)");
                return { raw: data, balance: String(bal) };
            } catch (e) {
                // try next post target
                continue;
            }
        }
        throw new Error("toncenter JSON-RPC attempts failed");
    };

    const doRest = async (base: string) => {
        const baseSanitized = base.replace(/\/+$/, "");
        const baseLower = baseSanitized.toLowerCase();
        // Try canonical REST path first
        const rest = baseLower.endsWith("/jsonrpc") ? baseSanitized.slice(0, -"/jsonrpc".length) : baseSanitized;

        const mkCfg = (extra: Record<string, string> = {}) =>
            ({
                headers,
                auth,
                timeout: TON_TIMEOUT_MS,
                params: apiKey ? { api_key: apiKey, ...extra } : extra,
                validateStatus: (s: number) => s >= 200 && s < 500,
            } as AxiosRequestConfig);

        // (1) /getAddressBalance?address=...
        try {
            const { data, status } = await axios.get(`${rest}/getAddressBalance`, mkCfg({ address }));
            if (typeof data === "string" && /<[^>]+>/.test(data)) {
                throw new Error(`HTML from toncenter (REST) status=${status}`);
            }
            const bal = extractTonBalanceValue(data);
            if (bal !== null) return { raw: data, balance: String(bal) };
        } catch (_) { /* pass */ }

        // (2) GET rest?method=getAddressBalance&address=...
        try {
            const { data, status } = await axios.get(rest, mkCfg({ method: "getAddressBalance", address }));
            if (typeof data === "string" && /<[^>]+>/.test(data)) {
                throw new Error(`HTML from toncenter (REST-query) status=${status}`);
            }
            const bal = extractTonBalanceValue(data);
            if (bal !== null) return { raw: data, balance: String(bal) };
        } catch (_) { /* pass */ }

        throw new Error("toncenter REST attempts failed");
    };

    // Try v3 JSON-RPC first, then REST, across all provided endpoints
    let lastErr: unknown = null;
    for (const ep of endpointCandidates) {
        try {
            const r = await doJsonRpc(ep);
            return {
                chain: "ton",
                balance: r.balance,
                unit: "nanotons",
                raw: r.raw,
                nodeId: node.id,
                provider: "chainstack",
            };
        } catch (e) {
            lastErr = e;
            // try REST for same endpoint
            try {
                const r2 = await doRest(ep);
                return {
                    chain: "ton",
                    balance: r2.balance,
                    unit: "nanotons",
                    raw: r2.raw,
                    nodeId: node.id,
                    provider: "chainstack",
                };
            } catch (e2) {
                lastErr = e2;
                // move on to next candidate
            }
        }
    }

    // Optional: public TONAPI fallback (best-effort)
    try {
        const tonapiKey = process.env.TONAPI_KEY?.trim();
        const { data } = await axios.get(
            `https://tonapi.io/v2/accounts/${encodeURIComponent(address)}`,
            {
                timeout: TON_TIMEOUT_MS,
                headers: tonapiKey ? { Authorization: `Bearer ${tonapiKey}` } : undefined,
                validateStatus: (s) => s >= 200 && s < 500,
            }
        );
        const bal = extractTonBalanceValue(data);
        if (bal !== null) {
            return {
                chain: "ton",
                balance: String(bal),
                unit: "nanotons",
                raw: data,
                nodeId: node.id,
                provider: "other",
            };
        }
    } catch (e3) {
        lastErr = e3;
    }

    // If we got here, everything failed
    if (lastErr instanceof Error) throw lastErr;
    throw new Error("Unable to fetch TON balance from any endpoint");
};


const normalizeAptosAddress = (address: string): string => {
    const trimmed = address.trim();
    const withoutPrefix = trimmed.startsWith("0x") ? trimmed.slice(2) : trimmed;

    if (!/^[0-9a-fA-F]*$/.test(withoutPrefix)) {
        throw new Error("Invalid Aptos address provided");
    }

    const normalizedHex = withoutPrefix.replace(/^0+/, "");
    const evenLengthHex = normalizedHex.length % 2 === 0 ? normalizedHex : `0${normalizedHex}`;

    if (evenLengthHex.length > 64) {
        throw new Error("Aptos address length exceeds 32 bytes");
    }

    const padded = evenLengthHex.padStart(64, "0");

    return `0x${padded.toLowerCase()}`;
};

const fetchAptosBalance = async (node: ChainstackNode, address: string): Promise<ChainBalanceResponse> => {
    const endpoint = ensureHttpsEndpoint(node);
    const config = buildNodeAxiosConfig(node);
    const normalizedAddress = normalizeAptosAddress(address);
    const resourceType = "0x1::coin::CoinStore<0x1::aptos_coin::AptosCoin>";
    const sanitizedEndpoint = endpoint.replace(/\/+$/, "");
    const url = `${sanitizedEndpoint}/v1/accounts/${normalizedAddress}/resource/${encodeURIComponent(resourceType)}`;

    try {
        const response = await axios.get(url, config);
        const data = response.data;
        const balance = data?.data?.coin?.value;

        if (typeof balance !== "string") {
            throw new Error("Invalid Aptos balance response");
        }

        return {
            chain: "apt",
            balance,
            unit: "octas",
            raw: data,
            nodeId: node.id,
            provider: "chainstack",
        };
    } catch (err) {
        if (axios.isAxiosError(err) && err.response?.status === 404) {
            try {
                const viewUrl = `${sanitizedEndpoint}/v1/view`;
                const viewPayload = {
                    function: "0x1::coin::balance",
                    type_arguments: ["0x1::aptos_coin::AptosCoin"],
                    arguments: [normalizedAddress],
                };

                const viewResponse = await axios.post(viewUrl, viewPayload, config);
                const viewData = viewResponse.data;
                const [viewBalance] = Array.isArray(viewData) ? viewData : [];
                const balance =
                    typeof viewBalance === "string"
                        ? viewBalance
                        : typeof viewBalance === "number"
                            ? viewBalance.toString()
                            : null;

                if (balance !== null) {
                    return {
                        chain: "apt",
                        balance,
                        unit: "octas",
                        raw: viewData,
                        nodeId: node.id,
                        provider: "chainstack",
                    };
                }
            } catch (viewError) {
                if (!axios.isAxiosError(viewError) || viewError.response?.status !== 404) {
                    throw viewError;
                }
            }

            return {
                chain: "apt",
                balance: "0",
                unit: "octas",
                raw: err.response?.data,
                nodeId: node.id,
                provider: "chainstack",
            };
        }

        throw err;
    }
};

const BITCOIN_SATOSHI_PRECISION = 8;

const normalizeBitcoinDecimalToSats = (value: string): string => {
    const trimmed = value.trim();

    if (trimmed === "") {
        throw new Error("Invalid Bitcoin balance response");
    }

    let digits = trimmed;
    let sign = "";

    if (digits.startsWith("+")) {
        digits = digits.slice(1);
    } else if (digits.startsWith("-")) {
        sign = "-";
        digits = digits.slice(1);
    }

    if (digits === "" || digits === ".") {
        throw new Error("Invalid Bitcoin balance response");
    }

    const [wholePartRaw, fractionalPartRaw = ""] = digits.split(".");
    const wholePart = wholePartRaw === "" ? "0" : wholePartRaw;

    if (!/^\d+$/.test(wholePart) || !/^\d*$/.test(fractionalPartRaw)) {
        throw new Error("Invalid Bitcoin balance response");
    }

    let fractionalPart = fractionalPartRaw;

    if (fractionalPart.length > BITCOIN_SATOSHI_PRECISION) {
        const extra = fractionalPart.slice(BITCOIN_SATOSHI_PRECISION);

        if (/[^0]/.test(extra)) {
            throw new Error("Bitcoin balance exceeds satoshi precision");
        }

        fractionalPart = fractionalPart.slice(0, BITCOIN_SATOSHI_PRECISION);
    }

    const paddedFraction = fractionalPart.padEnd(BITCOIN_SATOSHI_PRECISION, "0");
    const combined = stripLeadingZeros(`${wholePart}${paddedFraction}`);

    if (combined === "0") {
        return "0";
    }

    return sign === "-" ? `-${combined}` : combined;
};

const normalizeBitcoinBalanceToSats = (value: unknown): string => {
    if (typeof value === "number") {
        if (!Number.isFinite(value)) {
            throw new Error("Invalid Bitcoin balance response");
        }

        if (Number.isInteger(value)) {
            if (!Number.isSafeInteger(value)) {
                throw new Error("Bitcoin balance exceeds numeric precision");
            }

            return value.toString();
        }

        return normalizeBitcoinDecimalToSats(value.toFixed(BITCOIN_SATOSHI_PRECISION));
    }

    if (typeof value === "string") {
        const trimmed = value.trim();

        if (trimmed === "") {
            throw new Error("Invalid Bitcoin balance response");
        }

        if (/^-?\d+$/.test(trimmed)) {
            return trimmed;
        }

        if (/^-?(?:\d+)?(?:\.\d+)?$/.test(trimmed)) {
            return normalizeBitcoinDecimalToSats(trimmed);
        }

        if (/^-?(?:\d+)?(?:\.\d+)?e[-+]?\d+$/i.test(trimmed)) {
            const numeric = Number(trimmed);

            if (!Number.isFinite(numeric)) {
                throw new Error("Invalid Bitcoin balance response");
            }

            return normalizeBitcoinBalanceToSats(numeric);
        }

        throw new Error("Invalid Bitcoin balance response");
    }

    throw new Error("Invalid Bitcoin balance response");
};

const formatBitcoinFromSats = (value: string): string => {
    if (!/^-?\d+$/.test(value)) {
        throw new Error("Invalid satoshi balance");
    }

    let sign = "";
    let digits = value;

    if (value.startsWith("-")) {
        sign = "-";
        digits = value.slice(1);
    }

    digits = stripLeadingZeros(digits);

    if (digits === "0") {
        return `${sign}0`;
    }

    if (digits.length <= 8) {
        const fraction = digits.padStart(8, "0").replace(/0+$/, "");

        if (fraction.length === 0) {
            return `${sign}0`;
        }

        return `${sign}0.${fraction}`;
    }

    const whole = stripLeadingZeros(digits.slice(0, digits.length - 8));
    const fraction = digits.slice(digits.length - 8).replace(/0+$/, "");

    if (fraction.length === 0) {
        return `${sign}${whole}`;
    }

    return `${sign}${whole}.${fraction}`;
};

const btcCache = new Map<string, { bestblock: string; balance: string; at: number }>();
const BTC_CACHE_TTL_MS = 10 * 60 * 1000; // 10 minutes

// De-duplicate concurrent scans per address
const inFlightBtc = new Map<string, Promise<{ balance: string; raw: unknown }>>();

const getBestBlockHash = async (endpoint: string, config: AxiosRequestConfig) => {
    const payload = { jsonrpc: "1.0", id: "tip", method: "getbestblockhash", params: [] };
    const { data } = await axios.post(endpoint, payload, config);
    if (data?.error) throw new Error(data.error?.message || "getbestblockhash failed");
    return data.result as string;
};


const BITCOIN_RPC_TIMEOUT_MS = 15_000;

const fetchBitcoinBalance = async (node: ChainstackNode, address: string): Promise<ChainBalanceResponse> => {
    // IMPORTANT: BTC wants the auth key in the URL path, not in headers or basic auth.
    const baseEndpoint = ensureHttpsEndpoint(node);
    const btcEndpoint = ensureAuthKeyInPath(baseEndpoint, node.details?.auth_key);

    const config: AxiosRequestConfig = {
        ...buildNodeAxiosConfig(node, { noAuthHeaders: true }),
        timeout: BITCOIN_RPC_TIMEOUT_MS, // e.g. 15000
    };

    const getBestBlockHash = async (endpoint: string, cfg: AxiosRequestConfig) => {
        try {
            const payload = { jsonrpc: "1.0", id: "tip", method: "getbestblockhash", params: [] };
            const { data } = await axios.post(endpoint, payload, cfg);
            if (data?.error) throw new Error(data.error?.message || "getbestblockhash failed");
            return data.result as string;
        } catch (e: any) {
            if (axios.isAxiosError(e) && (e.response?.status === 499 || e.code === "ECONNABORTED")) {
                const err = new Error("BTC_TIP_TIMEOUT");
                (err as any).code = "BTC_TIP_TIMEOUT";
                throw err;
            }
            throw e;
        }
    };

    const tryScanTxOutSet = async (): Promise<{ balance: string; raw: unknown }> => {
        const tip = await getBestBlockHash(btcEndpoint, config);

        const cached = btcCache.get(address);
        if (cached && cached.bestblock === tip && Date.now() - cached.at < BTC_CACHE_TTL_MS) {
            return { balance: cached.balance, raw: { cached: true, bestblock: tip } };
        }

        const payload = {
            jsonrpc: "1.0",
            id: "balance",
            method: "scantxoutset",
            params: ["start", [{ desc: `addr(${address})` }]],
        };

        const { data } = await axios.post(btcEndpoint, payload, config);
        if (data?.error) {
            const message = data.error?.message || "Bitcoin scantxoutset failed";
            const lowered = String(message).toLowerCase();

            // Existing checks...
            if (lowered.includes("method not found") || lowered.includes("disabled") || lowered.includes("not enabled")) {
                const err = new Error("BTC_SCAN_UNAVAILABLE");
                (err as any).code = "BTC_SCAN_UNAVAILABLE";
                throw err;
            }

            // NEW: global lock contention on shared node
            if (lowered.includes("scan already in progress")) {
                const err = new Error('BTC_SCAN_IN_PROGRESS');
                (err as any).code = "BTC_SCAN_IN_PROGRESS";
                throw err;
            }

            const err = new Error(`BTC_SCAN_FAILED: ${message}`);
            (err as any).code = "BTC_SCAN_FAILED";
            throw err;
        }

        const amount = data?.result?.total_amount;
        const bestblock = data?.result?.bestblock;

        const balance =
            typeof amount === "number" ? amount.toString() :
                typeof amount === "string" ? amount : null;

        if (!balance || !bestblock) {
            const err = new Error("BTC_INVALID_RESPONSE");
            (err as any).code = "BTC_INVALID_RESPONSE";
            throw err;
        }

        btcCache.set(address, { bestblock, balance, at: Date.now() });
        return { balance, raw: data };
    };

    // de-dup concurrent scans
    const key = address;
    const existing = inFlightBtc.get(key);
    if (existing) {
        const result = await existing;
        return {
            chain: "btc",
            balance: result.balance,
            unit: "btc",
            raw: result.raw,
            nodeId: node.id,
            provider: "chainstack",
        };
    }

    const p = tryScanTxOutSet().finally(() => inFlightBtc.delete(key));
    inFlightBtc.set(key, p);

    const scanned = await p;
    return {
        chain: "btc",
        balance: scanned.balance,
        unit: "btc",
        raw: scanned.raw,
        nodeId: node.id,
        provider: "chainstack",
    };
};

const normalizeBitcoinPayload = (value: unknown): string => normalizeHexPayload(value).slice(2);

const broadcastBitcoinTransaction = async (
    node: ChainstackNode,
    signedPayload: string,
): Promise<ChainBroadcastResponse> => {
    const baseEndpoint = ensureHttpsEndpoint(node);
    const endpoint = ensureAuthKeyInPath(baseEndpoint, node.details?.auth_key);

    const config: AxiosRequestConfig = {
        ...buildNodeAxiosConfig(node, { noAuthHeaders: true }),
        timeout: BITCOIN_RPC_TIMEOUT_MS,
    };

    const payload = {
        jsonrpc: "1.0",
        id: "broadcast",
        method: "sendrawtransaction",
        params: [signedPayload],
    };

    const response = await axios.post(endpoint, payload, config);
    const data = response.data;

    if (data?.error) {
        throw new Error(data.error?.message || "Bitcoin broadcast failed");
    }

    return {
        chain: "btc",
        txId: data?.result,
        raw: data,
        nodeId: node.id,
        provider: "chainstack",
    };
};

const CHAIN_HANDLERS: Record<string, ChainHandler> = {
    btc: {
        selectNode: (nodes) => nodes.find((node) => endpointIncludes(node, "bitcoin")) ?? null,
        fetchBalance: (node, address) => fetchBitcoinBalance(node, address),
    },
    eth: {
        validateAddress: (address) => /^0x[a-fA-F0-9]{40}$/.test(address),
        selectNode: (nodes) => nodes.find((node) => endpointIncludes(node, "ethereum")) ?? null,
        fetchBalance: (node, address) => fetchEvmBalance("eth", node, address),
    },
    bnb: {
        validateAddress: (address) => /^0x[a-fA-F0-9]{40}$/.test(address),
        selectNode: (nodes) => nodes.find((node) => endpointIncludes(node, "bsc")) ?? null,
        fetchBalance: (node, address) => fetchEvmBalance("bnb", node, address),
    },
    sol: {
        selectNode: (nodes) => nodes.find((node) => endpointIncludes(node, "solana")) ?? null,
        fetchBalance: (node, address) => fetchSolanaBalance(node, address),
    },
    tron: {
        selectNode: (nodes) => nodes.find((node) => endpointIncludes(node, "tron")) ?? null,
        fetchBalance: (node, address) => fetchTronBalance(node, address),
    },
    ton: {
        selectNode: (nodes) =>
            nodes.find((node) => node.details?.toncenter_api_v2 || node.details?.toncenter_api_v3) ?? null,
        fetchBalance: (node, address) => fetchTonBalance(node, address),
    },
    apt: {
        validateAddress: (address) => {
            if (!/^(0x)?[a-fA-F0-9]+$/.test(address)) {
                return false;
            }

            const hex = address.startsWith("0x") ? address.slice(2) : address;
            return hex.length % 2 === 0;
        },
        selectNode: (nodes) => nodes.find((node) => endpointIncludes(node, "aptos")) ?? null,
        fetchBalance: (node, address) => fetchAptosBalance(node, address),
    },
};

const CHAIN_BROADCAST_HANDLERS: Record<string, ChainBroadcastHandler> = {
    btc: {
        normalizePayload: normalizeBitcoinPayload,
        selectNode: CHAIN_HANDLERS.btc.selectNode,
        broadcast: (node, payload) => broadcastBitcoinTransaction(node, payload as string),
    },
    eth: {
        normalizePayload: normalizeHexPayload,
        selectNode: CHAIN_HANDLERS.eth.selectNode,
        broadcast: (node, payload) => broadcastEvmTransaction("eth", node, payload as string),
    },
    bnb: {
        normalizePayload: normalizeHexPayload,
        selectNode: CHAIN_HANDLERS.bnb.selectNode,
        broadcast: (node, payload) => broadcastEvmTransaction("bnb", node, payload as string),
    },
    tron: {
        normalizePayload: normalizeHexPayload,
        selectNode: CHAIN_HANDLERS.tron.selectNode,
        broadcast: (node, payload) => broadcastTronTransaction(node, payload as string),
    },
    sol: {
        normalizePayload: normalizeBase64Payload,
        selectNode: CHAIN_HANDLERS.sol.selectNode,
        broadcast: (node, payload) => broadcastSolanaTransaction(node, payload as string),
    },
};

export const balance = async (req: express.Request, res: express.Response) => {
    const { chain, address } = req.params;

    if (!chain || !address) {
        res.status(400).send("Missing chain or address");
        return;
    }
    if (!CHAIN_PARAM_REGEX.test(chain)) {
        res.status(400).send("Invalid chain parameter");
        return;
    }

    const normalizedChain = chain.toLowerCase();
    const handler = CHAIN_HANDLERS[normalizedChain];

    if (!handler) {
        res.status(400).send("Unsupported chain");
        return;
    }

    const provider = parseBalanceProvider(req.query.provider);

    // Ignore provider=chainz for non-BTC (but surface it for observability)
    if (provider === "chainz" && normalizedChain !== "btc") {
        console.warn(`provider=chainz ignored for chain=${normalizedChain}`);
        res.setHeader("x-provider-override-ignored", "true");
    }

    if (provider === "blockstream" && normalizedChain !== "btc") {
        console.warn(`provider=blockstream ignored for chain=${normalizedChain}`);
        res.setHeader("x-provider-override-ignored", "true");
    }

    // Slightly extend server timeouts for BTC path
    if (normalizedChain === "btc") {
        const extendedTimeout = BITCOIN_RPC_TIMEOUT_MS + 30_000;
        if (typeof req.setTimeout === "function") req.setTimeout(extendedTimeout);
        if (typeof res.setTimeout === "function") res.setTimeout(extendedTimeout);
    }

    const applyProviderHeaders = (response: ChainBalanceResponse) => {
        res.setHeader("x-provider", response.provider);
        if (
            response.provider === "blockstream" &&
            response.rateLimitRemaining !== undefined
        ) {
            res.setHeader(
                "x-blockstream-ratelimit-remaining",
                String(response.rateLimitRemaining),
            );
        }
    };

    const sendBalanceResponse = (
        responsePayload: ChainBalanceResponse,
        fallbackReason?: string,
    ) => {
        applyProviderHeaders(responsePayload);
        if (fallbackReason) {
            res.setHeader("x-fallback-reason", fallbackReason);
        }
        res.status(200).json(responsePayload);
    };

    const tryBlockstreamFallback = async (reason: string): Promise<boolean> => {
        try {
            const balanceResponse = await fetchBlockstreamBalance(normalizedChain, address);
            sendBalanceResponse(balanceResponse, reason);
            return true;
        } catch (blockstreamErr) {
            console.error("BTC fallback to Blockstream failed:", blockstreamErr);
            return false;
        }
    };

    const tryChainzFallback = async (reason: string): Promise<boolean> => {
        try {
            const balanceResponse = await fetchChainzBalance(normalizedChain, address);
            sendBalanceResponse(balanceResponse, reason);
            return true;
        } catch (fallbackErr) {
            console.error("BTC fallback to Chainz failed:", fallbackErr);
            return false;
        }
    };

    try {
        // Per-chain address validation (when provided)
        if (handler.validateAddress && !handler.validateAddress(address)) {
            res.status(400).send("Invalid address format");
            return;
        }

        // If client explicitly asks for Chainz and it's BTC → go straight there
        if (normalizedChain === "btc" && provider === "chainz") {
            const balanceResponse = await fetchChainzBalance(normalizedChain, address);
            sendBalanceResponse(balanceResponse);
            return;
        }

        if (normalizedChain === "btc" && provider === "blockstream") {
            const balanceResponse = await fetchBlockstreamBalance(normalizedChain, address);
            sendBalanceResponse(balanceResponse);
            return;
        }

        // Default path: Chainstack (wrap node discovery in try/catch)
        let node: ChainstackNode | null = null;
        try {
            const nodes = await fetchChainstackNodes();
            node = handler.selectNode(nodes);
        } catch (fetchErr) {
            if (normalizedChain === "btc") {
                console.error(
                    "Fetching Chainstack nodes failed; falling back to alternative providers:",
                    fetchErr,
                );
                if (await tryBlockstreamFallback("nodes-fetch-failed")) return;
                if (await tryChainzFallback("nodes-fetch-failed")) return;
            }
            throw fetchErr;
        }

        if (!node) {
            if (normalizedChain === "btc") {
                if (await tryBlockstreamFallback("no-node")) return;
                if (await tryChainzFallback("no-node")) return;
            }
            console.error(`No Chainstack node available for ${normalizedChain}`);
            res.status(502).send("No Chainstack node available for requested chain");
            return;
        }

        // Try Chainstack balance
        try {
            const balanceResponse = await handler.fetchBalance(node, address);
            res.setHeader("x-provider", "chainstack");
            res.status(200).json(balanceResponse);
            return;
        } catch (err: any) {
            // BTC: fallback on ANY error (timeout, unsupported RPC, etc.)
            if (normalizedChain === "btc") {
                console.error("BTC Chainstack path failed, attempting fallbacks:", {
                    code: err?.code,
                    message: err?.message,
                });
                const reason = String(err?.code || err?.message || "unknown");
                if (await tryBlockstreamFallback(reason)) return;
                if (await tryChainzFallback(reason)) return;
            }
            throw err;
        }
    } catch (err) {
        console.error("balance(): error while fetching chain balance", err);

        if (axios.isAxiosError(err) && err.response) {
            const { status, data } = err.response;
            if (data !== undefined) {
                if (data !== null && typeof data === "object") {
                    res.status(status).json(data);
                } else {
                    res.status(status).json({ error: String(data) });
                }
            } else {
                res.sendStatus(status);
            }
            return;
        }

        // Final catch-all
        const msg = err instanceof Error ? err.message : "Unknown error";
        res.status(502).json({ error: msg });
    }
};

export const broadcast = async (req: express.Request, res: express.Response) => {
    const { chain } = req.params;
    const signedPayload = extractSignedPayload(req.body as BroadcastRequestBody);

    if (!chain) {
        res.status(400).send("Missing chain parameter");
        return;
    }

    if (!CHAIN_PARAM_REGEX.test(chain)) {
        res.status(400).send("Invalid chain parameter");
        return;
    }

    const normalizedChain = chain.toLowerCase();
    const handler = CHAIN_BROADCAST_HANDLERS[normalizedChain];

    if (!handler) {
        res.status(400).send("Unsupported chain for broadcast");
        return;
    }

    if (typeof signedPayload === "undefined") {
        res.status(400).send("Missing signed payload");
        return;
    }

    let normalizedPayload: unknown = signedPayload;
    try {
        if (handler.normalizePayload) {
            normalizedPayload = handler.normalizePayload(signedPayload);
        }
    } catch (err) {
        res.status(400).json({ error: err instanceof Error ? err.message : "Invalid payload" });
        return;
    }

    if (typeof normalizedPayload !== "string") {
        res.status(400).json({ error: "Signed payload must be a string" });
        return;
    }

    try {
        const nodes = await fetchChainstackNodes();
        const node = handler.selectNode(nodes);

        if (!node) {
            console.error(`No Chainstack node available for ${normalizedChain} broadcast`);
            res.status(502).send("No Chainstack node available for requested chain");
            return;
        }

        const response = await handler.broadcast(node, normalizedPayload);
        res.setHeader("x-provider", "chainstack");
        res.status(200).json(response);
    } catch (err) {
        console.error("broadcast(): error while submitting transaction", err);

        if (axios.isAxiosError(err) && err.response) {
            const { status, data } = err.response;
            if (data !== undefined) {
                if (data !== null && typeof data === "object") {
                    res.status(status).json(data);
                } else {
                    res.status(status).json({ error: String(data) });
                }
            } else {
                res.sendStatus(status);
            }
            return;
        }

        const msg = err instanceof Error ? err.message : "Unknown error";
        res.status(502).json({ error: msg });
    }
};

export const leaderboard = async (req: express.Request, res: express.Response) => {
    const { duration } = req.params;
    pipe(apiRequest(`leaderboard?duration=${duration}`, "GET"), res);
};

export const getAnnouncement = async (req: express.Request, res: express.Response) => {
    res.send(announcements)
}

export const referrals = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;
    const { max_id } = req.query;
    let u = `referrals/${username}?size=20`;
    if (max_id) {
        u += `&max_id=${max_id}`;
    }
    pipe(apiRequest(u, "GET"), res);
};

export const referralsStats = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;
    let u = `referrals/${username}/stats`;
    pipe(apiRequest(u, "GET"), res);
};


export const curation = async (req: express.Request, res: express.Response) => {
    const { duration } = req.params;
    pipe(apiRequest(`curation?duration=${duration}`, "GET"), res);
};

export const promotedEntries = async (req: express.Request, res: express.Response) => {
    const { limit = '200', short_content = '0' } = req.query;
    const posts = await getPromotedEntries(parseInt(limit as string), parseInt(short_content as string));
    res.send(posts);
};

export const commentHistory = async (req: express.Request, res: express.Response) => {
    const { author, permlink, onlyMeta } = req.body;

    let u = `comment-history/${author}/${permlink}`;
    if (onlyMeta === '1') {
        u += '?only_meta=1';
    }

    pipe(apiRequest(u, "GET"), res);
};

export const wavesTags = async (req: express.Request, res: express.Response) => {
    const { container, tag } = req.query;
    const u = `waves/tags?container=${container}&tag=${tag}`;
    pipe(apiRequest(u, "GET"), res);
};

export const wavesAccount = async (req: express.Request, res: express.Response) => {
    const { container, username } = req.query;
    const u = `waves/account?container=${container}&username=${username}`;
    pipe(apiRequest(u, "GET"), res);
};

export const wavesFollowing = async (req: express.Request, res: express.Response) => {
    const { container, username } = req.query;
    const u = `waves/following?container=${container}&username=${username}`;
    pipe(apiRequest(u, "GET"), res);
};

export const wavesTrendingTags = async (req: express.Request, res: express.Response) => {
    const { container, hours, days } = req.query;
    let u = `waves/trending/tags?container=${container}`;

    if (hours) {
        u += `&hours=${hours}`;
    }

    if (days) {
        u += `&days=${days}`;
    }

    pipe(apiRequest(u, "GET"), res);
};

export const wavesTrendingAuthors = async (req: express.Request, res: express.Response) => {
    const { container } = req.query;
    const u = `waves/trending/authors?container=${container}`;
    pipe(apiRequest(u, "GET"), res);
};

export const points = async (req: express.Request, res: express.Response) => {
    const { username } = req.body;
    pipe(apiRequest(`users/${username}`, "GET"), res);
};

export const pointList = async (req: express.Request, res: express.Response) => {
    const { username, type } = req.body;
    let u = `users/${username}/points?size=50`;
    if (type) {
        u += `&type=${type}`;
    }
    pipe(apiRequest(u, "GET"), res);
};

export const portfolio = async (req: express.Request, res: express.Response) => {
    const { username } = req.body;
    let u = `users/${username}/portfolio`;
    pipe(apiRequest(u, "GET"), res);
};

export const createAccount = async (req: express.Request, res: express.Response) => {
    const { username, email, referral } = req.body;

    const headers = { 'X-Real-IP-V': req.headers['x-forwarded-for'] || '' };
    const payload = { username, email, referral };

    pipe(apiRequest(`signup/account-create`, "POST", headers, payload), res);
};

export const createAccountFriend = async (req: express.Request, res: express.Response) => {
    const { username, email, friend } = req.body;

    const headers = { 'X-Real-IP-V': req.headers['x-forwarded-for'] || '' };
    const payload = { username, email, friend };

    pipe(apiRequest(`signup/account-create-friend`, "POST", headers, payload), res);
};

export const notifications = async (req: express.Request, res: express.Response) => {
    let username = await validateCode(req);
    const { filter, since, limit, user } = req.body;

    if (!username) {
        if (!user) {
            res.status(401).send("Unauthorized");
            return;
        } else {
            username = user;
        }
    }
    // if user defined but not same as user's code
    if (user && username !== user) {
        username = user;
    }

    let u = `activities/${username}`

    if (filter) {
        u = `${filter}/${username}`
    }

    if (since) {
        u += `?since=${since}`;
    }

    if (since && limit) {
        u += `&limit=${limit}`;
    }

    if (!since && limit) {
        u += `?limit=${limit}`;
    }

    pipe(apiRequest(u, "GET"), res);
};

export const publicUnreadNotifications = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;

    if (!username) {
        res.status(400).send("Missing username");
        return;
    }

    pipe(apiRequest(`activities/${username}/unread-count`, "GET"), res);
};

/* Login required endpoints */

export const unreadNotifications = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }

    pipe(apiRequest(`activities/${username}/unread-count`, "GET"), res);
};

export const markNotifications = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }

    const { id } = req.body;
    const data: { id?: string } = {};

    if (id) {
        data.id = id;
    }

    pipe(apiRequest(`activities/${username}`, "PUT", {}, data), res);
};

export const registerDevice = async (req: express.Request, res: express.Response) => {
    const _username = await validateCode(req);
    if (!_username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { username, token, system, allows_notify, notify_types } = req.body;
    const data = { username, token, system, allows_notify, notify_types };
    pipe(apiRequest(`rgstrmbldvc/`, "POST", {}, data), res);
};

export const detailDevice = async (req: express.Request, res: express.Response) => {
    const _username = await validateCode(req);
    if (!_username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { username, token } = req.body;
    pipe(apiRequest(`mbldvcdtl/${username}/${token}`, "GET"), res);
};

export const images = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }

    pipe(apiRequest(`images/${username}`, "GET"), res);
}

export const imagesDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`images/${username}/${id}`, "DELETE"), res);
}

export const imagesAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { url } = req.body;
    const data = { username, image_url: url };
    pipe(apiRequest(`image`, "POST", {}, data), res);
}

export const drafts = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`drafts/${username}`, "GET"), res);
}

export const draftsAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { title, body, tags, meta } = req.body;
    const data = { username, title, body, tags, meta };
    pipe(apiRequest(`draft`, "POST", {}, data), res);
}

export const draftsUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id, title, body, tags, meta } = req.body;
    const data = { username, title, body, tags, meta };
    pipe(apiRequest(`drafts/${username}/${id}`, "PUT", {}, data), res);
}

export const draftsDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`drafts/${username}/${id}`, "DELETE"), res);
}

export const bookmarks = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`bookmarks/${username}`, "GET"), res);
}

export const bookmarksAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { author, permlink } = req.body;
    const data = { username, author, permlink, chain: 'steem' };
    pipe(apiRequest(`bookmark`, "POST", {}, data), res);
}

export const bookmarksDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`bookmarks/${username}/${id}`, "DELETE"), res);
}

export const schedules = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`schedules/${username}`, "GET"), res);
}

export const schedulesAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }

    const { permlink, title, body, meta, options, schedule, reblog } = req.body;

    const data = { username, permlink, title, body, meta, options, schedule, reblog: reblog ? 1 : 0 };
    pipe(apiRequest(`schedules`, "POST", {}, data), res);
}

export const schedulesDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`schedules/${username}/${id}`, "DELETE"), res);
}

export const schedulesMove = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`schedules/${username}/${id}`, "PUT"), res);
}

export const favorites = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`favorites/${username}`, "GET"), res);
}

export const favoritesCheck = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { account } = req.body;
    pipe(apiRequest(`isfavorite/${username}/${account}`, "GET"), res);
}

export const favoritesAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { account } = req.body;
    const data = { username, account };
    pipe(apiRequest(`favorite`, "POST", {}, data), res);
}

export const favoritesDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { account } = req.body;
    pipe(apiRequest(`favoriteUser/${username}/${account}`, "DELETE"), res);
}

export const fragments = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`fragments/${username}`, "GET"), res);
}

export const fragmentsAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { title, body } = req.body;
    const data = { username, title, body };
    pipe(apiRequest(`fragment`, "POST", {}, data), res);
}

export const fragmentsUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id, title, body } = req.body;
    const data = { title, body };
    pipe(apiRequest(`fragments/${username}/${id}`, "PUT", {}, data), res);
}

export const fragmentsDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`fragments/${username}/${id}`, "DELETE"), res);
}

export const decks = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`decks/${username}`, "GET"), res);
}

export const decksAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { title, settings } = req.body;
    const data = { username, title, settings };
    pipe(apiRequest(`deck`, "POST", {}, data), res);
}

export const decksUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id, title, settings } = req.body;
    const data = { username, title, settings };
    pipe(apiRequest(`decks/${username}/${id}`, "PUT", {}, data), res);
}

export const decksDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`decks/${username}/${id}`, "DELETE"), res);
}

export const recoveries = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`recoveries/${username}`, "GET"), res);
}

export const recoveriesAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { email, public_keys } = req.body;
    const data = { username, email, public_keys };
    pipe(apiRequest(`recovery`, "POST", {}, data), res);
}

export const recoveriesUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id, email, public_keys } = req.body;
    const data = { username, email, public_keys };
    pipe(apiRequest(`recoveries/${username}/${id}`, "PUT", {}, data), res);
}

export const recoveriesDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`recoveries/${username}/${id}`, "DELETE"), res);
}

export const pointsClaim = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const data = { us: username };
    pipe(apiRequest(`claim`, "PUT", {}, data), res);
}

export const pointsCalc = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { amount } = req.body;
    pipe(apiRequest(`estm-calc?username=${username}&amount=${amount}`, "GET"), res);
}

export const promotePrice = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`promote-price`, "GET"), res);
}

export const promotedPost = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { author, permlink } = req.body;
    pipe(apiRequest(`promoted-posts/${author}/${permlink}`, "GET"), res);
}

export const boostPlusPrice = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`boost-plus-price`, "GET"), res);
}

export const boostedPlusAccount = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { account } = req.body;
    pipe(apiRequest(`boosted-plus-accounts/${account}`, "GET"), res);
}

export const boostOptions = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`boost-options`, "GET"), res);
}

export const boostedPost = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { author, permlink } = req.body;
    pipe(apiRequest(`boosted-posts/${author}/${permlink}`, "GET"), res);
}

export const activities = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { ty, bl, tx } = req.body;

    if (ty === 10) {
        const vip = req.headers['x-real-ip'] || req.connection.remoteAddress || req.headers['x-forwarded-for'] || '';
        let identifier = `${vip}`;

        let rec;
        try {
            rec = cache.get(identifier);
        } catch (e) {
            console.error(e);
            console.error("Cache get failed.");
        }

        if (rec) {
            if (new Date().getTime() - new Date(Number(rec)).getTime() < 900000) {
                res.status(201).send({});
            }
            try {
                cache.set(identifier, new Date().getTime().toString(), 901);
            } catch (error) {
                console.error(error);
                console.error("Cache set failed.");
            }
        } else {
            try {
                cache.set(identifier, new Date().getTime().toString(), 901);
            } catch (error) {
                console.error(error);
                console.error("Cache set failed.");
            }
        }
    }

    let pipe_json = {
        "us": username,
        "ty": ty
    }
    if (bl) {
        pipe_json["bl"] = bl
    }
    if (tx) {
        pipe_json["tx"] = tx
    }

    pipe(apiRequest(`usr-activity`, "POST", {}, pipe_json), res);
}

export const subscribeNewsletter = async (req: express.Request, res: express.Response) => {
    const { email } = req.body;
    const data = { email };
    pipe(apiRequest(`newsletter/subscribe`, "POST", {}, data), res);
}

export const unSubscribeNewsletter = async (req: express.Request, res: express.Response) => {
    const { id } = req.params;
    pipe(apiRequest(`newsletter/subscribe?id=${id}`, "PUT"), res);
}

export const marketData = async (req: express.Request, res: express.Response) => {
    const { fiat, token } = req.params;
    const { fixed } = req.query;
    pipe(apiRequest(`market-data/currency-rate/${fiat}/${token}?fixed=${fixed}`, "GET"), res);
};

export const marketDataLatest = async (req: express.Request, res: express.Response) => {
    pipe(apiRequest(`market-data/latest`, "GET"), res);
};

export const report = async (req: express.Request, res: express.Response) => {
    res.send({
        status: 200,
        body: {
            status: 'ok'
        }
    });
};

export const requestDelete = async (req: express.Request, res: express.Response) => {
    res.send({
        status: 200,
        body: {
            status: 'ok'
        }
    });
};

export const reblogs = async (req: express.Request, res: express.Response) => {
    const { author, permlink } = req.params;
    pipe(apiRequest(`post-reblogs/${author}/${permlink}`, "GET"), res);
};

export const reblogCount = async (req: express.Request, res: express.Response) => {
    const { author, permlink } = req.params;
    pipe(apiRequest(`post-reblog-count/${author}/${permlink}`, "GET"), res);
};

export const gameGet = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { game_type } = req.body;
    pipe(apiRequest(`game/${username}?type=${game_type}`, "GET"), res);
};

export const gamePost = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }

    const { key, game_type } = req.body;
    const data = { key: key };
    pipe(apiRequest(`game/${username}?type=${game_type}`, "POST", {}, data), res);
};

export const purchaseOrder = async (req: express.Request, res: express.Response) => {
    const { platform, product, receipt, user, meta } = req.body;
    if (user !== 'ecency') {
        const username = await validateCode(req);
        if (!username) {
            res.status(401).send("Unauthorized");
            return;
        }
    }

    const data = { platform, product, receipt, user, meta };
    pipe(apiRequest(`purchase-order`, "POST", {}, data), res);
};

export const chats = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`chats/${username}`, "GET"), res);
}

export const chatsAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { key, pubkey, iv, meta } = req.body;
    const data = { username, key, pubkey, iv, meta };
    pipe(apiRequest(`chats`, "POST", {}, data), res);
}

export const chatsUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id, key, pubkey, iv, meta } = req.body;
    const data = { key, pubkey, iv, meta };
    pipe(apiRequest(`chats/${username}/${id}`, "PUT", {}, data), res);
}

export const chatsPub = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;
    pipe(apiRequest(`chats/pub/${username}`, "GET"), res);
}

export const channelAdd = async (req: express.Request, res: express.Response) => {
    const creator = await validateCode(req);
    if (!creator || creator !== 'ecency') {
        res.status(401).send("Unauthorized");
        return;
    }

    const { username, channel_id, meta } = req.body;
    const data = { creator, username, channel_id, meta };
    pipe(apiRequest(`channel`, "POST", {}, data), res);
}

export const channelGet = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;
    pipe(apiRequest(`channel/${username}`, "GET"), res);
}



export const channelsGet = async (req: express.Request, res: express.Response) => {
    const { users } = req.body;
    const data = { users };
    pipe(apiRequest(`channels`, "POST", {}, data), res);
}

export const chatsGet = async (req: express.Request, res: express.Response) => {
    const { users } = req.body;
    const data = { users };
    pipe(apiRequest(`chats/pubs`, "POST", {}, data), res);
}

export const botsGet = async (req: express.Request, res: express.Response) => {
    res.send(bots)
}

export const wallets = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`wallets/${username}`, "GET"), res);
}

export const walletsAdd = async (req: express.Request, res: express.Response) => {
    const { username, token, address, meta, status } = req.body;
    const data = { username, token, address, meta, status };
    pipe(apiRequest(`wallet`, "POST", {}, data), res);
}

export const walletsUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id, token, address, meta } = req.body;
    const data = { username, token, address, meta };
    pipe(apiRequest(`wallets/${username}/${id}`, "PUT", {}, data), res);
}

export const walletsDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { id } = req.body;
    pipe(apiRequest(`wallets/${username}/${id}`, "DELETE"), res);
}

export const walletsExist = async (req: express.Request, res: express.Response) => {
    const { address, token } = req.body;
    pipe(apiRequest(`signup/exist-wallet-accounts?address=${address}&token=${token}`, "GET"), res);
}

export const walletsChkUser = async (req: express.Request, res: express.Response) => {
    const { username } = req.body;
    pipe(apiRequest(`signup/exist-wallet-user?username=${username}`, "GET"), res);
}

export const proposalActive = async (req: express.Request, res: express.Response) => {
    res.send(ACTIVE_PROPOSAL_META);
}
