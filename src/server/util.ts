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


export const baseApiRequest = (url: string, method: Method, headers: any = {}, payload: any = {}, params: any = {}): Promise<AxiosResponse> => {
    const requestConf: AxiosRequestConfig = {
        url,
        method,
        validateStatus: () => true,
        responseType: "json",
        headers: {...headers},
        data: {...payload},
        params: {...params}
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
