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
public static partial class SsrRpc
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

    // The configured secret, held as a SHA-256 digest so the comparison below is
    // over two equal-length values (FixedTimeEquals returns early on a length
    // mismatch, which would otherwise leak the secret's length). Replaceable for
    // tests, which cannot set process environment before Config initializes.
    internal static byte[]? SecretDigest = Digest(Config.SsrInternalSecret);

    internal static byte[]? Digest(string? secret) =>
        secret is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    // Replaceable for tests (loopback stub nodes, a small cache budget).
    internal static HiveRpcClient Client = new(
        Config.SsrRpcNodes ?? HiveClients.DefaultNodes.ToArray(),
        timeoutMs: Config.SsrNodeTimeoutMs,
        failoverThreshold: 1);

    internal static BytesCache Cache = new(Config.SsrCacheBytes);

    internal static int BudgetMs = Config.SsrBudgetMs;

    // Bounds detached fills: a fill outlives the request budget on purpose, so
    // a slow pool plus many distinct keys must not pile up unbounded calls.
    internal static SemaphoreSlim FillGate = new(Config.SsrMaxConcurrentFills, Config.SsrMaxConcurrentFills);

    // Bounds the fills WAITING for the gate as well: beyond this many, a new
    // miss fails fast instead of queueing work no reader will wait for.
    internal static int MaxQueuedFills = Config.SsrMaxQueuedFills;
    private static int _queuedFills;

    private sealed class Pending
    {
        public required Task<byte[]> Task;
        // Last time a reader attached to this fill (created it or coalesced onto
        // it). A queued fill is dropped only when nobody has attached within the
        // budget, since every earlier reader has given up by then. Attaching and
        // the expiry decision both happen under `lock (this)`, so a reader can
        // never attach to a fill that has just decided to expire: it finds
        // Expired set and starts a fresh fill instead.
        public long LastAttachMs = Environment.TickCount64;
        public bool Expired;

        public bool TryAttach()
        {
            lock (this)
            {
                if (Expired) return false;
                LastAttachMs = Environment.TickCount64;
                return true;
            }
        }

        public bool TryExpire(int budgetMs)
        {
            lock (this)
            {
                if (Environment.TickCount64 - LastAttachMs <= budgetMs) return false;
                Expired = true;
                return true;
            }
        }
    }

    private static readonly ConcurrentDictionary<string, Pending> InFlight = new();

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

        public double ReadUpstreamMs()
        {
            lock (_lock) return UpstreamMs;
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
        var expected = SecretDigest;
        if (expected is null) return false;
        if (!ctx.Request.Headers.TryGetValue(HeaderName, out var values)) return false;
        var presented = values.ToString();
        if (presented.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(Digest(presented), expected);
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
    /// `params` is the exact node the upstream call carries (an array for
    /// condenser methods, an object for bridge), and the key is derived from
    /// that same node.
    /// </summary>
    internal static async Task<Resolution> Resolve(MethodPolicy policy, JsonNode @params)
    {
        var key = CacheKey(policy, @params);
        var counter = CounterFor(policy.Key);

        if (Cache.TryGet(key, out var cached))
        {
            Interlocked.Increment(ref counter.Hit);
            return new Resolution(Outcome.Hit, cached, null);
        }

        // One wall-clock budget for the whole lookup, retry included.
        var deadline = Environment.TickCount64 + BudgetMs;
        var retried = false;
    again:
        var coalesced = true;
        Pending pending;
        while (true)
        {
            if (InFlight.TryGetValue(key, out var existing))
            {
                if (existing.TryAttach())
                {
                    pending = existing;
                    break;
                }
                // Expired while queued: clear it and start a fresh fill.
                InFlight.TryRemove(new KeyValuePair<string, Pending>(key, existing));
                continue;
            }
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var created = new Pending { Task = tcs.Task };
            var winner = InFlight.GetOrAdd(key, created);
            if (ReferenceEquals(winner, created))
            {
                coalesced = false;
                pending = created;
                _ = Fill(policy, @params, key, counter, tcs, created);
                break;
            }
            if (winner.TryAttach())
            {
                pending = winner;
                break;
            }
            InFlight.TryRemove(new KeyValuePair<string, Pending>(key, winner));
        }

        if (coalesced) Interlocked.Increment(ref counter.Coalesced);
        else Interlocked.Increment(ref counter.Miss);

        var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
        var finished = await Task.WhenAny(pending.Task, Task.Delay(remaining));
        if (!ReferenceEquals(finished, pending.Task))
        {
            Interlocked.Increment(ref counter.Timeout);
            return new Resolution(Outcome.Timeout, Array.Empty<byte>(), "budget exceeded");
        }

        try
        {
            var bytes = await pending.Task;
            return new Resolution(coalesced ? Outcome.Coalesced : Outcome.Miss, bytes, null);
        }
        catch (FillRejectedException) when (coalesced && !retried && Environment.TickCount64 < deadline)
        {
            // The fill this reader attached to was judged expired (or refused) before
            // the reader's own wait began, which can only happen if the reader was
            // descheduled for longer than the budget between attaching and waiting.
            // While its deadline has not passed, start over once; past it, the
            // lookup is a timeout and no replacement fill is started for it.
            retried = true;
            goto again;
        }
        catch (FillRejectedException) when (coalesced && Environment.TickCount64 >= deadline)
        {
            // Past the deadline the lookup is a timeout; a repeat rejection
            // before it falls through to the generic unavailable path below.
            Interlocked.Increment(ref counter.Timeout);
            return new Resolution(Outcome.Timeout, Array.Empty<byte>(), "budget exceeded");
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
    internal sealed class FillRejectedException : Exception
    {
        public FillRejectedException(string message) : base(message) { }
    }

    private static async Task Fill(MethodPolicy policy, JsonNode @params, string key, Counter counter,
        TaskCompletionSource<byte[]> tcs, Pending pending)
    {
        var acquired = false;
        var queued = false;
        try
        {
            // A reader can miss the cache, lose the race to another fill that
            // then completes and leaves, and only now win GetOrAdd: the value is
            // already cached, so serve it rather than calling upstream again.
            if (Cache.TryGet(key, out var cached))
            {
                tcs.TrySetResult(cached);
                return;
            }
            // A free slot is taken at once; otherwise the fill joins a bounded
            // queue. Only fills actually waiting count against the bound.
            if (FillGate.Wait(0))
            {
                acquired = true;
            }
            else
            {
                if (Interlocked.Increment(ref _queuedFills) > MaxQueuedFills)
                {
                    Interlocked.Decrement(ref _queuedFills);
                    throw new FillRejectedException("fill queue full");
                }
                queued = true;
                await FillGate.WaitAsync();
                acquired = true;
                // Nobody has attached within the budget: every reader gave up
                // while this sat in the queue, and calling upstream now would
                // only be stale traffic for nobody. A fresh reader that coalesced
                // in the meantime keeps the fill alive.
                if (pending.TryExpire(BudgetMs))
                {
                    throw new FillRejectedException("fill expired in queue");
                }
            }
            var started = Environment.TickCount64;
            // Call() places params inside its own request envelope; a node that
            // already hangs off the request body cannot be re-parented, so it
            // travels as a clone.
            var result = await Client.Call(policy.Api, policy.Method, @params.DeepClone());
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
            if (queued) Interlocked.Decrement(ref _queuedFills);
            if (acquired) FillGate.Release();
            InFlight.TryRemove(new KeyValuePair<string, Pending>(key, pending));
        }
    }

    // ---- handlers ------------------------------------------------------------
    //
    // The body is the JSON serialization of the upstream `result`, always with
    // an application/json content type: an object, an array, a string, a number
    // or `null` exactly as JSON. This is a new internal contract read with
    // res.json() by one consumer, not a pipe of an upstream HTTP body, so the
    // Express res.send quirks the proxied routes preserve (null as an empty
    // body, strings as text/html, numbers as text) do not apply here.

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
        // params must be present and structured (condenser methods take an
        // array, bridge methods an object); the key and the upstream call are
        // derived from that same node, so nothing is invented for either.
        var @params = body.Field("params");
        if (api is null || method is null || @params is not (JsonObject or JsonArray)
            || !Allowlist.TryGetValue($"{api}.{method}", out var policy))
        {
            await Routes.Fallback(ctx);
            return;
        }

        var resolution = await Resolve(policy, @params);
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
                ["upstream_ms"] = Math.Round(c.ReadUpstreamMs(), 1),
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
