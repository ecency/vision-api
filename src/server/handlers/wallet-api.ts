import express from "express";

import { baseApiRequest, pipe } from "../util";
import { fetchGlobalProps, getAccount } from "./dhive";
import { apiRequest } from "../helper";

//docs: https://hive-engine.github.io/engine-docs/
const BASE_ENGINE_URL = 'https://api2.hive-engine.com';//'https://api.hive-engine.com';
const BASE_SPK_URL = 'https://spk.good-karma.xyz';

const ENGINE_REWARDS_URL = 'https://scot-api.hive-engine.com/';
const ENGINE_CHART_URL = 'https://info-api.tribaldex.com/market/ohlcv';

//docs: https://github.com/hive-engine/ssc_tokens_history/tree/hive#api-usage
const ENGINE_ACCOUNT_HISTORY_URL = 'https://history.hive-engine.com/accountHistory';

const PATH_RPC = 'rpc';
export const PATH_CONTRACTS = 'contracts';

export const eapi = async (req: express.Request, res: express.Response) => {
    const data = req.body;

    const url = `${BASE_ENGINE_URL}/${PATH_RPC}/${PATH_CONTRACTS}`;
    const headers = { 'Content-type': 'application/json', 'User-Agent': 'Ecency' };

    pipe(baseApiRequest(url, "POST", headers, data), res);
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



const fetchSpkData = async (username:string) => {
    try{
        const url = `${BASE_SPK_URL}/@${username}`
        const response = await baseApiRequest(url, 'GET')
        if(!response.data){
            throw new Error("Invalid spk data");
        }

        return response.data;

    } catch(err){
        console.warn("Spk data fetch failed", err);
        //TODO: instead of throwing error, handle to skip spk data addition
    }
}



export const portfolio = async (req: express.Request, res: express.Response) => {
    try {

        const { username } = req.body;

        //fetch basic hive data
        const _globalProps = await fetchGlobalProps();
        const _userdata = await getAccount(username);

        //fetch points data
        //TODO: put back api request 
        //const _marketData = await apiRequest(`market-data/latest`, "GET");
        const _marketData = await dummyMarketData()

        //TODO: put back api request 
        //const _pointsSummary =await apiRequest(`users/${username}`, "GET");
        const _pointsData = await dummyPointSummary()

        //TODO: fetch engine assets
        const _engineData = {};


        //TODO: fetch spk assets
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


//TODO: remove before merging
const dummyPointSummary = async () => {
    return {
        "username": "${username}",
        "points": "5523.704",
        "points_by_type": {
            "10": "6750.500",
            "20": "100.000",
            "30": "7930.000",
            "100": "862.500",
            "110": "1075.105",
            "120": "190.200",
            "130": "70.400",
            "150": "5089.637"
        },
        "unclaimed_points": "276.167",
        "unclaimed_points_by_type": {
            "10": "96.500",
            "30": "160.000",
            "110": "19.667"
        }
    }
}

//TOOD: remove before merging
const dummyMarketData = async () => {
    return {
        "btc": {
            "quotes": {
                "btc": {
                    "last_updated": "2024-07-18T06:43:00.000Z",
                    "percent_change": 0,
                    "price": 1
                },
                "usd": {
                    "last_updated": "2024-07-18T06:43:00.000Z",
                    "percent_change": 0.0060308,
                    "price": 0.0001638804887141081
                }
            }
        },
        "estm": {
            "quotes": {
                "btc": {
                    "last_updated": "2022-08-12T04:57:00.000Z",
                    "percent_change": 0,
                    "price": 8e-8
                },
                "usd": {
                    "last_updated": "2022-08-12T04:57:00.000Z",
                    "percent_change": 0,
                    "price": 0.002
                }
            }
        },
        "eth": {
            "quotes": {
                "btc": {
                    "last_updated": "2024-07-18T06:43:00.000Z",
                    "percent_change": 0,
                    "price": 1.0797900018640657e-7
                },
                "usd": {
                    "last_updated": "2024-07-18T06:43:00.000Z",
                    "percent_change": 0,
                    "price": 0.007013003078153161
                }
            }
        },
        "hbd": {
            "quotes": {
                "btc": {
                    "last_updated": "2024-07-18T06:43:00.000Z",
                    "percent_change": 0.28189758,
                    "price": 0.00001531436662541532
                },
                "usd": {
                    "last_updated": "2024-07-18T06:43:00.000Z",
                    "percent_change": 0.19563211,
                    "price": 0.9947278858468801
                }
            }
        },
        "hive": {
            "quotes": {
                "btc": {
                    "last_updated": "2024-07-18T06:43:00.000Z",
                    "percent_change": 0.50376444,
                    "price": 0.0000034331098014452116
                },
                "usd": {
                    "last_updated": "2024-07-18T06:43:00.000Z",
                    "percent_change": 0.55258548,
                    "price": 0.22308583979341057
                }
            }
        }
    }
}

