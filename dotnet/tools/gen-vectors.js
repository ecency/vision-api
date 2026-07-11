/**
 * Golden test-vector generator for the C# port.
 *
 * Runs against the exact dhive/js-base64 versions the Node service uses, so the
 * C# HiveCrypto implementation can be verified byte-for-byte:
 *   node dotnet/tools/gen-vectors.js > dotnet/EcencyApi.Tests/fixtures/crypto-vectors.json
 *
 * All credentials below are synthetic — never put real account data here.
 */
const path = require("path");
const moduleRoot = process.env.VAPI_NODE_MODULES || path.resolve(__dirname, "../../node_modules");
const { PrivateKey, Signature, cryptoUtils } = require(path.join(moduleRoot, "@hiveio/dhive"));
const { Base64 } = require(path.join(moduleRoot, "js-base64"));

const b64uLookup = { "/": "_", _: "/", "+": "-", "-": "+", "=": ".", ".": "=" };
const b64uEnc = (str) => Base64.encode(str).replace(/(\+|\/|=)/g, (m) => b64uLookup[m]);

const logins = [
    { username: "alice", password: "P5JRFhxvW9zZ1QqWzSp6ZoPhq6yGKPrM", role: "posting" },
    { username: "bob.tester", password: "hunter2hunter2", role: "posting" },
    { username: "carol-x1", password: "correct horse battery staple", role: "active" },
    { username: "dave", password: "🔑 unicode pässwörd", role: "posting" },
    { username: "e", password: "x", role: "memo" },
];

const messages = [
    "hello world",
    "",
    "a",
    JSON.stringify({ signed_message: { type: "code", app: "ecency.app" }, authors: ["alice"], timestamp: 1751900000 }),
    "The quick brown fox jumps over the lazy dog",
    "unicode: ñçü 漢字 🚀",
    "x".repeat(1000),
];

const out = { fromLogin: [], sign: [], recover: [], b64u: [], hsTokenCreate: [] };

for (const l of logins) {
    const key = PrivateKey.fromLogin(l.username, l.password, l.role);
    out.fromLogin.push({
        ...l,
        wif: key.toString(),
        publicKey: key.createPublic().toString(),
    });
}

let i = 0;
for (const l of logins) {
    const key = PrivateKey.fromLogin(l.username, l.password, l.role);
    for (const m of messages) {
        // extra numbered variants so at least some vectors need >1 canonicality attempt
        for (const suffix of ["", ` #${i++}`, ` !${i++}`]) {
            const msg = m + suffix;
            const digest = cryptoUtils.sha256(msg);
            const sig = key.sign(digest);
            const sigStr = sig.toString();
            out.sign.push({
                username: l.username, password: l.password, role: l.role,
                message: msg,
                digestHex: digest.toString("hex"),
                signature: sigStr,
            });
            out.recover.push({
                signature: sigStr,
                digestHex: digest.toString("hex"),
                recoveredPublicKey: Signature.fromString(sigStr).recover(digest).toString(),
            });
        }
    }
}

for (const s of ["hello", "", "a", "ab", "abc", '{"json":true}', "unicode ñ 漢字 🚀", "??>>~~``", "x".repeat(300)]) {
    out.b64u.push({ input: s, encoded: b64uEnc(s) });
}

// Full hsTokenCreate replication with pinned timestamps (handler uses "now";
// tests inject the timestamp).
for (const [l, app, timestamp] of [
    [logins[0], "ecency.app", 1751900000],
    [logins[1], "ecency.app", 1700000001],
    [logins[3], "some.other.app", 1751912345],
]) {
    const messageObj = { signed_message: { type: "code", app }, authors: [`${l.username}`], timestamp };
    const hash = cryptoUtils.sha256(JSON.stringify(messageObj));
    const privateKey = PrivateKey.fromLogin(l.username, l.password, "posting");
    const signature = privateKey.sign(hash).toString();
    messageObj.signatures = [signature];
    out.hsTokenCreate.push({
        username: l.username, password: l.password, app, timestamp,
        signedJson: JSON.stringify(messageObj),
        code: b64uEnc(JSON.stringify(messageObj)),
    });
}


// validateCode-style re-serialization vectors: token JSON (as a client might
// b64u-encode it) -> the exact rawMessage Node re-serializes and hashes.
// Exercises nested key-order preservation, fractional timestamps, unicode.
const tokenJsons = [
    '{"signed_message":{"type":"code","app":"ecency.app"},"authors":["alice"],"timestamp":1751900000,"signatures":["ab"]}',
    '{"signed_message":{"app":"ecency.app","type":"code"},"authors":["bob.tester"],"timestamp":1751900000.123,"signatures":["cd"]}',
    '{"timestamp":1700000000.5,"signatures":["ef"],"signed_message":{"type":"login","app":"éçency 漢字"},"authors":["carol-x1"]}',
    '{"signed_message":{"type":"code","app":"x","extra":{"z":1,"a":[1.5,2e3,0.001]}},"authors":["dave","eve"],"timestamp":1e10,"signatures":["01"]}',
];
out.validateCodeRaw = tokenJsons.map((t) => {
    const decoded = JSON.parse(t);
    const { signed_message, authors, timestamp } = decoded;
    const rawMessage = JSON.stringify({ signed_message, authors, timestamp });
    const digest = cryptoUtils.sha256(rawMessage);
    return { tokenJson: t, rawMessage, digestHex: digest.toString("hex") };
});


// JS number-formatting vectors: JSON round-trip through V8 (the oracle for
// JsJson.FormatNumber's fixed/scientific thresholds).
const numberCases = [
    0, -0, 1, -1, 42, 1.5, -2.5, 0.1, 1e6, 123456789,
    1e20, 1e21, 5e21, -1e21, 1e-6, 0.000001, 1e-7, -1.5e-7, 0.0000015,
    123456789012345680000, 6.02e23, 1e-300, 1.7976931348623157e308,
    5e-324, 1751900000.123, 2 ** 53, 2 ** 53 + 2,
];
out.numberFormat = numberCases.map((v) => ({ value: v, text: JSON.stringify(v) }));

process.stdout.write(JSON.stringify(out, null, 1));
