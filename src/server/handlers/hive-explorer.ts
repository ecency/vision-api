import { baseApiRequest, parseAsset, parseToken } from '../util';


const BASE_URL = 'https://hivexplorer.com/api';


export const fetchGlobalProps = async () => {
    let globalDynamic;

    try {
        const _globalPropsUrl = `${BASE_URL}/get_dynamic_global_properties`;

        const globalDynamicResp = await baseApiRequest(_globalPropsUrl, 'GET');

        globalDynamic = globalDynamicResp.data;
   
        if (!globalDynamic) {
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


        const globalProps = {
            hivePerMVests,
        };

        return globalProps;
    } catch (e) {
        throw new Error("Failed to get globalProps")
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
       throw new Error("Failed to get account data")
    }
}
