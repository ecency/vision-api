using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;
using EcencyApi.Models;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of wallet-api.ts lines 1748-2165: Hive-Engine node pool + failover, the
/// engine passthrough routes, the portfolio data fetchers, and the portfolio /
/// portfolioV2 orchestration (fail-fast leg timeouts, partial-degrade on error).
/// </summary>
public static partial class WalletApi
{
    private static readonly string[] EngineNodes =
    {
        "https://herpc.dtools.dev",
        "https://api.hive-engine.com/rpc",
        "https://ha.herpc.dtools.dev",
        "https://herpc.kanibot.com",
        "https://herpc.actifit.io",
    };

    // min and max included
    private static int RandomIntFromInterval(int min, int max) =>
        (int)Math.Floor(Random.Shared.NextDouble() * (max - min + 1) + min);

    private static string PickRandomEngineUrl() =>
        $"{EngineNodes[RandomIntFromInterval(0, EngineNodes.Length - 1)]}/contracts";

    // Module-level mutable base URL, reselected on engine failure (dhive-style
    // rotation across the pool). Volatile: read/written from concurrent requests.
    private static string _baseEngineUrl = PickRandomEngineUrl();
    private static string BaseEngineUrl
    {
        get => Volatile.Read(ref _baseEngineUrl);
        set => Volatile.Write(ref _baseEngineUrl, value);
    }

    private const string BaseSpkUrl = "https://spk.good-karma.xyz";
    private const string EngineRewardsUrl = "https://scot-api.hive-engine.com/";
    private const string EngineChartUrl = "https://info-api.tribaldex.com/market/ohlcv";
    private const string EngineAccountHistoryUrl = "https://history.hive-engine.com/accountHistory";

    private static readonly KeyValuePair<string, string>[] EngineHeaders =
    {
        new("Content-type", "application/json"),
        new("User-Agent", "Ecency"),
    };

    // ---- routes ----------------------------------------------------------

    public static async Task Eapi(HttpContext ctx)
    {
        var data = await ctx.ReadBody();
        await Upstream.Pipe(EngineContractsRequest(data, BaseEngineUrl), ctx);
    }

    public static async Task Erewardapi(HttpContext ctx)
    {
        var username = ctx.Request.RouteValues["username"]?.ToString() ?? "";
        var query = QueryOf(ctx);
        await Upstream.Pipe(EngineRewardsRequest(username, query), ctx);
    }

    public static async Task Echartapi(HttpContext ctx)
    {
        var query = QueryOf(ctx);
        await Upstream.Pipe(
            Upstream.BaseApiRequest(EngineChartUrl, HttpMethod.Get, EngineHeaders, null, query, 30000), ctx);
    }

    public static async Task EngineAccountHistory(HttpContext ctx)
    {
        var query = QueryOf(ctx);
        await Upstream.Pipe(
            Upstream.BaseApiRequest(EngineAccountHistoryUrl, HttpMethod.Get, EngineHeaders, null, query, 30000), ctx);
    }

    private static List<KeyValuePair<string, string?>> QueryOf(HttpContext ctx)
    {
        // Express req.query -> axios params. Single-valued forwarding; duplicate
        // query keys (rare for these endpoints) take the first value.
        var list = new List<KeyValuePair<string, string?>>();
        foreach (var kv in ctx.Request.Query)
        {
            list.Add(new KeyValuePair<string, string?>(kv.Key, kv.Value.Count > 0 ? kv.Value[0] : ""));
        }
        return list;
    }

    // ---- raw engine calls ------------------------------------------------

    private static Task<UpstreamResponse> EngineContractsRequest(JsonNode? data, string url) =>
        Upstream.BaseApiRequest(url, HttpMethod.Post, EngineHeaders, data, null, 8000);

    private static Task<UpstreamResponse> EngineRewardsRequest(string username, IEnumerable<KeyValuePair<string, string?>> query)
    {
        var url = $"{EngineRewardsUrl}/@{username}";
        return Upstream.BaseApiRequest(url, HttpMethod.Get, EngineHeaders, null, query, 30000);
    }

    private static JsonObject EngineFindPayload(string contract, string table, JsonObject query) => new()
    {
        ["jsonrpc"] = HiveEngine.JsonRpc2,
        ["method"] = HiveEngine.MethodFind,
        ["params"] = new JsonObject
        {
            ["contract"] = contract,
            ["table"] = table,
            ["query"] = query,
        },
        ["id"] = HiveEngine.IdOne,
    };

    private static JsonNode? ResultOf(UpstreamResponse r) => r.Json?["result"];

    public static async Task<JsonArray> FetchEngineBalances(string account)
    {
        var data = EngineFindPayload(HiveEngine.ContractTokens, HiveEngine.TableBalances,
            new JsonObject { ["account"] = account });
        try
        {
            var response = await EngineContractsRequest(data.DeepClone(), BaseEngineUrl);
            var result = ResultOf(response);
            if (result is not JsonArray a) throw new Exception("Failed to get engine balances");
            return a;
        }
        catch
        {
            BaseEngineUrl = PickRandomEngineUrl();
            var response = await EngineContractsRequest(data.DeepClone(), BaseEngineUrl);
            var result = ResultOf(response);
            if (result is not JsonArray a) throw new Exception("Failed to get engine balances");
            return a;
        }
    }

    public static async Task<JsonArray> FetchEngineTokens(List<string> tokens)
    {
        var query = new JsonObject { ["symbol"] = new JsonObject { ["$in"] = ToJsonArray(tokens) } };
        var data = EngineFindPayload(HiveEngine.ContractTokens, HiveEngine.TableTokens, query);
        try
        {
            var response = await EngineContractsRequest(data.DeepClone(), BaseEngineUrl);
            if (ResultOf(response) is not JsonArray a) throw new Exception("Failed to get engine tokens data");
            return a;
        }
        catch
        {
            BaseEngineUrl = PickRandomEngineUrl();
            var response = await EngineContractsRequest(data.DeepClone(), BaseEngineUrl);
            if (ResultOf(response) is not JsonArray a) throw new Exception("Failed to get engine tokens data");
            return a;
        }
    }

    public static async Task<JsonArray> FetchEngineMetrics(List<string> tokens)
    {
        var query = new JsonObject { ["symbol"] = new JsonObject { ["$in"] = ToJsonArray(tokens) } };
        var data = EngineFindPayload(HiveEngine.ContractMarket, HiveEngine.TableMetrics, query);
        try
        {
            var response = await EngineContractsRequest(data.DeepClone(), BaseEngineUrl);
            if (ResultOf(response) is not JsonArray a) throw new Exception("Failed to get engine metrics data");
            return a;
        }
        catch
        {
            BaseEngineUrl = PickRandomEngineUrl();
            var response = await EngineContractsRequest(data.DeepClone(), BaseEngineUrl);
            if (ResultOf(response) is not JsonArray a) throw new Exception("Failed to get engine metrics data");
            return a;
        }
    }

    public static async Task<JsonArray> FetchEngineRewards(string username)
    {
        try
        {
            var response = await EngineRewardsRequest(username,
                new[] { new KeyValuePair<string, string?>("hive", "1") });

            // An account with no Hive-Engine activity is the common case, not a failure:
            // the upstream answers with an empty object, which falls through the loop
            // below to an empty result. Previously both that and a non-object response
            // threw into the catch, logging an error on every such request.
            if (response.Json is not JsonObject obj) return new JsonArray();

            var filtered = new JsonArray();
            foreach (var raw in obj.Select(kv => kv.Value))
            {
                var converted = HiveEngine.ConvertRewardsStatus(raw);
                var pendingToken = JsVal.AsNumber(converted["pendingToken"]);
                if (pendingToken is > 0) filtered.Add(converted);
            }
            return filtered;
        }
        catch (Exception err)
        {
            Console.WriteLine($"failed to get unclaimed engine rewards {err.Message}");
            return new JsonArray();
        }
    }

    private static async Task<JsonArray> FetchEngineTokensWithBalance(string username)
    {
        try
        {
            var balances = await FetchEngineBalances(username);

            var symbols = balances.Select(b => JsVal.AsString(JsVal.Prop(b, "symbol")) ?? JsVal.ToJsString(JsVal.Prop(b, "symbol")))
                .Where(s => s != null).Select(s => s!).ToList();

            var tokensTask = FetchEngineTokens(symbols);
            var metricsTask = FetchEngineMetrics(symbols);
            var unclaimedTask = FetchEngineRewards(username);
            await Task.WhenAll(tokensTask, metricsTask, unclaimedTask);
            var tokens = tokensTask.Result;
            var metrics = metricsTask.Result;
            var unclaimed = unclaimedTask.Result;

            var output = new JsonArray();
            foreach (var balance in balances)
            {
                var sym = JsVal.Prop(balance, "symbol");
                var symStr = JsVal.ToJsString(sym);
                var token = tokens.FirstOrDefault(t => JsVal.ToJsString(JsVal.Prop(t, "symbol")) == symStr);
                var metric = metrics.FirstOrDefault(t => JsVal.ToJsString(JsVal.Prop(t, "symbol")) == symStr);
                var pending = unclaimed.FirstOrDefault(t => JsVal.ToJsString(JsVal.Prop(t, "symbol")) == symStr);
                var converted = HiveEngine.ConvertEngineToken(
                    balance?.DeepClone(), token?.DeepClone(), metric?.DeepClone(), pending?.DeepClone());
                output.Add(converted);
            }
            return output;
        }
        catch (Exception err)
        {
            Console.WriteLine($"Engine data fetch failed {err.Message}");
            return new JsonArray();
        }
    }

    private static async Task<JsonNode?> FetchSpkData(string username)
    {
        try
        {
            var url = $"{BaseSpkUrl}/@{username}";
            var response = await Upstream.BaseApiRequest(url, HttpMethod.Get, null, null, null, 30000);
            var data = DataOf(response);
            if (!JsJson.IsTruthy(data)) throw new Exception("Invalid spk data");
            return data;
        }
        catch (Exception err)
        {
            Console.WriteLine($"Spk data fetch failed {err.Message}");
            return null;
        }
    }

    private static async Task<JsonNode?> ApiRequestData(string endpoint)
    {
        var resp = await ApiClient.ApiRequest(endpoint, HttpMethod.Get);
        var data = DataOf(resp);
        if (!JsJson.IsTruthy(data)) throw new Exception("failed to get data");
        return data;
    }

    // axios response.data: parsed JSON when present, else the raw string body.
    private static JsonNode? DataOf(UpstreamResponse r) =>
        r.Json ?? (r.RawText != null ? JsonValue.Create(r.RawText) : null);

    private static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }

    // ---- portfolio (v1) --------------------------------------------------

    public static async Task Portfolio(HttpContext ctx)
    {
        try
        {
            var body = await ctx.ReadBody();
            // JS `const {username} = req.body`: getAccount receives the raw value
            // (undefined/null -> RPC [null] -> empty), while `users/${username}`
            // uses String() coercion (undefined -> "undefined", null -> "null").
            var usernameNode = body.Field("username");
            var usernameForAccount = JsVal.AsString(usernameNode); // null when missing/non-string
            var usernameForPath = body.ContainsKey("username") ? JsVal.ToJsString(usernameNode) : "undefined";

            var globalPropsTask = HiveExplorer.FetchGlobalProps();
            var accountTask = HiveExplorer.GetAccount(usernameForAccount);
            var marketTask = ApiRequestData("market-data/latest");
            var pointsTask = ApiRequestData($"users/{usernameForPath}");
            var engineTask = FetchEngineTokensWithBalance(usernameForPath);
            var spkTask = FetchSpkData(usernameForPath);

            // Promise.all rejects on first failure; mirror by awaiting each and
            // surfacing the first exception's message as a 500.
            await Task.WhenAll(globalPropsTask, accountTask, marketTask, pointsTask, engineTask, spkTask)
                .ContinueWith(_ => { }); // swallow aggregate; we inspect individually below

            foreach (var t in new Task[] { globalPropsTask, accountTask, marketTask, pointsTask, engineTask, spkTask })
            {
                if (t.IsFaulted) throw t.Exception!.InnerExceptions[0];
            }

            var responses = new (string Key, JsonNode? Value)[]
            {
                ("globalProps", GlobalPropsJson(globalPropsTask.Result)),
                ("marketData", marketTask.Result),
                ("accountData", accountTask.Result),
                ("pointsData", pointsTask.Result),
                ("engineData", engineTask.Result),
                ("spkData", spkTask.Result),
            };

            var respObj = new JsonObject();
            foreach (var (key, value) in responses)
            {
                if (JsJson.IsTruthy(value)) respObj[key] = value!.DeepClone();
            }

            await ctx.SendJson(200, respObj);
        }
        catch (Exception err)
        {
            Console.WriteLine($"failed to compile portfolio {err.Message}");
            await ctx.SendText(500, err.Message);
        }
    }

    private static JsonObject GlobalPropsJson(HiveExplorer.GlobalProps g) => new()
    {
        ["hivePerMVests"] = g.HivePerMVests,
        ["hbdApr"] = g.HbdApr,
        ["hpApr"] = g.HpApr,
    };

    // ---- portfolioV2 -----------------------------------------------------

    private const int FastLegTimeout = 3000;
    private const int SlowLegTimeout = 4500;

    private static async Task<T> WithTimeout<T>(Task<T> task, int ms, T fallback)
    {
        var timeout = Task.Delay(ms);
        var completed = await Task.WhenAny(task, timeout);
        if (completed != task) return fallback;
        try { return await task; }
        catch { return fallback; }
    }

    public static async Task PortfolioV2(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var usernameNode = body.Field("username");
        var username = JsVal.AsString(usernameNode);

        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(400, "Missing username");
            return;
        }

        var normalizedCurrency = NormalizeCurrency(body.Field("currency"));
        var onlyEnabledFlag = ParseBoolean(body.Field("onlyEnabled"));

        try
        {
            var globalPropsPromise = HiveExplorer.FetchGlobalProps();
            var accountPromise = HiveExplorer.GetAccount(username);
            var marketEndpoint = normalizedCurrency == "usd"
                ? "market-data/latest"
                : $"market-data/latest?currency={normalizedCurrency}";
            var marketPromise = ApiRequestData(marketEndpoint);
            var pointsPromise = ApiRequestData($"users/{username}");
            var enginePromise = FetchEngineTokensWithBalance(username);

            // Phase 1 — fast Hive/internal legs.
            var globalPropsT = WithTimeout(WrapNullable(globalPropsPromise), FastLegTimeout, (HiveExplorer.GlobalProps?)null);
            var accountT = WithTimeout(WrapNullable(accountPromise), FastLegTimeout, (JsonNode?)null);
            var marketT = WithTimeout(marketPromise, FastLegTimeout, (JsonNode?)null);
            var pointsT = WithTimeout(pointsPromise, FastLegTimeout, (JsonNode?)null);
            await Task.WhenAll(globalPropsT, accountT, marketT, pointsT);
            var globalProps = globalPropsT.Result;
            var accountData = accountT.Result;
            var marketData = marketT.Result;
            var pointsData = pointsT.Result;

            var hivePrice = GetTokenPrice(marketData, "hive", normalizedCurrency);

            var wallets = new List<JsonObject>();
            wallets.AddRange(BuildPointsLayer(pointsData, marketData, normalizedCurrency));
            wallets.AddRange(BuildHiveLayer(accountData, globalProps, marketData, normalizedCurrency));

            var engineOnlyEnabled = onlyEnabledFlag == true;
            var engineAllowed = onlyEnabledFlag == true ? ExtractEnabledEngineTokenSymbols(accountData) : null;
            bool? chainOnlyEnabled = onlyEnabledFlag; // undefined -> null -> buildChainLayer with no options

            // Phase 2 — slow external layers in parallel, each fail-fast.
            var engineT = WithTimeout(enginePromise, SlowLegTimeout, new JsonArray());
            var chainT = WithTimeout(
                BuildChainLayer(accountData, marketData, normalizedCurrency, chainOnlyEnabled),
                SlowLegTimeout, new List<JsonObject>());
            await Task.WhenAll(engineT, chainT);
            var engineData = engineT.Result;
            var chainWallets = chainT.Result;

            wallets.AddRange(BuildEngineLayer(engineData, marketData, normalizedCurrency, hivePrice, engineOnlyEnabled, engineAllowed));
            wallets.AddRange(chainWallets);

            var filteredWallets = new JsonArray();
            foreach (var item in wallets)
            {
                var bal = JsVal.AsNumber(item["balance"]);
                if (bal.HasValue) filteredWallets.Add(item.DeepClone());
            }

            await ctx.SendJson(200, new JsonObject
            {
                ["username"] = username,
                ["currency"] = normalizedCurrency.ToUpperInvariant(),
                ["wallets"] = filteredWallets,
            });
        }
        catch (Exception err)
        {
            Console.WriteLine($"failed to compile portfolio v2 {err.Message}");
            var message = !string.IsNullOrEmpty(err.Message) ? err.Message : "Failed to compile portfolio";
            await ctx.SendText(500, message);
        }
    }

    private static async Task<T?> WrapNullable<T>(Task<T> task) where T : class => await task;
    private static async Task<HiveExplorer.GlobalProps?> WrapNullable(Task<HiveExplorer.GlobalProps> task) => await task;
}
