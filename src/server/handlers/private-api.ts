import express from "express";
import { createClient, RedisClient } from 'redis';
import { promisify } from 'util';
import hs from "hivesigner";
import axios, {AxiosRequestConfig} from "axios";

import config from "../../config";
import {announcements} from "./announcements";
import {apiRequest, getPromotedEntries} from "../helper";

import {pipe} from "../util";

const validateCode = async (req: express.Request, res: express.Response): Promise<string | false> => {
    const {code} = req.body;

    if (!code) {
        res.status(400).send("Bad Request");
        return false;
    }

    try {
        return await (new hs.Client({accessToken: code}).me().then((r: { name: string }) => r.name));
    } catch (e) {
        res.status(401).send("Unauthorized");
        return false;
    }
};

export const receivedVesting = async (req: express.Request, res: express.Response) => {
    const {username} = req.params;
    pipe(apiRequest(`delegatee_vesting_shares/${username}`, "GET"), res);
};

export const receivedRC = async (req: express.Request, res: express.Response) => {
    const {username} = req.params;
    pipe(apiRequest(`delegatee_rc/${username}`, "GET"), res);
};

export const rewardedCommunities = async (req: express.Request, res: express.Response) => {
    pipe(apiRequest(`rewarded-communities`, "GET"), res);
};

export const leaderboard = async (req: express.Request, res: express.Response) => {
    const {duration} = req.params;
    pipe(apiRequest(`leaderboard?duration=${duration}`, "GET"), res);
};

export const getAnnouncement = async (req: express.Request, res: express.Response) => {
    res.send(announcements)
}

export const referrals = async (req: express.Request, res: express.Response) => {
    const {username} = req.params;
    const {max_id} = req.query;
    let u = `referrals/${username}?size=20`;
    if (max_id) {
        u += `&max_id=${max_id}`;
    }
    pipe(apiRequest(u, "GET"), res);
};

export const referralsStats = async (req: express.Request, res: express.Response) => {
    const {username} = req.params;
    let u = `referrals/${username}/stats`;
    pipe(apiRequest(u, "GET"), res);
};


export const curation = async (req: express.Request, res: express.Response) => {
    const {duration} = req.params;
    pipe(apiRequest(`curation?duration=${duration}`, "GET"), res);
};

export const promotedEntries = async (req: express.Request, res: express.Response) => {
    const posts = await getPromotedEntries();
    res.send(posts);
};

export const commentHistory = async (req: express.Request, res: express.Response) => {
    const {author, permlink, onlyMeta} = req.body;

    let u = `comment-history/${author}/${permlink}`;
    if (onlyMeta === '1') {
        u += '?only_meta=1';
    }

    pipe(apiRequest(u, "GET"), res);
};

export const points = async (req: express.Request, res: express.Response) => {
    const {username} = req.body;
    pipe(apiRequest(`users/${username}`, "GET"), res);
};

export const pointList = async (req: express.Request, res: express.Response) => {
    const {username, type} = req.body;
    let u = `users/${username}/points?size=50`;
    if (type) {
        u += `&type=${type}`;
    }
    pipe(apiRequest(u, "GET"), res);
};

export const createAccount = async (req: express.Request, res: express.Response) => {
    const {username, email, referral} = req.body;

    const headers = {'X-Real-IP-V': req.headers['x-forwarded-for'] || ''};
    const payload = {username, email, referral};

    pipe(apiRequest(`signup/account-create`, "POST", headers, payload), res);
};

export const notifications = async (req: express.Request, res: express.Response) => {
    let username = await validateCode(req, res);
    const {filter, since, limit, user} = req.body;

    if (!username) {
        if (!user) {
            return;
        } else {
            username = user;
        }
    };
    // if user defined but not same as user's code
    if (user && username!==user) {
        username = user;
    }

    let u = `activities/${username}`

    if (filter) {
        u = `${filter}/${username}`
    }

    if (since) {
        u += `?since=${since}`;
    }

    if (since && limit) {
        u += `&limit=${limit}`;
    }

    if (!since && limit) {
        u += `?limit=${limit}`;
    }

    pipe(apiRequest(u, "GET"), res);
};

/* Login required endpoints */

export const unreadNotifications = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;

    pipe(apiRequest(`activities/${username}/unread-count`, "GET"), res);
};

export const markNotifications = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;

    const {id} = req.body;
    const data: { id?: string } = {};

    if (id) {
        data.id = id;
    }

    pipe(apiRequest(`activities/${username}`, "PUT", {}, data), res);
};

export const registerDevice = async (req: express.Request, res: express.Response) => {
    const _username = await validateCode(req, res);
    if (!_username) return;
    const {username, token, system, allows_notify, notify_types} = req.body;
    const data = {username, token, system, allows_notify, notify_types};
    pipe(apiRequest(`rgstrmbldvc/`, "POST", {}, data), res);
};

export const detailDevice = async (req: express.Request, res: express.Response) => {
    const _username = await validateCode(req, res);
    if (!_username) return;
    const {username, token} = req.body;
    pipe(apiRequest(`mbldvcdtl/${username}/${token}`, "GET"), res);
};

export const images = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;

    pipe(apiRequest(`images/${username}`, "GET"), res);
}

export const imagesDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id} = req.body;
    pipe(apiRequest(`images/${username}/${id}`, "DELETE"), res);
}

export const imagesAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {url} = req.body;
    const data = {username, image_url: url};
    pipe(apiRequest(`image`, "POST", {}, data), res);
}

export const drafts = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    pipe(apiRequest(`drafts/${username}`, "GET"), res);
}

export const draftsAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {title, body, tags, meta} = req.body;
    const data = {username, title, body, tags, meta};
    pipe(apiRequest(`draft`, "POST", {}, data), res);
}

export const draftsUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id, title, body, tags, meta} = req.body;
    const data = {username, title, body, tags, meta};
    pipe(apiRequest(`drafts/${username}/${id}`, "PUT", {}, data), res);
}

export const draftsDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id} = req.body;
    pipe(apiRequest(`drafts/${username}/${id}`, "DELETE"), res);
}

export const bookmarks = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    pipe(apiRequest(`bookmarks/${username}`, "GET"), res);
}

export const bookmarksAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {author, permlink} = req.body;
    const data = {username, author, permlink, chain: 'steem'};
    pipe(apiRequest(`bookmark`, "POST", {}, data), res);
}

export const bookmarksDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id} = req.body;
    pipe(apiRequest(`bookmarks/${username}/${id}`, "DELETE"), res);
}

export const schedules = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    pipe(apiRequest(`schedules/${username}`, "GET"), res);
}

export const schedulesAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;

    const {permlink, title, body, meta, options, schedule, reblog} = req.body;

    const data = {username, permlink, title, body, meta, options, schedule, reblog: reblog ? 1 : 0};
    pipe(apiRequest(`schedules`, "POST", {}, data), res);
}

export const schedulesDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id} = req.body;
    pipe(apiRequest(`schedules/${username}/${id}`, "DELETE"), res);
}

export const schedulesMove = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id} = req.body;
    pipe(apiRequest(`schedules/${username}/${id}`, "PUT"), res);
}

export const favorites = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    pipe(apiRequest(`favorites/${username}`, "GET"), res);
}

export const favoritesCheck = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {account} = req.body;
    pipe(apiRequest(`isfavorite/${username}/${account}`, "GET"), res);
}

export const favoritesAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {account} = req.body;
    const data = {username, account};
    pipe(apiRequest(`favorite`, "POST", {}, data), res);
}

export const favoritesDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {account} = req.body;
    pipe(apiRequest(`favoriteUser/${username}/${account}`, "DELETE"), res);
}

export const fragments = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    pipe(apiRequest(`fragments/${username}`, "GET"), res);
}

export const fragmentsAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {title, body} = req.body;
    const data = {username, title, body};
    pipe(apiRequest(`fragment`, "POST", {}, data), res);
}

export const fragmentsUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id, title, body} = req.body;
    const data = {title, body};
    pipe(apiRequest(`fragments/${username}/${id}`, "PUT", {}, data), res);
}

export const fragmentsDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id} = req.body;
    pipe(apiRequest(`fragments/${username}/${id}`, "DELETE"), res);
}

export const decks = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    pipe(apiRequest(`decks/${username}`, "GET"), res);
}

export const decksAdd = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {title, body, tags, meta} = req.body;
    const data = {username, title, body, tags, meta};
    pipe(apiRequest(`deck`, "POST", {}, data), res);
}

export const decksUpdate = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id, title, body, tags, meta} = req.body;
    const data = {username, title, body, tags, meta};
    pipe(apiRequest(`decks/${username}/${id}`, "PUT", {}, data), res);
}

export const decksDelete = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {id} = req.body;
    pipe(apiRequest(`decks/${username}/${id}`, "DELETE"), res);
}

export const pointsClaim = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const data = {us: username};
    pipe(apiRequest(`claim`, "PUT", {}, data), res);
}

export const pointsCalc = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {amount} = req.body;
    pipe(apiRequest(`estm-calc?username=${username}&amount=${amount}`, "GET"), res);
}

export const promotePrice = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    pipe(apiRequest(`promote-price`, "GET"), res);
}

export const promotedPost = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {author, permlink} = req.body;
    pipe(apiRequest(`promoted-posts/${author}/${permlink}`, "GET"), res);
}

export const boostOptions = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    pipe(apiRequest(`boost-options`, "GET"), res);
}

export const boostedPost = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {author, permlink} = req.body;
    pipe(apiRequest(`boosted-posts/${author}/${permlink}`, "GET"), res);
}

const redisGetAsync = (client: RedisClient) => promisify(client.get).bind(client);
const redisSetAsync = (client: RedisClient) => promisify(client.set).bind(client);

export const activities = async (req: express.Request, res: express.Response) => {
    const username = await validateCode(req, res);
    if (!username) return;
    const {ty, bl, tx} = req.body;
    if (ty === 10) {
        const vip = req.headers['x-real-ip'] || req.connection.remoteAddress || req.headers['x-forwarded-for'] || '';
        let identifier = `${vip}`;
        const client = createClient({
            url: config.redisUrl
        });

        const rec = await redisGetAsync(client)(identifier);
        if (rec) {
            if (new Date().getTime() - new Date(Number(rec)).getTime() < 900000) {
                res.status(201).send({})
                return
            }
            await redisSetAsync(client)(identifier, new Date().getTime().toString());
        } else {
            await redisSetAsync(client)(identifier, new Date().getTime().toString());
        }
    }

    let pipe_json = {
        "us": username,
        "ty": ty
    }
    if (bl) {
        pipe_json["bl"] = bl
    }
    if (tx) {
        pipe_json["tx"] = tx
    }

    pipe(apiRequest(`usr-activity`, "POST", {}, pipe_json), res);
}

export const subscribeNewsletter = async (req: express.Request, res: express.Response) => {
    const {email} = req.body;
    if (email) {
        const [first_name] = email.split('@');
        const data = {email, first_name};
        
        const requestConf: AxiosRequestConfig = {
            url: "https://www.getrevue.co/api/v2/subscribers",
            method: "POST",
            validateStatus: () => true,
            responseType: "json",
            headers: {"Authorization": `Token ${config.revueToken}`},
            data: {...data}
        }

        pipe(axios(requestConf), res)    
    } else {
        res.status(500).send("Server Error");
    }
    
}

export const marketData = async (req: express.Request, res: express.Response) => {
    const {fiat, token} = req.params;
    pipe(apiRequest(`market-data/currency-rate/${fiat}/${token}`, "GET"), res);
};

export const marketDataLatest = async (req: express.Request, res: express.Response) => {
    pipe(apiRequest(`market-data/latest`, "GET"), res);
};
