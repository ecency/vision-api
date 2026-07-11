# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

vision-api is Ecency's API proxy service: it routes search, auth, wallet/portfolio, and private-API traffic to the right backends, validates HiveSigner-style signed tokens, and talks to Hive RPC nodes with health-aware failover.

It is a **C# / ASP.NET Core (.NET 10) minimal API** under `dotnet/`. It replaced a Node/Express implementation in July 2026 as a verified drop-in (byte-identical responses); the last Node build is preserved as the `ecency/api:node-legacy` image. Many implementation choices below exist to preserve that observable behavior — treat them as contracts, not accidents.

## Commands

Run from `dotnet/`:

```bash
dotnet build EcencyApi/EcencyApi.csproj                 # compile
dotnet test  EcencyApi.Tests/EcencyApi.Tests.csproj     # full test suite (gates every PR in CI)
dotnet run --project EcencyApi -c Release               # run locally (env vars in README.md)
docker build -t ecency/api -f Dockerfile .              # container build
```

## Deployment — read before pushing

**Merging to `main` deploys to production.** The workflow (`.github/workflows/main.yml`) runs the test suite, builds `dotnet/Dockerfile`, pushes `ecency/api:latest` plus a per-commit `ecency/api:sha-<commit>` tag, and rolls out by immutable digest to all regions with converge checks. There is no staging gate in this repo — the ALPHA region tracks main alongside prod.

- PRs get the test gate but do not build/deploy.
- Rollback: `docker service update --image ecency/api:sha-<commit> vision_vapi` (or `ecency/api:node-legacy` for the pre-rewrite build).
- This repo is **public**: never put hostnames, IPs, secrets, traffic numbers, or other infrastructure details in code, comments, PR descriptions, or commit messages.

## Architecture

```
dotnet/EcencyApi/
  Program.cs               host, error middleware (500 "Server Error"), static files, quiet logging
  Config.cs                env-var configuration (same names/defaults as the old Node config)
  Handlers/
    Routes.cs              THE route table — every endpoint maps here, exact-match paths
    SearchApi.cs, AuthApi.cs, PrivateApi.*.cs, WalletApi.*.cs   (static partial classes)
    Announcements.cs       static announcement data (edit + merge to main to publish)
    HiveExplorer.cs        global-props/account helpers used by the portfolio endpoints
  Infrastructure/
    Upstream.cs            upstream HTTP + Express-compatible response piping
    ApiClient.cs           private-API requests (PRIVATE_API_AUTH header injection)
    HiveRpcClient.cs       Hive RPC with health-aware node failover
    HiveCrypto.cs          secp256k1: key-from-login, canonical signing, pubkey recovery
    JsJson.cs / JsVal.cs   JavaScript-semantics layer (see invariants)
    B64u.cs, MemCache.cs, HttpContextExtensions.cs
  Models/HiveEngine.cs     Hive-Engine payload converters
dotnet/EcencyApi.Tests/    xUnit: crypto golden vectors, failover, JS semantics
dotnet/parity/             differential HTTP parity harness (vs a reference image)
dotnet/tools/gen-vectors.js  regenerates crypto golden vectors from dhive
```

Handlers are `public static async Task Name(HttpContext ctx)` methods on static partial classes, wired 1:1 in `Routes.cs`. Dynamic JSON uses `System.Text.Json.Nodes` (`JsonNode`/`JsonObject`/`JsonArray`) throughout — do not introduce POCO serialization for proxied payloads.

## Critical invariants — do not "fix" these

1. **`JsJson.Stringify` output gets hashed and signed** (HiveSigner token create/validate). It is byte-exact with V8's `JSON.stringify`: property insertion order, JS string escaping, and ECMA number formatting (fixed notation for decimal exponents in (-6, 21], scientific outside). Any change must pass the golden-vector tests.
2. **`JsVal` deliberately mirrors JavaScript coercion semantics** — `Number("")` is 0 while `parseFloat("")` is NaN, truthiness treats 0/""/NaN as false, etc. The wallet/portfolio math depends on these. They are tested; don't normalize them to idiomatic C#.
3. **Express-compatible response behavior is a contract**: strings send as `text/html`, a bare JSON number sends as its string form, a JSON `null` upstream body sends as an empty body with no content-type, malformed request JSON yields 500 "Server Error", unmatched GETs get the 200 template page while other methods get the 404 finalhandler page.
4. **Upstream requests always carry a `{}` JSON body** (even GETs) with bare `application/json` content type — this matches what upstreams have always received (verified against recorded production traffic). Review bots repeatedly flag this; it is correct.
5. **`get_accounts` with a missing username must serialize as JSON `[null]`**, never the string `"null"` — `@null` is a real Hive account.
6. **No hot-path logging.** The service defaults to Warning level and writes ~3 lines at startup; container logs are size-capped but must stay quiet. Don't add `Console.WriteLine` or Information-level logging to request paths.

## Hive RPC failover

`HiveRpcClient` uses a per-node health tracker (adopted from the vision-next SDK): 429 responses park a node for `Retry-After` (or an escalating window), recent failures deprioritize it, and a latency EWMA orders the pool best-first with config order as tiebreak. RPC-level errors (JSON `error` field) surface immediately without failover — they're application errors, not node health. Covered by `HiveRpcFailoverTests`; keep new failover behavior test-backed.

## Testing

- `dotnet test` runs everything; CI requires it green on every PR.
- **Crypto golden vectors** (`EcencyApi.Tests/fixtures/crypto-vectors.json`) are generated from the real `@hiveio/dhive` by `tools/gen-vectors.js` (needs a directory with `@hiveio/dhive` + `js-base64` installed; see `dotnet/README.md`). If you touch `HiveCrypto`, `JsJson`, or `B64u`, the vector tests are the source of truth.
- **Parity harness** (`dotnet/parity/`): replays a catalog generated from `Routes.cs` against a reference image and the local build, diffing status/content-type/body. Intentional behavior changes must be added to `KNOWN_DIVERGENCES` in `driver.py`. If using the `node-legacy` image as reference, run it with `--restart unless-stopped` (it crashes on one of the malformed-input probes).

## Conventions

- Branch from `main`; PRs get automated bot reviews — verify bot claims before acting on them (several recurring bot findings here are wrong; see invariant 4).
- Behavior changes to endpoints need: the route/handler change, a parity `KNOWN_DIVERGENCES` entry when applicable, and a test.
- Static data changes (e.g. announcements) are code changes: edit the handler data, merge to main, CI ships it.
- Keep comments explaining *why* a quirk exists (usually "matches the original observable behavior") — they prevent well-meaning regressions.
