using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// Exercises the dhive-style node failover in HiveRpcClient against local stub
/// HTTP servers: rate-limit / overload / timeout on one node must transparently
/// roll over to the next healthy node, and a healthy node becomes sticky.
/// </summary>
public class HiveRpcFailoverTests
{
    /// <summary>Minimal loopback HTTP server returning a scripted response per request.</summary>
    private sealed class StubNode : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<int> _handler; // returns HTTP status; 200 => valid RPC result
        public string Url { get; }
        public int Hits;

        public StubNode(Func<int> handler)
        {
            _handler = handler;
            var port = GetFreePort();
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

                Interlocked.Increment(ref Hits);
                var status = _handler();
                byte[] body;
                if (status == 200)
                {
                    body = Encoding.UTF8.GetBytes(
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[{\"name\":\"served-by\",\"port\":\"" + Url + "\"}]}");
                }
                else if (status == -1)
                {
                    // Simulate a hang/timeout: delay past the client timeout, then close.
                    await Task.Delay(3000);
                    body = Encoding.UTF8.GetBytes("{}");
                    status = 200;
                }
                else if (status == -2)
                {
                    // Slow but successful: answers correctly after 1.5s (above the
                    // 1s unproven prior, below any test timeout).
                    await Task.Delay(1500);
                    body = Encoding.UTF8.GetBytes(
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[{\"name\":\"served-by\",\"port\":\"" + Url + "\"}]}");
                    status = 200;
                }
                else if (status == -3)
                {
                    // Malformed 200: valid JSON, no error field, no usable result —
                    // the shape observed from misbehaving production nodes.
                    body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1}");
                    status = 200;
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("rate limited");
                }

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

        private static int GetFreePort()
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

    [Fact]
    public async Task RateLimitedNode_FailsOverToHealthyNode()
    {
        await using var bad = new StubNode(() => 429);   // always rate-limited
        await using var good = new StubNode(() => 200);  // healthy

        var client = new HiveRpcClient(new[] { bad.Url, good.Url }, timeoutMs: 1500);

        var result = await client.Call("condenser_api", "get_accounts", new JsonArray());

        Assert.NotNull(result);
        Assert.Equal("served-by", result![0]!["name"]!.GetValue<string>());
        // A 429 must advance immediately — exactly one hit on the bad node, no wasted retry.
        Assert.Equal(1, bad.Hits);
        Assert.True(good.Hits >= 1);
    }

    [Fact]
    public async Task HealthyNode_BecomesSticky()
    {
        await using var bad = new StubNode(() => 503);
        await using var good = new StubNode(() => 200);

        var client = new HiveRpcClient(new[] { bad.Url, good.Url }, timeoutMs: 1500);

        await client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray());
        var badAfterFirst = bad.Hits;

        // Second call should go straight to the now-sticky healthy node.
        await client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray());

        Assert.Equal(badAfterFirst, bad.Hits); // bad node not touched again
        Assert.True(good.Hits >= 2);
    }

    [Fact]
    public async Task TimeoutNode_FailsOverToHealthyNode()
    {
        await using var slow = new StubNode(() => -1);   // hangs past the timeout
        await using var good = new StubNode(() => 200);

        var client = new HiveRpcClient(new[] { slow.Url, good.Url }, timeoutMs: 800, failoverThreshold: 1);

        var result = await client.Call("condenser_api", "get_accounts", new JsonArray());

        Assert.NotNull(result);
        Assert.Equal("served-by", result![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProvenSlowNode_IsDemotedByLatencyEwma()
    {
        // Adopted from @ecency/sdk's NodeHealthTracker: once a node's latency
        // EWMA is trusted (3 samples) and exceeds the unproven prior (1s), an
        // unexplored node is tried first.
        await using var slow = new StubNode(() => -2);   // responds 200 after 1.5s
        await using var fast = new StubNode(() => 200);

        var client = new HiveRpcClient(new[] { slow.Url, fast.Url }, timeoutMs: 5000);

        // three successful-but-slow calls build a trusted ~1500ms EWMA
        for (var i = 0; i < 3; i++)
        {
            await client.Call("condenser_api", "get_accounts", new JsonArray());
        }
        Assert.Equal(3, slow.Hits);
        Assert.Equal(0, fast.Hits);

        // fourth call: slow node scores ~1500 > 1000 prior -> fast node explored first
        await client.Call("condenser_api", "get_accounts", new JsonArray());
        Assert.Equal(3, slow.Hits);
        Assert.True(fast.Hits >= 1);
    }

    [Fact]
    public async Task MalformedResultNode_FailsOverWithoutRetry()
    {
        await using var malformed = new StubNode(() => -3); // 200, valid JSON, no result
        await using var good = new StubNode(() => 200);

        var client = new HiveRpcClient(new[] { malformed.Url, good.Url }, timeoutMs: 1500);

        var accounts = await client.GetAccounts(new[] { "good-karma" });

        Assert.NotNull(accounts);
        Assert.Equal("served-by", accounts![0]!["name"]!.GetValue<string>());
        // Validation failure advances immediately — one hit, no same-node retry.
        Assert.Equal(1, malformed.Hits);
        Assert.True(good.Hits >= 1);
    }

    [Fact]
    public async Task MalformedResultNode_IsDemotedOnSubsequentCalls()
    {
        await using var malformed = new StubNode(() => -3);
        await using var good = new StubNode(() => 200);

        var client = new HiveRpcClient(new[] { malformed.Url, good.Url }, timeoutMs: 1500);

        await client.GetAccounts(new[] { "good-karma" });
        var malformedAfterFirst = malformed.Hits;
        await client.GetAccounts(new[] { "good-karma" });

        Assert.Equal(malformedAfterFirst, malformed.Hits); // recent failure: not tried again
        Assert.True(good.Hits >= 2);
    }

    [Fact]
    public async Task AllNodesMalformed_ThrowsNamingTheNode()
    {
        await using var bad1 = new StubNode(() => -3);
        await using var bad2 = new StubNode(() => -3);

        var client = new HiveRpcClient(new[] { bad1.Url, bad2.Url }, timeoutMs: 1500);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetAccounts(new[] { "good-karma" }));
        Assert.Contains("unusable get_accounts result", ex.Message);
        Assert.Contains("http://127.0.0.1:", ex.Message); // node URL for diagnosability
    }

    [Fact]
    public async Task AllNodesDown_Throws()
    {
        await using var bad1 = new StubNode(() => 500);
        await using var bad2 = new StubNode(() => 502);

        var client = new HiveRpcClient(new[] { bad1.Url, bad2.Url }, timeoutMs: 1000, failoverThreshold: 1);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.Call("condenser_api", "get_accounts", new JsonArray()));
    }
}
