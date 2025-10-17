import express from "express";

import { baseApiRequest, pipe, parseToken } from "../util";
import { fetchGlobalProps, getAccount } from "./hive-explorer";
import { apiRequest, ChainBalanceResponse } from "../helper";

import { EngineContracts, EngineIds, EngineMetric, EngineRequestPayload, EngineTables, JSON_RPC, Methods, Token, TokenBalance, TokenStatus } from "../../models/hiveEngine.types";
import { convertEngineToken, convertRewardsStatus } from "../../models/converters";

type PortfolioLayer = "points" | "hive" | "chain" | "spk" | "engine";

interface PortfolioItem {
    name: string;
    symbol: string;
    layer: PortfolioLayer;
    balance: number;
    fiatPrice: number;
    address?: string;
    pendingRewards?: number;
    pendingRewardsFiat?: number;
    liquid?: number;
    liquidFiat?: number;
    savings?: number;
    savingsFiat?: number;
    staked?: number;
    stakedFiat?: number;
}

interface ExternalWalletMetadata {
    address: string;
    chain: string;
    symbol: string;
    name: string;
    decimals?: number;
}

interface ChainConfig {
    name: string;
    symbol: string;
    decimals: number;
    aliases?: string[];
}

const CHAIN_CONFIG: Record<string, ChainConfig> = {
    btc: { name: "Bitcoin", symbol: "BTC", decimals: 8, aliases: ["bitcoin"] },
    eth: { name: "Ethereum", symbol: "ETH", decimals: 18 },
    bnb: { name: "BNB Chain", symbol: "BNB", decimals: 18 },
    sol: { name: "Solana", symbol: "SOL", decimals: 9 },
    tron: { name: "Tron", symbol: "TRX", decimals: 6, aliases: ["trx"] },
    ton: { name: "TON", symbol: "TON", decimals: 9 },
    apt: { name: "Aptos", symbol: "APT", decimals: 8 },
};

const CHAIN_SYMBOL_LOOKUP = new Map<string, { key: string; config: ChainConfig }>();

Object.entries(CHAIN_CONFIG).forEach(([key, config]) => {
    const baseSymbol = config.symbol.toLowerCase();
    if (!CHAIN_SYMBOL_LOOKUP.has(baseSymbol)) {
        CHAIN_SYMBOL_LOOKUP.set(baseSymbol, { key, config });
    }

    CHAIN_SYMBOL_LOOKUP.set(key.toLowerCase(), { key, config });

    if (config.aliases && Array.isArray(config.aliases)) {
        config.aliases.forEach((alias) => {
            const normalized = alias.toLowerCase();
            if (!CHAIN_SYMBOL_LOOKUP.has(normalized)) {
                CHAIN_SYMBOL_LOOKUP.set(normalized, { key, config });
            }
        });
    }
});

const firstDefined = (...values: any[]) => {
    for (let i = 0; i < values.length; i += 1) {
        const value = values[i];
        if (value !== undefined && value !== null) {
            return value;
        }
    }
    return undefined;
};

const resolveChainConfig = (
    value: unknown,
    fallback?: unknown,
): { key: string; config: ChainConfig } | null => {
    const candidates = [value, fallback];

    for (const candidate of candidates) {
        if (candidate === undefined || candidate === null) {
            continue;
        }

        const normalized = String(candidate).trim().toLowerCase();

        if (!normalized) {
            continue;
        }

        const match = CHAIN_SYMBOL_LOOKUP.get(normalized);

        if (match) {
            return match;
        }
    }

    return null;
};

const parseMaybeNumber = (value: unknown): number | null => {
    if (typeof value === "number") {
        return Number.isFinite(value) ? value : null;
    }

    if (typeof value === "string") {
        const trimmed = value.trim();

        if (!trimmed) {
            return null;
        }

        const direct = Number(trimmed);
        if (!Number.isNaN(direct)) {
            return direct;
        }

        const match = trimmed.match(/-?\d+(?:\.\d+)?/);
        if (match) {
            const parsed = Number(match[0]);
            return Number.isNaN(parsed) ? null : parsed;
        }
    }

    return null;
};

const pickFirstNumericValue = (...candidates: unknown[]): number | null => {
    for (const candidate of candidates) {
        const parsed = parseMaybeNumber(candidate);
        if (parsed !== null) {
            return parsed;
        }
    }

    return null;
};

const convertBaseUnitsToAmount = (value: string, decimals: number): number => {
    if (!value) {
        return 0;
    }

    const normalized = value.trim();

    if (/^-?\d+$/.test(normalized)) {
        const negative = normalized.startsWith("-");
        const digits = negative ? normalized.slice(1) : normalized;
        const padded = digits.padStart(decimals + 1, "0");
        const integerPart = padded.slice(0, padded.length - decimals) || "0";
        const fractionalPart = padded.slice(padded.length - decimals).replace(/0+$/, "");
        const combined = `${negative ? "-" : ""}${integerPart}`;
        const formatted = fractionalPart ? `${combined}.${fractionalPart}` : combined;
        const parsed = Number(formatted);
        return Number.isNaN(parsed) ? 0 : parsed;
    }

    const numeric = Number(normalized);
    return Number.isNaN(numeric) ? 0 : numeric;
};

const convertChainBalanceToAmount = (
    balanceResponse: ChainBalanceResponse | undefined,
    decimals: number,
): number => {
    if (!balanceResponse) {
        return 0;
    }

    const { balance, unit } = balanceResponse;

    if (balance === null || typeof balance === "undefined") {
        return 0;
    }

    if (typeof balance === "number") {
        return balance;
    }

    if (typeof balance === "string") {
        const trimmed = balance.trim();

        if (!trimmed) {
            return 0;
        }

        if (unit === "btc") {
            const parsed = Number(trimmed);
            return Number.isNaN(parsed) ? 0 : parsed;
        }

        return convertBaseUnitsToAmount(trimmed, decimals);
    }

    return 0;
};

const normalizeCurrency = (currency?: string): string => {
    if (!currency || typeof currency !== "string") {
        return "usd";
    }

    return currency.trim().toLowerCase() || "usd";
};

const parseBoolean = (value: unknown): boolean | undefined => {
    if (value === undefined || value === null) {
        return undefined;
    }

    if (typeof value === "boolean") {
        return value;
    }

    if (typeof value === "number") {
        if (value === 1) {
            return true;
        }

        if (value === 0) {
            return false;
        }
    }

    if (typeof value === "string") {
        const normalized = value.trim().toLowerCase();

        if (!normalized) {
            return undefined;
        }

        if (["true", "1", "yes", "on"].includes(normalized)) {
            return true;
        }

        if (["false", "0", "no", "off"].includes(normalized)) {
            return false;
        }
    }

    return undefined;
};

const getTokenPrice = (marketData: any, token: string, currency: string): number => {
    if (!marketData || !token) {
        return 0;
    }

    const currencyKey = currency.toLowerCase();
    const tokenKey = token.toLowerCase();
    const upperToken = token.toUpperCase();

    const candidates: any[] = [];

    if (marketData && typeof marketData === "object") {
        if (tokenKey in marketData) {
            candidates.push((marketData as any)[tokenKey]);
        }

        if (upperToken in marketData && upperToken !== tokenKey) {
            candidates.push((marketData as any)[upperToken]);
        }

        if (token in marketData && token !== tokenKey && token !== upperToken) {
            candidates.push((marketData as any)[token]);
        }
    }

    for (let i = 0; i < candidates.length; i += 1) {
        const candidate = candidates[i];
        if (!candidate) {
            continue;
        }

        if (typeof candidate === "number") {
            return candidate;
        }

        if (candidate && typeof candidate === "object") {
            let direct: any;

            if (currencyKey in candidate) {
                direct = candidate[currencyKey];
            } else {
                const upperCurrency = currencyKey.toUpperCase();
                if (upperCurrency in candidate) {
                    direct = candidate[upperCurrency];
                }
            }

            if (typeof direct === "number") {
                return direct;
            }

            if (direct && typeof direct === "object") {
                const nestedKeys = ["price", "rate", "value"];
                for (let j = 0; j < nestedKeys.length; j += 1) {
                    const key = nestedKeys[j];
                    const val = (direct as any)[key];
                    if (typeof val === "number") {
                        return val;
                    }
                }
            }

            const candidateKeys = ["price", "rate", "value"];
            for (let k = 0; k < candidateKeys.length; k += 1) {
                const key = candidateKeys[k];
                const val = (candidate as any)[key];
                if (typeof val === "number") {
                    return val;
                }
            }
        }
    }

    return 0;
};

interface PortfolioItemOptions {
    address?: string;
    pendingRewards?: number;
    savings?: number;
    staked?: number;
}

const makePortfolioItem = (
    name: string,
    symbol: string,
    layer: PortfolioLayer,
    balance: number,
    fiatRate: number,
    options: PortfolioItemOptions = {},
): PortfolioItem => {
    const normalizedBalance = Number.isFinite(balance) ? balance : 0;
    const normalizedRate = Number.isFinite(fiatRate) ? fiatRate : 0;
    const hasSavings =
        typeof options.savings === "number" && Number.isFinite(options.savings);
    const savingsValue = hasSavings ? options.savings || 0 : 0;
    const hasStaked = typeof options.staked === "number" && Number.isFinite(options.staked);
    const stakedValue = hasStaked ? options.staked || 0 : 0;
    const totalBalance = normalizedBalance + (hasSavings ? savingsValue : 0) + (hasStaked ? stakedValue : 0);
    const hasPendingRewards =
        typeof options.pendingRewards === "number" && Number.isFinite(options.pendingRewards);
    const normalizedPendingRewards = hasPendingRewards ? options.pendingRewards || 0 : undefined;

    const item: PortfolioItem = {
        name,
        symbol,
        layer,
        balance: totalBalance,
        fiatPrice: totalBalance * normalizedRate,
        ...(options.address ? { address: options.address } : {}),
        ...(hasPendingRewards
            ? {
                  pendingRewards: normalizedPendingRewards || 0,
                  pendingRewardsFiat: (normalizedPendingRewards || 0) * normalizedRate,
              }
            : {}),
    };

    if (hasSavings || hasStaked) {
        item.liquid = normalizedBalance;
        item.liquidFiat = normalizedBalance * normalizedRate;
    }

    if (hasSavings) {
        item.savings = savingsValue;
        item.savingsFiat = savingsValue * normalizedRate;
    }

    if (hasStaked) {
        item.staked = stakedValue;
        item.stakedFiat = stakedValue * normalizedRate;
    }

    return item;
};

const vestsToHivePower = (vests: number, hivePerMVests: number): number => {
    if (!Number.isFinite(vests) || !Number.isFinite(hivePerMVests)) {
        return 0;
    }

    return (vests / 1e6) * hivePerMVests;
};

const extractExternalWallets = (
    accountData: any,
    options?: { onlyEnabled?: boolean },
): ExternalWalletMetadata[] => {
    if (!accountData) {
        return [];
    }

    const metadataString = accountData.posting_json_metadata;

    if (!metadataString || typeof metadataString !== "string") {
        return [];
    }

    let parsed: any;

    try {
        parsed = JSON.parse(metadataString);
    } catch (_) {
        return [];
    }

    if (!parsed || typeof parsed !== "object") {
        return [];
    }

    const profile = parsed.profile && typeof parsed.profile === "object" ? parsed.profile : parsed;
    const walletsRootCandidate = firstDefined(profile.wallets, profile.wallet, parsed.wallets);
    const walletsRoot = walletsRootCandidate !== undefined ? walletsRootCandidate : null;
    const tokensRoot = Array.isArray(profile.tokens)
        ? profile.tokens
        : Array.isArray(parsed.tokens)
            ? parsed.tokens
            : [];

    const results = new Map<string, ExternalWalletMetadata>();
    const onlyEnabled = Boolean(options && options.onlyEnabled);

    const addResult = (wallet: ExternalWalletMetadata) => {
        const key = `${wallet.chain}::${wallet.address}`.toLowerCase();
        if (!results.has(key)) {
            results.set(key, wallet);
        }
    };

    if (walletsRoot && typeof walletsRoot === "object") {
        const enabled =
            typeof walletsRoot.enabled === "boolean"
                ? walletsRoot.enabled
                : typeof profile.wallets_enabled === "boolean"
                    ? profile.wallets_enabled
                    : true;

        if (enabled) {
            const items = Array.isArray((walletsRoot as any).items)
                ? (walletsRoot as any).items
                : Array.isArray((walletsRoot as any).wallets)
                    ? (walletsRoot as any).wallets
                    : Array.isArray((walletsRoot as any).list)
                        ? (walletsRoot as any).list
                        : [];

            for (const rawItem of items) {
                if (!rawItem || typeof rawItem !== "object") {
                    continue;
                }

                const meta = rawItem.meta && typeof rawItem.meta === "object" ? rawItem.meta : null;

                if (onlyEnabled) {
                    const showCandidate = firstDefined(
                        typeof rawItem.show === "boolean" ? rawItem.show : undefined,
                        typeof rawItem.enabled === "boolean" ? rawItem.enabled : undefined,
                        meta && typeof meta.show === "boolean" ? meta.show : undefined,
                    );

                    if (showCandidate !== true) {
                        continue;
                    }
                }

                const addressCandidate = firstDefined(
                    rawItem.address,
                    rawItem.addr,
                    rawItem.wallet,
                    rawItem.value,
                    meta && (meta.address || meta.addr),
                );
                const chainCandidate = firstDefined(
                    rawItem.chain,
                    rawItem.network,
                    rawItem.type,
                    rawItem.token,
                    rawItem.symbol,
                    rawItem.name,
                );
                const symbolCandidate = firstDefined(rawItem.symbol, rawItem.token, rawItem.name);
                const resolved = resolveChainConfig(chainCandidate, symbolCandidate);

                if (!addressCandidate || !resolved) {
                    continue;
                }

                const { key: chain, config } = resolved;
                const decimalsSource = firstDefined(
                    rawItem.decimals,
                    rawItem.precision,
                    rawItem.scale,
                    rawItem.decimal,
                    meta && (meta.decimals || meta.precision || meta.scale || meta.decimal),
                );
                const symbolSource = firstDefined(rawItem.symbol, rawItem.token, config.symbol, chain);
                const nameSource = firstDefined(rawItem.name, config.name, symbolSource);
                const symbol = String(symbolSource !== undefined ? symbolSource : config.symbol).toUpperCase();
                const name = String(nameSource !== undefined ? nameSource : symbol);
                const decimals = parseMaybeNumber(decimalsSource);

                addResult({
                    address: String(addressCandidate),
                    chain,
                    symbol,
                    name,
                    ...(decimals !== null ? { decimals } : {}),
                });
            }
        }
    }

    if (Array.isArray(tokensRoot) && tokensRoot.length > 0) {
        for (const token of tokensRoot) {
            if (!token || typeof token !== "object") {
                continue;
            }

            const typeCandidate = firstDefined(token.type, token.layer);
            const type = typeof typeCandidate === "string" ? typeCandidate.trim().toLowerCase() : "";

            if (type !== "chain") {
                continue;
            }

            const meta = token.meta && typeof token.meta === "object" ? token.meta : null;

            if (onlyEnabled) {
                const showCandidate = meta && typeof meta.show === "boolean" ? meta.show : undefined;
                const fallbackShow = typeof token.show === "boolean" ? token.show : undefined;

                if (showCandidate !== true && fallbackShow !== true) {
                    continue;
                }
            }

            const addressCandidate = firstDefined(
                meta && (meta.address || meta.addr || meta.wallet || meta.value),
                token.address,
                token.wallet,
                token.value,
            );

            if (!addressCandidate) {
                continue;
            }

            const chainCandidate = firstDefined(token.chain, token.network, token.symbol, token.name, token.type);
            const symbolCandidate = firstDefined(token.symbol, meta && meta.symbol, chainCandidate);
            const resolved = resolveChainConfig(chainCandidate, symbolCandidate);

            if (!resolved) {
                continue;
            }

            const { key: chain, config } = resolved;
            const symbolSource = firstDefined(token.symbol, config.symbol, chainCandidate, chain);
            const nameSource = firstDefined(token.name, config.name, symbolSource);
            const decimalsSource = firstDefined(
                token.decimals,
                meta && (meta.decimals || meta.precision || meta.scale || meta.decimal),
            );
            const symbol = String(symbolSource !== undefined ? symbolSource : config.symbol).toUpperCase();
            const name = String(nameSource !== undefined ? nameSource : symbol);
            const decimals = parseMaybeNumber(decimalsSource);

            addResult({
                address: String(addressCandidate),
                chain,
                symbol,
                name,
                ...(decimals !== null ? { decimals } : {}),
            });
        }
    }

    return Array.from(results.values());
};

const buildPointsLayer = (pointsData: any, marketData: any, currency: string): PortfolioItem[] => {
    if (!pointsData) {
        return [];
    }

    const possible = [
        pointsData.points,
        pointsData.balance,
        pointsData.point_balance,
        pointsData.point,
        pointsData.total_points,
    ];

    let balance = 0;

    for (const candidate of possible) {
        const parsed = parseMaybeNumber(candidate);
        if (parsed !== null) {
            balance = parsed;
            break;
        }

        if (candidate && typeof candidate === "object") {
            const nestedSource = firstDefined(candidate.points, candidate.balance, candidate.available);
            const nested = parseMaybeNumber(nestedSource);
            if (nested !== null) {
                balance = nested;
                break;
            }
        }
    }

    if (!Number.isFinite(balance)) {
        balance = 0;
    }

    const pendingCandidates = [
        pointsData.pendingRewards,
        pointsData.pending_rewards,
        pointsData.pending,
        pointsData.pending_points,
        pointsData.pendingPoints,
        pointsData.pending_token,
        pointsData.pendingToken,
        pointsData.unclaimed,
        pointsData.unclaimed_points,
        pointsData.unclaimedPoints,
        pointsData.unclaimed_balance,
        pointsData.unclaimedBalance,
        pointsData.rewards,
        pointsData.claims,
    ];

    let pendingRewards: number | undefined;

    for (const candidate of pendingCandidates) {
        if (candidate === undefined || candidate === null) {
            continue;
        }

        if (typeof candidate === "object") {
            const nested = pickFirstNumericValue(
                (candidate as any).points,
                (candidate as any).balance,
                (candidate as any).amount,
                (candidate as any).value,
                (candidate as any).pending,
                (candidate as any).total,
            );

            if (nested !== null) {
                pendingRewards = nested;
                break;
            }

            continue;
        }

        const parsed = parseMaybeNumber(candidate);
        if (parsed !== null) {
            pendingRewards = parsed;
            break;
        }
    }

    if (pendingRewards !== undefined && !Number.isFinite(pendingRewards)) {
        pendingRewards = undefined;
    }

    const price = getTokenPrice(marketData, "points", currency);

    const options: PortfolioItemOptions = {};

    if (pendingRewards !== undefined) {
        options.pendingRewards = pendingRewards;
    }

    return [makePortfolioItem("Ecency Points", "POINTS", "points", balance, price, options)];
};

const buildHiveLayer = (
    accountData: any,
    globalProps: any,
    marketData: any,
    currency: string,
): PortfolioItem[] => {
    if (!accountData || !globalProps) {
        return [];
    }

    const hiveBalance = parseToken(accountData.balance || "0 HIVE");
    const hiveSavings = parseToken(accountData.savings_balance || "0 HIVE");
    const hbdBalance = parseToken(accountData.hbd_balance || "0 HBD");
    const hbdSavings = parseToken(accountData.savings_hbd_balance || "0 HBD");

    const totalVests = parseToken(accountData.vesting_shares || "0 VESTS");
    const delegatedVests = parseToken(accountData.delegated_vesting_shares || "0 VESTS");
    const receivedVests = parseToken(accountData.received_vesting_shares || "0 VESTS");

    const hivePerMVests = typeof globalProps.hivePerMVests === "number" ? globalProps.hivePerMVests : 0;
    const netVests = totalVests - delegatedVests + receivedVests;

    const hivePower = vestsToHivePower(netVests, hivePerMVests);

    const pendingHive = parseToken(accountData.reward_hive_balance || "0 HIVE");
    const pendingHbd = parseToken(accountData.reward_hbd_balance || "0 HBD");
    const pendingVests = parseToken(accountData.reward_vesting_balance || "0 VESTS");
    const pendingHivePower = vestsToHivePower(pendingVests, hivePerMVests);

    const hivePrice = getTokenPrice(marketData, "hive", currency);
    const hbdPrice = getTokenPrice(marketData, "hbd", currency);

    const hivePendingTotal = pendingHive + pendingHivePower;

    return [
        makePortfolioItem("Hive", "HIVE", "hive", hiveBalance, hivePrice, {
            savings: hiveSavings,
            staked: hivePower,
            pendingRewards: hivePendingTotal,
        }),
        makePortfolioItem("Hive Dollar", "HBD", "hive", hbdBalance, hbdPrice, {
            savings: hbdSavings,
            pendingRewards: pendingHbd,
        }),
    ];
};

const buildEngineLayer = (engineData: any, marketData: any, currency: string, hivePrice: number): PortfolioItem[] => {
    if (!Array.isArray(engineData) || engineData.length === 0) {
        return [];
    }

    const items: PortfolioItem[] = [];

    for (const token of engineData) {
        if (!token) {
            continue;
        }

        const balanceParsed = parseMaybeNumber(token.balance);
        const stakedParsed = parseMaybeNumber(token.stakedBalance);
        const balance = balanceParsed !== null ? balanceParsed : 0;
        const staked = stakedParsed !== null ? stakedParsed : 0;

        const tokenPrice = typeof token.tokenPrice === "number" ? token.tokenPrice : 0;
        const priceInHive = tokenPrice > 0 ? tokenPrice : 0;
        const fiatRate = hivePrice * priceInHive;

        const symbol = typeof token.symbol === "string" ? token.symbol : "";
        const name = typeof token.name === "string" && token.name ? token.name : symbol;

        const pendingRewards = typeof token.pendingRewards === "number" ? token.pendingRewards : undefined;

        const options: PortfolioItemOptions = {
            staked,
        };

        if (pendingRewards !== undefined) {
            options.pendingRewards = pendingRewards;
        }

        items.push(
            makePortfolioItem(
                name || symbol,
                symbol || name || "ENGINE",
                "engine",
                balance,
                fiatRate,
                options,
            ),
        );
    }

    return items;
};

const buildSpkLayer = (spkData: any, marketData: any, currency: string): PortfolioItem[] => {
    if (!spkData || typeof spkData !== "object") {
        return [];
    }

    const balanceSource =
        spkData.balance && typeof spkData.balance === "object"
            ? spkData.balance
            : spkData.balances && typeof spkData.balances === "object"
                ? spkData.balances
                : {};

    const readValue = (...keys: string[]): number | null => {
        const candidates: unknown[] = [];

        for (const key of keys) {
            if (balanceSource && typeof balanceSource === "object" && key in balanceSource) {
                candidates.push((balanceSource as any)[key]);
            }

            if (key in spkData) {
                candidates.push((spkData as any)[key]);
            }

            if (spkData.account && typeof spkData.account === "object" && key in spkData.account) {
                candidates.push((spkData.account as any)[key]);
            }

            if (spkData.power && typeof spkData.power === "object" && key in spkData.power) {
                candidates.push((spkData.power as any)[key]);
            }
        }

        return pickFirstNumericValue(...candidates);
    };

    const items: PortfolioItem[] = [];

    const spkBalance = readValue("spk", "SPK", "balance_spk", "liquid_spk");

    if (spkBalance !== null) {
        const spkPrice = getTokenPrice(marketData, "spk", currency);
        items.push(makePortfolioItem("SPK", "SPK", "spk", spkBalance, spkPrice));
    }

    const larynxBalance = readValue("larynx", "LARYNX");
    const larynxPower = readValue("larynx_power", "larynxPower", "LARYNX_POWER");

    if (larynxBalance !== null || larynxPower !== null) {
        const liquid = larynxBalance !== null ? larynxBalance : 0;
        const staked = larynxPower !== null ? larynxPower : 0;
        const larynxPrice = getTokenPrice(marketData, "larynx", currency);

        items.push(
            makePortfolioItem("LARYNX", "LARYNX", "spk", liquid, larynxPrice, {
                staked,
            }),
        );
    }

    return items;
};

const buildChainLayer = async (
    accountData: any,
    marketData: any,
    currency: string,
    options?: { onlyEnabled?: boolean },
): Promise<PortfolioItem[]> => {
    const wallets = extractExternalWallets(accountData, options);

    if (wallets.length === 0) {
        return [];
    }

    const items = await Promise.all(
        wallets.map(async (wallet) => {
            const chain = wallet.chain.toLowerCase();
            const config = CHAIN_CONFIG[chain];
            const decimals = wallet.decimals !== undefined ? wallet.decimals : config.decimals;

            try {
                const response = await apiRequest(
                    `balance/${chain}/${encodeURIComponent(wallet.address)}`,
                    "GET",
                );

                const data = response.data as ChainBalanceResponse | undefined;
                const balance = convertChainBalanceToAmount(data, decimals);
                const price = getTokenPrice(marketData, wallet.symbol, currency);

                return makePortfolioItem(
                    wallet.name || config.name,
                    wallet.symbol || config.symbol,
                    "chain",
                    balance,
                    price,
                    { address: wallet.address },
                );
            } catch (err) {
                console.warn("Failed to fetch external wallet balance", { chain, address: wallet.address, err });
                const price = getTokenPrice(marketData, wallet.symbol, currency);
                return makePortfolioItem(
                    wallet.name || config.name,
                    wallet.symbol || config.symbol,
                    "chain",
                    0,
                    price,
                    { address: wallet.address },
                );
            }
        }),
    );

    return items.filter(Boolean);
};


//docs: https://hive-engine.github.io/engine-docs/
//available nodes: https://beacon.peakd.com/ select tab 'Hive Engine'
const ENGINE_NODES = [
    "https://herpc.dtools.dev",
    "https://api.hive-engine.com/rpc",
    "https://ha.herpc.dtools.dev",
    "https://herpc.kanibot.com",
    "https://herpc.actifit.io",
  ];

// min and max included
const randomIntFromInterval = (min: number, max: number) => {
    return Math.floor(Math.random() * (max - min + 1) + min);
}

let BASE_ENGINE_URL = `${ENGINE_NODES[randomIntFromInterval(0,4)]}/contracts`;
const BASE_SPK_URL = 'https://spk.good-karma.xyz';

const ENGINE_REWARDS_URL = 'https://scot-api.hive-engine.com/';
const ENGINE_CHART_URL = 'https://info-api.tribaldex.com/market/ohlcv';

//docs: https://github.com/hive-engine/ssc_tokens_history/tree/hive#api-usage
const ENGINE_ACCOUNT_HISTORY_URL = 'https://history.hive-engine.com/accountHistory';

export const PATH_CONTRACTS = 'contracts';


//client engine api requests
export const eapi = async (req: express.Request, res: express.Response) => {
    const data = req.body;
    pipe(engineContractsRequest(data, BASE_ENGINE_URL), res);
}


export const erewardapi = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;
    const params = req.query;
    pipe(engineRewardsRequest(username, params), res);
}

export const echartapi = async (req: express.Request, res: express.Response) => {
    const params = req.query;

    const url = `${ENGINE_CHART_URL}`;
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency' };

    pipe(baseApiRequest(url, "GET", headers, undefined, params), res);
}

export const engineAccountHistory = (req: express.Request, res: express.Response) => {
    const params = req.query;

    const url = `${ENGINE_ACCOUNT_HISTORY_URL}`;
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency' };

    pipe(baseApiRequest(url, "GET", headers, undefined, params), res);
}


//raw engine api call
const engineContractsRequest = (data: EngineRequestPayload, url: string) => {
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency' };

    return baseApiRequest(url, "POST", headers, data)
}

//raw engine rewards api call
const engineRewardsRequest = (username:string, params:any) => {
    const url = `${ENGINE_REWARDS_URL}/@${username}`;
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency' };

    return baseApiRequest(url, "GET", headers, undefined, params)
}


//engine contracts methods
export const fetchEngineBalances = async (account: string): Promise<TokenBalance[]> => {
    const data: EngineRequestPayload = {
        jsonrpc: JSON_RPC.RPC_2,
        method: Methods.FIND,
        params: {
            contract: EngineContracts.TOKENS,
            table: EngineTables.BALANCES,
            query: {
                account,
            },
        },
        id: EngineIds.ONE,
    };
    try {
        const response = await engineContractsRequest(data, BASE_ENGINE_URL);

        if (!response.data?.result) {
            throw new Error("Failed to get engine balances")
        }

        return response.data.result;
    }
    catch (e) {
        BASE_ENGINE_URL = `${ENGINE_NODES[randomIntFromInterval(0,6)]}/contracts`;
        const response = await engineContractsRequest(data, BASE_ENGINE_URL);

        if (!response.data?.result) {
            throw new Error("Failed to get engine balances")
        }

        return response.data.result;
    }
};


export const fetchEngineTokens = async (tokens: string[]): Promise<Token[]> => {
    const data: EngineRequestPayload = {
        jsonrpc: JSON_RPC.RPC_2,
        method: Methods.FIND,
        params: {
            contract: EngineContracts.TOKENS,
            table: EngineTables.TOKENS,
            query: {
                symbol: { $in: tokens },
            },
        },
        id: EngineIds.ONE,
    };
    try {
        const response = await engineContractsRequest(data, BASE_ENGINE_URL);

        if (!response.data?.result) {
            throw new Error("Failed to get engine tokens data")
        }

        return response.data.result;
    } catch(e) {
        BASE_ENGINE_URL = `${ENGINE_NODES[randomIntFromInterval(0,6)]}/contracts`;
        const response = await engineContractsRequest(data, BASE_ENGINE_URL);

        if (!response.data?.result) {
            throw new Error("Failed to get engine tokens data")
        }

        return response.data.result;
    }
}


export const fetchEngineMetics = async (tokens: string[]): Promise<EngineMetric[]> => {
    const data = {
        jsonrpc: JSON_RPC.RPC_2,
        method: Methods.FIND,
        params: {
            contract: EngineContracts.MARKET,
            table: EngineTables.METRICS,
            query: {
                symbol: { $in: tokens },
            },
        },
        id: EngineIds.ONE,
    };
    try {
        const response = await engineContractsRequest(data, BASE_ENGINE_URL);

        if (!response.data?.result) {
            throw new Error("Failed to get engine metrics data")
        }

        return response.data.result;
    } catch(e) {
        BASE_ENGINE_URL = `${ENGINE_NODES[randomIntFromInterval(0,6)]}/contracts`;
        const response = await engineContractsRequest(data, BASE_ENGINE_URL);

        if (!response.data?.result) {
            throw new Error("Failed to get engine metrics data")
        }
        return response.data.result;
    }
}


export const fetchEngineRewards = async (username: string): Promise<TokenStatus[]> => {
    try {
        const response = await engineRewardsRequest(username, {hive : 1});

        const rawData:TokenStatus[] = Object.values(response.data);
        if (!rawData || rawData.length === 0) {
          throw new Error('No rewards data returned');
        }

        const data = rawData.map(convertRewardsStatus);
        const filteredData = data.filter((item) => item && item.pendingToken > 0);

        return filteredData;
      } catch (err) {
        console.warn('failed to get unclaimed engine rewards', err);
        return [];
      }
}



//portfolio compilation methods
const fetchEngineTokensWithBalance = async (username: string) => {
    try {

        const balances = await fetchEngineBalances(username);

        if (!balances) {
             throw new Error("failed to fetch engine balances");
        }

        const symbols = balances.map((t) => t.symbol);

        const promiseTokens = fetchEngineTokens(symbols);
        const promiseMmetrices = fetchEngineMetics(symbols);
        const promiseUnclaimed = fetchEngineRewards(username)

        const [tokens, metrices, unclaimed] =
            await Promise.all([promiseTokens, promiseMmetrices, promiseUnclaimed])

        return balances.map((balance: any) => {
            const token = tokens.find((t: any) => t.symbol == balance.symbol);
            const metrics = metrices.find((t: any) => t.symbol == balance.symbol);
            const pendingRewards = unclaimed.find((t: any) => t.symbol == balance.symbol);
            return convertEngineToken(balance, token, metrics, pendingRewards);
        });

    } catch (err) {
        console.warn("Engine data fetch failed", err);
        // instead of throwing error, handle to skip engine data addition
        return [];
    }
}



const fetchSpkData = async (username: string) => {
    try {
        const url = `${BASE_SPK_URL}/@${username}`
        const response = await baseApiRequest(url, 'GET')
        if (!response.data) {
            throw new Error("Invalid spk data");
        }

        return response.data;

    } catch (err) {
        console.warn("Spk data fetch failed", err);
        //instead of throwing error, handle to skip spk data addition
        return null;
    }
}


const apiRequestData = async (endpoint: string) => {
    const resp = await apiRequest(endpoint, "GET");

    if (!resp.data) {
        throw new Error("failed to get data");
    }
    return resp.data;
}

export const portfolio = async (req: express.Request, res: express.Response) => {
    try {

        const respObj: { [key: string]: any } = {};
        const { username } = req.body;

        //fetch basic hive data
        const globalProps = fetchGlobalProps();
        const accountData = getAccount(username);

        //fetch market and points data
        const marketData = apiRequestData(`market-data/latest`);
        const pointsData = apiRequestData(`users/${username}`);

        //fetch engine assets
        const engineData = fetchEngineTokensWithBalance(username);

        //fetch spk assets
        const spkData = fetchSpkData(username);

        const responses = await Promise.all([globalProps, marketData, accountData, pointsData, engineData, spkData]);
        const responseKeys = ["globalProps", "marketData", "accountData", "pointsData", "engineData", "spkData"]
        responses.forEach((response, index) => {
            if (response) {
                respObj[responseKeys[index]] = response;
            }
        })

        return res.send(respObj)

    } catch (err) {
        console.warn("failed to compile portfolio", err);
        return res.status(500).send(err.message)
    }

}

export const portfolioV2 = async (req: express.Request, res: express.Response) => {
    const { username, currency, onlyEnabled } = req.body || {};

    if (!username || typeof username !== "string") {
        res.status(400).send("Missing username");
        return;
    }

    const normalizedCurrency = normalizeCurrency(currency);
    const onlyEnabledFlag = parseBoolean(onlyEnabled);

    try {
        const globalPropsPromise = fetchGlobalProps();
        const accountPromise = getAccount(username);
        const marketPromise = apiRequestData(`market-data/latest`);
        const pointsPromise = apiRequestData(`users/${username}`);
        const enginePromise = fetchEngineTokensWithBalance(username);
        const spkPromise = fetchSpkData(username);

        const [globalProps, accountData, marketData, pointsData, engineData, spkData] = await Promise.all([
            globalPropsPromise,
            accountPromise,
            marketPromise,
            pointsPromise,
            enginePromise,
            spkPromise,
        ]);

        const hivePrice = getTokenPrice(marketData, "hive", normalizedCurrency);

        const wallets: PortfolioItem[] = [];

        wallets.push(
            ...buildPointsLayer(pointsData, marketData, normalizedCurrency),
            ...buildHiveLayer(accountData, globalProps, marketData, normalizedCurrency),
            ...buildSpkLayer(spkData, marketData, normalizedCurrency),
            ...buildEngineLayer(engineData, marketData, normalizedCurrency, hivePrice),
        );

        const chainOptions =
            onlyEnabledFlag === undefined
                ? undefined
                : {
                      onlyEnabled: onlyEnabledFlag,
                  };
        const chainWallets = await buildChainLayer(accountData, marketData, normalizedCurrency, chainOptions);
        wallets.push(...chainWallets);

        const filteredWallets = wallets.filter((item) => item && Number.isFinite(item.balance));

        res.send({
            username,
            currency: normalizedCurrency.toUpperCase(),
            wallets: filteredWallets,
        });
    } catch (err: any) {
        console.warn("failed to compile portfolio v2", err);
        const message = err && err.message ? err.message : "Failed to compile portfolio";
        res.status(500).send(message);
    }
};
