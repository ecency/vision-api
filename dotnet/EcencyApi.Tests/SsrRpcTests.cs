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
                var n = Interlocked.Increment(ref Hits);
                string reqBody;
                using (var reader = new StreamReader(ctx.Request.InputStream))
                {
                    reqBody = await reader.ReadToEndAsync();
                }
                var method = JsonNode.Parse(reqBody)?["params"]?[1]?.GetValue<string>() ?? "?";
                if (DelayMs > 0) await Task.Delay(DelayMs);
                var body = RpcError
                    ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"message\":\"stub error\"}}"
                    : "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"method\":\"" + method + "\",\"n\":" + n + ",\"text\":\"caf\\u00e9 \\ud83d\"}}";
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                try { await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close(); } catch { }
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
    private static readonly SsrRpc.MethodPolicy Props = SsrRpc.Allowlist["condenser_api.get_dynamic_global_properties"];

    private static void Use(RpcStub stub, long cacheBytes = 1 << 20, int budgetMs = 1500, int maxFills = 64)
    {
        SsrRpc.Client = new HiveRpcClient(new[] { stub.Url }, timeoutMs: 1000, failoverThreshold: 1);
        SsrRpc.Cache = new BytesCache(cacheBytes);
        SsrRpc.BudgetMs = budgetMs;
        SsrRpc.FillGate = new SemaphoreSlim(maxFills, maxFills);
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
                 })
        {
            Assert.True(SsrRpc.Allowlist.ContainsKey(key), key);
            Assert.True(SsrRpc.Allowlist[key].TtlMs > 0, key);
        }
        Assert.False(SsrRpc.Allowlist.ContainsKey("condenser_api.broadcast_transaction"));
        Assert.False(SsrRpc.Allowlist.ContainsKey("database_api.get_accounts"));
    }
}
