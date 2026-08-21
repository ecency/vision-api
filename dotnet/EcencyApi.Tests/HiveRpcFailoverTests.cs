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

        /// <summary>Whether this node serves account metadata. Nodes that strip it
        /// answer with a well-formed account whose posting_json_metadata is empty.</summary>
        public bool ServesMetadata = true;

        // A healthy node's get_accounts result. Account-metadata presence matters:
        // GetAccounts prefers a node that serves it, so the default stub carries it.
        private string AccountResultBody =>
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[{\"name\":\"served-by\",\"port\":\"" + Url
            + "\",\"posting_json_metadata\":" + (ServesMetadata ? "\"{\\\"profile\\\":{}}\"" : "\"\"") + "}]}";

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
                        AccountResultBody);
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
                        AccountResultBody);
                    status = 200;
                }
                else if (status == -3)
                {
                    // Malformed 200: valid JSON, no error field, no usable result —
                    // the shape observed from misbehaving production nodes.
                    body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1}");
                    status = 200;
                }
                else if (status == -4)
                {
                    // RPC-level error: the node answered, the error is the
                    // application's (no failover, no health penalty).
                    body = Encoding.UTF8.GetBytes(
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"message\":\"boom\"}}");
                    status = 200;
                }
                else if (status == -5)
                {
                    // Well-formed array whose entries are scalars: passes a bare
                    // "is an array" check but carries no readable account.
                    body = Encoding.UTF8.GetBytes(
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[\"invalid\"]}");
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
    public async Task DeadNode_IsParkedAfterThreeTimeouts_AndSkippedWhileOthersServe()
    {
        // A node that never answers used to keep its "unexplored" standing
        // (timeouts below the slow-failure floor left no latency sample, and a
        // failure only demoted it for 30s), so it was retried every window and
        // took whole bursts of concurrent calls. Now: a timeout is a latency
        // sample, three failures in a row park it, and a parked node is skipped
        // while any other node is available.
        await using var dead = new StubNode(() => -1);      // hangs past the timeout
        var flakyStatus = 200;
        await using var flaky = new StubNode(() => flakyStatus);
        var goodStatus = 200;
        await using var good = new StubNode(() => goodStatus);

        long now = 0;
        var client = new HiveRpcClient(new[] { dead.Url, flaky.Url, good.Url }, timeoutMs: 300, failoverThreshold: 1, clock: () => now);

        // Three calls, each past the previous one's 30s recent-failure window:
        // the dead node (config order, all unproven) is tried first every time.
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) now += 31_000;
            await client.Call("condenser_api", "get_accounts", new JsonArray());
        }
        // The stub's accept loop is single-threaded and its hang outlives the
        // client timeout, so later attempts queue in the listener backlog and
        // never reach its hit counter; the client's own per-node counters are
        // the measure of what was attempted.
        var view = client.HealthSnapshot()[0]!;
        Assert.Equal(3, view["calls"]!.GetValue<long>());
        Assert.Equal(3, view["timeouts"]!.GetValue<long>());
        Assert.Equal(3, view["samples"]!.GetValue<int>());       // the timeouts ARE latency samples
        // ...floored at the unproven prior: a 300ms timeout must not make a
        // node that never answered look faster than nodes never tried.
        Assert.True(view["ewma_ms"]!.GetValue<double>() > 1000);
        Assert.True(view["parked_for_ms"]!.GetValue<long>() > 0); // parked after the third

        // The leader hiccups: with the dead node parked, the call skips it and
        // fails over from flaky straight to good instead of handing the dead
        // node the burst.
        flakyStatus = 500;
        await client.Call("condenser_api", "get_accounts", new JsonArray());
        flakyStatus = 200;
        Assert.Equal(3, client.HealthSnapshot()[0]!["calls"]!.GetValue<long>());
        Assert.True(good.Hits >= 1);

        // The park lapses (30s). Its timeouts were recorded as latency (floored
        // above the unproven prior), so the dead node now ranks behind the
        // proven leader AND behind the never-tried node: a leader hiccup goes
        // to the untried node, not to it.
        now += 31_000;
        flakyStatus = 500;
        await client.Call("condenser_api", "get_accounts", new JsonArray());
        Assert.Equal(3, client.HealthSnapshot()[0]!["calls"]!.GetValue<long>());

        // Only when every other node fails is it probed: one attempt, which
        // fails and re-parks it for twice as long.
        flakyStatus = 500;
        goodStatus = 500;
        await Assert.ThrowsAnyAsync<Exception>(() => client.Call("condenser_api", "get_accounts", new JsonArray()));
        flakyStatus = 200;
        goodStatus = 200;
        view = client.HealthSnapshot()[0]!;
        Assert.Equal(4, view["calls"]!.GetValue<long>());
        Assert.True(view["parked_for_ms"]!.GetValue<long>() > 30_000);

        // Inside the doubled park no probe is made, even past the recent-failure window.
        now += 45_000;
        await client.Call("condenser_api", "get_accounts", new JsonArray());
        Assert.Equal(4, client.HealthSnapshot()[0]!["calls"]!.GetValue<long>());
    }

    [Fact]
    public async Task ParkingEndsTheSameNodeRetry_WithTheDefaultFailoverThreshold()
    {
        // failoverThreshold 2 (the default client) retries a node once before
        // moving on. The failure that parks the node must end that retry too,
        // or the parked node gets one more attempt, one more timeout, and a
        // park that starts out twice as long.
        await using var dead = new StubNode(() => -1);
        await using var good = new StubNode(() => 200);
        long now = 0;
        var client = new HiveRpcClient(new[] { dead.Url, good.Url }, timeoutMs: 200, failoverThreshold: 2, clock: () => now);

        await client.Call("condenser_api", "get_accounts", new JsonArray()); // dead: 2 attempts, then good
        var view = client.HealthSnapshot()[0]!;
        Assert.Equal(2, view["calls"]!.GetValue<long>());
        Assert.Equal(0, view["parked_for_ms"]!.GetValue<long>());

        now += 31_000;
        await client.Call("condenser_api", "get_accounts", new JsonArray()); // third failure parks: ONE attempt
        view = client.HealthSnapshot()[0]!;
        Assert.Equal(3, view["calls"]!.GetValue<long>());
        Assert.Equal(30_000, view["parked_for_ms"]!.GetValue<long>());
    }

    [Fact]
    public async Task AllNodesFailureParked_AreStillTried()
    {
        // A pool that is entirely parked degrades to "try them", never to
        // "try nothing": the caller gets the node error, not a synthetic one.
        await using var dead = new StubNode(() => -1);
        long now = 0;
        var client = new HiveRpcClient(new[] { dead.Url }, timeoutMs: 200, failoverThreshold: 1, clock: () => now);
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.Call("condenser_api", "get_accounts", new JsonArray()));
        }
        Assert.Equal(3, client.HealthSnapshot()[0]!["calls"]!.GetValue<long>());
        Assert.True(client.HealthSnapshot()[0]!["parked_for_ms"]!.GetValue<long>() > 0);
        await Assert.ThrowsAnyAsync<Exception>(() => client.Call("condenser_api", "get_accounts", new JsonArray()));
        Assert.Equal(4, client.HealthSnapshot()[0]!["calls"]!.GetValue<long>());
    }

    [Fact]
    public async Task RateLimits_DoNotCountTowardFailureParking()
    {
        // 429s have their own parking and a throttled node is responsive: two
        // 429s and one hard failure must not hard-park it.
        var status = 429;
        await using var throttled = new StubNode(() => status);
        await using var good = new StubNode(() => 200);
        long now = 0;
        var client = new HiveRpcClient(new[] { throttled.Url, good.Url }, timeoutMs: 500, failoverThreshold: 1, clock: () => now);

        await client.Call("condenser_api", "get_accounts", new JsonArray()); // 429 -> rate-limit parked
        now += 61_000;                                                         // that park lapses
        await client.Call("condenser_api", "get_accounts", new JsonArray()); // 429 again
        now += 61_000;
        status = 500;
        await client.Call("condenser_api", "get_accounts", new JsonArray()); // one hard failure
        var view = client.HealthSnapshot()[0]!;
        Assert.Equal(3, view["calls"]!.GetValue<long>());
        Assert.Equal(2, view["rate_limited"]!.GetValue<long>());
        Assert.Equal(0, view["parked_for_ms"]!.GetValue<long>());
    }

    [Fact]
    public async Task ASuccessClearsAFailurePark()
    {
        // An all-parked pool offers every node; the one that recovers must not
        // be excluded again by its stale park deadline once another node's
        // park lapses.
        var mode = -1;
        await using var flapping = new StubNode(() => mode);
        long now = 0;
        var client = new HiveRpcClient(new[] { flapping.Url }, timeoutMs: 200, failoverThreshold: 1, clock: () => now);
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.Call("condenser_api", "get_accounts", new JsonArray()));
        }
        Assert.True(client.HealthSnapshot()[0]!["parked_for_ms"]!.GetValue<long>() > 0);
        // The only node, so it is still offered; it recovers and answers.
        mode = 200;
        await Task.Delay(3500); // let the stub's hung handlers drain before it can answer
        await client.Call("condenser_api", "get_accounts", new JsonArray());
        Assert.Equal(0, client.HealthSnapshot()[0]!["parked_for_ms"]!.GetValue<long>());
        Assert.Equal(0, client.HealthSnapshot()[0]!["consecutive_failures"]!.GetValue<int>());
    }

    [Fact]
    public void DefaultPool_DoesNotCarryTheUnreachableNode()
    {
        Assert.DoesNotContain(HiveClients.DefaultNodes, n => n.Contains("arcange", StringComparison.Ordinal));
        Assert.True(HiveClients.DefaultNodes.Count >= 6);
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

    // A node can strip account metadata: balances and reputation are correct but
    // posting_json_metadata comes back empty. That is a well-formed array, so shape
    // validation passes and the latency EWMA happily keeps such a node first —
    // silently blanking portfolio engine/chain token visibility, which is derived
    // entirely from that field. GetAccounts routes around it.
    [Fact]
    public async Task MetadataStrippingNode_IsSkippedForAccountFetches()
    {
        await using var stripped = new StubNode(() => 200) { ServesMetadata = false };
        await using var full = new StubNode(() => 200);

        var client = new HiveRpcClient(new[] { stripped.Url, full.Url }, timeoutMs: 1500);

        var accounts = await client.GetAccounts(new[] { "good-karma" });

        Assert.NotNull(accounts);
        var meta = accounts![0]!["posting_json_metadata"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(meta), "should have used the node serving metadata");
        Assert.Equal(1, stripped.Hits); // consulted once, no same-node retry
        Assert.True(full.Hits >= 1);
    }

    // The preference is soft: an account that genuinely has no metadata looks
    // identical to a stripped response, so once no node can do better the answer
    // is returned rather than failing the request.
    [Fact]
    public async Task NoNodeServesMetadata_StillReturnsTheAccount()
    {
        await using var a = new StubNode(() => 200) { ServesMetadata = false };
        await using var b = new StubNode(() => 200) { ServesMetadata = false };

        var client = new HiveRpcClient(new[] { a.Url, b.Url }, timeoutMs: 1500);

        var accounts = await client.GetAccounts(new[] { "good-karma" });

        Assert.NotNull(accounts);
        Assert.Equal("served-by", accounts![0]!["name"]!.GetValue<string>());
        // The *first* well-formed answer is the floor, not whichever probe ran last.
        Assert.Equal(a.Url, accounts[0]!["port"]!.GetValue<string>());
    }

    // The probe is optional, so its failure must not fail the request: an RPC-level
    // error from the node we only consulted to improve on an answer we already hold
    // would otherwise turn a call that previously succeeded into a hard failure.
    [Fact]
    public async Task PreferenceProbeHittingRpcError_StillReturnsTheFirstAnswer()
    {
        await using var stripped = new StubNode(() => 200) { ServesMetadata = false };
        await using var erroring = new StubNode(() => -4);

        var client = new HiveRpcClient(new[] { stripped.Url, erroring.Url }, timeoutMs: 1500);

        var accounts = await client.GetAccounts(new[] { "good-karma" });

        Assert.NotNull(accounts);
        Assert.Equal(stripped.Url, accounts![0]!["port"]!.GetValue<string>());
    }

    // A node answering with scalar entries passes a bare "is an array" check but
    // reads as empty downstream — the same silent blanking a metadata-stripping
    // node causes, so it must fail over rather than be accepted.
    [Fact]
    public async Task ScalarAccountArray_FailsOverAsUnusable()
    {
        await using var scalar = new StubNode(() => -5);
        await using var good = new StubNode(() => 200);

        var client = new HiveRpcClient(new[] { scalar.Url, good.Url }, timeoutMs: 1500);

        var accounts = await client.GetAccounts(new[] { "good-karma" });

        Assert.NotNull(accounts);
        Assert.Equal(good.Url, accounts![0]!["port"]!.GetValue<string>());
        Assert.Equal(1, scalar.Hits); // unusable result advances immediately
    }

    // Probing is bounded: an account with no metadata is a common case that no node
    // can satisfy, so the pool must not be swept on every such request.
    [Fact]
    public async Task MetadataPreference_ProbesAtMostTwoNodes()
    {
        await using var a = new StubNode(() => 200) { ServesMetadata = false };
        await using var b = new StubNode(() => 200) { ServesMetadata = false };
        await using var c = new StubNode(() => 200) { ServesMetadata = false };
        await using var d = new StubNode(() => 200) { ServesMetadata = false };

        var client = new HiveRpcClient(new[] { a.Url, b.Url, c.Url, d.Url }, timeoutMs: 1500);

        Assert.NotNull(await client.GetAccounts(new[] { "good-karma" }));

        Assert.Equal(2, a.Hits + b.Hits + c.Hits + d.Hits);
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
