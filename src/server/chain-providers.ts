// Static pools of public endpoints per supported chain, ordered by preference.
// Each pool can be overridden without a code change via a comma-separated env
// var (ETH_RPC_URLS, BNB_RPC_URLS, SOL_RPC_URLS, BTC_ESPLORA_URLS).

export interface RpcProvider {
    id: string;
    url: string;
    // Extra request headers (e.g. an Authorization bearer for a keyed endpoint),
    // kept out of the URL so credentials never land in access logs or error traces.
    headers?: Record<string, string>;
}

export interface EsploraProvider {
    id: string;
    url: string;
    // Authenticate with the Blockstream enterprise OAuth token (BLOCKSTREAM_CLIENT_ID/SECRET)
    bearerAuth?: boolean;
}

const hostnameId = (url: string): string => {
    try {
        return new URL(url).hostname;
    } catch (err) {
        return url;
    }
};

const poolFromEnv = (envName: string): RpcProvider[] | null => {
    const raw = process.env[envName];

    if (!raw) {
        return null;
    }

    const providers = raw
        .split(",")
        .map((value) => value.trim())
        .filter(Boolean)
        .map((url) => ({ id: hostnameId(url), url }));

    return providers.length > 0 ? providers : null;
};

export const ETH_RPC_POOL: RpcProvider[] = poolFromEnv("ETH_RPC_URLS") || [
    { id: "publicnode", url: "https://ethereum-rpc.publicnode.com" },
    { id: "drpc", url: "https://eth.drpc.org" },
    { id: "mevblocker", url: "https://rpc.mevblocker.io" },
];

export const BNB_RPC_POOL: RpcProvider[] = poolFromEnv("BNB_RPC_URLS") || [
    { id: "publicnode", url: "https://bsc-rpc.publicnode.com" },
    { id: "bnbchain", url: "https://bsc-dataseed.bnbchain.org" },
    { id: "defibit", url: "https://bsc-dataseed1.defibit.io" },
];

const heliusKey = (process.env.HELIUS_API_KEY || "").trim();
const heliusRpcUrl = (apiKey: string): string =>
    `https://mainnet.helius-rpc.com/?api-key=${encodeURIComponent(apiKey)}`;

export const SOL_RPC_POOL: RpcProvider[] = poolFromEnv("SOL_RPC_URLS") || [
    { id: "publicnode", url: "https://solana-rpc.publicnode.com" },
    { id: "solana-foundation", url: "https://api.mainnet.solana.com" },
    // Helius RPC authenticates with the api-key query parameter.
    ...(heliusKey
        ? [{ id: "helius", url: heliusRpcUrl(heliusKey) }]
        : []),
];

const blockstreamCredsConfigured = Boolean(
    (process.env.BLOCKSTREAM_CLIENT_ID || "").trim() && (process.env.BLOCKSTREAM_CLIENT_SECRET || "").trim(),
);

// enterprise.blockstream.info requires the Blockstream OAuth bearer token; infer
// it so an env override can include the enterprise endpoint without silently
// running unauthenticated.
const BEARER_AUTH_HOSTS = new Set(["enterprise.blockstream.info"]);

const esploraProviderFromUrl = (url: string): EsploraProvider => {
    const provider: EsploraProvider = { id: hostnameId(url), url };
    if (BEARER_AUTH_HOSTS.has(hostnameId(url))) {
        provider.bearerAuth = true;
    }
    return provider;
};

export const BTC_ESPLORA_POOL: EsploraProvider[] = (() => {
    const rawEnv = process.env.BTC_ESPLORA_URLS;
    if (rawEnv) {
        const fromEnv = rawEnv
            .split(",")
            .map((value) => value.trim())
            .filter(Boolean)
            .map(esploraProviderFromUrl);
        if (fromEnv.length > 0) {
            return fromEnv;
        }
    }

    const pool: EsploraProvider[] = [{ id: "mempool", url: "https://mempool.space/api" }];

    if (blockstreamCredsConfigured) {
        pool.push({ id: "blockstream", url: "https://enterprise.blockstream.info/api", bearerAuth: true });
    }

    pool.push({ id: "blockstream-public", url: "https://blockstream.info/api" });

    return pool;
})();
