import express from "express";
import hs from "hivesigner";
import axios from "axios";
import { cryptoUtils, Signature, Client } from '@hiveio/dhive'

import { announcements } from "./announcements";
import { spotlights, Spotlight } from "./spotlights";
import {
    apiRequest,
    getPromotedEntries,
    ChainBalanceResponse,
    fetchEsploraBalance,
} from "../helper";
import {
    RpcProvider,
    EsploraProvider,
    ETH_RPC_POOL,
    BNB_RPC_POOL,
    SOL_RPC_POOL,
    BTC_ESPLORA_POOL,
} from "../chain-providers";

import { pipe } from "../util";
import { cache } from '../cache';
import config from "../../config";
import { ACTIVE_PROPOSAL_META, bots } from "./constants";


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

    // Normalize base64url → standard base64 (client encodes with b64uEnc which replaces +→-, /→_, =→.)
    const normalizedCode = trimmedCode.replace(/-/g, '+').replace(/_/g, '/').replace(/\./g, '=');

    try {
        const buffer = Buffer.from(normalizedCode, "base64");

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

// Public list of Pro usernames for the Pro badge roster. No auth: this is a
// public, cached list served straight from the backend.
export const proMembers = async (req: express.Request, res: express.Response) => {
    pipe(apiRequest(`pro-members`, "GET"), res);
};

const CHAIN_PARAM_REGEX = /^[a-z0-9-]+$/i;

const PROVIDER_TIMEOUT_MS = 10_000;

const JSON_RPC_HEADERS: Record<string, string> = {
    "Content-Type": "application/json",
};

// A request the client got wrong (bad address, unsupported chain). It is
// rejected up front with its HTTP status and never reaches a provider, so it
// must not be reported as a 502 upstream failure.
class TerminalProviderError extends Error {
    status: number;

    constructor(message: string, status = 400) {
        super(message);
        this.name = "TerminalProviderError";
        this.status = status;
    }
}

// Try each provider in the pool in order until one succeeds; fail over on any
// provider-side error to maximize balance availability.
const tryProviders = async <P extends { id: string }, T>(
    chain: string,
    providers: P[],
    operation: string,
    fn: (provider: P) => Promise<T>,
): Promise<T> => {
    let lastError: unknown = new Error(`No ${operation} providers configured for ${chain}`);

    for (const provider of providers) {
        try {
            return await fn(provider);
        } catch (err) {
            if (err instanceof TerminalProviderError) {
                throw err;
            }

            lastError = err;
            const detail = axios.isAxiosError(err)
                ? `status=${err.response?.status || "none"} code=${err.code || "none"}`
                : err instanceof Error
                    ? err.message
                    : String(err);
            console.warn(`${chain} ${operation} failed on ${provider.id} (${detail}), trying next provider`);
        }
    }

    throw lastError;
};

const logUpstreamError = (context: string, err: unknown) => {
    if (axios.isAxiosError(err)) {
        console.error(context, {
            status: err.response?.status || "none",
            code: err.code || "none",
            message: err.message,
        });
        return;
    }

    console.error(context, err);
};

// Shared JSON-RPC POST used by every EVM/SOL balance and proxy call.
const jsonRpcPost = async (
    provider: RpcProvider,
    method: string,
    params: unknown[],
    id: string,
    errorLabel: string,
): Promise<any> => {
    const response = await axios.post(
        provider.url,
        { jsonrpc: "2.0", id, method, params },
        { headers: { ...JSON_RPC_HEADERS, ...provider.headers }, timeout: PROVIDER_TIMEOUT_MS },
    );
    const data = response.data;

    if (data?.error) {
        throw new Error(data.error?.message || errorLabel);
    }

    return data;
};

// Short-lived per-address balance cache (via the shared node-cache) plus an
// in-flight dedup map so concurrent requests for one address make one upstream call.
const BALANCE_CACHE_TTL_SECONDS = 15;
const inFlightBalance = new Map<string, Promise<ChainBalanceResponse>>();

const balanceCacheKey = (chain: string, address: string) => `chain-balance:${chain}:${address}`;

const parseHexBalance = (value: unknown): string => {
    if (typeof value !== "string") {
        throw new Error("Invalid hexadecimal balance response");
    }

    const normalized = value.startsWith("0x") || value.startsWith("0X") ? value.slice(2) : value;

    if (normalized === "") {
        return "0";
    }

    if (!/^[a-fA-F0-9]+$/.test(normalized)) {
        throw new Error("Invalid hexadecimal balance response");
    }

    return BigInt(`0x${normalized}`).toString();
};

const findJsonObjectEnd = (text: string, start: number): number => {
    let depth = 0;
    let inString = false;
    let escaped = false;

    for (let index = start; index < text.length; index += 1) {
        const char = text[index];

        if (inString) {
            if (escaped) {
                escaped = false;
            } else if (char === "\\") {
                escaped = true;
            } else if (char === "\"") {
                inString = false;
            }
            continue;
        }

        if (char === "\"") {
            inString = true;
        } else if (char === "{") {
            depth += 1;
        } else if (char === "}") {
            depth -= 1;
            if (depth === 0) {
                return index;
            }
        }
    }

    return -1;
};

const extractTopLevelNumericProperty = (text: string, property: string): string | null => {
    let depth = 0;
    let inString = false;
    let escaped = false;
    const keyLiteral = `"${property}"`;

    for (let index = 0; index < text.length; index += 1) {
        const char = text[index];

        if (inString) {
            if (escaped) {
                escaped = false;
            } else if (char === "\\") {
                escaped = true;
            } else if (char === "\"") {
                inString = false;
            }
            continue;
        }

        if (char === "\"") {
            if (depth === 0 && text.slice(index, index + keyLiteral.length) === keyLiteral) {
                let cursor = index + keyLiteral.length;
                while (/\s/.test(text[cursor] || "")) cursor += 1;
                if (text[cursor] !== ":") {
                    inString = true;
                    continue;
                }
                cursor += 1;
                while (/\s/.test(text[cursor] || "")) cursor += 1;

                const numericMatch = text.slice(cursor).match(/^\d+/);
                return numericMatch ? numericMatch[0] : null;
            }

            inString = true;
        } else if (char === "{" || char === "[") {
            depth += 1;
        } else if (char === "}" || char === "]") {
            depth -= 1;
        }
    }

    return null;
};

const extractSolanaLamports = (rawText: string, parsed: any): string => {
    if (typeof parsed?.result?.value !== "number") {
        throw new Error("Invalid Solana balance response");
    }

    const resultMatch = rawText.match(/"result"\s*:/);
    if (resultMatch && resultMatch.index !== undefined) {
        const resultStart = resultMatch.index + resultMatch[0].length;
        const objectStart = rawText.indexOf("{", resultStart);
        if (objectStart !== -1) {
            const objectEnd = findJsonObjectEnd(rawText, objectStart);
            if (objectEnd !== -1) {
                const resultObject = rawText.slice(objectStart + 1, objectEnd);
                const rawLamports = extractTopLevelNumericProperty(resultObject, "value");
                if (rawLamports) {
                    return rawLamports;
                }
            }
        }
    }

    return Math.trunc(parsed.result.value).toString();
};

const fetchEvmBalance = async (
    chain: string,
    provider: RpcProvider,
    address: string,
): Promise<ChainBalanceResponse> => {
    const data = await jsonRpcPost(provider, "eth_getBalance", [address, "latest"], "balance", "EVM balance request failed");
    const balance = parseHexBalance(data?.result);

    return {
        chain,
        balance,
        unit: "wei",
        raw: data,
        nodeId: provider.id,
        provider: provider.id,
    };
};

const fetchSolanaBalance = async (provider: RpcProvider, address: string): Promise<ChainBalanceResponse> => {
    // Keep the raw response text: lamports is a u64 and JSON.parse would cap a
    // balance above 2^53 as a lossy double, so read the integer from the text.
    const response = await axios.post(
        provider.url,
        { jsonrpc: "2.0", id: "balance", method: "getBalance", params: [address, { commitment: "finalized" }] },
        {
            headers: { ...JSON_RPC_HEADERS, ...provider.headers },
            timeout: PROVIDER_TIMEOUT_MS,
            transformResponse: (raw) => raw,
        },
    );

    const rawText = typeof response.data === "string" ? response.data : "";

    let parsed: any;
    try {
        parsed = JSON.parse(rawText);
    } catch (err) {
        throw new Error("Invalid Solana balance response");
    }

    if (parsed?.error) {
        throw new Error(parsed.error?.message || "Solana balance request failed");
    }

    const balance = extractSolanaLamports(rawText, parsed);

    return {
        chain: "sol",
        balance,
        unit: "lamports",
        raw: parsed,
        nodeId: provider.id,
        provider: provider.id,
    };
};

// Address validators double as a hard allowlist: every accepted address is a
// safe URL path segment (no "/", ".", "%"), which blocks path-traversal into
// other provider endpoints via the balance route.
const EVM_ADDRESS_REGEX = /^0x[a-fA-F0-9]{40}$/;
const BTC_ADDRESS_REGEX = /^(bc1[a-z0-9]{6,87}|[13][a-km-zA-HJ-NP-Z1-9]{25,62})$/;
const SOL_ADDRESS_REGEX = /^[1-9A-HJ-NP-Za-km-z]{32,44}$/;

interface ChainHandler {
    validateAddress?: (address: string) => boolean;
    fetchBalance: (address: string) => Promise<ChainBalanceResponse>;
}

const CHAIN_HANDLERS: Record<string, ChainHandler> = {
    btc: {
        validateAddress: (address) => BTC_ADDRESS_REGEX.test(address),
        fetchBalance: (address) =>
            tryProviders("btc", BTC_ESPLORA_POOL, "balance", (provider) => fetchEsploraBalance(provider, address)),
    },
    eth: {
        validateAddress: (address) => EVM_ADDRESS_REGEX.test(address),
        fetchBalance: (address) =>
            tryProviders("eth", ETH_RPC_POOL, "balance", (provider) => fetchEvmBalance("eth", provider, address)),
    },
    bnb: {
        validateAddress: (address) => EVM_ADDRESS_REGEX.test(address),
        fetchBalance: (address) =>
            tryProviders("bnb", BNB_RPC_POOL, "balance", (provider) => fetchEvmBalance("bnb", provider, address)),
    },
    sol: {
        validateAddress: (address) => SOL_ADDRESS_REGEX.test(address),
        fetchBalance: (address) =>
            tryProviders("sol", SOL_RPC_POOL, "balance", (provider) => fetchSolanaBalance(provider, address)),
    },
};

const RPC_POOL_BY_CHAIN: Record<string, RpcProvider[]> = {
    sol: SOL_RPC_POOL,
    eth: ETH_RPC_POOL,
    bnb: BNB_RPC_POOL,
};

/**
 * Fetches balance for a given chain and address directly (without HTTP overhead)
 * This is the core logic extracted from the balance endpoint for reuse
 */
export const fetchChainBalance = async (chain: string, address: string): Promise<ChainBalanceResponse> => {
    if (!chain || !address) {
        throw new Error("Missing chain or address");
    }

    if (!CHAIN_PARAM_REGEX.test(chain)) {
        throw new Error("Invalid chain parameter");
    }

    const normalizedChain = chain.toLowerCase();
    const handler = CHAIN_HANDLERS[normalizedChain];

    if (!handler) {
        throw new TerminalProviderError("Unsupported chain", 400);
    }

    if (handler.validateAddress && !handler.validateAddress(address)) {
        throw new TerminalProviderError("Invalid address format", 400);
    }

    const cacheKey = balanceCacheKey(normalizedChain, address);

    const cached = cache.get<ChainBalanceResponse>(cacheKey);
    if (cached) {
        return cached;
    }

    const existing = inFlightBalance.get(cacheKey);
    if (existing) {
        return existing;
    }

    const pending = handler
        .fetchBalance(address)
        .then((response) => {
            cache.set(cacheKey, response, BALANCE_CACHE_TTL_SECONDS);
            return response;
        })
        .finally(() => {
            inFlightBalance.delete(cacheKey);
        });

    inFlightBalance.set(cacheKey, pending);
    return pending;
};

export const balance = async (req: express.Request, res: express.Response) => {
    const { chain, address } = req.params;

    if (!chain || !address) {
        res.status(400).send("Missing chain or address");
        return;
    }

    // Older clients retry with ?provider=... (e.g. chainz); the parameter is
    // accepted and ignored — provider selection is handled by the pool.

    try {
        const balanceResponse = await fetchChainBalance(chain, address);

        res.setHeader("x-provider", balanceResponse.provider);
        if (balanceResponse.rateLimitRemaining !== undefined) {
            res.setHeader("x-provider-ratelimit-remaining", String(balanceResponse.rateLimitRemaining));
        }

        res.status(200).json(balanceResponse);
    } catch (err) {
        if (err instanceof TerminalProviderError) {
            res.status(err.status).json({ error: err.message });
            return;
        }

        logUpstreamError("balance(): error while fetching chain balance", err);

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

/**
 * Allowed JSON-RPC methods per chain for the RPC proxy.
 * Only read-only / transaction-building methods are allowed; signing and
 * broadcasting happen client-side (MetaMask submits the transaction directly).
 */
const ALLOWED_RPC_METHODS: Record<string, Set<string>> = {
    sol: new Set([
        "getLatestBlockhash",
        "getBalance",
        "getBlockHeight",
        "getFeeForMessage",
        "getMinimumBalanceForRentExemption",
        "getRecentPrioritizationFees",
    ]),
    eth: new Set([
        "eth_chainId",
        "eth_gasPrice",
        "eth_estimateGas",
        "eth_getBalance",
        "eth_blockNumber",
        "eth_getTransactionCount",
    ]),
    bnb: new Set([
        "eth_chainId",
        "eth_gasPrice",
        "eth_estimateGas",
        "eth_getBalance",
        "eth_blockNumber",
        "eth_getTransactionCount",
    ]),
};

/**
 * Generic JSON-RPC proxy for supported chains.
 * Forwards allowed read-only RPC calls to the configured public RPC providers.
 *
 * POST /private-api/rpc/:chain
 * Body: standard JSON-RPC payload { jsonrpc, id, method, params }
 */
export const chainRpc = async (req: express.Request, res: express.Response) => {
    const { chain } = req.params;

    if (!chain || !CHAIN_PARAM_REGEX.test(chain)) {
        res.status(400).json({ error: "Invalid chain parameter" });
        return;
    }

    const normalizedChain = chain.toLowerCase();
    const allowedMethods = ALLOWED_RPC_METHODS[normalizedChain];

    if (!allowedMethods) {
        res.status(400).json({ error: `RPC proxy not available for chain: ${chain}` });
        return;
    }

    const body = req.body;
    if (!body || typeof body !== "object" || !body.method) {
        res.status(400).json({ error: "Invalid JSON-RPC payload" });
        return;
    }

    if (!allowedMethods.has(body.method)) {
        res.status(403).json({ error: `Method not allowed: ${body.method}` });
        return;
    }

    const pool = RPC_POOL_BY_CHAIN[normalizedChain];
    if (!pool) {
        res.status(400).json({ error: "Unsupported chain" });
        return;
    }

    try {
        const result = await tryProviders(normalizedChain, pool, "rpc", async (provider) => {
            const response = await axios.post(provider.url, body, {
                headers: { ...JSON_RPC_HEADERS, ...provider.headers },
                timeout: PROVIDER_TIMEOUT_MS,
            });

            // A JSON-RPC error body arrives with HTTP 200; treat it as a provider
            // failure so tryProviders advances to the next pool member (e.g. a
            // rate-limited node returning {error:{code:-32005}}), but keep the
            // envelope so a genuine application error can still reach the client
            // if every provider returns one.
            if (response.data?.error) {
                const rpcErr: any = new Error(response.data.error?.message || "RPC error");
                rpcErr.jsonRpcBody = response.data;
                throw rpcErr;
            }

            return { providerId: provider.id, data: response.data };
        });

        res.setHeader("x-provider", result.providerId);
        res.status(200).json(result.data);
    } catch (err) {
        // Every provider returned a JSON-RPC error — pass the last envelope through verbatim.
        if (err && typeof err === "object" && "jsonRpcBody" in err) {
            res.status(200).json((err as any).jsonRpcBody);
            return;
        }

        logUpstreamError(`chainRpc(${normalizedChain}): error`, err);

        if (axios.isAxiosError(err) && err.response) {
            const { status, data } = err.response;
            res.status(status).json(data !== undefined ? data : { error: "RPC request failed" });
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

export const getSpotlight = async (req: express.Request, res: express.Response) => {
    const now = Date.now();
    // Bare ISO dates ("YYYY-MM-DD") parse as UTC midnight. `start` is inclusive from the
    // beginning of its day; `end` is inclusive through the END of its day, so an item with
    // end "2026-06-30" still shows throughout June 30 (UTC). A malformed date fails open
    // (the item stays visible) rather than silently hiding a live spotlight.
    const dayMs = 86_400_000;
    const isBareDate = (d: string) => /^\d{4}-\d{2}-\d{2}$/.test(d);
    const active = spotlights.filter((s: Spotlight) => {
        const startMs = s.start ? new Date(s.start).getTime() : NaN;
        const endMs = s.end ? new Date(s.end).getTime() : NaN;
        const endBoundary = s.end && isBareDate(s.end) ? endMs + dayMs - 1 : endMs;
        const startsOk = !s.start || Number.isNaN(startMs) || startMs <= now;
        const endsOk = !s.end || Number.isNaN(endMs) || endBoundary >= now;
        return startsOk && endsOk;
    });
    res.send(active);
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
    const params = new URLSearchParams();

    // container is optional: omit it for combined trending tags across all
    // containers (do not forward a literal "undefined").
    if (container && !Array.isArray(container)) params.set("container", String(container));
    if (hours && !Array.isArray(hours)) params.set("hours", String(hours));
    if (days && !Array.isArray(days)) params.set("days", String(days));

    const u = `waves/trending/tags?${params.toString()}`;
    pipe(apiRequest(u, "GET"), res);
};

export const wavesTrendingAuthors = async (req: express.Request, res: express.Response) => {
    const { container } = req.query;
    const u = `waves/trending/authors?container=${container}`;
    pipe(apiRequest(u, "GET"), res);
};

export const wavesFeed = async (req: express.Request, res: express.Response) => {
    const { limit, cursor, tag, following, author, observer, container } = req.query;
    const params = new URLSearchParams();

    // These are single-value; ignore array input (duplicate params) so a
    // String([...]) never forwards a comma-joined value to the backend.
    if (limit && !Array.isArray(limit)) params.set("limit", String(limit));
    if (cursor && !Array.isArray(cursor)) params.set("cursor", String(cursor));
    if (tag && !Array.isArray(tag)) params.set("tag", String(tag));
    if (following && !Array.isArray(following)) params.set("following", String(following));
    if (author && !Array.isArray(author)) params.set("author", String(author));
    if (observer && !Array.isArray(observer)) params.set("observer", String(observer));

    // container is optional and repeatable (omit for the full combined feed).
    if (Array.isArray(container)) {
        (container as unknown[]).forEach((c) => params.append("container", String(c)));
    } else if (container) {
        params.append("container", String(container));
    }

    const u = `waves/feed?${params.toString()}`;
    pipe(apiRequest(u, "GET"), res);
};

export const wavesShorts = async (req: express.Request, res: express.Response) => {
    const { limit, cursor, tag, author, observer, container } = req.query;
    const params = new URLSearchParams();

    // Single-value params; ignore array (duplicate) input as wavesFeed does.
    if (limit && !Array.isArray(limit)) params.set("limit", String(limit));
    if (cursor && !Array.isArray(cursor)) params.set("cursor", String(cursor));
    if (tag && !Array.isArray(tag)) params.set("tag", String(tag));
    if (author && !Array.isArray(author)) params.set("author", String(author));
    if (observer && !Array.isArray(observer)) params.set("observer", String(observer));

    // container is optional and repeatable (omit for the full combined shorts feed).
    if (Array.isArray(container)) {
        (container as unknown[]).forEach((c) => params.append("container", String(c)));
    } else if (container) {
        params.append("container", String(container));
    }

    const u = `waves/shorts?${params.toString()}`;
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

export const quests = async (req: express.Request, res: express.Response) => {
    const { username } = req.body;
    pipe(apiRequest(`users/${username}/quests`, "GET"), res);
};

// Resolve the authenticated username from the signed token, or send 401 and return
// undefined. Used by the spend endpoints, which must never trust a body-supplied user.
const requireAuthedUsername = async (
    req: express.Request,
    res: express.Response
): Promise<string | undefined> => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return undefined;
    }
    return username;
};

// Streak Freeze buys/spends Points, so the username is taken from the authenticated
// token (validateCode), never from the request body.
export const streakFreeze = async (req: express.Request, res: express.Response) => {
    const username = await requireAuthedUsername(req, res);
    if (!username) {
        return;
    }
    pipe(apiRequest(`users/${username}/streak-freeze`, "GET"), res);
};

export const streakFreezeBuy = async (req: express.Request, res: express.Response) => {
    const username = await requireAuthedUsername(req, res);
    if (!username) {
        return;
    }
    // Require the idempotency key: a spend must never reach ePoints without it (a
    // missing key drops from the payload and removes double-charge protection on retry).
    const { idempotency_key } = req.body as { idempotency_key?: unknown };
    if (typeof idempotency_key !== "string" || !idempotency_key.trim()) {
        res.status(400).send("idempotency_key is required");
        return;
    }
    pipe(apiRequest(`users/${username}/streak-freeze`, "POST", {}, { idempotency_key }), res);
};

/**
 * Resolve the originating client IP for the account-create endpoints from the
 * proxy-set X-Real-IP header. X-Forwarded-For is not used here because it can
 * carry client-supplied values; there is no X-Forwarded-For fallback. Returns ''
 * when the header is absent.
 */
export const signupClientIp = (req: express.Request): string => {
    const realIp = req.headers['x-real-ip'];
    return (Array.isArray(realIp) ? realIp[0] : realIp) || '';
};

/**
 * Server-side Cloudflare Turnstile verification -- the single captcha verifier for the
 * account-create and paid-account-create routes. Returns true ONLY on a confirmed-human
 * token; fails CLOSED on a missing secret/token, provider error, or rejection (a network
 * blip must never open the gate). The secret is server-side only (config.turnstileSecret).
 */
const verifyTurnstile = async (token: unknown, ip: string): Promise<boolean> => {
    const secret = config.turnstileSecret;
    if (!secret || typeof token !== "string" || !token.trim()) {
        return false;
    }
    try {
        const form = new URLSearchParams();
        form.append("secret", secret);
        form.append("response", token.trim());
        if (ip) form.append("remoteip", ip);
        const r = await axios.post(
            "https://challenges.cloudflare.com/turnstile/v0/siteverify",
            form, { timeout: 8000, headers: { "Content-Type": "application/x-www-form-urlencoded" } });
        return r?.data?.success === true;
    } catch (err) {
        console.warn("verifyTurnstile: siteverify error", (err as Error)?.message);
        return false;
    }
};

/**
 * Turnstile gate for account creation. Returns true when the request must be REJECTED.
 * Enforced for every request by default (config.captchaMode "hard"); an operator can set
 * "off" as an emergency break-glass, since verification otherwise fails closed.
 */
const accountCaptchaRejected = async (token: unknown, ip: string): Promise<boolean> => {
    if (config.captchaMode === "off") {
        return false;
    }
    return !(await verifyTurnstile(token, ip));
};

export const createAccount = async (req: express.Request, res: express.Response) => {
    const { username, email, referral, captcha_token } = req.body;
    const ip = signupClientIp(req);

    // Single server-side Turnstile gate, enforced by default. vapi is the sole verifier, so the
    // (single-use) token is consumed here and never forwarded downstream.
    if (await accountCaptchaRejected(captcha_token, ip)) {
        res.status(406).json({ code: 113, message: "Please complete the verification and try again." });
        return;
    }

    const headers = { 'X-Real-IP-V': ip };
    const payload = { username, email, referral };

    // On-chain account creation/broadcast can take longer than the default.
    pipe(apiRequest(`signup/account-create`, "POST", headers, payload, {}, 30000), res);
};

export const createAccountFriend = async (req: express.Request, res: express.Response) => {
    const { username, email, friend } = req.body;

    const headers = { 'X-Real-IP-V': signupClientIp(req) };
    const payload = { username, email, friend };

    // On-chain account creation/broadcast can take longer than the default.
    pipe(apiRequest(`signup/account-create-friend`, "POST", headers, payload, {}, 30000), res);
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

    pipe(apiRequest(`images/${username}`, "GET", {}, {}, req.query), res);
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
    pipe(apiRequest(`drafts/${username}`, "GET", {}, {}, req.query), res);
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
    pipe(apiRequest(`bookmarks/${username}`, "GET", {}, {}, req.query), res);
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

/**
 * Parse and validate the support-settings payload. Both fields must be integers
 * within 0..100 (booleans, strings and floats are rejected). Returns the parsed
 * pair, or null when the payload is invalid. Exported for unit tests.
 */
export const parseSupportSettingsPayload = (
    body: unknown
): { beneficiary_percent: number; curation_percent: number } | null => {
    const { beneficiary_percent, curation_percent } = (body || {}) as {
        beneficiary_percent?: unknown;
        curation_percent?: unknown;
    };
    const isPercent = (v: unknown): v is number =>
        typeof v === "number" && Number.isInteger(v) && v >= 0 && v <= 100;
    if (!isPercent(beneficiary_percent) || !isPercent(curation_percent)) {
        return null;
    }
    return { beneficiary_percent, curation_percent };
};

// Support settings are per-user opt-ins, so the username is taken from the
// authenticated token (validateCode), never from the request body.
export const supportSettings = async (req: express.Request, res: express.Response) => {
    const username = await requireAuthedUsername(req, res);
    if (!username) {
        return;
    }
    pipe(apiRequest(`support-settings/${username}`, "GET"), res);
};

export const supportSettingsUpdate = async (req: express.Request, res: express.Response) => {
    const username = await requireAuthedUsername(req, res);
    if (!username) {
        return;
    }
    const parsed = parseSupportSettingsPayload(req.body);
    if (!parsed) {
        res.status(400).send(
            "beneficiary_percent and curation_percent must be integers between 0 and 100"
        );
        return;
    }
    const { beneficiary_percent, curation_percent } = parsed;
    pipe(
        apiRequest(`support-settings/${username}`, "PUT", {}, {
            username,
            beneficiary_percent,
            curation_percent
        }),
        res
    );
};

export const schedules = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`schedules/${username}`, "GET", {}, {}, req.query), res);
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
    pipe(apiRequest(`favorites/${username}`, "GET", {}, {}, req.query), res);
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
    pipe(apiRequest(`fragments/${username}`, "GET", {}, {}, req.query), res);
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

export const rcDelegationPrice = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`rc-delegation-price`, "GET"), res);
}

export const rcDelegationActive = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    // Self-only: always check the authenticated user's own active top-up.
    pipe(apiRequest(`rc-delegation-active/${username}`, "GET"), res);
}

export const stripeCreateIntent = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    // Fail closed at the route (not a whole-service startup crash): never forward a money
    // request with an empty/absent shared secret.
    const secret = config.stripeInternalSecret;
    if (!secret) {
        res.status(503).send("payments not configured");
        return;
    }
    // `user` is ALWAYS the authenticated caller (never client-supplied). sku/nonce/meta
    // are validated server-side by ePoints (amount + points come from its catalog, the
    // nonce is required for idempotency, the sku is points-only). The shared secret
    // authenticates this hop to ePoints; ePoints never trusts a client-sent price.
    // hosting_target is optional: on the hosting rail it activates a DIFFERENT tenant than the
    // buyer (e.g. a community hive-NNNNN whose owner pays); ePoints validates it and ignores it
    // on every non-hosting rail. The buyer (user) stays the authenticated caller.
    // gift_recipient / gift_message (Points gift rail): the buyer pays and the Points are credited
    // to gift_recipient instead of the buyer. Optional; ePoints validates + verifies the recipient
    // exists before charging and ignores these on non-Points skus.
    const { sku, nonce, meta, hosting_target, gift_recipient, gift_message } = req.body as {
        sku?: string; nonce?: string; meta?: object; hosting_target?: string;
        gift_recipient?: string; gift_message?: string;
    };
    // hosting_target is client-supplied and crosses into the trusted internal request. Reject a
    // malformed value at this boundary before forwarding (ePoints also validates it and only honours
    // it on the hosting rail from the trusted create-intent path). Absent -> the buyer's own blog.
    if (hosting_target !== undefined &&
        (typeof hosting_target !== "string" ||
         !/^(?=.{3,16}$)[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*$/.test(hosting_target))) {
        // typeof guard first: req.body is JSON, so a non-string (e.g. ["hive-1"]) would otherwise be
        // coerced by RegExp.test and forwarded as a non-string, defeating the boundary check.
        res.status(400).send("invalid hosting target");
        return;
    }
    // Same boundary guard for the gift recipient (typeof before regex); ePoints does the strict
    // shape + on-chain existence check before charging. Hive names are lowercase, so normalize
    // typed input (trim + lowercase) before validating so a recipient like "Alice" is accepted as
    // "alice" rather than rejected, and forward the canonical form.
    const giftRecipient =
        typeof gift_recipient === "string" ? gift_recipient.trim().toLowerCase() : gift_recipient;
    if (giftRecipient !== undefined &&
        (typeof giftRecipient !== "string" ||
         !/^(?=.{3,16}$)[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*$/.test(giftRecipient))) {
        res.status(400).send("invalid gift recipient");
        return;
    }
    if (gift_message !== undefined && (typeof gift_message !== "string" || gift_message.length > 200)) {
        res.status(400).send("invalid gift message");
        return;
    }
    const headers = { "X-Internal-Secret": secret };
    const payload = {
        user: username, sku, nonce, meta, hosting_target,
        gift_recipient: giftRecipient, gift_message,
    };
    pipe(apiRequest(`stripe/create-intent`, "POST", headers, payload, {}, 20000), res);
}

export const stripeOrderStatus = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const secret = config.stripeInternalSecret;
    if (!secret) {
        res.status(503).send("payments not configured");
        return;
    }
    const { payment_intent } = req.body as { payment_intent?: string };
    if (typeof payment_intent !== "string" || !payment_intent.trim()) {
        res.status(400).send("payment_intent required");
        return;
    }
    // Owner-scoped: ePoints filters by ?user, so a caller can only read its own order.
    const headers = { "X-Internal-Secret": secret };
    pipe(apiRequest(`stripe/order/${encodeURIComponent(payment_intent.trim())}?user=${encodeURIComponent(username)}`, "GET", headers, {}, {}, 20000), res);
}

export const stripeCreateAccountIntent = async (req: express.Request, res: express.Response) => {
    // ANONYMOUS paid-account purchase: NO validateCode (the buyer has no Hive account yet).
    // The captcha is the human gate (ALWAYS enforced for this paid flow, independent of
    // CAPTCHA_MODE). The backend re-validates the username/email/availability and computes
    // the amount server-side before charging; this hop only authenticates with the shared
    // secret and forwards the request.
    const secret = config.stripeInternalSecret;
    if (!secret) {
        res.status(503).send("payments not configured");
        return;
    }
    const ip = signupClientIp(req);
    const { sku, nonce, meta, captcha_token } = req.body as {
        sku?: string; nonce?: string;
        meta?: { username?: string; email?: string; referral?: string };
        captcha_token?: string;
    };
    // This route mints ONLY the accounts rail; never let it create a points order (which the
    // points route binds to an authenticated caller).
    if (typeof sku !== "string" || !sku.endsWith("accounts")) {
        res.status(400).send("invalid product");
        return;
    }
    // Validate cheap inputs BEFORE the single-use Turnstile check, so a fixable input error
    // (e.g. a missing username) never burns the user's one-time captcha token.
    const username = typeof meta?.username === "string" ? meta.username.trim().toLowerCase() : "";
    if (!username) {
        res.status(400).send("username required");
        return;
    }
    if (!(await verifyTurnstile(captcha_token, ip))) {
        res.status(406).json({ code: 113, message: "Please complete the verification and try again." });
        return;
    }
    // `user` carries the new account name as the order identity; the backend re-derives +
    // re-validates it. Forward a NORMALIZED meta.username so meta and `user` agree. Forward the
    // client IP for backend-side rate-limiting / audit.
    const headers = { "X-Internal-Secret": secret, "X-Real-IP-V": ip };
    const payload = { user: username, sku, nonce, meta: meta ? { ...meta, username } : meta };
    pipe(apiRequest(`stripe/create-intent`, "POST", headers, payload, {}, 20000), res);
}

export const stripeAccountStatus = async (req: express.Request, res: express.Response) => {
    // ANONYMOUS status poll: no Hive account yet, so no validateCode owner-binding. Scope by
    // the buyer-supplied username + payment_intent; the backend filters by ?user, so the order
    // resolves only when BOTH match what it created. Status is low-sensitivity.
    const secret = config.stripeInternalSecret;
    if (!secret) {
        res.status(503).send("payments not configured");
        return;
    }
    const { payment_intent, username } = req.body as { payment_intent?: string; username?: string };
    const pi = typeof payment_intent === "string" ? payment_intent.trim() : "";
    const user = typeof username === "string" ? username.trim().toLowerCase() : "";
    if (!pi || !user) {
        res.status(400).send("payment_intent and username required");
        return;
    }
    const headers = { "X-Internal-Secret": secret };
    pipe(apiRequest(`stripe/order/${encodeURIComponent(pi)}?user=${encodeURIComponent(user)}`, "GET", headers, {}, {}, 20000), res);
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
    const { currency } = req.query;
    const queryString = currency ? `?currency=${currency}` : '';
    pipe(apiRequest(`market-data/latest${queryString}`, "GET"), res);
};

export const report = async (req: express.Request, res: express.Response) => {
    const { type, author, permlink, reporter, notes } = req.body;

    if (!type || !author) {
        res.status(400).send("Missing required fields: type, author");
        return;
    }

    if (type !== 'post' && type !== 'account') {
        res.status(400).send("Invalid report type. Must be 'post' or 'account'");
        return;
    }

    if (type === 'post' && !permlink) {
        res.status(400).send("Missing required field: permlink for post reports");
        return;
    }

    const data: Record<string, string> = { type, author };
    if (type === 'post') {
        data.permlink = permlink;
    }
    data.reporter = reporter || 'anonymous';
    if (notes) {
        data.notes = notes;
    }

    pipe(apiRequest("report", "POST", {}, data), res);
};

export const requestDelete = async (req: express.Request, res: express.Response) => {
    res.send({
        status: 200,
        body: {
            status: 'ok'
        }
    });
};

export const tips = async (req: express.Request, res: express.Response) => {
    const { author, permlink } = req.body;
    pipe(apiRequest(`post-tips/${author}/${permlink}`, "GET"), res);
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
    // External payment/receipt validation can be slow.
    pipe(apiRequest(`purchase-order`, "POST", {}, data, {}, 30000), res);
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

export const aiGeneratePrice = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`ai-image-price`, "GET"), res);
}

export const aiGenerateImage = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { prompt, aspect_ratio, power } = req.body;
    const data = { us: username, prompt, aspect_ratio, power };
    // AI image generation legitimately takes 10-60s+; keep it long.
    pipe(apiRequest(`ai-image-generate`, "POST", {}, data, {}, 120000), res);
}

export const aiAssistPrice = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    pipe(apiRequest(`ai-assist-price?us=${username}`, "GET"), res);
}

export const aiAssist = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req);
    if (!username) {
        res.status(401).send("Unauthorized");
        return;
    }
    const { action, text } = req.body;
    const data = { us: username, action, text };
    // AI assist generation can take a long time; keep it long.
    pipe(apiRequest(`ai-assist`, "POST", {}, data, {}, 120000), res);
}
