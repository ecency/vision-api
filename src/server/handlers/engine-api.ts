import express from "express";

import {baseApiRequest, pipe} from "../util";

//docs: https://hive-engine.github.io/engine-docs/
const BASE_URL = 'https://api2.hive-engine.com';//'https://api2.hive-engine.com';

const ENGINE_REWARDS_URL = 'https://scot-api.hive-engine.com/';
const ENGINE_CHART_URL = 'https://info-api.tribaldex.com/market/ohlcv';

//docs: https://github.com/hive-engine/ssc_tokens_history/tree/hive#api-usage
const ENGINE_ACCOUNT_HISTORY_URL = 'https://history.hive-engine.com/accountHistory';

const PATH_RPC = 'rpc';
export const PATH_CONTRACTS = 'contracts';

export const eapi = async (req: express.Request, res: express.Response) => {
    const data = req.body;

    const url = `${BASE_URL}/${PATH_RPC}/${PATH_CONTRACTS}`;
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency' };

    pipe(baseApiRequest(url, "POST", headers, data), res);
}


export const erewardapi = async (req: express.Request, res: express.Response) => {
    const {username} = req.params;
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

export const engineAccountHistory =  (req: express.Request, res: express.Response) => {
    const params = req.query;

    const url = `${ENGINE_ACCOUNT_HISTORY_URL}`;
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency Apps' };

    pipe(baseApiRequest(url, "GET", headers, undefined, params), res);
}

