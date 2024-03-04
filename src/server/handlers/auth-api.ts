import express from "express";
import {cryptoUtils, PrivateKey} from "@hiveio/dhive";
import { Base64 } from 'js-base64'

import {decodeToken, getTokenUrl} from "../../common/helper/hive-signer";
import {baseApiRequest, pipe} from "../util";
import config from "../../config";

const b64uLookup: Record<string, string> = {
    '/': '_',
    _: '/',
    '+': '-',
    '-': '+',
    '=': '.',
    '.': '='
}

export const hsTokenRefresh = async (req: express.Request, res: express.Response) => {
    const {code} = req.body;
    if (!decodeToken(code)) res.status(401).send("Unauthorized");

    pipe(baseApiRequest(getTokenUrl(code, config.hsClientSecret), "GET"), res);
};

export function b64uEnc (str: string): string {
    return Base64.encode(str).replace(/(\+|\/|=)/g, m => b64uLookup[m])
}
export function b64uDec (str: string): string {
    return Base64.decode(str).replace(/(-|_|\.)/g, m => b64uLookup[m])
}

export const hsTokenCreate = async (req: express.Request, res: express.Response) => {
    const {username, password, app} = req.body;

    const timestamp = parseInt((new Date().getTime() / 1000) + '', 10)
    const messageObj: Record<string, object | object[] | number> = { signed_message: {"type":"code",app}, authors: [`${username}`], timestamp }
    const hash = cryptoUtils.sha256(JSON.stringify(messageObj))
    const privateKey = PrivateKey.fromLogin(username, password, 'posting')
    const signature = privateKey.sign(hash).toString()
    messageObj.signatures = [signature]

    res.send(b64uEnc(JSON.stringify(messageObj)))
};



