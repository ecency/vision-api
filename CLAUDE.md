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
    NodeHealthTracker.cs   per-node health state + pool ordering (shared by the RPC clients)
    HiveRpcClient.cs       Hive RPC with health-aware node failover
    EngineRpcClient.cs     Hive-Engine /contracts with the same failover
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
7. **Numbers are JavaScript doubles end to end — raw-literal preservation is NOT a goal.** The Node service parsed every request body and upstream response with `JSON.parse` (doubles) and re-emitted with `JSON.stringify`, so integers above 2^53 have always rounded on these paths (e.g. `18446744073709551615` → `18446744073709552000`). `JsJson.Stringify` reproduces this byte-for-byte on purpose. Do not flag double-rounding on payload/response serialization as precision loss, and do not "fix" it by preserving raw literals — either direction breaks parity. The single deliberate exception is the Solana lamports raw-text scan (`PrivateApi.Chain`), which keeps `ToJsonString` because it extracts a u64 from raw text before any parse.
8. **Lone-surrogate `\u` escapes are valid input and output.** JavaScript strings are arbitrary UTF-16, so `JSON.parse`/`JSON.stringify` accept and re-emit lone surrogates; System.Text.Json throws on them in BOTH directions (string materialization AND the writer, regardless of encoder). All string extraction must go through `JsVal.TryGetStringLenient` and all tree serialization of client/upstream data through `JsJson.Stringify` — never raw `GetValue<string>`/`TryGetValue<string>` or `ToJsonString` on such data. Suggesting a switch back to the strict System.Text.Json APIs reintroduces production 500s.

## Upstream node failover

`NodeHealthTracker` (adopted from the vision-next SDK) holds per-node health state: 429 responses park a node for `Retry-After` (or an escalating window), recent failures deprioritize it, and a latency EWMA orders the pool best-first with config order as tiebreak. Two clients build on it:

- `HiveRpcClient` (Hive JSON-RPC): RPC-level errors (JSON `error` field) surface immediately without failover — they're application errors, not node health. The typed helpers additionally validate the result *shape* (`get_accounts` → array, `get_dynamic_global_properties` → object): a 200 with valid JSON but no usable result is a node failure that fails over — without this, a node serving malformed 200s is recorded as healthy and stays ranked first (observed in production as multi-hour windows of token-validation 401s).
- `EngineRpcClient` (Hive-Engine): one instance per pool — the `/contracts` RPC pool and the history-API pool. The portfolio `Find` calls are fixed-shape queries that always yield a `result` array on a healthy node, so an error payload or non-JSON body *is* a node failure and rolls over to the next node. The raw passthroughs (`engine-api`, `engine-account-history`) fail over only on transport errors and 429/5xx; other responses belong to the caller's query and pipe as-is.

Covered by `HiveRpcFailoverTests` and `EngineFailoverTests`; keep new failover behavior test-backed.

## Testing

- `dotnet test` runs everything; CI requires it green on every PR.
- **Crypto golden vectors** (`EcencyApi.Tests/fixtures/crypto-vectors.json`) are generated from the real `@hiveio/dhive` by `tools/gen-vectors.js` (needs a directory with `@hiveio/dhive` + `js-base64` installed; see `dotnet/README.md`). If you touch `HiveCrypto`, `JsJson`, or `B64u`, the vector tests are the source of truth.
- **Parity harness** (`dotnet/parity/`): replays a catalog generated from `Routes.cs` against a reference image and the local build, diffing status/content-type/body. Intentional behavior changes must be added to `KNOWN_DIVERGENCES` in `driver.py`. If using the `node-legacy` image as reference, run it with `--restart unless-stopped` (it crashes on one of the malformed-input probes).

## Conventions

- Branch from `main`; PRs get automated bot reviews — verify bot claims before acting on them (several recurring bot findings here are wrong; see invariant 4).
- Behavior changes to endpoints need: the route/handler change, a parity `KNOWN_DIVERGENCES` entry when applicable, and a test.
- Static data changes (e.g. announcements) are code changes: edit the handler data, merge to main, CI ships it.
- Keep comments explaining *why* a quirk exists (usually "matches the original observable behavior") — they prevent well-meaning regressions.

## Attributing a `/private-api/*` 401

A 401 on a private-API route does not necessarily come from this service. These
handlers resolve the username from the signed code and then **pipe an upstream
response through unchanged**, so a 401 returned by the backend behind the
gateway reaches the client looking identical to one this service produced.

The response body length is the quickest way to tell them apart: this service
answers `SendText(401, "Unauthorized")`, a 12-byte body. A longer body almost
always means the 401 was piped from further upstream.

Two cheap checks before assuming a token problem:

- **Compare against another private-API route that shares `ValidateCode`.** If
  notifications succeed while one endpoint 401s for the same session, the code
  validated fine and the failure is downstream of this service. Route-specific
  auth on the backend, not the caller's token, is then the thing to look at.
- **Check the failure rate.** A token or session problem affects a subset of
  users; a backend gate that is misconfigured fails for everyone. An endpoint at
  100% 401 with siblings at 100% success is not an authentication bug in the
  usual sense.

Handlers using `RequireAuthedUsername` differ from those calling `ValidateCode`
directly only in that the former sends the 401 for you - the validation is the
same, so it is never the explanation for one route failing while another passes.
