import axios, {AxiosRequestConfig, AxiosResponse, Method} from "axios";

import express from "express";

export const pipe = (promise: Promise<AxiosResponse>, res: express.Response) => {
    promise.then(r => {
        const ddata = typeof r.data === 'number' ? r.data.toString() : r.data;
        res.status(r.status).send(ddata);
    }).catch(() => {
        res.status(500).send("Server Error");
    });
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
