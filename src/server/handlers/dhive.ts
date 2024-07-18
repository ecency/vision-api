import {
    Client,
    cryptoUtils,
    utils,
    Types,
    Transaction,
    Operation,
    TransactionConfirmation,
    Asset,
} from '@hiveio/dhive';
import { baseApiRequest, parseAsset, parseToken } from '../util';


const BASE_URL = 'https://hivexplorer.com/api';


let client = new Client([
    'https://api.hive.blog',
    'https://anyx.io',
    'https://techcoderx.com',
    'https://api.openhive.network',
    'https://api.deathwing.me',
    'https://rpc.ausbit.dev',
    'https://hived.emre.sh',
    'https://hive-api.arcange.eu',
], {
    timeout: 4000,
    failoverThreshold: 10,
    consoleOnFailover: true,
});

export const fetchGlobalProps = async () => {
    let globalDynamic;
    let medianHistory;

    try {

        const _globalPropsUrl = `${BASE_URL}/get_dynamic_global_properties`;
        const _mediaHistoryUrl = `${BASE_URL}/get_current_median_history_price`;

        const globalDynamicResp = await baseApiRequest(_globalPropsUrl, 'GET'); client.database.getDynamicGlobalProperties();
        const medianHistoryResp = await baseApiRequest(_mediaHistoryUrl, 'GET'); client.database.call('get_feed_history');

        globalDynamic = globalDynamicResp.data;
        medianHistory = medianHistoryResp.data;

        if (!globalDynamic || !medianHistory) {
            throw new Error("Invalid global props data")
        }


        const _getVestAmount = (value: string | any) => {
            if (typeof value === 'string') {
                return parseToken(value);
            } else {
                return value.amount / (Math.pow(10, value.precision))
            }
        }

        let _totalFunds = _getVestAmount(globalDynamic.total_vesting_fund_hive);
        let _totalShares = _getVestAmount(globalDynamic.total_vesting_shares);


        const hivePerMVests =
            (_totalFunds / _totalShares) * 1e6;



        const base = parseAsset(medianHistory.base).amount;
        const quote = parseAsset(medianHistory.quote).amount;

        const globalProps = {
            hivePerMVests,
            base,
            quote,
        };

        return globalProps;

    } catch (e) {
        throw new Error("Failed to get globalProps", e)
    }
};


/**
* @method getAccount fetch raw account data without post processings
* @param username username
*/
export const getAccount = async (username: string) => {
    try {
        const url = `${BASE_URL}/get_accounts?names=["${username}"]`;
        const response = await baseApiRequest(url, 'GET');

        const data = response.data
        if (data.length) {
            return data[0];
        }
        throw new Error(`Account not found, ${JSON.stringify(data)}`);


    } catch (error) {
       throw new Error("Failed to get account data", error)
    }
}
