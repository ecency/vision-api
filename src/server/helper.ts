import axios, {AxiosResponse, Method} from "axios";

import {cache} from "./cache";

import config from "../config";

import {baseApiRequest} from "./util";

import {EsploraProvider} from "./chain-providers";

export interface ChainBalanceResponse {
    chain: string;
    balance: string | null;
    unit: string;
    raw: unknown;
    nodeId?: string;
    provider: string;
    rateLimitRemaining?: number;
}

const BLOCKSTREAM_TOKEN_CACHE_KEY = "blockstream:access-token";
const BLOCKSTREAM_TOKEN_URL =
    "https://login.blockstream.com/realms/blockstream-public/protocol/openid-connect/token";

const toBigInt = (value: unknown): bigint => {
    if (typeof value === "bigint") return value;
    if (typeof value === "number" && Number.isFinite(value)) return BigInt(Math.trunc(value));
    if (typeof value === "string" && /^-?\d+$/.test(value.trim())) {
        try {
            return BigInt(value.trim());
        } catch (_) {
            return BigInt(0);
        }
    }
    return BigInt(0);
};

const parseRateLimitHeader = (headerValue: unknown): number | undefined => {
    if (headerValue === undefined || headerValue === null) return undefined;

    if (Array.isArray(headerValue)) {
        for (const value of headerValue) {
            const parsed = parseRateLimitHeader(value);
            if (parsed !== undefined) return parsed;
        }
        return undefined;
    }

    const text = String(headerValue).trim();
    if (!text) return undefined;

    const num = Number(text);
    return Number.isFinite(num) ? num : undefined;
};

const getBlockstreamAccessToken = async (): Promise<string> => {
    const cached = cache.get<string>(BLOCKSTREAM_TOKEN_CACHE_KEY);
    if (cached) return cached;

    const clientId = process.env.BLOCKSTREAM_CLIENT_ID?.trim();
    const clientSecret = process.env.BLOCKSTREAM_CLIENT_SECRET?.trim();

    if (!clientId || !clientSecret) {
        throw new Error("Blockstream credentials are not configured");
    }

    const params = new URLSearchParams({
        client_id: clientId,
        client_secret: clientSecret,
        grant_type: "client_credentials",
        scope: "openid",
    });

    const resp = await axios.post(BLOCKSTREAM_TOKEN_URL, params.toString(), {
        headers: {
            "Content-Type": "application/x-www-form-urlencoded",
        },
        timeout: 10000,
    });

    const { access_token: accessToken, expires_in: expiresIn } = resp.data ?? {};

    if (typeof accessToken !== "string" || !accessToken) {
        throw new Error("Blockstream token response missing access_token");
    }

    let ttl = 240; // default 4 minutes
    const expiresNumeric =
        typeof expiresIn === "number"
            ? expiresIn
            : typeof expiresIn === "string"
            ? Number(expiresIn)
            : undefined;
    if (typeof expiresNumeric === "number" && Number.isFinite(expiresNumeric)) {
        const adjusted = Math.max(30, Math.floor(expiresNumeric - 30));
        if (Number.isFinite(adjusted) && adjusted > 0) ttl = adjusted;
    }

    cache.set(BLOCKSTREAM_TOKEN_CACHE_KEY, accessToken, ttl);
    return accessToken;
};

const ESPLORA_USER_AGENT = "EcencyBalanceBot/1.0 (+https://ecency.com)";

const buildEsploraHeaders = async (provider: EsploraProvider): Promise<Record<string, string>> => {
    const headers: Record<string, string> = {
        "User-Agent": ESPLORA_USER_AGENT,
    };

    if (provider.bearerAuth) {
        headers["Authorization"] = `Bearer ${await getBlockstreamAccessToken()}`;
    }

    return headers;
};

export const fetchEsploraBalance = async (
    provider: EsploraProvider,
    address: string,
): Promise<ChainBalanceResponse> => {
    const attempt = async (allowAuthRetry: boolean): Promise<ChainBalanceResponse> => {
        const resp = await axios.get(`${provider.url}/address/${encodeURIComponent(address)}`, {
            headers: await buildEsploraHeaders(provider),
            timeout: 10000,
            validateStatus: (status) => status >= 200 && status < 500,
        });

        if (resp.status === 401 && provider.bearerAuth && allowAuthRetry) {
            cache.del(BLOCKSTREAM_TOKEN_CACHE_KEY);
            return attempt(false);
        }

        if (resp.status !== 200) {
            throw new Error(`Esplora API error from ${provider.id} (${resp.status})`);
        }

        const data = resp.data ?? {};
        const chainStats = data.chain_stats ?? {};
        const mempoolStats = data.mempool_stats ?? {};

        const confirmedFunded = toBigInt(chainStats.funded_txo_sum);
        const confirmedSpent = toBigInt(chainStats.spent_txo_sum);
        const mempoolFunded = toBigInt(mempoolStats.funded_txo_sum);
        const mempoolSpent = toBigInt(mempoolStats.spent_txo_sum);

        const confirmedBalance = confirmedFunded - confirmedSpent;
        const mempoolBalance = mempoolFunded - mempoolSpent;
        const total = confirmedBalance + mempoolBalance;

        const rateLimitRemaining =
            parseRateLimitHeader(resp.headers["x-ratelimit-remaining"]) ??
            parseRateLimitHeader(resp.headers["ratelimit-remaining"]);

        return {
            chain: "btc",
            balance: total.toString(),
            unit: "satoshi",
            raw: data,
            nodeId: provider.id,
            provider: provider.id,
            rateLimitRemaining,
        };
    };

    return attempt(true);
};


const makeApiAuth = () => {
    if (typeof config.privateApiAuth !== "string") {
        return null;
    }

    const encoded = config.privateApiAuth.trim();

    if (!encoded) {
        return null;
    }

    try {
        const buffer = Buffer.from(encoded, "base64");

        if (buffer.length === 0) {
            return null;
        }

        const decoded = buffer.toString("utf-8");
        const parsed = JSON.parse(decoded);

        if (!parsed || typeof parsed !== "object") {
            return null;
        }

        return parsed;
    } catch (e) {
        return null;
    }
}

export const apiRequest = (endpoint: string, method: Method, extraHeaders: any = {}, payload: any = {}, params: any = {}, timeout?: number): Promise<AxiosResponse> | Promise<any> => {
    const apiAuth = makeApiAuth();
    if (!apiAuth) {
        return new Promise((resolve, reject) => {
            console.error("Api auth couldn't be create!");
            reject("Api auth couldn't be create!");
        })
    }

    const url = `${config.privateApiAddr}/${endpoint}`;

    const headers = {
        "Content-Type": "application/json",
        ...apiAuth,
        ...extraHeaders
    }

    return baseApiRequest(url, method, headers, payload, params, timeout)
}

interface Entry{

}

export const fetchPromotedEntries = async (limit=200,short_content=0): Promise<Entry[]> => {
    // fetch list from api
    const list: { author: string, permlink: string, post_data?: Entry }[] = (await apiRequest(`promoted-posts?limit=${limit}&short_content=${short_content}`, 'GET')).data;

    // baseApiRequest treats any HTTP status as success, so when the upstream is
    // unhealthy `data` can be a non-array error body. Guard before sorting so we
    // degrade to no promotions instead of throwing "sort is not a function".
    if (!Array.isArray(list)) {
        return [];
    }

    // random sort & random pick 18 (6*3)
    const promoted = list.sort(() => Math.random() - 0.5).filter((x, i) => i < 18);

    return promoted.map(x => x.post_data).filter(x => x) as Entry[];
}

export const getPromotedEntries = async (limit: number, short_content: number): Promise<Entry[]> => {
    let promoted: Entry[] | undefined = cache.get(`promotedentries-${short_content}-${limit}`);

    if (promoted === undefined) {
        try {
            promoted = await fetchPromotedEntries(limit, short_content);
            if (promoted && promoted.length > 0) {
                cache.set(`promotedentries-${short_content}-${limit}`, promoted, 300);
            }
        } catch (e) {
            console.log('warn: failed to fetch promoted', e)
            promoted = [];
        }
    }

    return promoted.sort(() => Math.random() - 0.5);
}
