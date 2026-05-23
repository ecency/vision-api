import express from "express";

import config from "../../config";

import {baseApiRequest, pipe} from "../util";

export const search = async (req: express.Request, res: express.Response) => {
    const {q, sort, hide_low, since, scroll_id, votes, include_nsfw} = req.body;

    const url = `${config.searchApiAddr}/search`;
    const headers = {'Authorization': config.searchApiToken};

    const payload: { q: string, sort: string, hide_low: string, since?: string, scroll_id?: string, votes?: number, include_nsfw?: number } = {q, sort, hide_low};

    if (since) payload.since = since;
    if (scroll_id) payload.scroll_id = scroll_id;
    if (votes) payload.votes = votes;
    if (include_nsfw) payload.include_nsfw = include_nsfw;

    // Search can be moderately slow; allow more than the default.
    pipe(baseApiRequest(url, "POST", headers, payload, {}, 15000), res);
}


export const searchFollower = async (req: express.Request, res: express.Response) => {
    const {q, following} = req.body;

    const url = `${config.searchApiAddr}/search-follower/${following}`;
    const headers = {'Authorization': config.searchApiToken};

    pipe(baseApiRequest(url, "POST", headers, {q: q}, {}, 15000), res);
}

export const searchFollowing = async (req: express.Request, res: express.Response) => {
    const {follower, q} = req.body;

    const url = `${config.searchApiAddr}/search-following/${follower}`;
    const headers = {'Authorization': config.searchApiToken};

    pipe(baseApiRequest(url, "POST", headers, {q}, {}, 15000), res);
}

export const searchAccount = async (req: express.Request, res: express.Response) => {
    const {q, limit, random} = req.body;

    const url = `${config.searchApiAddr}/search-account`;
    const headers = {'Authorization': config.searchApiToken};

    pipe(baseApiRequest(url, "POST", headers, {q, limit, random}, {}, 15000), res);
}

export const searchTag = async (req: express.Request, res: express.Response) => {
    const {q, limit, random} = req.body;

    const url = `${config.searchApiAddr}/search-tag`;
    const headers = {'Authorization': config.searchApiToken};

    pipe(baseApiRequest(url, "POST", headers, {q, limit, random}, {}, 15000), res);
}

export const searchPath = async (req: express.Request, res: express.Response) => {
    const {q} = req.body;

    const url = `${config.searchApiAddr}/search-path`;
    const headers = {'Authorization': config.searchApiToken};

    pipe(baseApiRequest(url, "POST", headers, {q}, {}, 15000), res);
}
