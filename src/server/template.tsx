import React from "react";

import { renderToString } from "react-dom/server";

import { Helmet } from "react-helmet";

import App from "../common/app";

let assets: any;

const syncLoadAssets = () => {
  assets = require(process.env.RAZZLE_ASSETS_MANIFEST!);
};
syncLoadAssets();

export const render = () => {
  const markup = renderToString(
      <App />
  );

  const helmet = Helmet.renderStatic();
  const headHelmet =
    helmet.meta.toString() + helmet.title.toString() + helmet.link.toString();

  return `<!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <link rel="icon" href="/favicon.png" />
                <meta name="theme-color" content="#000000" />
                <link rel="apple-touch-icon" href="/logo192.png" />
                <link rel="manifest" href="/manifest.json" />
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "WebSite",
                  "url": "https://ecency.com/",
                  "potentialAction": {
                    "@type": "SearchAction",
                    "target": "https://ecency.com/search/?q={search_term_string}",
                    "query-input": "required name=search_term_string"
                  }
                }
                </script>
                ${headHelmet}
                ${
                  assets.client.css
                    ? `<link rel="stylesheet" href="${assets.client.css}">`
                    : ""
                }
                ${
                  process.env.NODE_ENV === "production"
                    ? `<script src="${assets.client.js}" defer></script>`
                    : `<script src="${assets.client.js}" defer crossorigin></script>`
                }
            </head>
            <body>
                <div id="root">${markup}</div>
            </body>
        </html>`;
};
