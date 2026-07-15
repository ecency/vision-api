using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// Exercises EngineRpcClient's health-aware failover against local stub HTTP
/// servers: a dead / error-payload / rate-limited node must roll over to the
/// next healthy node, the find calls must treat a missing result array as a
/// node failure, and the raw passthrough must preserve non-overload responses.
/// </summary>
public class EngineFailoverTests
{
    private const string ResultBody =
        "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":[{\"symbol\":\"LEO\",\"balance\":\"1.0\"}]}";

    /// <summary>Minimal loopback HTTP server returning a scripted (status, body) per request.</summary>
    private sealed class StubNode : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<(int Status, string Body)> _handler;
        public string Url { get; } // no trailing slash: the client appends the path
        public int Hits;
        public volatile string? LastPathAndQuery;

        public StubNode(Func<(int, string)> handler)
        {
            _handler = handler;
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
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

                Interlocked.Increment(ref Hits);
                LastPathAndQuery = ctx.Request.Url?.PathAndQuery;
                var (status, bodyText) = _handler();
                var body = Encoding.UTF8.GetBytes(bodyText);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch { /* client may have moved on */ }
            }
        }

        public static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await Task.CompletedTask;
        }
    }

    private static EngineRpcClient Client(params string[] nodes) =>
        new(nodes, Array.Empty<KeyValuePair<string, string>>());

    private static JsonObject FindPayload() => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "find",
        ["params"] = new JsonObject { ["contract"] = "tokens", ["table"] = "balances" },
        ["id"] = "1",
    };

    [Fact]
    public async Task Find_DeadNode_FailsOverToHealthyNode()
    {
        var deadUrl = $"http://127.0.0.1:{StubNode.GetFreePort()}"; // nothing listening
        await using var good = new StubNode(() => (200, ResultBody));

        var result = await Client(deadUrl, good.Url).Find(FindPayload());

        Assert.Single(result);
        Assert.Equal("LEO", result[0]!["symbol"]!.GetValue<string>());
        Assert.Equal(1, good.Hits);
    }

    [Fact]
    public async Task Find_ErrorPayload_IsANodeFailure_FailsOver()
    {
        // A healthy node always returns a result array for these fixed find
        // queries; an error payload means the node is broken, not the query.
        await using var broken = new StubNode(() => (200, "{\"error\":{\"message\":\"contract store unavailable\"}}"));
        await using var good = new StubNode(() => (200, ResultBody));

        var result = await Client(broken.Url, good.Url).Find(FindPayload());

        Assert.Single(result);
        Assert.Equal(1, broken.Hits);
        Assert.Equal(1, good.Hits);
    }

    [Fact]
    public async Task Find_RateLimitedNode_IsParkedOnSubsequentCalls()
    {
        await using var limited = new StubNode(() => (429, "rate limited"));
        await using var good = new StubNode(() => (200, ResultBody));

        var client = Client(limited.Url, good.Url);
        await client.Find(FindPayload());
        await client.Find(FindPayload());

        // First call burns one hit discovering the 429; the park keeps the
        // second call away entirely.
        Assert.Equal(1, limited.Hits);
        Assert.Equal(2, good.Hits);
    }

    [Fact]
    public async Task Find_FailedNode_IsDeprioritizedOnSubsequentCalls()
    {
        await using var bad = new StubNode(() => (503, "bad gateway"));
        await using var good = new StubNode(() => (200, ResultBody));

        var client = Client(bad.Url, good.Url);
        await client.Find(FindPayload());
        var badAfterFirst = bad.Hits;
        await client.Find(FindPayload());

        Assert.Equal(badAfterFirst, bad.Hits); // recent failure: not retried
        Assert.Equal(2, good.Hits);
    }

    [Fact]
    public async Task Find_AllNodesFailing_Throws()
    {
        await using var bad1 = new StubNode(() => (500, "boom"));
        await using var bad2 = new StubNode(() => (200, "{\"error\":\"nope\"}"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Client(bad1.Url, bad2.Url).Find(FindPayload()));
        Assert.Equal(1, bad1.Hits);
        Assert.Equal(1, bad2.Hits);
    }

    [Fact]
    public async Task ContractsRaw_TransportFailure_FailsOver()
    {
        var deadUrl = $"http://127.0.0.1:{StubNode.GetFreePort()}";
        await using var good = new StubNode(() => (200, ResultBody));

        var resp = await Client(deadUrl, good.Url).ContractsRaw(FindPayload());

        Assert.Equal(200, resp.Status);
        Assert.NotNull(resp.Json?["result"]);
    }

    [Fact]
    public async Task ContractsRaw_NonOverloadResponse_PipesAsIs_NoFailover()
    {
        // A 200 error payload (bad caller query) belongs to the caller — the
        // passthrough must not shop it around the pool.
        await using var first = new StubNode(() => (200, "{\"error\":\"unknown contract\"}"));
        await using var second = new StubNode(() => (200, ResultBody));

        var resp = await Client(first.Url, second.Url).ContractsRaw(FindPayload());

        Assert.Equal(200, resp.Status);
        Assert.Equal("unknown contract", resp.Json?["error"]?.GetValue<string>());
        Assert.Equal(0, second.Hits);
    }

    [Fact]
    public async Task ContractsRaw_AllOverloaded_ReturnsLastUpstreamResponse()
    {
        await using var bad1 = new StubNode(() => (503, "overloaded"));
        await using var bad2 = new StubNode(() => (502, "bad gateway"));

        var resp = await Client(bad1.Url, bad2.Url).ContractsRaw(FindPayload());

        Assert.True(resp.Status is 502 or 503);
    }

    [Fact]
    public async Task ContractsRaw_RateLimitedNode_IsParkedOnSubsequentCalls()
    {
        await using var limited = new StubNode(() => (429, "rate limited"));
        await using var good = new StubNode(() => (200, ResultBody));

        var client = Client(limited.Url, good.Url);
        var first = await client.ContractsRaw(FindPayload());
        var second = await client.ContractsRaw(FindPayload());

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(1, limited.Hits); // parked after the discovery hit
        Assert.Equal(2, good.Hits);
    }

    [Fact]
    public void ParseRetryAfterMs_HugeDeltaSeconds_SaturatesInsteadOfOverflowing()
    {
        var ms = NodeHealthTracker.ParseRetryAfterMs("2147484"); // *1000 would overflow int
        Assert.NotNull(ms);
        Assert.True(ms > 0);
    }

    [Fact]
    public async Task GetRaw_DeadNode_FailsOver_PreservingPathAndQuery()
    {
        var deadUrl = $"http://127.0.0.1:{StubNode.GetFreePort()}";
        await using var good = new StubNode(() => (200, "[{\"symbol\":\"LEO\"}]"));

        var query = new[]
        {
            new KeyValuePair<string, string?>("account", "good-karma"),
            new KeyValuePair<string, string?>("symbol", "LEO"),
        };
        var resp = await Client(deadUrl, good.Url)
            .GetRaw("/accountHistory", query, perAttemptTimeoutMs: 2000, maxAttempts: 2);

        Assert.Equal(200, resp.Status);
        Assert.Equal("/accountHistory?account=good-karma&symbol=LEO", good.LastPathAndQuery);
    }

    [Fact]
    public async Task ContractsRaw_MaxAttempts_CapsPoolWalk()
    {
        await using var bad1 = new StubNode(() => (503, "x"));
        await using var bad2 = new StubNode(() => (503, "x"));
        await using var bad3 = new StubNode(() => (503, "x"));
        await using var good = new StubNode(() => (200, ResultBody));

        var resp = await Client(bad1.Url, bad2.Url, bad3.Url, good.Url)
            .ContractsRaw(FindPayload(), maxAttempts: 3);

        // The cap stops the walk before the healthy 4th node; the last 503 is returned.
        Assert.Equal(503, resp.Status);
        Assert.Equal(0, good.Hits);
    }
}
