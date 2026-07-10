# vision-api — C# / ASP.NET Core port

A behavior-preserving rewrite of the Node/Express `vision-api` proxy in C#
(.NET 10, ASP.NET Core minimal API). It is a **drop-in replacement**: same
port (4000), same `/healthcheck.json` contract, same environment variables, and
byte-for-byte identical HTTP responses across the deterministic request surface.

## Why C#

`vision-api` is a thin, latency-sensitive proxy in front of the internal API,
the search backend, Hive RPC nodes, and various chain/engine endpoints. ASP.NET
Core's Kestrel + `SocketsHttpHandler` connection pooling, real multithreading
(no single event-loop bottleneck), and lower/steadier GC give it more headroom
per replica than the single-threaded Node build. Nothing here is CPU-bound; the
win is a faster, more predictable proxy under concurrent load.

## Layout

```
dotnet/
  EcencyApi/                 the service
    Program.cs               host, middleware, static files, error handler
    Config.cs                mirror of src/config.ts (same env vars + defaults)
    Handlers/
      Routes.cs              1:1 map of src/server/index.tsx route table
      SearchApi.cs           search-api.ts
      AuthApi.cs             auth-api.ts (HiveSigner code create/refresh)
      PrivateApi.*.cs        private-api.ts, split by concern
      WalletApi.*.cs         wallet-api.ts (market helpers, portfolio layers, engine)
      Announcements.cs / Spotlights.cs / HiveExplorer.cs / Constants.cs / ChainProviders.cs
    Infrastructure/
      Upstream.cs            axios baseApiRequest + Express pipe() parity
      ApiClient.cs           helper.ts apiRequest (PRIVATE_API_AUTH header injection)
      HiveRpcClient.cs       dhive-style Client with node failover (see below)
      HiveCrypto.cs          dhive crypto: key-from-login, canonical ECDSA, recovery
      JsJson.cs              JS-identical JSON.stringify (for signed messages)
      JsVal.cs               JS parseFloat / Number / typeof coercions
      B64u.cs, MemCache.cs, HttpContextExtensions.cs
    Models/HiveEngine.cs     hiveEngine.types.ts + converters.ts
  EcencyApi.Tests/           xUnit: crypto golden vectors, failover, JS semantics
  parity/                    differential HTTP parity harness (Node vs C#)
  tools/gen-vectors.js       regenerates crypto golden vectors from dhive
  Dockerfile                 drop-in image build
```

## Node failover (dhive parity + rate-limit handling)

`HiveRpcClient` reproduces the dhive `Client` behavior the Node service relies
on (`timeout: 2000`, `failoverThreshold: 2`) and adds explicit rate-limit
handling:

- Requests walk the node ring from a **sticky index** (the last node that
  answered), so a healthy node keeps serving subsequent calls.
- **Transient failures** (timeout, network error, 5xx, non-JSON body) get up to
  `failoverThreshold` attempts on a node before advancing.
- **Rate-limit / overload** responses (429, 502, 503, 504) advance to the next
  node **immediately** — a throttled node won't recover in the few ms a local
  retry would take.
- **RPC-level errors** (a JSON `error` field) surface without failover — that's
  a real application error, not an unhealthy node (matches dhive).

The chain-balance providers (`ChainProviders.cs` / `PrivateApi.Chain.cs`) have
their own equivalent provider-pool failover for the EVM/Solana/BTC endpoints.

Covered by `EcencyApi.Tests/HiveRpcFailoverTests.cs` (rate-limit rollover,
sticky node, timeout rollover, all-down error).

## Verifying identical responses

Two layers of verification:

**1. Unit tests** (`dotnet test`, 40 tests):
- Crypto is checked **byte-for-byte against dhive**. `tools/gen-vectors.js` runs
  the exact `@hiveio/dhive` the Node service uses and emits golden vectors
  (key-from-login, canonical ECDSA signatures, public-key recovery, the full
  `hs-token-create` flow, and `validateCode` re-serialization);
  `HiveCryptoTests` asserts the C# output matches.
- `JsValTests` pin the JS coercion edge cases (`Number("") === 0` but
  `parseFloat("")` is `NaN`, etc.) that the wallet numeric parity depends on.
- `HiveRpcFailoverTests` exercise the failover against local stub nodes.

**2. Differential HTTP parity** (`parity/`): fires an identical catalog of 305
request variants (every route × empty/populated/bad-auth bodies, plus error and
fallback probes) at the running Node image and the C# build, then diffs status,
content-type, and body.

```bash
# 1. mock upstream (records every proxied request)
python3 parity/mock_upstream.py &

# 2. run Node reference and C# build against the same mock (see parity/ for env)
#    Node on :14000, C# on :14001

# 3. capture + diff  (node2 = second Node run, marks nondeterministic cases loose)
python3 parity/driver.py run node   http://127.0.0.1:14000
python3 parity/driver.py run node2  http://127.0.0.1:14000
python3 parity/driver.py run csharp http://127.0.0.1:14001
python3 parity/driver.py diff node csharp node2
```

Latest result: **0 real mismatches / 305 cases.** 6 cases compare "loose"
(status + content-type only) because they are inherently nondeterministic —
timestamped HiveSigner tokens, random promoted-entry shuffles, and live
portfolio data. The full `portfolio-v2` aggregation was additionally compared
field-by-field against the Node output for a real account and matches to full
double precision (HP/vesting math, delegation adjustments, APR, prices).

## Known intentional divergences

- **`/auth-api/hs-token-refresh` with a missing `code`.** The Node handler calls
  `code.replace(...)` on `undefined`, throwing inside an async Express handler —
  an unhandled rejection that leaves the request hanging with no response. The
  C# port returns `401 Unauthorized` instead. This is the single deliberate
  behavior change (a latent Node bug); it's recorded in the parity harness.

## Remaining edges (low-risk, documented)

- `Intl.NumberFormat()` and `toFixed(3)` HP/LP display strings in the portfolio
  `extraData` use an en-US formatter; these only appear on the live-data
  portfolio endpoints (loose-compared) and were validated to match on real data.
- Duplicate query-string keys are forwarded first-value-wins (Express would
  array-serialize); none of the proxied endpoints use repeated params.

## Build & run

```bash
dotnet build EcencyApi/EcencyApi.csproj
dotnet test  EcencyApi.Tests/EcencyApi.Tests.csproj
API_PORT=4000 PRIVATE_API_ADDR=... dotnet run --project EcencyApi

# container (same image contract as the Node build)
docker build -t ecency/api-csharp -f Dockerfile .
```

Environment variables are unchanged from the Node service (see `Config.cs` /
`docker-compose.yml`): `PRIVATE_API_ADDR`, `PRIVATE_API_AUTH`,
`HIVESIGNER_SECRET`, `SEARCH_API_ADDR`, `SEARCH_API_SECRET`,
`STRIPE_INTERNAL_SECRET`, `TURNSTILE_SECRET`, `CAPTCHA_MODE`,
`BLOCKSTREAM_CLIENT_ID/SECRET`, `HELIUS_API_KEY`,
`ETH_RPC_URLS`, `BNB_RPC_URLS`, `SOL_RPC_URLS`, `BTC_ESPLORA_URLS`.
