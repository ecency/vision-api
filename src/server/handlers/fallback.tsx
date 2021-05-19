import express from "express";

import {render} from "../template";

export default async (req: express.Request, res: express.Response) => {

    res.send(render());
};

export const healthCheck = async (req: express.Request, res: express.Response) => {
    res.send({
        status: 200,
        body: {
            status: 'ok'
        }
    })
};
