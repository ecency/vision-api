using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Internal read-through cache for the Hive RPC reads the web tier makes while
/// rendering pages on the server.
///
/// Every renderer process used to fetch the same accounts, profiles,
/// communities and per-tag feeds straight from public nodes, each at full
/// upstream latency and with nothing shared between processes. This route
/// answers those reads from one cache per host: the upstream `result` is
/// serialized once (JsJson, lone-surrogate safe) and the bytes are written to
/// every reader unchanged; concurrent misses for one key make one upstream
/// call (single-flight, the same pattern as the chain balance fetch).
///
/// Boundaries, all deliberate:
///  - Allowlisted read methods only, with a TTL each. Anything else, and any
///    call without the shared internal header, answers exactly like a route
///    that does not exist (Routes.Fallback), so nothing is learnable from
///    outside and the parity harness sees no new behavior.
///  - The response is the raw `result`, so the consumer sees exactly what a
///    direct node call returns. No envelope, no reshaping.
///  - A lookup that outlives the budget answers 504 while the upstream call
///    completes in the background and fills the cache; an RPC-level error
///    answers 502. Either way the consumer falls back to its own node pool.
///  - No per-request logging (invariant 6); counters on /stats instead.
/// </summary>
public static class SsrRpc
{
    internal sealed record MethodPolicy(string Api, string Method, int TtlMs)
    {
        public string Key => $"{Api}.{Method}";
    }

    // TTLs follow how fast each read legitimately changes for a page render:
    // dynamic props every block, a created feed within seconds, a post's
    // votes/payout within tens of seconds, a profile or community rarely.
    internal static readonly IReadOnlyDictionary<string, MethodPolicy> Allowlist = new[]
    {
        new MethodPolicy("bridge", "get_ranked_posts", 15_000),
        new MethodPolicy("bridge", "get_account_posts", 30_000),
        new MethodPolicy("bridge", "get_post", 30_000),
        new MethodPolicy("bridge", "get_discussion", 30_000),
        new MethodPolicy("bridge", "get_profile", 60_000),
        new MethodPolicy("bridge", "get_profiles", 60_000),
        new MethodPolicy("bridge", "get_community", 300_000),
        new MethodPolicy("bridge", "list_communities", 300_000),
        new MethodPolicy("condenser_api", "get_accounts", 30_000),
        new MethodPolicy("condenser_api", "get_content", 30_000),
        new MethodPolicy("condenser_api", "get_dynamic_global_properties", 3_000),
        new MethodPolicy("condenser_api", "get_trending_tags", 300_000),
    }.ToDictionary(p => p.Key, p => p);

    internal const string HeaderName = "X-Ecency-Internal";

    // Replaceable for tests (loopback stub nodes, a small cache budget).
    internal static HiveRpcClient Client = new(
        Config.SsrRpcNodes ?? HiveClients.DefaultNodes,
        timeoutMs: Config.SsrNodeTimeoutMs,
        failoverThreshold: 1);

    internal static BytesCache Cache = new(Config.SsrCacheBytes);

    internal static int BudgetMs = Config.SsrBudgetMs;

    private static readonly ConcurrentDictionary<string, Task<byte[]>> InFlight = new();

    internal sealed class Counter
    {
        public long Hit, Miss, Coalesced, Error, Timeout;
        // Upstream latency EWMA for misses, milliseconds.
        public double UpstreamMs;
        private readonly object _lock = new();

        public void RecordUpstream(double ms)
        {
            lock (_lock) UpstreamMs = UpstreamMs == 0 ? ms : UpstreamMs * 0.8 + ms * 0.2;
        }
    }

    private static readonly ConcurrentDictionary<string, Counter> Counters = new();

    internal static Counter CounterFor(string key) => Counters.GetOrAdd(key, _ => new Counter());

    internal static void ResetForTests()
    {
        InFlight.Clear();
        Counters.Clear();
    }

    // ---- auth ----------------------------------------------------------------

    internal static bool Authorized(HttpContext ctx)
    {
        var secret = Config.SsrInternalSecret;
        if (secret is null) return false;
        if (!ctx.Request.Headers.TryGetValue(HeaderName, out var values)) return false;
        var presented = values.ToString();
        if (presented.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(secret));
    }

    // ---- key -----------------------------------------------------------------

    /// <summary>
    /// One key per distinct call: method plus params with object keys sorted at
    /// every level, so two call sites that build the same params in a different
    /// order share an entry.
    /// </summary>
    internal static string CacheKey(MethodPolicy policy, JsonNode? @params) =>
        policy.Key + ":" + JsJson.Stringify(Canonical(@params));

    internal static JsonNode? Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    sorted[key] = Canonical(obj[key]);
                }
                return sorted;
            case JsonArray arr:
                var copy = new JsonArray();
                foreach (var item in arr) copy.Add(Canonical(item));
                return copy;
            default:
                return node?.DeepClone();
        }
    }

    // ---- core ----------------------------------------------------------------

    internal enum Outcome { Hit, Miss, Coalesced, Timeout, RpcError, Unavailable }

    internal readonly record struct Resolution(Outcome Outcome, byte[] Bytes, string? Error);

    /// <summary>
    /// Answer one allowlisted call from the cache, or from one shared upstream
    /// call, within the budget. Pure of HTTP so it can be exercised directly.
    /// </summary>
    internal static async Task<Resolution> Resolve(MethodPolicy policy, JsonNode? @params)
    {
        var key = CacheKey(policy, @params);
        var counter = CounterFor(policy.Key);

        if (Cache.TryGet(key, out var cached))
        {
            Interlocked.Increment(ref counter.Hit);
            return new Resolution(Outcome.Hit, cached, null);
        }

        var coalesced = true;
        if (!InFlight.TryGetValue(key, out var pending))
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var winner = InFlight.GetOrAdd(key, tcs.Task);
            if (ReferenceEquals(winner, tcs.Task))
            {
                coalesced = false;
                pending = tcs.Task;
                _ = Fill(policy, @params, key, counter, tcs);
            }
            else
            {
                pending = winner;
            }
        }

        if (coalesced) Interlocked.Increment(ref counter.Coalesced);
        else Interlocked.Increment(ref counter.Miss);

        var finished = await Task.WhenAny(pending, Task.Delay(BudgetMs));
        if (!ReferenceEquals(finished, pending))
        {
            Interlocked.Increment(ref counter.Timeout);
            return new Resolution(Outcome.Timeout, Array.Empty<byte>(), "budget exceeded");
        }

        try
        {
            var bytes = await pending;
            return new Resolution(coalesced ? Outcome.Coalesced : Outcome.Miss, bytes, null);
        }
        catch (HiveRpcClient.RpcException e)
        {
            Interlocked.Increment(ref counter.Error);
            return new Resolution(Outcome.RpcError, Array.Empty<byte>(), e.Message);
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref counter.Error);
            return new Resolution(Outcome.Unavailable, Array.Empty<byte>(), e.Message);
        }
    }

    // The one upstream call behind a key. Runs to completion even when every
    // waiter has given up, so the cache still gets filled for the next reader.
    private static async Task Fill(MethodPolicy policy, JsonNode? @params, string key, Counter counter,
        TaskCompletionSource<byte[]> tcs)
    {
        var started = Environment.TickCount64;
        try
        {
            var result = await Client.Call(policy.Api, policy.Method, @params ?? new JsonObject());
            var bytes = Encoding.UTF8.GetBytes(result is null ? "null" : JsJson.Stringify(result));
            counter.RecordUpstream(Environment.TickCount64 - started);
            Cache.Set(key, bytes, policy.TtlMs);
            tcs.TrySetResult(bytes);
        }
        catch (Exception e)
        {
            tcs.TrySetException(e);
            _ = tcs.Task.Exception; // observe: every waiter may already be gone
        }
        finally
        {
            InFlight.TryRemove(new KeyValuePair<string, Task<byte[]>>(key, tcs.Task));
        }
    }

    // ---- handlers ------------------------------------------------------------

    public static async Task Rpc(HttpContext ctx)
    {
        if (!Authorized(ctx))
        {
            await Routes.Fallback(ctx);
            return;
        }

        var body = await ctx.ReadBody();
        var api = body.Str("api");
        var method = body.Str("method");
        if (api is null || method is null || !Allowlist.TryGetValue($"{api}.{method}", out var policy))
        {
            await Routes.Fallback(ctx);
            return;
        }

        var resolution = await Resolve(policy, body.Field("params"));
        ctx.Response.Headers["X-Ssr-Cache"] = resolution.Outcome.ToString().ToUpperInvariant();
        switch (resolution.Outcome)
        {
            case Outcome.Hit:
            case Outcome.Miss:
            case Outcome.Coalesced:
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength = resolution.Bytes.Length;
                await ctx.Response.Body.WriteAsync(resolution.Bytes);
                return;
            case Outcome.Timeout:
                await ctx.SendJson(504, new JsonObject { ["error"] = "Upstream Timeout" });
                return;
            default:
                await ctx.SendJson(502, new JsonObject { ["error"] = resolution.Error ?? "Upstream Error" });
                return;
        }
    }

    public static async Task Stats(HttpContext ctx)
    {
        if (!Authorized(ctx))
        {
            await Routes.Fallback(ctx);
            return;
        }

        var methods = new JsonObject();
        foreach (var kv in Counters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var c = kv.Value;
            methods[kv.Key] = new JsonObject
            {
                ["hit"] = Interlocked.Read(ref c.Hit),
                ["miss"] = Interlocked.Read(ref c.Miss),
                ["coalesced"] = Interlocked.Read(ref c.Coalesced),
                ["error"] = Interlocked.Read(ref c.Error),
                ["timeout"] = Interlocked.Read(ref c.Timeout),
                ["upstream_ms"] = Math.Round(c.UpstreamMs, 1),
            };
        }
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.SendJson(200, new JsonObject
        {
            ["cache"] = new JsonObject
            {
                ["bytes"] = Cache.Bytes,
                ["count"] = Cache.Count,
                ["budget"] = Cache.Budget,
            },
            ["budget_ms"] = BudgetMs,
            ["methods"] = methods,
        });
    }
}
