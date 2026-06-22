/* !!! DO NOT IMPORT config.js TO FRONTEND CODE !!! */

export default {
    privateApiAddr: process.env.PRIVATE_API_ADDR || "https://domain.com/api",
    privateApiAuth: process.env.PRIVATE_API_AUTH || "privateapiauth",
    hsClientSecret: process.env.HIVESIGNER_SECRET || "hivesignerclientsecret",
    searchApiAddr: process.env.SEARCH_API_ADDR || "https://api.search.com",
    searchApiToken: process.env.SEARCH_API_SECRET || "searchApiSecret",
    // Shared secret sent to ePoints on the Stripe money endpoints (defence in depth on
    // top of network position). Must match STRIPE_INTERNAL_SECRET in the ePoints env.
    // NO default: when unset, the Stripe routes fail closed (503) rather than forward an
    // empty secret -- and without coupling the rest of vapi to this one secret.
    stripeInternalSecret: process.env.STRIPE_INTERNAL_SECRET,
    // Cloudflare Turnstile (server-side captcha) secret. The single verifier for the
    // account-create and paid-account-create routes; server-side only, never sent to clients.
    turnstileSecret: process.env.TURNSTILE_SECRET,
    // account-create captcha enforcement. "hard" (default) requires a valid token for every
    // request; "off" is an operator-only break-glass (e.g. a Turnstile provider outage), since
    // verification otherwise fails closed. The paid-account route always verifies regardless.
    captchaMode: (process.env.CAPTCHA_MODE || "hard").trim().toLowerCase()
};
