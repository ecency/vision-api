import React from "react";

import {renderToString} from "react-dom/server";

import App from "../common/app";

let assets: any;

const syncLoadAssets = () => {
    assets = require(process.env.RAZZLE_ASSETS_MANIFEST!);
};
syncLoadAssets();

export const render = () => {
    const markup = renderToString(
        <App/>
    );

    return `<!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <link rel="icon" href="/favicon.png" />
                <meta name="theme-color" content="#000000" />
                <title>Ecency Api</title>
            </head>
            <body>
                <div id="root">${markup}</div>
            </body>
        </html>`;
};
