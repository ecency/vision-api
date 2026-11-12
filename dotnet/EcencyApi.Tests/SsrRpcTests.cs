using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The SSR RPC cache against a loopback stub node: one upstream call per key
/// under concurrency, hits until the TTL runs out, the allowlist and header
/// gates answering like an unknown route, the budget turning a slow upstream
/// into a 504 while the fill still completes, and the byte budget evicting.
/// </summary>
[Collection("ssr-rpc")]
public class SsrRpcTests
{
    /// <summary>Loopback JSON-RPC node answering any method with a result that
    /// names the method and the hit number, after an optional delay.</summary>
    private sealed class RpcStub : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public int Hits;
        public int DelayMs;
        public bool RpcError;
        public bool NullResult;
        // Any other hivemind assert, e.g. one that echoes a param back.
        public string? CustomError;
        // Answer with this HTTP status instead of 200 (a node out of service).
        public int Status = 200;
        // Extra delay before an `unknown method` answer only.
        public int UnknownDelayMs;
        public volatile string LastMethod = "";
        // Methods this node answers `unknown method` for, the way a hivemind
        // without the v2 reads does (HTTP 200, code -32602, assert message).
        public HashSet<string> Unknown = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Methods = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Params = new();

        public RpcStub()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Loop();
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                // Concurrently, like a real node: a burst must not queue here.
                _ = Handle(ctx);
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            {
                var n = Interlocked.Increment(ref Hits);
                string reqBody;
                using (var reader = new StreamReader(ctx.Request.InputStream))
                {
                    reqBody = await reader.ReadToEndAsync();
                }
                // hived semantics: the legacy `call` envelope resolves only hived
                // APIs, so a bridge read sent that way is an RPC error; the dotted
                // form (`bridge.get_post`) is routed to hivemind.
                var req = JsonNode.Parse(reqBody);
                var rawMethod = req?["method"]?.GetValue<string>() ?? "?";
                string method;
                var legacyBridge = false;
                if (rawMethod == "call")
                {
                    var api = req?["params"]?[0]?.GetValue<string>() ?? "?";
                    method = req?["params"]?[1]?.GetValue<string>() ?? "?";
                    legacyBridge = api == "bridge";
                }
                else
                {
                    method = rawMethod.Contains('.') ? rawMethod[(rawMethod.IndexOf('.') + 1)..] : rawMethod;
                }
                LastMethod = rawMethod;
                Methods.Enqueue(rawMethod);
                Params.Enqueue(req?["params"]?.ToJsonString() ?? "");
                if (DelayMs > 0) await Task.Delay(DelayMs);
                // Real nodes: the v1 bridge reads reject the v2-only `top_votes`
                // param ("got an unexpected keyword argument").
                var unexpectedArg = rawMethod.StartsWith("bridge.") && !rawMethod.EndsWith("_v2")
                    && req?["params"] is JsonObject po && po.ContainsKey("top_votes");
                var assertion = CustomError ?? (unexpectedArg ? "got an unexpected keyword argument 'top_votes'"
                    : Unknown.Contains(rawMethod) ? "unknown method: bridge_api" + method : null);
                if (Unknown.Contains(rawMethod) && UnknownDelayMs > 0) await Task.Delay(UnknownDelayMs);
                var body = assertion is not null
                    ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32602,\"message\":\"Assert Exception:" + assertion
                      + "\",\"data\":{\"code\":10,\"extension\":{\"assertion_expression\":\"" + assertion + "\"}}}}"
                    : RpcError || legacyBridge
                    ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"message\":\"" + (legacyBridge ? "Assert Exception:api_itr != data._registered_apis.end(): Could not find API bridge" : "stub error") + "\"}}"
                    : NullResult
                        ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null}"
                        : "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"method\":\"" + method + "\",\"n\":" + n + ",\"text\":\"caf\\u00e9 \\ud83d\"}}";
                var bytes = Encoding.UTF8.GetBytes(body);
                // Inside the try: a handler that wakes after the listener closed
                // (the hanging-node spec) must not throw an unobserved exception.
                try
                {
                    ctx.Response.StatusCode = Status;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await Task.CompletedTask;
        }
    }

    private static readonly SsrRpc.MethodPolicy Post = SsrRpc.Allowlist["bridge.get_post"];
    private static readonly SsrRpc.MethodPolicy Ranked = SsrRpc.Allowlist["bridge.get_ranked_posts"];
    private static readonly SsrRpc.MethodPolicy Props = SsrRpc.Allowlist["condenser_api.get_dynamic_global_properties"];

    private static void Use(RpcStub stub, long cacheBytes = 1 << 20, int budgetMs = 1500, int maxFills = 64, int maxQueued = 256)
    {
        SsrRpc.Client = new HiveRpcClient(new[] { stub.Url }, timeoutMs: 1000, failoverThreshold: 1);
        SsrRpc.Cache = new BytesCache(cacheBytes);
        SsrRpc.BudgetMs = budgetMs;
        SsrRpc.FillGate = new SemaphoreSlim(maxFills, maxFills);
        SsrRpc.MaxQueuedFills = maxQueued;
        SsrRpc.SecretDigest = null;
        SsrRpc.Now = () => Environment.TickCount64;
        SsrRpc.CallClasses = true;
        SsrRpc.ResetForTests();
    }

    private static JsonObject P(string author, string permlink) =>
        new() { ["author"] = author, ["permlink"] = permlink };

    [Fact]
    public async Task Concurrent_misses_for_one_key_make_one_upstream_call()
    {
        await using var stub = new RpcStub { DelayMs = 200 };
        Use(stub);
        var tasks = Enumerable.Range(0, 12).Select(_ => SsrRpc.Resolve(Post, P("a", "b"))).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, stub.Hits);
        Assert.Single(results.Where(r => r.Outcome == SsrRpc.Outcome.Miss));
        Assert.Equal(11, results.Count(r => r.Outcome == SsrRpc.Outcome.Coalesced));
        var bodies = results.Select(r => Encoding.UTF8.GetString(r.Bytes)).Distinct().ToArray();
        Assert.Single(bodies);
        Assert.Contains("\"method\":\"get_post\"", bodies[0]);
    }

    [Fact]
    public async Task Reads_use_the_dotted_method_form_so_bridge_reaches_hivemind()
    {
        await using var stub = new RpcStub();
        Use(stub);
        var r = await SsrRpc.Resolve(Post, P("a", "b"));
        Assert.Equal(SsrRpc.Outcome.Miss, r.Outcome);
        Assert.Equal("bridge.get_post", stub.LastMethod);
        var props = await SsrRpc.Resolve(Props, new JsonArray());
        Assert.Equal(SsrRpc.Outcome.Miss, props.Outcome);
        Assert.Equal("condenser_api.get_dynamic_global_properties", stub.LastMethod);
        // The legacy envelope would have been refused for bridge, as hived does.
        var legacy = new HiveRpcClient(new[] { stub.Url }, timeoutMs: 1000, failoverThreshold: 1);
        await Assert.ThrowsAsync<HiveRpcClient.RpcException>(() => legacy.Call("bridge", "get_post", P("a", "b")));
    }

    [Fact]
    public async Task Second_call_is_a_hit_with_the_same_bytes_and_params_order_does_not_matter()
    {
        await using var stub = new RpcStub();
        Use(stub);
        var first = await SsrRpc.Resolve(Post, P("a", "b"));
        var reordered = new JsonObject { ["permlink"] = "b", ["author"] = "a" };
        var second = await SsrRpc.Resolve(Post, reordered);
        Assert.Equal(SsrRpc.Outcome.Miss, first.Outcome);
        Assert.Equal(SsrRpc.Outcome.Hit, second.Outcome);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(1, stub.Hits);
        // A different post is a different key.
        var other = await SsrRpc.Resolve(Post, P("a", "c"));
        Assert.Equal(SsrRpc.Outcome.Miss, other.Outcome);
        Assert.Equal(2, stub.Hits);
    }

    [Fact]
    public async Task Bytes_are_the_upstream_result_serialized_once_lone_surrogate_included()
    {
        await using var stub = new RpcStub();
        Use(stub);
        var r = await SsrRpc.Resolve(Post, P("a", "b"));
        var text = Encoding.UTF8.GetString(r.Bytes);
        // The result object itself, not the JSON-RPC envelope.
        Assert.StartsWith("{\"method\":\"get_post\"", text);
        Assert.DoesNotContain("jsonrpc", text);
        // JsJson re-emits the lone surrogate as an escape instead of throwing.
        Assert.Contains("\\ud83d", text);
    }

    [Fact]
    public async Task Ttl_expiry_goes_upstream_again()
    {
        await using var stub = new RpcStub();
        Use(stub);
        var shortLived = Props with { TtlMs = 150 };
        Assert.Equal(SsrRpc.Outcome.Miss, (await SsrRpc.Resolve(shortLived, new JsonArray())).Outcome);
        Assert.Equal(SsrRpc.Outcome.Hit, (await SsrRpc.Resolve(shortLived, new JsonArray())).Outcome);
        await Task.Delay(300);
        Assert.Equal(SsrRpc.Outcome.Miss, (await SsrRpc.Resolve(shortLived, new JsonArray())).Outcome);
        Assert.Equal(2, stub.Hits);
    }

    [Fact]
    public async Task Budget_exceeded_answers_timeout_while_the_fill_still_lands_in_the_cache()
    {
        await using var stub = new RpcStub { DelayMs = 400 };
        Use(stub, budgetMs: 100);
        var r = await SsrRpc.Resolve(Post, P("slow", "post"));
        Assert.Equal(SsrRpc.Outcome.Timeout, r.Outcome);
        await Task.Delay(600);
        SsrRpc.BudgetMs = 1500;
        var again = await SsrRpc.Resolve(Post, P("slow", "post"));
        Assert.Equal(SsrRpc.Outcome.Hit, again.Outcome);
        Assert.Equal(1, stub.Hits);
    }

    [Fact]
    public async Task Rpc_level_error_is_reported_not_cached()
    {
        await using var stub = new RpcStub { RpcError = true };
        Use(stub);
        var r = await SsrRpc.Resolve(Post, P("a", "b"));
        Assert.Equal(SsrRpc.Outcome.RpcError, r.Outcome);
        Assert.Equal("stub error", r.Error);
        var again = await SsrRpc.Resolve(Post, P("a", "b"));
        Assert.Equal(SsrRpc.Outcome.RpcError, again.Outcome);
        Assert.Equal(2, stub.Hits);
    }

    [Fact]
    public async Task Unreachable_node_is_reported_as_unavailable()
    {
        SsrRpc.Client = new HiveRpcClient(new[] { "http://127.0.0.1:9/" }, timeoutMs: 500, failoverThreshold: 1);
        SsrRpc.Cache = new BytesCache(1 << 20);
        SsrRpc.BudgetMs = 1500;
        SsrRpc.ResetForTests();
        var r = await SsrRpc.Resolve(Post, P("a", "b"));
        Assert.Equal(SsrRpc.Outcome.Unavailable, r.Outcome);
    }

    [Fact]
    public async Task Fills_in_progress_are_bounded_so_distinct_keys_queue_instead_of_piling_up()
    {
        await using var stub = new RpcStub { DelayMs = 250 };
        Use(stub, maxFills: 1);
        var started = Environment.TickCount64;
        var a = SsrRpc.Resolve(Post, P("a", "1"));
        var b = SsrRpc.Resolve(Post, P("a", "2"));
        var results = await Task.WhenAll(a, b);
        Assert.All(results, r => Assert.Equal(SsrRpc.Outcome.Miss, r.Outcome));
        // The second fill waited for the first: two delays back to back.
        Assert.True(Environment.TickCount64 - started >= 450, "fills ran concurrently despite the bound");
        Assert.Equal(2, stub.Hits);
    }

    [Fact]
    public async Task Under_pressure_expired_entries_go_before_live_ones()
    {
        var cache = new BytesCache(100);
        cache.Set("live-old", new byte[40], 60_000);
        cache.Set("expired", new byte[40], 50);
        await Task.Delay(120);
        // Over budget now: the expired entry must be the one that goes, even
        // though the live entry is the least recently used.
        cache.Set("new", new byte[40], 60_000);
        Assert.True(cache.TryGet("live-old", out _));
        Assert.False(cache.TryGet("expired", out _));
        Assert.True(cache.TryGet("new", out _));
    }

    [Fact]
    public async Task Every_over_budget_set_purges_expired_entries_first_not_only_once_in_a_while()
    {
        var cache = new BytesCache(120);
        cache.Set("live-a", new byte[40], 60_000);
        cache.Set("short-1", new byte[40], 50);
        await Task.Delay(120);
        cache.Set("live-b", new byte[40], 60_000); // over budget: short-1 (expired) goes
        Assert.False(cache.TryGet("short-1", out _));
        Assert.True(cache.TryGet("live-a", out _));
        // A second expiry moments later, then more pressure: it must go before
        // the live entry at the LRU head, even though a purge just ran.
        cache.Set("short-2", new byte[40], 50);
        await Task.Delay(120);
        cache.Set("live-c", new byte[40], 60_000);
        Assert.False(cache.TryGet("short-2", out _));
        Assert.True(cache.TryGet("live-a", out _));
        Assert.True(cache.TryGet("live-b", out _));
        Assert.True(cache.TryGet("live-c", out _));
        Assert.Equal(120, cache.Bytes);
    }

    [Fact]
    public async Task Keys_that_share_an_expiry_millisecond_are_indexed_and_purged_independently()
    {
        var cache = new BytesCache(100);
        // Same TTL set back to back, keys that differ only in characters a
        // culture-aware comparison can treat as ignorable.
        cache.Set("k-a", new byte[30], 50);
        cache.Set("k-\u00ADa", new byte[30], 50);
        cache.Set("k-A", new byte[30], 50);
        Assert.Equal(3, cache.Count);
        await Task.Delay(120);
        cache.Set("live", new byte[40], 60_000); // over budget: all three expired must go
        Assert.Equal(1, cache.Count);
        Assert.Equal(40, cache.Bytes);
        Assert.True(cache.TryGet("live", out _));
    }

    [Fact]
    public async Task Rpc_requires_structured_params_and_keys_the_call_on_them()
    {
        await using var stub = new RpcStub();
        Use(stub);
        // Missing params answers like an unknown route and never reaches upstream.
        var missing = Request("POST", "/private-api/ssr/rpc", null, "{\"api\":\"bridge\",\"method\":\"get_post\"}");
        await SsrRpc.Rpc(missing);
        Assert.Equal(404, missing.Response.StatusCode);
        Assert.Equal(0, stub.Hits);
        // An array and an object are both legitimate shapes and distinct keys.
        var arr = await SsrRpc.Resolve(Props, new JsonArray());
        var obj = await SsrRpc.Resolve(Props, new JsonObject());
        Assert.Equal(SsrRpc.Outcome.Miss, arr.Outcome);
        Assert.Equal(SsrRpc.Outcome.Miss, obj.Outcome);
        Assert.Equal(2, stub.Hits);
    }

    [Fact]
    public async Task Queued_fills_are_bounded_and_a_fill_that_outlived_the_budget_in_the_queue_is_dropped()
    {
        await using var stub = new RpcStub { DelayMs = 400 };
        Use(stub, budgetMs: 100, maxFills: 1, maxQueued: 1);
        // First fill holds the gate; second queues; third is over the queue bound.
        var a = SsrRpc.Resolve(Post, P("q", "1"));
        await Task.Delay(50);
        var b = SsrRpc.Resolve(Post, P("q", "2"));
        await Task.Delay(20);
        var c = SsrRpc.Resolve(Post, P("q", "3"));
        var ra = await a; var rb = await b; var rc = await c;
        Assert.True(ra.Outcome == SsrRpc.Outcome.Timeout, $"a: {ra.Outcome} {ra.Error}");
        Assert.Equal(SsrRpc.Outcome.Timeout, rb.Outcome);
        Assert.Equal(SsrRpc.Outcome.Unavailable, rc.Outcome);
        Assert.Equal("fill queue full", rc.Error);
        // The queued second fill waited past the budget for the gate, so it never
        // calls upstream: one upstream hit in total, and its key is not cached.
        await Task.Delay(900);
        Assert.Equal(1, stub.Hits);
        SsrRpc.BudgetMs = 1500;
        Assert.Equal(SsrRpc.Outcome.Hit, (await SsrRpc.Resolve(Post, P("q", "1"))).Outcome);
        Assert.Equal(SsrRpc.Outcome.Miss, (await SsrRpc.Resolve(Post, P("q", "2"))).Outcome);
    }

    [Fact]
    public async Task A_fresh_reader_that_coalesces_onto_a_queued_fill_keeps_it_alive()
    {
        await using var stub = new RpcStub { DelayMs = 200 };
        Use(stub, budgetMs: 150, maxFills: 1);
        var a = SsrRpc.Resolve(Post, P("k", "1"));   // holds the gate until ~200ms
        await Task.Delay(10);
        var b = SsrRpc.Resolve(Post, P("k", "2"));   // queued at ~10ms, its reader gives up at ~160ms
        await Task.Delay(180);
        var c = SsrRpc.Resolve(Post, P("k", "2"));   // fresh reader at ~190ms, coalesces
        await Task.WhenAll(a, b, c);
        Assert.Equal(SsrRpc.Outcome.Timeout, a.Result.Outcome);
        Assert.Equal(SsrRpc.Outcome.Timeout, b.Result.Outcome);
        // Judged by the fresh attach, not by the original enqueue: the fill ran.
        await Task.Delay(500);
        Assert.Equal(2, stub.Hits);
        SsrRpc.BudgetMs = 1500;
        Assert.Equal(SsrRpc.Outcome.Hit, (await SsrRpc.Resolve(Post, P("k", "2"))).Outcome);
    }

    [Fact]
    public async Task A_coalesced_reader_whose_deadline_passed_before_its_fill_was_rejected_gets_timeout_and_no_replacement_fill()
    {
        await using var stub = new RpcStub { DelayMs = 300 };
        Use(stub, budgetMs: 1000, maxFills: 1);
        // A controllable clock: real time drives the stub and the waits, the
        // clock drives the deadline and the attach/expiry bookkeeping.
        long offset = 0;
        SsrRpc.Now = () => Environment.TickCount64 + Interlocked.Read(ref offset);
        var timeoutsBefore = Interlocked.Read(ref SsrRpc.CounterFor(Post.Key).Timeout);

        var a = SsrRpc.Resolve(Post, P("d", "1"));   // holds the gate for ~300ms
        await Task.Delay(30);
        var creator = SsrRpc.Resolve(Post, P("d", "2")); // queued fill, waits for the gate
        await Task.Delay(30);
        var late = SsrRpc.Resolve(Post, P("d", "2"));    // coalesces onto it, deadline = now + 1000
        await Task.Delay(30);
        // Jump the clock past every deadline and past the attach window, while
        // the readers' real waits (1000ms) are still running.
        Interlocked.Exchange(ref offset, 1_500);
        // The gate frees at ~300ms real; the queued fill is then judged expired.
        var results = await Task.WhenAll(a, creator, late);

        Assert.Equal(SsrRpc.Outcome.Miss, results[0].Outcome);
        Assert.Equal(SsrRpc.Outcome.Unavailable, results[1].Outcome); // the creator is not coalesced
        Assert.Equal(SsrRpc.Outcome.Timeout, results[2].Outcome);     // past its deadline: timeout, no retry
        Assert.Equal(timeoutsBefore + 1, Interlocked.Read(ref SsrRpc.CounterFor(Post.Key).Timeout));
        await Task.Delay(400);
        Assert.Equal(1, stub.Hits); // no replacement fill went upstream
        SsrRpc.Now = () => Environment.TickCount64;
    }

    [Fact]
    public async Task With_the_secret_configured_both_routes_serve_the_matching_header_and_nothing_else()
    {
        await using var stub = new RpcStub();
        Use(stub);
        SsrRpc.SecretDigest = SsrRpc.Digest("right-secret");
        try
        {
            var ok = Request("POST", "/private-api/ssr/rpc", "right-secret",
                "{\"api\":\"bridge\",\"method\":\"get_post\",\"params\":{\"author\":\"a\",\"permlink\":\"b\"}}");
            await SsrRpc.Rpc(ok);
            Assert.True(ok.Response.StatusCode == 200, $"status {ok.Response.StatusCode}: {ResponseText(ok)}");
            Assert.Equal("MISS", ok.Response.Headers["X-Ssr-Cache"].ToString());
            Assert.StartsWith("{\"method\":\"get_post\"", ResponseText(ok));

            var again = Request("POST", "/private-api/ssr/rpc", "right-secret",
                "{\"api\":\"bridge\",\"method\":\"get_post\",\"params\":{\"permlink\":\"b\",\"author\":\"a\"}}");
            await SsrRpc.Rpc(again);
            Assert.Equal("HIT", again.Response.Headers["X-Ssr-Cache"].ToString());
            Assert.Equal(1, stub.Hits);

            var stats = Request("GET", "/private-api/ssr/stats", "right-secret");
            await SsrRpc.Stats(stats);
            Assert.Equal(200, stats.Response.StatusCode);
            Assert.Contains("\"bridge.get_post\"", ResponseText(stats));
            Assert.Contains("\"hit\":1", ResponseText(stats));

            foreach (var header in new[] { "wrong-secret", "right-secret-but-longer", "", null })
            {
                var denied = Request("POST", "/private-api/ssr/rpc", header,
                    "{\"api\":\"bridge\",\"method\":\"get_post\",\"params\":{}}");
                await SsrRpc.Rpc(denied);
                Assert.Equal(404, denied.Response.StatusCode);
                var deniedStats = Request("GET", "/private-api/ssr/stats", header);
                await SsrRpc.Stats(deniedStats);
                Assert.Equal(200, deniedStats.Response.StatusCode);
                Assert.DoesNotContain("methods", ResponseText(deniedStats));
            }
            Assert.Equal(1, stub.Hits);
        }
        finally
        {
            SsrRpc.SecretDigest = null;
        }
    }

    [Fact]
    public async Task Stats_count_one_slow_fill_for_many_waiter_timeouts_and_report_per_node_health()
    {
        // `timeout` is per waiting reader; `slow_fill` is per fill. A hot key
        // with five readers on one slow upstream call is five timeouts and one
        // slow fill, and the per-node section shows which node served it.
        await using var stub = new RpcStub { DelayMs = 400 };
        Use(stub, budgetMs: 100);
        SsrRpc.SecretDigest = SsrRpc.Digest("right-secret");
        try
        {
            var readers = Enumerable.Range(0, 5).Select(_ => SsrRpc.Resolve(Post, P("hot", "key"))).ToArray();
            var outcomes = await Task.WhenAll(readers);
            Assert.All(outcomes, r => Assert.Equal(SsrRpc.Outcome.Timeout, r.Outcome));
            await Task.Delay(600); // the detached fill completes and lands

            var stats = Request("GET", "/private-api/ssr/stats", "right-secret");
            await SsrRpc.Stats(stats);
            var body = JsonNode.Parse(ResponseText(stats))!;
            var post = body["methods"]!["bridge.get_post"]!;
            Assert.Equal(5, post["timeout"]!.GetValue<long>());
            Assert.Equal(1, post["slow_fill"]!.GetValue<long>());
            Assert.Equal(4, post["coalesced"]!.GetValue<long>());
            Assert.Equal(1, stub.Hits);

            var nodes = body["nodes"]!.AsArray();
            var node = Assert.Single(nodes)!;
            Assert.Equal("127.0.0.1", node["node"]!.GetValue<string>());
            Assert.Equal(1, node["calls"]!.GetValue<long>());
            Assert.Equal(1, node["ok"]!.GetValue<long>());
            Assert.Equal(0, node["timeouts"]!.GetValue<long>());
            Assert.Equal(0, node["parked_for_ms"]!.GetValue<long>());
        }
        finally
        {
            SsrRpc.SecretDigest = null;
            SsrRpc.BudgetMs = 1500;
        }
    }

    [Fact]
    public async Task A_slow_fill_that_then_fails_is_still_a_slow_fill()
    {
        // During an upstream outage every reader times out and the fill ends
        // in an error; that is precisely when the slow-fill count must not
        // read zero.
        await using var stub = new RpcStub { DelayMs = 400, RpcError = true };
        Use(stub, budgetMs: 100);
        SsrRpc.SecretDigest = SsrRpc.Digest("right-secret");
        try
        {
            var r = await SsrRpc.Resolve(Post, P("slow", "broken"));
            Assert.Equal(SsrRpc.Outcome.Timeout, r.Outcome);
            await Task.Delay(600);
            var stats = Request("GET", "/private-api/ssr/stats", "right-secret");
            await SsrRpc.Stats(stats);
            var post = JsonNode.Parse(ResponseText(stats))!["methods"]!["bridge.get_post"]!;
            Assert.Equal(1, post["slow_fill"]!.GetValue<long>());
            Assert.Equal(1, post["timeout"]!.GetValue<long>());
        }
        finally
        {
            SsrRpc.SecretDigest = null;
            SsrRpc.BudgetMs = 1500;
        }
    }

    [Fact]
    public async Task A_null_result_is_served_as_json_null_with_a_json_content_type()
    {
        await using var stub = new RpcStub { NullResult = true };
        Use(stub);
        SsrRpc.SecretDigest = SsrRpc.Digest("s");
        try
        {
            var ctx = Request("POST", "/private-api/ssr/rpc", "s",
                "{\"api\":\"bridge\",\"method\":\"get_post\",\"params\":{\"author\":\"none\",\"permlink\":\"none\"}}");
            await SsrRpc.Rpc(ctx);
            Assert.True(ctx.Response.StatusCode == 200, $"status {ctx.Response.StatusCode}: {ResponseText(ctx)}");
            Assert.StartsWith("application/json", ctx.Response.ContentType);
            Assert.Equal("null", ResponseText(ctx));
        }
        finally
        {
            SsrRpc.SecretDigest = null;
        }
    }

    [Fact]
    public void Byte_budget_evicts_least_recently_used_and_refuses_oversize()
    {
        var cache = new BytesCache(100);
        cache.Set("a", new byte[40], 60_000);
        cache.Set("b", new byte[40], 60_000);
        Assert.True(cache.TryGet("a", out _)); // a is now most recent
        cache.Set("c", new byte[40], 60_000);  // evicts b
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(80, cache.Bytes);
        cache.Set("huge", new byte[101], 60_000);
        Assert.False(cache.TryGet("huge", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task Expired_entry_is_dropped_on_read()
    {
        var cache = new BytesCache(1000);
        cache.Set("k", new byte[10], 50);
        Assert.True(cache.TryGet("k", out _));
        await Task.Delay(120);
        Assert.False(cache.TryGet("k", out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void Canonical_key_sorts_object_keys_at_every_level_and_keeps_array_order()
    {
        var a = JsonNode.Parse("{\"z\":1,\"a\":{\"y\":[2,{\"d\":1,\"c\":2}],\"b\":null}}");
        var b = JsonNode.Parse("{\"a\":{\"b\":null,\"y\":[2,{\"c\":2,\"d\":1}]},\"z\":1}");
        Assert.Equal(SsrRpc.CacheKey(Post, a), SsrRpc.CacheKey(Post, b));
        var c = JsonNode.Parse("{\"a\":{\"b\":null,\"y\":[{\"c\":2,\"d\":1},2]},\"z\":1}");
        Assert.NotEqual(SsrRpc.CacheKey(Post, a), SsrRpc.CacheKey(Post, c));
    }

    private static DefaultHttpContext Request(string method, string path, string? header, string? body = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (header != null) ctx.Request.Headers[SsrRpc.HeaderName] = header;
        if (body != null)
        {
            ctx.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ResponseText(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task Without_the_secret_configured_both_routes_answer_like_unknown_routes()
    {
        // SSR_INTERNAL_SECRET is not set in the test environment.
        Assert.Null(Config.SsrInternalSecret);
        var post = Request("POST", "/private-api/ssr/rpc", "anything", "{\"api\":\"bridge\",\"method\":\"get_post\"}");
        await SsrRpc.Rpc(post);
        Assert.Equal(404, post.Response.StatusCode);
        Assert.Contains("Cannot POST /private-api/ssr/rpc", ResponseText(post));

        var get = Request("GET", "/private-api/ssr/stats", "anything");
        await SsrRpc.Stats(get);
        Assert.Equal(200, get.Response.StatusCode);
        Assert.Contains("text/html", get.Response.ContentType);
        Assert.DoesNotContain("methods", ResponseText(get));
    }

    [Fact]
    public void Authorized_requires_the_configured_secret_and_a_matching_header()
    {
        // With no secret configured nothing authorizes, header or not.
        SsrRpc.SecretDigest = null;
        Assert.False(SsrRpc.Authorized(Request("POST", "/x", null)));
        Assert.False(SsrRpc.Authorized(Request("POST", "/x", "")));
        Assert.False(SsrRpc.Authorized(Request("POST", "/x", "guess")));
    }

    [Fact]
    public void Allowlist_is_read_only_and_names_every_method_the_consumer_routes()
    {
        foreach (var key in new[]
                 {
                     "bridge.get_ranked_posts", "bridge.get_account_posts", "bridge.get_post", "bridge.get_discussion",
                     "bridge.get_profile", "bridge.get_profiles", "bridge.get_community", "bridge.list_communities",
                     "condenser_api.get_accounts", "condenser_api.get_content",
                     "condenser_api.get_dynamic_global_properties", "condenser_api.get_trending_tags",
                     "bridge.get_post_v2", "bridge.get_ranked_posts_v2", "bridge.get_account_posts_v2",
                     "bridge.get_discussion_v2",
                 })
        {
            Assert.True(SsrRpc.Allowlist.ContainsKey(key), key);
            Assert.True(SsrRpc.Allowlist[key].TtlMs > 0, key);
        }
        Assert.False(SsrRpc.Allowlist.ContainsKey("condenser_api.broadcast_transaction"));
        Assert.False(SsrRpc.Allowlist.ContainsKey("database_api.get_accounts"));
        // Votes are read by the client-side votes dialog only, and a node at the
        // head of the pool without list_votes would answer every call with an
        // RPC error (no failover, counted as a fast success).
        Assert.False(SsrRpc.Allowlist.ContainsKey("bridge.list_votes"));
    }

    [Fact]
    public void Allowlist_classifies_the_feed_shaped_reads_as_heavy()
    {
        // A page of feed rows or a whole comment tree, built per request by
        // hivemind; the rest are point reads. The pool is ordered from the
        // latency profile of the class, so a misclassified method is ranked on
        // measurements of a different call shape.
        var heavy = SsrRpc.Allowlist.Values
            .Where(p => p.Class == CallClass.Heavy)
            .Select(p => p.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "bridge.get_account_posts", "bridge.get_account_posts_v2", "bridge.get_discussion",
                "bridge.get_discussion_v2", "bridge.get_ranked_posts", "bridge.get_ranked_posts_v2",
            },
            heavy);
    }

    [Fact]
    public async Task A_heavy_read_is_measured_in_the_heavy_profile()
    {
        await using var stub = new RpcStub();
        Use(stub);

        await SsrRpc.Resolve(Ranked, new JsonObject { ["sort"] = "trending", ["tag"] = "" });
        await SsrRpc.Resolve(Post, P("a", "b"));

        var view = SsrRpc.Client.HealthSnapshot()[0]!;
        Assert.Equal(1, view["samples"]!.GetValue<int>());
        Assert.Equal(1, view["heavy_samples"]!.GetValue<int>());
    }

    [Fact]
    public async Task Stats_report_the_call_class_of_each_method_and_the_heavy_node_profile()
    {
        // The stats route is the only way to tell, on a running deployment,
        // whether a node's blended number was hiding feed-query cost, so the
        // fields that answer that have to be in the payload, not just in the
        // snapshot the route reads from.
        await using var stub = new RpcStub();
        Use(stub);
        SsrRpc.SecretDigest = SsrRpc.Digest("right-secret");
        try
        {
            await SsrRpc.Resolve(Ranked, new JsonObject { ["sort"] = "trending", ["tag"] = "" });
            await SsrRpc.Resolve(Post, P("a", "b"));

            var stats = Request("GET", "/private-api/ssr/stats", "right-secret");
            await SsrRpc.Stats(stats);
            var body = JsonNode.Parse(ResponseText(stats))!;

            Assert.True(body["call_classes"]!.GetValue<bool>());
            Assert.Equal("heavy", body["methods"]!["bridge.get_ranked_posts"]!["class"]!.GetValue<string>());
            Assert.Equal("cheap", body["methods"]!["bridge.get_post"]!["class"]!.GetValue<string>());

            var node = Assert.Single(body["nodes"]!.AsArray())!;
            Assert.Equal(1, node["samples"]!.GetValue<int>());
            Assert.Equal(1, node["heavy_samples"]!.GetValue<int>());
            Assert.NotNull(node["heavy_ewma_ms"]);
        }
        finally
        {
            SsrRpc.SecretDigest = null;
        }
    }

    [Fact]
    public async Task Call_classes_off_files_every_read_under_the_cheap_profile()
    {
        // The kill switch restores the single-profile ordering this service had
        // before call classes existed, without a rebuild.
        await using var stub = new RpcStub();
        Use(stub);
        SsrRpc.CallClasses = false;

        await SsrRpc.Resolve(Ranked, new JsonObject { ["sort"] = "trending", ["tag"] = "" });

        var view = SsrRpc.Client.HealthSnapshot()[0]!;
        Assert.Equal(1, view["samples"]!.GetValue<int>());
        Assert.Equal(0, view["heavy_samples"]!.GetValue<int>());
    }

    // ---- vote-free bridge v2 reads with a per-node v1 fallback (#119) --------

    private static SsrRpc.MethodPolicy Policy(string key)
    {
        Assert.True(SsrRpc.Allowlist.TryGetValue(key, out var policy), $"{key} is not allowlisted");
        return policy!;
    }

    private static JsonObject PV2(string author, string permlink) =>
        new() { ["author"] = author, ["permlink"] = permlink, ["top_votes"] = 0 };

    [Fact]
    public async Task A_node_that_serves_get_post_v2_answers_v2_and_the_result_is_cached()
    {
        await using var stub = new RpcStub();
        Use(stub);
        var v2 = Policy("bridge.get_post_v2");

        var first = await SsrRpc.Resolve(v2, PV2("a", "b"));
        Assert.Equal(SsrRpc.Outcome.Miss, first.Outcome);
        Assert.StartsWith("{\"method\":\"get_post_v2\"", Encoding.UTF8.GetString(first.Bytes));
        Assert.Equal(new[] { "bridge.get_post_v2" }, stub.Methods.ToArray());
        // The v2 params travel as given, top_votes included.
        Assert.Contains("\"top_votes\":0", stub.Params.Single());

        var second = await SsrRpc.Resolve(v2, PV2("a", "b"));
        Assert.Equal(SsrRpc.Outcome.Hit, second.Outcome);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(1, stub.Hits);
    }

    [Fact]
    public async Task Unknown_method_retries_the_same_node_with_v1_without_failing_it_over_and_then_goes_straight_to_v1()
    {
        await using var lacking = new RpcStub { Unknown = { "bridge.get_post_v2" } };
        await using var other = new RpcStub();
        Use(lacking);
        SsrRpc.Client = new HiveRpcClient(new[] { lacking.Url, other.Url }, timeoutMs: 1000, failoverThreshold: 1);
        var v2 = Policy("bridge.get_post_v2");

        var r = await SsrRpc.Resolve(v2, PV2("a", "b"));
        Assert.True(r.Outcome == SsrRpc.Outcome.Miss, $"{r.Outcome}: {r.Error}");
        // The v1 answer, from the same node, with the v2-only param dropped (a
        // v1 node rejects it as an unexpected keyword argument).
        Assert.StartsWith("{\"method\":\"get_post\"", Encoding.UTF8.GetString(r.Bytes));
        Assert.Equal(new[] { "bridge.get_post_v2", "bridge.get_post" }, lacking.Methods.ToArray());
        Assert.DoesNotContain("top_votes", lacking.Params.Last());
        Assert.Equal(0, other.Hits);
        var health = SsrRpc.Client.HealthSnapshot()[0]!;
        Assert.Equal(0, health["failures"]!.GetValue<long>());
        Assert.Equal(0, health["consecutive_failures"]!.GetValue<int>());

        // Cached under the requested (v2) key as returned.
        var hit = await SsrRpc.Resolve(v2, PV2("a", "b"));
        Assert.Equal(SsrRpc.Outcome.Hit, hit.Outcome);
        Assert.Equal(r.Bytes, hit.Bytes);

        // The node is now known to lack v2: the next miss goes straight to v1.
        var next = await SsrRpc.Resolve(v2, PV2("a", "c"));
        Assert.Equal(SsrRpc.Outcome.Miss, next.Outcome);
        Assert.Equal(new[] { "bridge.get_post_v2", "bridge.get_post", "bridge.get_post" }, lacking.Methods.ToArray());
        Assert.Equal(0, other.Hits);
    }

    [Fact]
    public async Task An_error_from_the_v1_retry_is_an_rpc_error_not_a_node_failure()
    {
        await using var stub = new RpcStub { Unknown = { "bridge.get_post_v2", "bridge.get_post" } };
        Use(stub);
        var r = await SsrRpc.Resolve(Policy("bridge.get_post_v2"), PV2("a", "b"));
        Assert.Equal(SsrRpc.Outcome.RpcError, r.Outcome);
        Assert.Equal(new[] { "bridge.get_post_v2", "bridge.get_post" }, stub.Methods.ToArray());
        Assert.Equal(0, SsrRpc.Client.HealthSnapshot()[0]!["failures"]!.GetValue<long>());
    }

    [Fact]
    public async Task Other_rpc_errors_on_a_v2_read_do_not_mark_the_node_or_retry_v1()
    {
        await using var stub = new RpcStub { RpcError = true };
        Use(stub);
        var v2 = Policy("bridge.get_post_v2");
        Assert.Equal(SsrRpc.Outcome.RpcError, (await SsrRpc.Resolve(v2, PV2("a", "b"))).Outcome);
        Assert.Equal(new[] { "bridge.get_post_v2" }, stub.Methods.ToArray());
        stub.RpcError = false;
        Assert.Equal(SsrRpc.Outcome.Miss, (await SsrRpc.Resolve(v2, PV2("a", "c"))).Outcome);
        Assert.Equal(new[] { "bridge.get_post_v2", "bridge.get_post_v2" }, stub.Methods.ToArray());
    }

    [Fact]
    public async Task Once_the_mark_has_lapsed_v2_is_tried_first_again()
    {
        await using var stub = new RpcStub { Unknown = { "bridge.get_post_v2" } };
        Use(stub);
        long now = 1_000_000;
        SsrRpc.Client = new HiveRpcClient(new[] { stub.Url }, 1000, 1, () => now);
        var v2 = Policy("bridge.get_post_v2");

        await SsrRpc.Resolve(v2, PV2("a", "1"));
        Assert.Equal(new[] { "bridge.get_post_v2", "bridge.get_post" }, stub.Methods.ToArray());

        // Still inside the mark: straight to v1.
        now += HiveRpcClient.UnsupportedMethodTtlMs - 1;
        await SsrRpc.Resolve(v2, PV2("a", "2"));
        Assert.Equal("bridge.get_post", stub.Methods.Last());
        Assert.Equal(3, stub.Hits);

        // The node has been upgraded meanwhile; past the mark it is asked v2 again.
        stub.Unknown.Clear();
        now += 2;
        var r = await SsrRpc.Resolve(v2, PV2("a", "3"));
        Assert.Equal(SsrRpc.Outcome.Miss, r.Outcome);
        Assert.Equal("bridge.get_post_v2", stub.Methods.Last());
        Assert.Equal(4, stub.Hits);
        Assert.StartsWith("{\"method\":\"get_post_v2\"", Encoding.UTF8.GetString(r.Bytes));
    }

    [Fact]
    public async Task Stats_list_the_v2_methods_with_their_class_and_the_node_marks()
    {
        await using var stub = new RpcStub { Unknown = { "bridge.get_ranked_posts_v2" } };
        Use(stub);
        SsrRpc.SecretDigest = SsrRpc.Digest("right-secret");
        try
        {
            await SsrRpc.Resolve(Policy("bridge.get_post_v2"), PV2("a", "b"));
            await SsrRpc.Resolve(Policy("bridge.get_ranked_posts_v2"), new JsonObject { ["sort"] = "trending", ["tag"] = "" });
            await SsrRpc.Resolve(Policy("bridge.get_account_posts_v2"), new JsonObject { ["sort"] = "posts", ["account"] = "a" });
            await SsrRpc.Resolve(Policy("bridge.get_discussion_v2"), P("a", "b"));

            var stats = Request("GET", "/private-api/ssr/stats", "right-secret");
            await SsrRpc.Stats(stats);
            var body = JsonNode.Parse(ResponseText(stats))!;
            var methods = body["methods"]!;
            Assert.Equal("cheap", methods["bridge.get_post_v2"]?["class"]?.GetValue<string>());
            Assert.Equal("heavy", methods["bridge.get_ranked_posts_v2"]?["class"]?.GetValue<string>());
            Assert.Equal("heavy", methods["bridge.get_account_posts_v2"]?["class"]?.GetValue<string>());
            Assert.Equal("heavy", methods["bridge.get_discussion_v2"]?["class"]?.GetValue<string>());
            Assert.Equal(1, methods["bridge.get_ranked_posts_v2"]?["miss"]?.GetValue<long>());

            // Which node lacks which v2 read, so the fallback can be retired
            // once the list stays empty.
            var node = Assert.Single(body["nodes"]!.AsArray())!;
            var unsupported = node["unsupported"]?.AsArray().Select(m => m!.GetValue<string>()).ToArray();
            Assert.Equal(new[] { SsrRpc.V2Family }, unsupported);
        }
        finally
        {
            SsrRpc.SecretDigest = null;
        }
    }

    [Fact]
    public void V2_twins_take_the_v1_ttl_and_class()
    {
        foreach (var v1 in new[] { "bridge.get_post", "bridge.get_ranked_posts", "bridge.get_account_posts", "bridge.get_discussion" })
        {
            var one = Policy(v1);
            var two = Policy(v1 + "_v2");
            Assert.Equal(one.TtlMs, two.TtlMs);
            Assert.Equal(one.Class, two.Class);
            Assert.Equal(one.Method, two.Fallback);
            Assert.Null(one.Fallback);
        }
    }

    [Fact]
    public async Task A_param_echoed_into_an_assert_or_another_methods_unknown_method_does_not_mark_the_node()
    {
        // hivemind echoes params into its -32602 asserts ("Post a/x does not
        // exist", "invalid tag `x`"), so a crafted permlink must not be able to
        // mark a v2-capable node as lacking v2.
        await using var stub = new RpcStub { CustomError = "Post a/unknown method does not exist" };
        Use(stub);
        var v2 = Policy("bridge.get_post_v2");
        Assert.Equal(SsrRpc.Outcome.RpcError, (await SsrRpc.Resolve(v2, PV2("a", "unknown method"))).Outcome);
        // An echo that even ends with the method's name is still not the assert.
        stub.CustomError = "Post a/unknown method: bridge_apiget_post_v2";
        Assert.Equal(SsrRpc.Outcome.RpcError, (await SsrRpc.Resolve(v2, PV2("a", "tag"))).Outcome);
        // An unknown-method assert that names a different method is not this one.
        stub.CustomError = "unknown method: bridge_apiget_ranked_posts_v2";
        Assert.Equal(SsrRpc.Outcome.RpcError, (await SsrRpc.Resolve(v2, PV2("a", "other"))).Outcome);
        Assert.Equal(new[] { "bridge.get_post_v2", "bridge.get_post_v2", "bridge.get_post_v2" }, stub.Methods.ToArray());

        stub.CustomError = null;
        var ok = await SsrRpc.Resolve(v2, PV2("a", "real"));
        Assert.StartsWith("{\"method\":\"get_post_v2\"", Encoding.UTF8.GetString(ok.Bytes));
        Assert.Empty(SsrRpc.Client.HealthSnapshot()[0]!["unsupported"]!.AsArray());
    }

    [Fact]
    public async Task Positional_params_on_a_v2_read_answer_like_an_unknown_route()
    {
        // The v1 fallback drops `top_votes` by name; a positional one would reach
        // a v1 node as one argument too many, so v2 reads take objects only.
        await using var stub = new RpcStub();
        Use(stub);
        SsrRpc.SecretDigest = SsrRpc.Digest("s");
        try
        {
            var arr = Request("POST", "/private-api/ssr/rpc", "s",
                "{\"api\":\"bridge\",\"method\":\"get_post_v2\",\"params\":[\"a\",\"b\",\"\",0]}");
            await SsrRpc.Rpc(arr);
            Assert.Equal(404, arr.Response.StatusCode);
            Assert.Contains("Cannot POST /private-api/ssr/rpc", ResponseText(arr));
            Assert.Equal(0, stub.Hits);

            var obj = Request("POST", "/private-api/ssr/rpc", "s",
                "{\"api\":\"bridge\",\"method\":\"get_post_v2\",\"params\":{\"author\":\"a\",\"permlink\":\"b\"}}");
            await SsrRpc.Rpc(obj);
            Assert.Equal(200, obj.Response.StatusCode);
            Assert.Equal(1, stub.Hits);
        }
        finally
        {
            SsrRpc.SecretDigest = null;
        }
    }

    [Fact]
    public async Task The_mark_belongs_to_the_node_that_lacks_the_method_not_to_its_neighbours()
    {
        // The capable node is first in the pool; the lacking one is only reached
        // while the capable one is out, and then the capable one is back.
        await using var capable = new RpcStub { Status = 503 };
        await using var lacking = new RpcStub { Unknown = { "bridge.get_post_v2" } };
        Use(capable);
        SsrRpc.Client = new HiveRpcClient(new[] { capable.Url, lacking.Url }, timeoutMs: 1000, failoverThreshold: 1);
        var v2 = Policy("bridge.get_post_v2");

        var first = await SsrRpc.Resolve(v2, PV2("a", "1"));
        Assert.True(first.Outcome == SsrRpc.Outcome.Miss, $"{first.Outcome}: {first.Error}");
        Assert.Equal(new[] { "bridge.get_post_v2", "bridge.get_post" }, lacking.Methods.ToArray());

        capable.Status = 200;
        lacking.Status = 503;
        var second = await SsrRpc.Resolve(v2, PV2("a", "2"));
        Assert.True(second.Outcome == SsrRpc.Outcome.Miss, $"{second.Outcome}: {second.Error}");
        Assert.Equal("bridge.get_post_v2", capable.Methods.Last());
        Assert.StartsWith("{\"method\":\"get_post_v2\"", Encoding.UTF8.GetString(second.Bytes));

        var snap = SsrRpc.Client.HealthSnapshot();
        Assert.Empty(snap[0]!["unsupported"]!.AsArray());
        Assert.Equal(SsrRpc.V2Family, Assert.Single(snap[1]!["unsupported"]!.AsArray())!.GetValue<string>());

        // And the marked node, reached again, is asked v1 directly.
        capable.Status = 503;
        lacking.Status = 200;
        var third = await SsrRpc.Resolve(v2, PV2("a", "3"));
        Assert.True(third.Outcome == SsrRpc.Outcome.Miss, $"{third.Outcome}: {third.Error}");
        Assert.StartsWith("{\"method\":\"get_post\"", Encoding.UTF8.GetString(third.Bytes));
        Assert.Equal(1, lacking.Methods.Count(m => m == "bridge.get_post_v2"));
    }

    [Fact]
    public async Task A_fallback_attempt_records_the_latency_of_the_v1_answer_not_of_the_probe()
    {
        // The node is asked v1 directly for the next half hour, so the latency
        // that ranks it is the v1 answer's; the one-off v2 probe is excluded.
        await using var stub = new RpcStub { Unknown = { "bridge.get_post_v2" }, UnknownDelayMs = 400 };
        Use(stub);
        var r = await SsrRpc.Resolve(Policy("bridge.get_post_v2"), PV2("a", "b"));
        Assert.Equal(SsrRpc.Outcome.Miss, r.Outcome);
        var node = SsrRpc.Client.HealthSnapshot()[0]!;
        Assert.Equal(1, node["samples"]!.GetValue<int>());
        Assert.True(node["ewma_ms"]!.GetValue<double>() < 300, $"ewma {node["ewma_ms"]}");
    }

    [Fact]
    public async Task A_cold_burst_against_a_v1_only_node_completes_in_one_probe_plus_v1_each()
    {
        // Distinct keys, so the cache's single-flight does not coalesce them,
        // and the probe is slow, so every call reaches the node before any mark
        // exists. Each may probe (up to N), but none waits for another.
        await using var stub = new RpcStub { Unknown = { "bridge.get_post_v2" }, UnknownDelayMs = 300 };
        Use(stub);
        var v2 = Policy("bridge.get_post_v2");
        const int n = 8;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, n).Select(i => SsrRpc.Resolve(v2, PV2("a", "p" + i))));
        sw.Stop();
        Assert.All(results, r => Assert.True(r.Outcome == SsrRpc.Outcome.Miss, $"{r.Outcome}: {r.Error}"));
        Assert.All(results, r => Assert.StartsWith("{\"method\":\"get_post\"", Encoding.UTF8.GetString(r.Bytes)));
        Assert.InRange(stub.Methods.Count(m => m == "bridge.get_post_v2"), 1, n);
        Assert.Equal(n, stub.Methods.Count(m => m == "bridge.get_post"));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        // Each sample is the v1 answer, not the probe before it.
        var node = SsrRpc.Client.HealthSnapshot()[0]!;
        Assert.Equal(n, node["samples"]!.GetValue<int>());
        Assert.True(node["ewma_ms"]!.GetValue<double>() < 250, $"ewma {node["ewma_ms"]}");
    }

    [Fact]
    public async Task A_burst_with_a_hanging_first_node_fails_over_in_parallel_not_in_a_queue()
    {
        // Regression: when calls waited for one probe, a probe that hung made
        // waiter k wait k node timeouts.
        await using var hanging = new RpcStub { DelayMs = 5_000 };
        await using var healthy = new RpcStub();
        Use(hanging);
        SsrRpc.Client = new HiveRpcClient(new[] { hanging.Url, healthy.Url }, timeoutMs: 300, failoverThreshold: 1);
        var v2 = Policy("bridge.get_post_v2");
        const int n = 8;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            var r = await SsrRpc.Resolve(v2, PV2("a", "p" + i));
            return (r, t.ElapsedMilliseconds);
        }));
        sw.Stop();
        Assert.All(results, x => Assert.True(x.r.Outcome == SsrRpc.Outcome.Miss, $"{x.r.Outcome}: {x.r.Error}"));
        // Queued behind each other the last call would land near 8 x 300 ms.
        Assert.True(results.Max(x => x.ElapsedMilliseconds) < 1200, $"slowest call took {results.Max(x => x.ElapsedMilliseconds)} ms");
        Assert.Equal(n, healthy.Methods.Count(m => m == "bridge.get_post_v2"));
    }

    [Fact]
    public async Task A_burst_on_a_v2_node_runs_in_parallel_cold_and_after_the_ttl()
    {
        await using var stub = new RpcStub { DelayMs = 400 };
        Use(stub);
        long now = 1_000_000;
        SsrRpc.Client = new HiveRpcClient(new[] { stub.Url }, 1000, 1, () => now);
        var v2 = Policy("bridge.get_post_v2");
        const int n = 6;

        async Task Burst(string author)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                var r = await SsrRpc.Resolve(v2, PV2(author, "p" + i));
                return (r, t.ElapsedMilliseconds);
            }));
            // One round trip each: none of them waited for another's answer.
            Assert.All(results, x => Assert.True(x.r.Outcome == SsrRpc.Outcome.Miss, $"{x.r.Outcome}: {x.r.Error}"));
            Assert.All(results, x => Assert.True(x.ElapsedMilliseconds < 750, $"one call took {x.ElapsedMilliseconds} ms"));
        }

        await Burst("cold");
        now += HiveRpcClient.UnsupportedMethodTtlMs + 1;
        await Burst("later");
        Assert.Equal(2 * n, stub.Methods.Count(m => m == "bridge.get_post_v2"));
        Assert.Equal(0, stub.Methods.Count(m => m == "bridge.get_post"));
    }


    [Fact]
    public async Task One_unknown_method_answer_marks_all_four_v2_reads_on_that_node()
    {
        await using var stub = new RpcStub
        {
            Unknown = { "bridge.get_post_v2", "bridge.get_ranked_posts_v2", "bridge.get_account_posts_v2", "bridge.get_discussion_v2" },
        };
        Use(stub);
        await SsrRpc.Resolve(Policy("bridge.get_post_v2"), PV2("a", "b"));
        await SsrRpc.Resolve(Policy("bridge.get_ranked_posts_v2"), new JsonObject { ["sort"] = "trending", ["tag"] = "" });
        await SsrRpc.Resolve(Policy("bridge.get_account_posts_v2"), new JsonObject { ["sort"] = "posts", ["account"] = "a" });
        await SsrRpc.Resolve(Policy("bridge.get_discussion_v2"), P("a", "b"));
        Assert.Equal(
            new[] { "bridge.get_post_v2", "bridge.get_post", "bridge.get_ranked_posts", "bridge.get_account_posts", "bridge.get_discussion" },
            stub.Methods.ToArray());
    }

    [Fact]
    public async Task The_same_node_may_serve_as_params_and_as_fallback_params()
    {
        await using var stub = new RpcStub { Unknown = { "bridge.get_post_v2" } };
        var client = new HiveRpcClient(new[] { stub.Url }, timeoutMs: 1000, failoverThreshold: 1);
        var p = P("a", "b");
        var result = await client.CallMethod("bridge.get_post_v2", p, fallback: ("bridge.get_post", p, "bridge.*_v2"));
        Assert.Equal("get_post", result?["method"]?.GetValue<string>());
    }

}
