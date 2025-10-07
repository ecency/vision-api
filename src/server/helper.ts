import axios, {AxiosResponse, Method} from "axios";

import {cache} from "./cache";

import config from "../config";

import {baseApiRequest} from "./util";

export type BalanceProvider = "chainstack" | "chainz";

export interface ChainBalanceResponse {
    chain: string;
    balance: string | null;
    unit: string;
    raw: unknown;
    nodeId?: string;
    provider: BalanceProvider;
}

// Build per-coin endpoint: https://chainz.cryptoid.info/btc/api.dws
const chainzEndpointFor = (coin: string) => `https://chainz.cryptoid.info/${coin}/api.dws`;

interface ChainzChainConfig {
    coin: string;
    decimals: number;
    unit: string;
}

const stripLeadingZeros = (value: string): string => {
    const normalized = value.replace(/^0+/, "");
    return normalized === "" ? "0" : normalized;
};

const convertDecimalToIntegerString = (value: string, decimals: number): string => {
    if (!Number.isInteger(decimals) || decimals < 0) {
        throw new Error("Invalid decimal precision provided");
    }

    const trimmed = value.trim();

    if (trimmed === "") {
        throw new Error("Invalid decimal value");
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
        throw new Error("Invalid decimal value");
    }

    const [wholePartRaw, fractionalPartRaw = ""] = digits.split(".");

    if (!/^\d*$/.test(wholePartRaw) || !/^\d*$/.test(fractionalPartRaw)) {
        throw new Error("Invalid decimal value");
    }

    if (fractionalPartRaw.length > decimals) {
        const truncated = fractionalPartRaw.slice(decimals);

        if (/[^0]/.test(truncated)) {
            throw new Error("Decimal value exceeds supported precision");
        }
    }

    const paddedFractional = fractionalPartRaw.padEnd(decimals, "0").slice(0, decimals);
    const combined = `${wholePartRaw === "" ? "0" : wholePartRaw}${paddedFractional}`;
    const normalized = stripLeadingZeros(combined);

    if (sign === "-") {
        return `-${normalized}`;
    }

    return normalized;
};

const normalizeChainzBalance = (value: string): string => {
    const trimmed = value.trim();

    // Guard: HTML or not-a-number responses → throw a clearer error
    if (/[<][a-z!/]/i.test(trimmed)) {
        throw new Error("Chainz returned HTML (likely error or rate limit)");
    }

    if (trimmed === "") {
        throw new Error("Chainz returned balance in unexpected format");
    }

    if (/^[+-]?\d+(\.\d+)?$/.test(trimmed)) {
        return trimmed;
    }

    const scientificMatch = trimmed.match(/^([+-]?)(\d+(?:\.\d+)?)[eE]([+-]?\d+)$/);
    if (!scientificMatch) {
        throw new Error("Chainz returned balance in unexpected format");
    }

    const [, sign = "", significand, exponentRaw] = scientificMatch;
    const exponent = Number(exponentRaw);

    if (!Number.isFinite(exponent)) {
        throw new Error("Chainz returned balance in unexpected format");
    }

    const [wholePartRaw, fractionalPartRaw = ""] = significand.split(".");
    const digits = `${wholePartRaw}${fractionalPartRaw}`;
    const decimalIndex = wholePartRaw.length + exponent;

    if (decimalIndex >= digits.length) {
        const zeros = "0".repeat(decimalIndex - digits.length);
        return `${sign}${digits}${zeros}`;
    }

    if (decimalIndex <= 0) {
        const zeros = "0".repeat(Math.abs(decimalIndex));
        return `${sign}0.${zeros}${digits}`;
    }

    const whole = digits.slice(0, decimalIndex);
    const fraction = digits.slice(decimalIndex);

    if (fraction === "") {
        return `${sign}${whole}`;
    }

    return `${sign}${whole}.${fraction}`;
};

const CHAINZ_CHAIN_CONFIG: Record<string, ChainzChainConfig> = {
    btc: {
        coin: "btc",
        decimals: 8,
        unit: "satoshi",
    }
};

export const parseBalanceProvider = (value: unknown): BalanceProvider => {
    if (typeof value === "string" && value.trim().toLowerCase() === "chainz") {
        return "chainz";
    }

    return "chainstack";
};

export const fetchChainzBalance = async (
    chain: string,
    address: string,
): Promise<ChainBalanceResponse> => {
    const cfg = CHAINZ_CHAIN_CONFIG[chain];
    if (!cfg) throw new Error("Requested chain is not supported by Chainz provider");

    const endpoint = chainzEndpointFor(cfg.coin);
    const apiKey = process.env.CHAINZ_API_KEY?.trim();

    const attempt = async (queryName: "getbalance" | "addressbalance") => {
        const params = new URLSearchParams({ q: queryName, a: address });
        if (apiKey) params.set("key", apiKey);
        // You can uncomment this if needed:
        // params.set("format", "plain");

        const url = `${endpoint}?${params.toString()}`;
        const resp = await axios.get<string | number>(url, {
            timeout: 8000,
            headers: {
                "Accept": "text/plain, application/json;q=0.9, */*;q=0.8",
                "User-Agent": "EcencyBalanceBot/1.0 (+https://ecency.com)",
                "Cache-Control": "no-cache",
                "Pragma": "no-cache",
            },
            validateStatus: (s) => s >= 200 && s < 500,
        });

        const text = String(resp.data ?? "").trim();

        // bail if HTML or empty
        if (!text || /[<][a-z!/]/i.test(text)) {
            const status = resp.status;
            const first = text.slice(0, 120);
            throw new Error(`Chainz non-numeric response (${status}): ${first || "<empty>"}`);
        }

        // normalize number (supports scientific notation)
        const raw = normalizeChainzBalance(text);
        const normalized = convertDecimalToIntegerString(raw, cfg.decimals);

        return {
            chain,
            balance: normalized,
            unit: cfg.unit,
            raw: text,
            provider: "chainz" as const,
        };
    };

    try {
        return await attempt("getbalance");
    } catch (_) {
        // fall through to the second query name
    }
    return await attempt("addressbalance");
};

// Ensures the Chainstack auth_key is present as the first path segment.
// e.g. https://bitcoin-mainnet.core.chainstack.com -> https://bitcoin-mainnet.core.chainstack.com/<AUTH_KEY>
export const ensureAuthKeyInPath = (endpoint: string, authKey?: string): string => {
    if (!authKey) return endpoint;
    try {
        const url = new URL(endpoint);
        if (!url.hostname.endsWith(".chainstack.com")) return endpoint;
        const segs = url.pathname.split("/").filter(Boolean);
        if (segs[0] === authKey) return endpoint; // already there
        url.pathname = `/${[authKey, ...segs].join("/")}`;
        return url.toString();
    } catch {
        return endpoint;
    }
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

export const apiRequest = (endpoint: string, method: Method, extraHeaders: any = {}, payload: any = {}): Promise<AxiosResponse> | Promise<any> => {
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

    return baseApiRequest(url, method, headers, payload)
}

interface Entry{

}

export const fetchPromotedEntries = async (limit=200,short_content=0): Promise<Entry[]> => {
    // fetch list from api
    const list: { author: string, permlink: string, post_data?: Entry }[] = (await apiRequest(`promoted-posts?limit=${limit}&short_content=${short_content}`, 'GET')).data;

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
