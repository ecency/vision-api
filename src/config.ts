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
    stripeInternalSecret: process.env.STRIPE_INTERNAL_SECRET
};
