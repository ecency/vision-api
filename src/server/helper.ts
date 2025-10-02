import axios, {AxiosResponse, Method} from "axios";

import {cache} from "./cache";

import config from "../config";

import {baseApiRequest} from "./util";

export type BalanceProvider = "chainstack" | "bitquery";

export interface ChainBalanceResponse {
    chain: string;
    balance: string | null;
    unit: string;
    raw: unknown;
    nodeId?: string;
    provider: BalanceProvider;
}

const BITQUERY_ENDPOINT = "https://streaming.bitquery.io/graphql";

interface BitqueryResponse {
    data?: unknown;
    errors?: { message?: string }[];
}

interface BitqueryChainConfig {
    query: string;
    datasetKey: string;
    balancePath: string[];
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

const BITQUERY_CHAIN_CONFIG: Record<string, BitqueryChainConfig> = {
    eth: {
        query: `query ($address: String!) {
    ethereum(network: ethereum) {
        address(address: {is: $address}) {
            balance
        }
    }
}`,
        datasetKey: "ethereum",
        balancePath: ["address", "0", "balance"],
        decimals: 18,
        unit: "wei",
    },
    bnb: {
        query: `query ($address: String!) {
    ethereum(network: bsc) {
        address(address: {is: $address}) {
            balance
        }
    }
}`,
        datasetKey: "ethereum",
        balancePath: ["address", "0", "balance"],
        decimals: 18,
        unit: "wei",
    },
    sol: {
        query: `query ($address: String!) {
    solana(network: mainnet) {
        address(address: {is: $address}) {
            balance
        }
    }
}`,
        datasetKey: "solana",
        balancePath: ["address", "0", "balance"],
        decimals: 9,
        unit: "lamports",
    },
    tron: {
        query: `query ($address: String!) {
    tron(network: mainnet) {
        address(address: {is: $address}) {
            balance
        }
    }
}`,
        datasetKey: "tron",
        balancePath: ["address", "0", "balance"],
        decimals: 6,
        unit: "sun",
    },
    btc: {
        query: `query ($address: String!) {
    bitcoin(network: bitcoin) {
        address(address: {is: $address}) {
            balance
        }
    }
}`,
        datasetKey: "bitcoin",
        balancePath: ["address", "0", "balance"],
        decimals: 8,
        unit: "satoshi",
    },
    ton: {
        query: `query ($address: String!) {
    ton(network: mainnet) {
        address(address: {is: $address}) {
            balance
        }
    }
}`,
        datasetKey: "ton",
        balancePath: ["address", "0", "balance"],
        decimals: 9,
        unit: "nanotons",
    },
    apt: {
        query: `query ($address: String!) {
    aptos(network: mainnet) {
        account(address: {is: $address}) {
            balance
        }
    }
}`,
        datasetKey: "aptos",
        balancePath: ["account", "0", "balance"],
        decimals: 8,
        unit: "octas",
    },
};

const dig = (obj: any, path: string[]): unknown => {
    if (!obj) {
        return undefined;
    }

    let current: any = obj;

    for (const segment of path) {
        if (current === null || current === undefined) {
            return undefined;
        }

        if (Array.isArray(current)) {
            const index = Number(segment);

            if (Number.isNaN(index) || index < 0 || index >= current.length) {
                return undefined;
            }

            current = current[index];
        } else {
            current = current[segment];
        }
    }

    return current;
};

export const parseBalanceProvider = (value: unknown): BalanceProvider => {
    if (typeof value === "string" && value.trim().toLowerCase() === "bitquery") {
        return "bitquery";
    }

    return "chainstack";
};

export const fetchBitqueryBalance = async (
    chain: string,
    address: string,
): Promise<ChainBalanceResponse> => {
    const config = BITQUERY_CHAIN_CONFIG[chain];

    if (!config) {
        throw new Error("Requested chain is not supported by Bitquery provider");
    }

    const apiKey = process.env.BITQUERY_API_KEY?.trim();
    const accessToken = process.env.BITQUERY_ACCESS_TOKEN?.trim();

    if (!apiKey && !accessToken) {
        throw new Error("Bitquery API key/access token is not configured");
    }

    try {
        const headers: Record<string, string> = {
            "Content-Type": "application/json",
        };

        if (accessToken) {
            headers.Authorization = `Bearer ${accessToken}`;
        } else if (apiKey) {
            headers["X-API-KEY"] = apiKey;
        }

        const response = await axios.post<BitqueryResponse>(
            BITQUERY_ENDPOINT,
            {
                query: config.query,
                variables: { address },
            },
            { headers },
        );

        if (response.data?.errors?.length) {
            const [firstError] = response.data.errors;
            throw new Error(firstError?.message || "Bitquery query failed");
        }

        const dataset: any = (response.data?.data as Record<string, unknown>)?.[config.datasetKey];

        if (!dataset) {
            throw new Error("Bitquery response did not include expected dataset");
        }

        const rawBalance = dig(dataset, config.balancePath);

        const normalizedBalance = (() => {
            if (rawBalance === null || rawBalance === undefined) {
                return null;
            }

            if (typeof rawBalance === "number" || typeof rawBalance === "string") {
                const rawString = rawBalance.toString();

                if (rawString.includes(".") || rawString.toLowerCase().includes("e")) {
                    return convertDecimalToIntegerString(rawString, config.decimals);
                }

                return stripLeadingZeros(rawString);
            }

            throw new Error("Bitquery returned balance in unexpected format");
        })();

        return {
            chain,
            balance: normalizedBalance,
            unit: config.unit,
            raw: response.data?.data,
            provider: "bitquery",
        };
    } catch (error) {
        if (axios.isAxiosError(error)) {
            const message = error.response?.data?.error?.message || error.response?.data?.message;

            if (message) {
                throw new Error(message);
            }
        }

        throw error;
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
