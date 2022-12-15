import express from "express";

import {baseApiRequest, pipe} from "../util";

const BASE_URL = 'https://api.hive-engine.com';
const ENGINE_REWARDS_URL = 'https://scot-api.hive-engine.com/';
const ENGIEN_CHART_URL = 'https://info-api.tribaldex.com/market/ohlcv';
const PATH_RPC = 'rpc';
export const PATH_CONTRACTS = 'contracts';

export const eapi = async (req: express.Request, res: express.Response) => {
    const data = req.body;

    const url = `${BASE_URL}/${PATH_RPC}/${PATH_CONTRACTS}`;
    const headers = { 'Content-type': 'application/json' };

    pipe(baseApiRequest(url, "POST", headers, data), res);
}


export const erewardapi = async (req: express.Request, res: express.Response) => {
    const params = req.params;

    const url = `${ENGINE_REWARDS_URL}${req.path}`;
    const headers = { 'Content-type': 'application/json' };

    pipe(baseApiRequest(url, "GET", headers, undefined, params), res);
}

export const echartapi = async (req: express.Request, res: express.Response) => {
    const params = req.params;

    const url = `${ENGIEN_CHART_URL}`;
    const headers = { 'Content-type': 'application/json' };

    pipe(baseApiRequest(url, "GET", headers, undefined, params), res);
}

