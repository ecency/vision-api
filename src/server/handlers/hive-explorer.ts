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
        const _virtualSupply = _getVestAmount(globalDynamic.virtual_supply);

        const hivePerMVests =
            (_totalFunds / _totalShares) * 1e6;

        const hbdInterestRateRaw =
            typeof globalDynamic.hbd_interest_rate === 'number'
                ? globalDynamic.hbd_interest_rate
                : Number(globalDynamic.hbd_interest_rate) || 0;
        const hbdApr = Number.isFinite(hbdInterestRateRaw) ? hbdInterestRateRaw / 100 : 0;

        const vestingRewardPercentRaw =
            typeof globalDynamic.vesting_reward_percent === 'number'
                ? globalDynamic.vesting_reward_percent
                : Number(globalDynamic.vesting_reward_percent) || 0;
        const vestingRewardPercent = vestingRewardPercentRaw / 10000;

        const headBlockNumber =
            typeof globalDynamic.head_block_number === 'number'
                ? globalDynamic.head_block_number
                : Number(globalDynamic.head_block_number) || 0;

        // The blockchain exposes the current inflation rate (in basis points) as part
        // of the global props. Prefer that exact value when present so the APR stays
        // perfectly aligned with what on-chain tooling reports.
        const currentInflationRateRaw =
            typeof globalDynamic.current_inflation_rate === 'number'
                ? globalDynamic.current_inflation_rate
                : Number(globalDynamic.current_inflation_rate) || 0;

        const inflationBase = 9.5;
        const inflationDecreasePerStep = 0.01;
        const blocksPerStep = 250000;
        const inflationFloor = 0.95;
        // Historically Hive did not start lowering the inflation rate until several
        // million blocks into the chain. Mirror that behaviour so the fallback math
        // matches the node implementation when `current_inflation_rate` is missing.
        const inflationStartBlock = 7000000;
        const inflationReductionSteps = Math.max(headBlockNumber - inflationStartBlock, 0) / blocksPerStep;
        const derivedInflationRate = Math.max(
            inflationFloor,
            inflationBase - Math.floor(inflationReductionSteps) * inflationDecreasePerStep,
        );

        const inflationRatePercent = currentInflationRateRaw > 0
            ? currentInflationRateRaw / 100
            : derivedInflationRate;

        const hpAprCandidate =
            _totalFunds > 0 && _virtualSupply > 0 && vestingRewardPercent > 0
                ? (_virtualSupply * (inflationRatePercent / 100) * vestingRewardPercent * 100) / _totalFunds
                : 0;
        const hpApr = Number.isFinite(hpAprCandidate) ? hpAprCandidate : 0;

        const globalProps = {
            hivePerMVests,
            hbdApr,
            hpApr,
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
