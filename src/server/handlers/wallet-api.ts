import express from "express";

import { baseApiRequest, pipe } from "../util";
import { fetchGlobalProps, getAccount } from "./hive-explorer";
import { apiRequest } from "../helper";
import { EngineContracts, EngineIds, EngineMetric, EngineRequestPayload, EngineTables, JSON_RPC, Methods, Token, TokenBalance, TokenStatus } from "../../models/hiveEngine.types";
import { convertEngineToken, convertRewardsStatus } from "../../models/converters";

//docs: https://hive-engine.github.io/engine-docs/
//available nodes: https://beacon.peakd.com/ select tab 'Hive Engine'
const ENGINE_NODES = [
    "https://engine.rishipanthee.com",
    "https://herpc.dtools.dev",
    "https://api.hive-engine.com/rpc",
    "https://ha.herpc.dtools.dev",
    "https://herpc.kanibot.com",
    "https://he.sourov.dev",
    "https://herpc.actifit.io",
    "https://api2.hive-engine.com/rpc"
  ];

const BASE_ENGINE_URL = ENGINE_NODES[0];
const BASE_SPK_URL = 'https://spk.good-karma.xyz';

const ENGINE_REWARDS_URL = 'https://scot-api.hive-engine.com/';
const ENGINE_CHART_URL = 'https://info-api.tribaldex.com/market/ohlcv';

//docs: https://github.com/hive-engine/ssc_tokens_history/tree/hive#api-usage
const ENGINE_ACCOUNT_HISTORY_URL = 'https://history.hive-engine.com/accountHistory';

export const PATH_CONTRACTS = 'contracts';


//client engine api requests
export const eapi = async (req: express.Request, res: express.Response) => {
    const data = req.body;
    pipe(engineContractsRequest(data), res);
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
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency Apps' };

    pipe(baseApiRequest(url, "GET", headers, undefined, params), res);
}


//raw engine api call
const engineContractsRequest = (data: EngineRequestPayload) => {
    const url = `${BASE_ENGINE_URL}/${PATH_CONTRACTS}`;
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

    const response = await engineContractsRequest(data);

    if (!response.data?.result) {
        throw new Error("Failed to get engine balances")
    }

    return response.data.result;
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

    const response = await engineContractsRequest(data);

    if (!response.data?.result) {
        throw new Error("Failed to get engine tokens data")
    }

    return response.data.result;
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

    const response = await engineContractsRequest(data);

    if (!response.data?.result) {
        throw new Error("Failed to get engine metrics data")
    }

    return response.data.result;
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
    
        console.log('unclaimed engine rewards data', filteredData);
        return filteredData;
      } catch (err) {
        console.warn('failed ot get unclaimed engine rewards', err);
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
        return null;
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

        res.send(respObj)

    } catch (err: any) {
        console.warn("failed to compile portfolio", err);
        res.status(500).send(err.message)
    }

}
