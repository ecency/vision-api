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

## Node failover (health-aware, adopted from @ecency/sdk)

`HiveRpcClient` keeps the dhive `Client` parameters the Node service used
(`timeout: 2000`, `failoverThreshold: 2`) but replaces dhive's simple ring
failover with a health tracker adopted from the vision-next SDK's
`NodeHealthTracker` (simplified for a proxy's call rates):

- **Per-node health state**: consecutive failures, rate-limit parking, and a
  latency EWMA order the pool best-first on every call.
- **429 parks the node** for the server's `Retry-After` when present, else an
  escalating window (10s doubling to 60s max; the streak resets after 120s
  without a throttle). Parked nodes sort last but remain a final resort.
- **Recent failures deprioritize** (30s window) — one bad response moves
  traffic away without banning the node; it re-enters when the window lapses.
- **Latency EWMA ranking** (alpha 0.3, trusted after 3 samples, stale after
  5 min): a proven-slow node (>1s) is demoted behind unexplored nodes; config
  order breaks ties so cold start behaves like the configured list.
- **Overload responses** (429/502/503/504) advance to the next node
  immediately — no wasted local retry on a throttled node.
- **RPC-level errors** (a JSON `error` field) surface without failover — an
  application error, not an unhealthy node (matches dhive).

Not adopted from the SDK (overkill at proxy call rates, documented for later:
request hedging, per-API failure profiles, head-block staleness checks).

The chain-balance providers (`ChainProviders.cs` / `PrivateApi.Chain.cs`) have
their own equivalent provider-pool failover for the EVM/Solana/BTC endpoints.

Covered by `EcencyApi.Tests/HiveRpcFailoverTests.cs` (rate-limit parking,
failure deprioritization, timeout rollover, EWMA demotion, all-down error).

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

**2. Differential HTTP parity** (`parity/`): fires an identical catalog of 301
request variants (every route × empty/populated/bad-auth bodies, plus error and
fallback probes) at the running Node image and the C# build, then diffs status,
content-type, and body.

The request catalog is generated from the route table in
`EcencyApi/Handlers/Routes.cs` (override with `VAPI_ROUTES_CS`, or point
`VAPI_INDEX_TSX` at a legacy Express `index.tsx`). The Node reference is the
`ecency/api:node-legacy` image — run it with a **restart policy**
(`--restart unless-stopped`): the legacy build crashes outright on the
malformed hs-token-refresh probe, and without auto-restart the remainder of
the capture fails.

```bash
# 1. mock upstream (records every proxied request)
python3 parity/mock_upstream.py &

# 2. run the node-legacy reference and the C# build against the same mock
#    (see parity/ for env) — Node on :14000, C# on :14001

# 3. capture + diff  (node2 = second Node run, marks nondeterministic cases loose)
python3 parity/driver.py run node   http://127.0.0.1:14000
python3 parity/driver.py run node2  http://127.0.0.1:14000
python3 parity/driver.py run csharp http://127.0.0.1:14001
python3 parity/driver.py diff node csharp node2
```

Latest result: **0 unexplained mismatches / 301 cases** (the intentional
divergences listed above are recorded in the harness). A handful of cases compare "loose"
(status + content-type only) because they are inherently nondeterministic —
timestamped HiveSigner tokens, random promoted-entry shuffles, and live
portfolio data. The full `portfolio-v2` aggregation was additionally compared
field-by-field against the Node output for a real account and matches to full
double precision (HP/vesting math, delegation adjustments, APR, prices).

## Measured performance vs the Node build

Both services benchmarked side by side on the same host against the same local
upstream stub (identical ~1.6KB JSON responses), autocannon, 10s runs after
warmup, zero errors in every run. "Constrained" applies the production stack
limits (0.9 CPU / 2GB) to both via cgroups.

| scenario | node req/s | C# req/s | node p50/p99 | C# p50/p99 | C# vs node |
|---|---|---|---|---|---|
| health (no upstream), c=64 | 3,556 | 28,520 | 16 / 31 ms | 2 / 4 ms | **8.0x** |
| proxied POST, c=64 | 995 | 16,589 | 63 / 96 ms | 3 / 11 ms | **16.7x** |
| proxied POST, c=8 | 1,052 | 13,601 | 7 / 14 ms | <1 / 1 ms | **12.9x** |
| hs-token-create (secp256k1), c=16 | 2,324 | 14,563 | 6 / 16 ms | <1 / 3 ms | **6.3x** |
| **constrained 0.9 CPU:** health c=64 | 2,318 | 9,596 | 20 / 81 ms | 3 / 80 ms | **4.1x** |
| **constrained 0.9 CPU:** proxied POST c=64 | 606 | 1,838 | 93 / 237 ms | 23 / 107 ms | **3.0x** |

Memory (RSS): Node ~45 MiB idle / ~49 MiB under load; C# ~76 MiB idle /
~100-155 MiB under load (Server GC trades memory for throughput; it sizes to
the cgroup limit in a container). Both fit trivially in the 2GB stack limit.

Notes on why the gap is big: Kestrel uses all cores (Node is one event loop),
`SocketsHttpHandler` pools upstream connections (the Node service opens a new
TCP connection per proxied request — Node 16 default agent has keep-alive off),
and there's no Express middleware overhead. The constrained rows are the
prod-realistic numbers: **~3x throughput and ~2-4x lower p50 latency at the
same CPU budget**.

## Known intentional divergences

- **`/auth-api/hs-token-refresh` with a missing `code`.** The Node handler calls
  `code.replace(...)` on `undefined`, throwing inside an async Express handler —
  an unhandled rejection that leaves the request hanging with no response. The
  C# port returns `401 Unauthorized` instead.
- **`/private-api/request-delete`** now returns the account-deletion
  acknowledgment stub (200 `{status, body}`). Hive accounts cannot be deleted
  on-chain; the endpoint exists to satisfy the app-store account-deletion
  requirement, but the old route table pointed it at the report handler, whose
  validation rejected the mobile payload with 400.
- **`/private-api/post-reblogs` and `/private-api/post-reblog-count` were
  removed.** They had no client callers and no traffic, and read `:author` /
  `:permlink` route params their POST routes never declared, so they always
  queried `undefined/undefined` upstream.

All three are recorded in the parity harness (`KNOWN_DIVERGENCES` /
route-table-driven catalog).

## Remaining edges (low-risk, documented)

- `Intl.NumberFormat()` and `toFixed(3)` HP/LP display strings in the portfolio
  `extraData` use an en-US formatter; these only appear on the live-data
  portfolio endpoints (loose-compared) and were validated to match on real data.
- Duplicate query-string keys are forwarded first-value-wins (Express would
  array-serialize); none of the proxied endpoints use repeated params.

## Getting started

### Prerequisites

.NET 10 SDK (only for building from source — the Docker image needs nothing
but Docker). Install on Linux with Microsoft's install script:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet
export PATH="$HOME/.dotnet:$PATH"
dotnet --version   # 10.0.x
```

Or grab an installer from https://dotnet.microsoft.com/download/dotnet/10.0.

### Build and test

Run from this `dotnet/` directory:

```bash
dotnet build EcencyApi/EcencyApi.csproj          # compile
dotnet test  EcencyApi.Tests/EcencyApi.Tests.csproj   # 40 tests: crypto vectors, failover, JS semantics
```

### Run locally

Configuration comes entirely from environment variables — the same set, with
the same defaults, as the Node service (`Config.cs` mirrors `src/config.ts`):

| variable | purpose | default |
|---|---|---|
| `API_PORT` | listen port | `4000` |
| `PRIVATE_API_ADDR` | internal private API base URL | placeholder |
| `PRIVATE_API_AUTH` | base64-encoded JSON object of auth headers for the private API | placeholder |
| `HIVESIGNER_SECRET` | HiveSigner OAuth client secret | placeholder |
| `SEARCH_API_ADDR` / `SEARCH_API_SECRET` | search backend + token | placeholder |
| `STRIPE_INTERNAL_SECRET` | shared secret for the Stripe money endpoints; unset = those routes fail closed with 503 | unset |
| `TURNSTILE_SECRET` | Cloudflare Turnstile secret for account-create captcha | unset |
| `CAPTCHA_MODE` | `hard` (default) or `off` (operator break-glass) | `hard` |
| `BLOCKSTREAM_CLIENT_ID` / `BLOCKSTREAM_CLIENT_SECRET` | optional Blockstream enterprise esplora auth | unset |
| `HELIUS_API_KEY` | optional extra Solana RPC provider | unset |
| `ETH_RPC_URLS` / `BNB_RPC_URLS` / `SOL_RPC_URLS` / `BTC_ESPLORA_URLS` | comma-separated overrides for the chain provider pools | built-in pools |
| `PUBLIC_DIR` | static assets directory | `<app>/public` |
| `Logging__LogLevel__Default` | log level; unset defaults to `Warning` (set `Information` for per-request logs) | `Warning` |

```bash
API_PORT=4000 \
PRIVATE_API_ADDR=https://example.com/api \
PRIVATE_API_AUTH=$(printf '{"Authorization":"..."}' | base64 -w0) \
HIVESIGNER_SECRET=... \
SEARCH_API_ADDR=https://search.example.com \
SEARCH_API_SECRET=... \
dotnet run --project EcencyApi -c Release
```

Verify it's up:

```bash
curl http://localhost:4000/healthcheck.json
# {"status":200,"body":{"status":"ok"}}
```

### Run in Docker (drop-in for the Node image)

The image keeps the Node build's contract: port 4000, the same env vars, and a
built-in `HEALTHCHECK` that polls `/healthcheck.json`.

```bash
docker build -t ecency/api-csharp -f Dockerfile .

docker run -d --name vapi-csharp -p 4000:4000 \
  -e PRIVATE_API_ADDR=... -e PRIVATE_API_AUTH=... \
  -e HIVESIGNER_SECRET=... \
  -e SEARCH_API_ADDR=... -e SEARCH_API_SECRET=... \
  ecency/api-csharp
```

For swarm, `docker-compose.yml` in this directory is a drop-in equivalent of
the repo-root stack file (same service shape, ports, env list, and deploy
policy) pointing at the C# image.

### Deployment tags & rollback

CI (`.github/workflows/main.yml`) tests, then builds this Dockerfile on every
merge to main and pushes it as both `ecency/api:latest` and
`ecency/api:sha-<commit>`, deploying by immutable digest. Rollback options:

```bash
# roll back to any previous build (every merge is tagged)
docker service update --image ecency/api:sha-<previous-commit> vision_vapi

# roll all the way back to the last Node build (preserved once, before the
# first C# image overwrote :latest)
docker service update --image ecency/api:node-legacy vision_vapi
```

The deploy hosts prune unused local images after each rollout, so rollback
pulls from the registry — which is why every build gets a durable tag.

### Publish a self-contained build (no Docker)

```bash
dotnet publish EcencyApi/EcencyApi.csproj -c Release -o /opt/vapi-csharp
API_PORT=4000 ... dotnet /opt/vapi-csharp/EcencyApi.dll
```

### Regenerate crypto golden vectors

Only needed if the dhive dependency of the Node service changes:

```bash
# the repo no longer carries Node dependencies; install the two packages anywhere
(mkdir -p /tmp/vectors && cd /tmp/vectors && npm install @hiveio/dhive js-base64)
# then run from this dotnet/ directory:
VAPI_NODE_MODULES=/tmp/vectors/node_modules \
  node tools/gen-vectors.js > EcencyApi.Tests/fixtures/crypto-vectors.json
dotnet test EcencyApi.Tests/EcencyApi.Tests.csproj
```
