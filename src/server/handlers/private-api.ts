import express from "express";
import hs from "hivesigner";
import axios from "axios";
import { cryptoUtils, Signature, Client } from '@hiveio/dhive'

import { announcements } from "./announcements";
import { apiRequest, getPromotedEntries } from "../helper";

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
    const { code } = req.body;
    if (!code) return false;

    try {
        const decoded = JSON.parse(Buffer.from(code, 'base64').toString()) as DecodedToken;
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

interface NormalizedBalanceResponse {
    chain: string;
    balance: string | null;
    unit: string;
    raw: unknown;
    nodeId: string;
}

interface ChainHandler {
    validateAddress?: (address: string) => boolean;
    selectNode: (nodes: ChainstackNode[]) => ChainstackNode | null;
    fetchBalance: (node: ChainstackNode, address: string) => Promise<NormalizedBalanceResponse>;
}

let cachedNodes: ChainstackNode[] | null = null;
let nodesCacheExpiry = 0;

const buildNodeAxiosConfig = (node: ChainstackNode): AxiosNodeConfig => {
    const headers: Record<string, string> = {
        "Content-Type": "application/json",
    };

    if (node.details?.auth_key) {
        headers["x-api-key"] = node.details.auth_key;
    }

    const hasBasicAuth = node.details?.auth_username && node.details?.auth_password;
    const auth = hasBasicAuth
        ? {
            username: node.details!.auth_username as string,
            password: node.details!.auth_password as string,
        }
        : undefined;

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

const parseHexBalance = (value: unknown): string => {
    if (typeof value !== "string") {
        throw new Error("Invalid hexadecimal balance response");
    }

    try {
        const normalized = value.startsWith("0x") ? value : `0x${value}`;
        return BigInt(normalized).toString(10);
    } catch (err) {
        throw new Error("Failed to parse hexadecimal balance");
    }
};

const fetchEvmBalance = async (chain: string, node: ChainstackNode, address: string): Promise<NormalizedBalanceResponse> => {
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
    };
};

const fetchSolanaBalance = async (node: ChainstackNode, address: string): Promise<NormalizedBalanceResponse> => {
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
    };
};

const fetchTronBalance = async (node: ChainstackNode, address: string): Promise<NormalizedBalanceResponse> => {
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
    };
};

const fetchTonBalance = async (node: ChainstackNode, address: string): Promise<NormalizedBalanceResponse> => {
    const baseEndpoint = node.details?.toncenter_api_v3 || node.details?.toncenter_api_v2;

    if (!baseEndpoint) {
        throw new Error("TON node does not provide a Toncenter endpoint");
    }

    const sanitizedBase = baseEndpoint.endsWith("/") ? baseEndpoint.slice(0, -1) : baseEndpoint;
    const url = `${sanitizedBase}/getAddressBalance`;
    const headers: Record<string, string> = {};

    if (node.details?.auth_key) {
        headers["X-API-KEY"] = node.details.auth_key;
        headers["x-api-key"] = node.details.auth_key;
    }

    const response = await axios.get(url, {
        headers,
        params: {
            address,
            api_key: node.details?.auth_key,
        },
    });

    const data = response.data;

    const ok = typeof data?.ok === "boolean" ? data.ok : true;

    if (!ok) {
        throw new Error(data?.error || "TON balance request failed");
    }

    const balance = typeof data?.result === "string" ? data.result : null;

    return {
        chain: "ton",
        balance,
        unit: "nanotons",
        raw: data,
        nodeId: node.id,
    };
};

const fetchAptosBalance = async (node: ChainstackNode, address: string): Promise<NormalizedBalanceResponse> => {
    const endpoint = ensureHttpsEndpoint(node);
    const config = buildNodeAxiosConfig(node);
    const resourceType = "0x1::coin::CoinStore<0x1::aptos_coin::AptosCoin>";
    const sanitizedEndpoint = endpoint.endsWith("/") ? endpoint.slice(0, -1) : endpoint;
    const url = `${sanitizedEndpoint}/v1/accounts/${address}/resource/${encodeURIComponent(resourceType)}`;

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
        };
    } catch (err) {
        if (axios.isAxiosError(err) && err.response?.status === 404) {
            return {
                chain: "apt",
                balance: "0",
                unit: "octas",
                raw: err.response.data,
                nodeId: node.id,
            };
        }

        throw err;
    }
};

const fetchBitcoinBalance = async (node: ChainstackNode, address: string): Promise<NormalizedBalanceResponse> => {
    const endpoint = ensureHttpsEndpoint(node);
    const config = buildNodeAxiosConfig(node);

    const payload = {
        jsonrpc: "1.0",
        id: "balance",
        method: "scantxoutset",
        params: ["start", [`addr(${address})`]],
    };

    const response = await axios.post(endpoint, payload, config);
    const data = response.data;

    if (data?.error) {
        throw new Error(data.error?.message || "Bitcoin balance request failed");
    }

    const amount = data?.result?.total_amount;

    const balance = typeof amount === "number" ? amount.toString() : null;

    return {
        chain: "btc",
        balance,
        unit: "btc",
        raw: data,
        nodeId: node.id,
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
        validateAddress: (address) => /^0x[a-fA-F0-9]+$/.test(address),
        selectNode: (nodes) => nodes.find((node) => endpointIncludes(node, "aptos")) ?? null,
        fetchBalance: (node, address) => fetchAptosBalance(node, address),
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

    try {
        if (handler.validateAddress && !handler.validateAddress(address)) {
            res.status(400).send("Invalid address format");
            return;
        }

        const nodes = await fetchChainstackNodes();
        const node = handler.selectNode(nodes);

        if (!node) {
            console.error(`No Chainstack node available for ${normalizedChain}`);
            res.status(502).send("No Chainstack node available for requested chain");
            return;
        }

        const balanceResponse = await handler.fetchBalance(node, address);

        res.status(200).json(balanceResponse);
    } catch (err) {
        console.error("balance(): error while fetching chain balance", err);

        if (axios.isAxiosError(err) && err.response) {
            res.status(err.response.status).send(err.response.data);
            return;
        }

        if (err instanceof Error) {
            res.status(502).send(err.message);
            return;
        }

        res.status(502).send("Failed to fetch balance from Chainstack");
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
    const { container } = req.query;
    const u = `waves/trending/tags?container=${container}`;
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
