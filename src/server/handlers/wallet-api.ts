import express from "express";

import { baseApiRequest, pipe } from "../util";
import { fetchGlobalProps, getAccount } from "./hive-explorer";
import { apiRequest } from "../helper";
import { EngineContracts, EngineIds, EngineMetric, EngineRequestPayload, EngineTables, JSON_RPC, Methods, Token, TokenBalance } from "../../models/hiveEngine.types";
import { convertEngineToken } from "../../models/converters";

//docs: https://hive-engine.github.io/engine-docs/
const BASE_ENGINE_URL = 'https://api2.hive-engine.com';//'https://api2.hive-engine.com';
const BASE_SPK_URL = 'https://spk.good-karma.xyz';

const ENGINE_REWARDS_URL = 'https://scot-api.hive-engine.com/';
const ENGINE_CHART_URL = 'https://info-api.tribaldex.com/market/ohlcv';

//docs: https://github.com/hive-engine/ssc_tokens_history/tree/hive#api-usage
const ENGINE_ACCOUNT_HISTORY_URL = 'https://history.hive-engine.com/accountHistory';

const PATH_RPC = 'rpc';
export const PATH_CONTRACTS = 'contracts';


//client engine api requests
export const eapi = async (req: express.Request, res: express.Response) => {
    const data = req.body;
    pipe(engineContractsRequest(data), res);
}


export const erewardapi = async (req: express.Request, res: express.Response) => {
    const { username } = req.params;
    const params = req.query;

    const url = `${ENGINE_REWARDS_URL}@${username}`;
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency' };

    pipe(baseApiRequest(url, "GET", headers, undefined, params), res);
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


//raw engine api calls
const engineContractsRequest = (data: EngineRequestPayload) => {
    const url = `${BASE_ENGINE_URL}/${PATH_RPC}/${PATH_CONTRACTS}`;
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency' };

    return baseApiRequest(url, "POST", headers, data)
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



//portfolio compilation methods
const fetchEngineTokensWithBalance = async (username: string) => {
    try {

        const balances = await fetchEngineBalances(username);

        if(!balances){
            throw new Error("failed to fetch engine balances");
        }

        const symbols = balances.map((t) => t.symbol);

        const promiseTokens = fetchEngineTokens(symbols);
        const promiseMmetrices = fetchEngineMetics(symbols);

        const [tokens, metrices] = await Promise.all([promiseTokens, promiseMmetrices])

        // const unclaimed = await fetchUnclaimedRewards(username); //TODO: handle rewards later

        return balances.map((balance: any) => {
            const token = tokens.find((t: any) => t.symbol == balance.symbol);
            const metrics = metrices.find((t: any) => t.symbol == balance.symbol);
            // const pendingRewards = unclaimed.find((t: any) => t.symbol == balance.symbol); //TODO: handle rewards later
            return convertEngineToken(balance, token, metrics);
        });

    } catch (err) {
        console.warn("Engine data fetch failed", err);
        // instead of throwing error, handle to skip spk data addition
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
        //TODO: instead of throwing error, handle to skip spk data addition
        return null;
    }
}



export const portfolio = async (req: express.Request, res: express.Response) => {
    try {

        const { username } = req.body;

        //fetch basic hive data
        const _globalProps = await fetchGlobalProps();
        const _userdata = await getAccount(username);

        //fetch market data
        const _marketData = await apiRequest(`market-data/latest`, "GET");

        //fetch points data
        const _pointsData = await apiRequest(`users/${username}`, "GET");


        //fetch engine assets
        const _engineData = await fetchEngineTokensWithBalance(username)


        //fetch spk assets
        const _spkData = await fetchSpkData(username);

        res.send({
            globalProps: _globalProps,
            marketData: _marketData,
            accountData: _userdata,
            pointsData: _pointsData,
            engineData: _engineData,
            spkData: _spkData,
        })

    } catch (err: any) {
        console.warn("failed to compile portfolio", err);
        res.status(500).send(err.message)
    }

}
