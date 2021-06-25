import axios, {AxiosResponse, Method} from "axios";

import {cache} from "./cache";

import config from "../config";

import {baseApiRequest} from "./util";

const makeApiAuth = () => {
    try {
        const auth = new Buffer(config.privateApiAuth, "base64").toString("utf-8");
        return JSON.parse(auth);
    } catch (e) {
        return null;
    }
}

export const apiRequest = (endpoint: string, method: Method, extraHeaders: any = {}, payload: any = {}): Promise<AxiosResponse> | Promise<any> => {
    const apiAuth = makeApiAuth();
    if (!apiAuth) {
        return new Promise((resolve, reject) => {
            console.error("Api auth couldn't be create!");
            reject("Api auth couldn't be create!");
        })
    }

    const url = `${config.privateApiAddr}/${endpoint}`;

    const headers = {
        "Content-Type": "application/json",
        ...apiAuth,
        ...extraHeaders
    }

    return baseApiRequest(url, method, headers, payload)
}

interface Entry{

}

export const fetchPromotedEntries = async (): Promise<Entry[]> => {
    // fetch list from api
    const list: { author: string, permlink: string, post_data?: Entry }[] = (await apiRequest('promoted-posts?limit=200', 'GET')).data;

    // random sort & random pick 18 (6*3)
    const promoted = list.sort(() => Math.random() - 0.5).filter((x, i) => i < 18);

    return promoted.map(x => x.post_data).filter(x => x) as Entry[];
}

export const getPromotedEntries = async (): Promise<Entry[]> => {
    let promoted: Entry[] | undefined = cache.get('promoted-entries');
    if (promoted === undefined) {
        try {
            promoted = await fetchPromotedEntries();
            cache.set("promoted-entries", promoted, 60);
        } catch (e) {
            promoted = [];
        }
    }

    return promoted.sort(() => Math.random() - 0.5);
}
