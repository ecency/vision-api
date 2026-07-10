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
    public async Task AllNodesDown_Throws()
    {
        await using var bad1 = new StubNode(() => 500);
        await using var bad2 = new StubNode(() => 502);

        var client = new HiveRpcClient(new[] { bad1.Url, bad2.Url }, timeoutMs: 1000, failoverThreshold: 1);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.Call("condenser_api", "get_accounts", new JsonArray()));
    }
}
