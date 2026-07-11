# [Ecency vision][ecency_vision] – API

The API proxy service behind [Ecency](https://ecency.com) — routes search,
auth, wallet/portfolio, and private-API traffic to the right backends with
health-aware failover across Hive RPC nodes.

Implemented in **C# / ASP.NET Core (.NET 10)** under [`dotnet/`](dotnet/). It
replaced the original Node/Express implementation as a verified drop-in
(byte-identical responses across a 300-case differential parity suite; the
history of that migration is in the git log and `dotnet/README.md`). The last
Node build remains available as the `ecency/api:node-legacy` image tag.

## Quick start

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
(or just Docker).

```bash
git clone https://github.com/ecency/vision-api
cd vision-api/dotnet

dotnet build EcencyApi/EcencyApi.csproj              # compile
dotnet test  EcencyApi.Tests/EcencyApi.Tests.csproj  # test suite

API_PORT=4000 PRIVATE_API_ADDR=... dotnet run --project EcencyApi -c Release
curl http://localhost:4000/healthcheck.json
```

Or with Docker:

```bash
cd dotnet
docker build -t ecency/api -f Dockerfile .
docker run -it --rm -p 4000:4000 \
  -e PRIVATE_API_ADDR=https://api.example.com \
  -e PRIVATE_API_AUTH=verysecret \
  ecency/api
```

## Environment variables

| variable | purpose |
|---|---|
| `API_PORT` | listen port (default `4000`) |
| `PRIVATE_API_ADDR` | private api endpoint |
| `PRIVATE_API_AUTH` | private api auth (base64-encoded JSON header object) |
| `HIVESIGNER_SECRET` | hivesigner client secret |
| `SEARCH_API_ADDR` | hivesearcher api endpoint |
| `SEARCH_API_SECRET` | hivesearcher api auth token |
| `STRIPE_INTERNAL_SECRET` | shared secret for the Stripe money endpoints (unset = they fail closed) |
| `TURNSTILE_SECRET` | Cloudflare Turnstile secret for account-create captcha |
| `CAPTCHA_MODE` | `hard` (default) or `off` (operator break-glass) |
| `BLOCKSTREAM_CLIENT_ID` / `BLOCKSTREAM_CLIENT_SECRET` | optional Blockstream Enterprise esplora auth (BTC fallback) |
| `HELIUS_API_KEY` | optional Helius API key added as an extra Solana RPC fallback |
| `ETH_RPC_URLS` / `BNB_RPC_URLS` / `SOL_RPC_URLS` / `BTC_ESPLORA_URLS` | optional comma-separated endpoint lists overriding the built-in chain provider pools |
| `Logging__LogLevel__Default` | log level (default `Warning`; set `Information` for per-request logs) |

## Swarm

Deploy with the example stack file (which also bounds container log size):

```bash
cd dotnet
docker stack deploy -c docker-compose.yml vision-api
```

## Deployment & rollback

CI (`.github/workflows/main.yml`) tests every PR; on merge to main it builds
the image, pushes `ecency/api:latest` + `ecency/api:sha-<commit>`, and rolls
out by immutable digest. Roll back by redeploying any previous tag:

```bash
docker service update --image ecency/api:sha-<previous-commit> vision_vapi
docker service update --image ecency/api:node-legacy vision_vapi   # pre-rewrite Node build
```

## Pushing new code / Pull requests

- Branch off your changes from the `main` branch.
- Run `dotnet test dotnet/EcencyApi.Tests/EcencyApi.Tests.csproj` and add tests
  for your changes (the test suite gates every PR in CI).
- Code on!

## More

`dotnet/README.md` has the full details: architecture and layout, the Hive RPC
failover design, the differential parity harness, benchmark methodology and
results, and how to regenerate the crypto golden vectors.

## Issues

To report a non-critical issue, please file an issue on this GitHub project.

[//]: # 'LINKS'
[ecency_vision]: https://github.com/ecency/vision
