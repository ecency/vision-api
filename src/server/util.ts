import axios, {AxiosRequestConfig, AxiosResponse, Method} from "axios";

import express from "express";

export const pipe = async (promise: Promise<AxiosResponse>, res: express.Response) => {
    try {
        const r = await promise;

        if (!res.headersSent) {
            const ddata = typeof r.data === 'number' ? r.data.toString() : r.data;
            res.status(r.status).send(ddata);
        } else {
            console.warn("pipe(): headers already sent, skipping response");
        }

    } catch (e) {
        console.error("pipe(): error while processing API call:", e);

        if (!res.headersSent) {
            res.status(500).send("Server Error");
        } else {
            console.warn("pipe(): headers already sent, cannot send error response");
        }
    }
};


export const baseApiRequest = (url: string, method: Method, headers: any = {}, payload: any = {}, params: any = {}, timeout: number = 30000): Promise<AxiosResponse> => {
    const requestConf: AxiosRequestConfig = {
        url,
        method,
        validateStatus: () => true,
        responseType: "json",
        headers: {...headers},
        data: {...payload},
        params: {...params},
        timeout
    }

    return axios(requestConf)
}


export const parseToken = (strVal: string) => {
    // checks if first part of string is float
    const regex = /^\-?[0-9]+(e[0-9]+)?(\.[0-9]+)? .*$/;
    if (!regex.test(strVal)) {
      return 0;
    }

    return Number(parseFloat(strVal.split(' ')[0]));
  };


export const parseAsset = (strVal:string) => {
    if (typeof strVal !== 'string') {
      // console.log(strVal);
      return {
        amount: 0,
        symbol: '',
      };
    }
    const sp = strVal.split(' ');
    return {
      amount: parseFloat(sp[0]),
      symbol: Symbol[sp[1]],
    };
  };


/**
 * calculates hours difference between two dates, negative value will mean first date
 * is from past time
 * @param date1 Base date from which date2 will be subtracted
 * @param date2 Date to be subtracted
 * @returns number of hours difference between two dates
 */
export const getHoursDifferntial = (date1: Date, date2: Date): number => {
  if (date1 instanceof Date && date2 instanceof Date) {
    return (Number(date1) - Number(date2)) / (60 * 60 * 1000);
  }

  return 0;
};
