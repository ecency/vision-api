using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// Learned per-node sending rates (vision-api#128): the Hive pool learns how
/// fast each node lets this host call it and stays under that, instead of
/// sending everything to the fastest node until it answers 429.
/// </summary>
public class NodeRateLearningTests
{
    private const int Pause = NodeHealthTracker.RateLimitFirstPauseMs;

    /// <summary>A node's per-IP limiter: a token bucket, like nginx limit_req.</summary>
    private sealed class FakeLimiter
    {
        private readonly double _rate;
        private readonly double _burst;
        private double _tokens;
        private long _at;

        public FakeLimiter(double rate, double burst, long now)
        {
            _rate = rate;
            _burst = burst;
            _tokens = burst;
            _at = now;
        }

        public bool Take(long now)
        {
            _tokens = Math.Min(_burst, _tokens + _rate * (now - _at) / 1000.0);
            _at = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }

    /// <summary>
    /// Demand of 3N calls/s against one node that allows N/s. Calls the node
    /// has no rate for are dropped here; in the client they go to other nodes.
    /// Returns (calls answered per second, share of sent calls answered 429),
    /// measured after a warm-up.
    /// </summary>
    private static (double AnsweredPerS, double RateLimitedShare) Simulate(bool learnRates, double limit)
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates);
        var limiter = new FakeLimiter(limit, burst: limit * 2, now);
        const int stepMs = 10;
        var perStep = 3 * limit * stepMs / 1000.0;
        double owed = 0;
        long sent = 0, limited = 0;
        const long warmupMs = 60_000, endMs = 240_000;
        for (now = 0; now < endMs; now += stepMs)
        {
            owed += perStep;
            while (owed >= 1)
            {
                owed -= 1;
                var admission = tracker.Admit(0, allowRateLimitParked: false, rateUse: NodeHealthTracker.RateUse.Require);
                if (admission != NodeHealthTracker.Admission.Admitted) continue;
                var ok = limiter.Take(now);
                if (ok) tracker.RecordSuccess(0, 50, CallClass.Cheap);
                else tracker.RecordRateLimited(0, null, sentAtMs: now);
                tracker.EndAttempt(0);
                if (now < warmupMs) continue;
                sent++;
                if (!ok) limited++;
            }
        }
        var seconds = (endMs - warmupMs) / 1000.0;
        return ((sent - limited) / seconds, sent == 0 ? 0 : (double)limited / sent);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(20)]
    public void ALimitedNode_IsUsedNearItsLimit_WithFewRateLimits(double limit)
    {
        var (answeredPerS, limitedShare) = Simulate(learnRates: true, limit);

        Assert.InRange(answeredPerS, 0.6 * limit, 1.05 * limit);
        Assert.True(limitedShare < 0.05, $"429 share {limitedShare:P1}");
    }

    [Fact]
    public void WithoutLearnedRates_TheSameNodeSitsParkedMostOfTheTime()
    {
        // The baseline the learned rate replaces, kept as a contrast (the Engine
        // client still runs this way): the node is overrun, parked, overrun
        // again, and answers a fraction of what it allows.
        var (answeredPerS, _) = Simulate(learnRates: false, limit: 20);

        Assert.True(answeredPerS < 0.5 * 20, $"answered {answeredPerS:F1}/s of 20");
    }

    [Fact]
    public void ARateLimit_HalvesTheLearnedRate_AndLimitedWindowsRaiseItAgain()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        var start = tracker.Snapshot()[0].RatePerS;

        tracker.RecordRateLimited(0, null);
        Assert.Equal(start / 2, tracker.Snapshot()[0].RatePerS, 3);

        // Demand above the rate for several windows, all answered: it climbs.
        for (var w = 0; w < 5; w++)
        {
            now += 2_000; // past any park
            while (tracker.Admit(0, allowRateLimitParked: false, rateUse: NodeHealthTracker.RateUse.Require) == NodeHealthTracker.Admission.Admitted)
            {
                tracker.RecordSuccess(0, 50, CallClass.Cheap);
                tracker.EndAttempt(0);
            }
        }
        now += 1_000;
        tracker.Admit(0, allowRateLimitParked: false);
        Assert.True(tracker.Snapshot()[0].RatePerS > start / 2 + 1);
    }

    [Fact]
    public void RateLimitsInOneBurst_HalveTheRateOnce()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        var start = tracker.Snapshot()[0].RatePerS;

        // Calls already in flight come back 429 together: one signal, not five.
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordRateLimited(0, null);
            now += 50;
        }

        Assert.Equal(start / 2, tracker.Snapshot()[0].RatePerS, 3);
    }

    [Fact]
    public void AnIdleNode_DoesNotGrowItsRate()
    {
        // Growth needs demand that ran into the rate; otherwise an idle node
        // would come back from a quiet hour with a rate it never proved.
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        var start = tracker.Snapshot()[0].RatePerS;
        for (var i = 0; i < 60; i++)
        {
            now += 1_000;
            Assert.Equal(NodeHealthTracker.Admission.Admitted, tracker.Admit(0, allowRateLimitParked: false, rateUse: NodeHealthTracker.RateUse.Require));
            tracker.RecordSuccess(0, 50, CallClass.Cheap);
            tracker.EndAttempt(0);
        }

        Assert.Equal(start, tracker.Snapshot()[0].RatePerS, 3);
    }

    [Fact]
    public void ARetryAfter_IsHonouredExactly_WithLearnedRates()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);

        tracker.RecordRateLimited(0, 3_000);

        Assert.Equal(3_000, tracker.RateLimitedForMs(0));
    }

    [Fact]
    public void RepeatedRetryAfterRateLimits_WithNoAnswerBetween_ClimbTheLadder()
    {
        // A node that refuses every call while naming a short wait is still
        // refusing us: honouring only its header would retry it each second.
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);

        tracker.RecordRateLimited(0, 1_000, sentAtMs: now);
        Assert.Equal(1_000, tracker.RateLimitedForMs(0));

        now = 1_500;
        tracker.RecordRateLimited(0, 1_000, sentAtMs: now);
        Assert.Equal(10_000, tracker.RateLimitedForMs(0));

        now = 12_000;
        tracker.RecordRateLimited(0, 1_000, sentAtMs: now);
        Assert.Equal(20_000, tracker.RateLimitedForMs(0));

        // An answer ends the streak: the next named wait is honoured as is.
        now = 33_000;
        tracker.RecordSuccess(0, 50, CallClass.Cheap);
        tracker.RecordRateLimited(0, 1_000, sentAtMs: now);
        Assert.Equal(1_000, tracker.RateLimitedForMs(0));
    }

    [Fact]
    public void TheRate_DoesNotGrow_WhenNothingIsAnswered()
    {
        // Calls that never come back (a hung node) must not raise its rate
        // just because more calls queued for it than it had rate for.
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        var start = tracker.Snapshot()[0].RatePerS;
        for (var s = 0; s < 20; s++)
        {
            for (var i = 0; i < 20; i++) tracker.Admit(0, allowRateLimitParked: false, NodeHealthTracker.RateUse.Require);
            now += 1_000;
        }

        Assert.Equal(start, tracker.Snapshot()[0].RatePerS, 3);
    }

    [Fact]
    public void ANewNode_DoublesItsRateUntilItsFirstRateLimit_ThenGrowsByAddition()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        void SaturatedSecond()
        {
            while (tracker.Admit(0, allowRateLimitParked: false, NodeHealthTracker.RateUse.Require) == NodeHealthTracker.Admission.Admitted)
            {
                tracker.RecordSuccess(0, 50, CallClass.Cheap);
                tracker.EndAttempt(0);
            }
            now += 1_000;
        }

        for (var w = 0; w < 4; w++) SaturatedSecond();
        tracker.Admit(0, allowRateLimitParked: false); // rolls the last window
        tracker.EndAttempt(0);
        Assert.Equal(80, tracker.Snapshot()[0].RatePerS, 3);

        tracker.RecordRateLimited(0, null, sentAtMs: now);
        Assert.Equal(40, tracker.Snapshot()[0].RatePerS, 3);
        now += Pause + 500;
        SaturatedSecond();
        tracker.Admit(0, allowRateLimitParked: false);
        tracker.EndAttempt(0);
        Assert.Equal(40.5, tracker.Snapshot()[0].RatePerS, 3);
    }

    [Fact]
    public void OverRateOrder_PutsARecentlyRateLimitedNodeLast()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(2, () => now, learnRates: true);
        tracker.RecordRateLimited(0, null, sentAtMs: now);
        now = 2_000; // its pause is over, the 429 is recent
        for (var i = 0; i < 10; i++)
        {
            tracker.Admit(1, allowRateLimitParked: false, NodeHealthTracker.RateUse.Overdraw);
            tracker.EndAttempt(1);
        }

        // Node 1 is deeper over its rate, but node 0 just refused a call.
        Assert.Equal(new[] { 1, 0 }, tracker.OverRateOrder(new[] { 0, 1 }));

        now = 12_000; // no longer recent
        Assert.Equal(0, tracker.OverRateOrder(new[] { 0, 1 })[0]);
    }

    [Fact]
    public void WithLearnedRates_AFirstHeaderlessRateLimit_PausesBriefly_AndRepeatsEscalate()
    {
        // A node at its limit gets a short pause: the halved rate is what keeps
        // it from 429ing again. A node that 429s again without answering
        // anything in between is refusing us, and gets the long ladder.
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);

        tracker.RecordRateLimited(0, null);
        Assert.Equal(Pause, tracker.RateLimitedForMs(0));

        now += Pause + 500;
        tracker.RecordRateLimited(0, null);
        Assert.Equal(10_000, tracker.RateLimitedForMs(0));

        now += 11_000;
        tracker.RecordSuccess(0, 50, CallClass.Cheap);
        tracker.RecordRateLimited(0, null);
        Assert.Equal(Pause, tracker.RateLimitedForMs(0));
    }

    [Fact]
    public void WithLearnedRates_ARateLimit_IsNotARecentFailure()
    {
        // The halved rate is the response. Demoting the node as well left one
        // at its limit idle for as long as the others could take the traffic.
        long now = 0;
        var learning = new NodeHealthTracker(1, () => now, learnRates: true);
        var plain = new NodeHealthTracker(1, () => now);

        learning.RecordRateLimited(0, null, sentAtMs: now);
        plain.RecordRateLimited(0, null);

        Assert.False(learning.Snapshot()[0].RecentFailure);
        Assert.True(plain.Snapshot()[0].RecentFailure);
    }

    [Fact]
    public void ProbeRateLimitsFromOneBurst_ClimbOneStep_NotTheWholeLadder()
    {
        // Every node parked: each call in flight probes the soonest-lapsing
        // one, so several probes are out when the first 429 comes back. The
        // rest are the same burst; if each climbed a step, one burst would
        // park the node for 60s.
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        tracker.RecordRateLimited(0, null, sentAtMs: 0); // first pause
        now = 1_000;
        var sentAt = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            tracker.Admit(0, allowRateLimitParked: true, NodeHealthTracker.RateUse.Spend, out var at);
            sentAt.Add(at);
            now += 10;
        }
        foreach (var at in sentAt)
        {
            now += 20;
            tracker.RecordRateLimited(0, null, sentAtMs: at);
            tracker.EndAttempt(0);
        }

        Assert.InRange(tracker.RateLimitedForMs(0), 9_000, 10_000);
    }

    [Fact]
    public void AfterARefusalEnds_TheRateRampsBackByDoubling()
    {
        // A node that refused for a while had its rate halved at each lapse.
        // Once it answers again it doubles back instead of adding 0.5/s.
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        for (var i = 0; i < 6; i++)
        {
            tracker.RecordRateLimited(0, null, sentAtMs: now);
            now += 61_000;
        }
        var floor = tracker.Snapshot()[0].RatePerS;
        Assert.True(floor < 1, $"rate {floor}");

        for (var w = 0; w < 6; w++)
        {
            while (tracker.Admit(0, allowRateLimitParked: false, NodeHealthTracker.RateUse.Require) == NodeHealthTracker.Admission.Admitted)
            {
                tracker.RecordSuccess(0, 50, CallClass.Cheap);
                tracker.EndAttempt(0);
            }
            now += 1_000;
        }
        tracker.Admit(0, allowRateLimitParked: false);
        tracker.EndAttempt(0);

        Assert.True(tracker.Snapshot()[0].RatePerS >= floor * 32, $"rate {tracker.Snapshot()[0].RatePerS} from {floor}");
    }

    [Fact]
    public void ALaterRateLimit_NeverShortensANamedWait()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        tracker.RecordRateLimited(0, 60_000, sentAtMs: 0);

        now = 1_000; // a probe's 429, a new signal: the ladder's 10s step
        tracker.RecordRateLimited(0, null, sentAtMs: 1_000);

        Assert.Equal(59_000, tracker.RateLimitedForMs(0));
    }

    [Fact]
    public void ANamedWait_IsHonoured_EvenFromACallSentBeforeTheDecrease()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        tracker.RecordRateLimited(0, null, sentAtMs: 0);

        now = 100;
        tracker.RecordRateLimited(0, 5_000, sentAtMs: 0);

        Assert.Equal(5_000, tracker.RateLimitedForMs(0));
    }

    [Fact]
    public void OverRateOrder_BreaksATieByFewestSentThisSecond()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(2, () => now, learnRates: true);
        for (var n = 0; n < 2; n++)
        {
            // Both deep in debt: their waits tie at the cap.
            for (var i = 0; i < 20 + n * 5; i++)
            {
                tracker.Admit(n, allowRateLimitParked: false, NodeHealthTracker.RateUse.Overdraw);
                tracker.EndAttempt(n);
            }
        }

        Assert.Equal(new[] { 0, 1 }, tracker.OverRateOrder(new[] { 1, 0 }));
    }

    [Fact]
    public void Debt_IsCapped_SoANodeOverdrawnInABurstRecoversInSeconds()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        for (var i = 0; i < 1_000; i++)
        {
            tracker.Admit(0, allowRateLimitParked: false, NodeHealthTracker.RateUse.Overdraw);
            tracker.EndAttempt(0);
        }

        // A second's worth of debt at most: back within rate after about 1.2s.
        now = 1_300;
        Assert.Equal(NodeHealthTracker.Admission.Admitted,
            tracker.Admit(0, allowRateLimitParked: false, NodeHealthTracker.RateUse.Require));
    }

    [Fact]
    public void WithoutLearnedRates_AdmissionIgnoresTheRate()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(NodeHealthTracker.Admission.Admitted, tracker.Admit(0, allowRateLimitParked: false, rateUse: NodeHealthTracker.RateUse.Require));
            tracker.EndAttempt(0);
        }
    }

    // ---- through the client --------------------------------------------------

    private sealed class LimitedStub : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<bool> _allow;
        private readonly int _delayMs;
        public string Url { get; }
        public int Ok, Limited;

        /// <param name="delayMs">Real time before answering, so the latency
        /// ranking (measured on the real clock) prefers the other node.</param>
        public LimitedStub(Func<bool> allow, int delayMs = 0)
        {
            _allow = allow;
            _delayMs = delayMs;
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            // Several accept loops, so calls in flight together are served together.
            for (var i = 0; i < 16; i++) _ = Loop();
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                using (var reader = new StreamReader(ctx.Request.InputStream)) await reader.ReadToEndAsync();
                if (_delayMs > 0) await Task.Delay(_delayMs);
                var allowed = _allow();
                if (allowed) Interlocked.Increment(ref Ok); else Interlocked.Increment(ref Limited);
                var body = Encoding.UTF8.GetBytes(allowed
                    ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"by\":\"" + Url + "\"}}"
                    : "rate limited");
                ctx.Response.StatusCode = allowed ? 200 : 429;
                ctx.Response.ContentLength64 = body.Length;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch { /* client moved on */ }
            }
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ALimitedNode_ServesContinuously_AndTheRestSpillsToTheNext()
    {
        // Both nodes are limited, together just above the demand. On the
        // frozen test clock both answer in 0ms, so config order ranks the
        // limited node first.
        long now = 0;
        var rateLimitedAt = new List<long>();
        var limiter = new FakeLimiter(rate: 5, burst: 10, now);
        var otherLimiter = new FakeLimiter(rate: 12, burst: 24, now);
        var perSecondOk = new Dictionary<long, int>();
        await using var fast = new LimitedStub(() =>
        {
            var ok = limiter.Take(now);
            if (ok) perSecondOk[now / 1000] = perSecondOk.GetValueOrDefault(now / 1000) + 1;
            else rateLimitedAt.Add(now);
            return ok;
        });
        await using var other = new LimitedStub(() => otherLimiter.Take(now));
        var client = new HiveRpcClient(new[] { fast.Url, other.Url }, timeoutMs: 1000, failoverThreshold: 1,
            clock: () => now);

        // 15 calls a second for 40 seconds against nodes that allow 5 and 12.
        var throttled = 0;
        for (var i = 0; i < 600; i++)
        {
            try
            {
                Assert.NotNull(await client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray()));
            }
            catch (HiveRpcClient.UpstreamRateLimitedException)
            {
                throttled++;
            }
            now += 1000 / 15;
        }
        Assert.True(throttled < 0.02 * 600, $"{throttled} calls throttled");

        // After warm-up the limited node keeps answering near its limit
        // (5/s), whichever node ranks first; the old routing overran it and
        // parked it 10 to 60s at a time. Neither node is overrun.
        var fastAnswered = Enumerable.Range(10, 30).Sum(x => perSecondOk.GetValueOrDefault(x));
        Assert.True(fastAnswered >= 0.6 * 5 * 30,
            $"limited node answered {fastAnswered} in 30s; per second " +
            string.Join(",", Enumerable.Range(0, 40).Select(x => perSecondOk.GetValueOrDefault(x))) +
            "; 429 at ms " + string.Join(",", rateLimitedAt));
        Assert.True(fast.Limited < 0.1 * fast.Ok, $"fast node: {fast.Ok} ok, {fast.Limited} 429");
        Assert.True(fast.Ok + other.Ok >= 0.95 * 600, $"answered {fast.Ok + other.Ok} of 600");
        Assert.True(other.Limited < 0.1 * other.Ok, $"other node: {other.Ok} ok, {other.Limited} 429");
    }

    [Fact]
    public async Task WhenEveryNodeIsOverItsRate_EveryCallStillGoesOut_ToTheNodeThatFreesUpSoonest()
    {
        long now = 0;
        await using var a = new LimitedStub(() => true);
        await using var b = new LimitedStub(() => true);
        var client = new HiveRpcClient(new[] { a.Url, b.Url }, timeoutMs: 1000, failoverThreshold: 1, clock: () => now);

        // The clock never moves, so no rate comes back: past the first two
        // bucketfuls, every call finds both nodes over their rate.
        for (var i = 0; i < 40; i++)
        {
            Assert.NotNull(await client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray()));
        }

        Assert.Equal(40, a.Ok + b.Ok);
        // Over-rate sends go into debt, which moves the next one to the other node.
        Assert.True(a.Ok > 10 && b.Ok > 10, $"a {a.Ok}, b {b.Ok}");
        var snap = client.HealthSnapshot();
        Assert.Equal(30, snap[0]!["over_rate"]!.GetValue<long>() + snap[1]!["over_rate"]!.GetValue<long>());
    }

    [Fact]
    public async Task UnderOverload_WithManyCallsInFlight_UpstreamRequestsStayNearOnePerCall()
    {
        // Four nodes allowing 10/s each, asked for 80/s in batches of 50 calls
        // in flight. The answered share is capped by the nodes; what this
        // guards is the cost of the rest. Coming back to a node too soon after
        // its 429 let the over-rate calls drain its limiter again at once, and
        // each call then drew several 429s (about 1.4 requests a call with a 1s
        // pause; the old 10s park cost about 1.0).
        long now = 0;
        var stubs = new List<LimitedStub>();
        for (var n = 0; n < 4; n++)
        {
            var limiter = new FakeLimiter(rate: 10, burst: 20, now);
            stubs.Add(new LimitedStub(() => { lock (limiter) return limiter.Take(Interlocked.Read(ref now)); }, delayMs: n));
        }
        try
        {
            var client = new HiveRpcClient(stubs.Select(x => x.Url).ToArray(), timeoutMs: 2000, failoverThreshold: 1,
                clock: () => Interlocked.Read(ref now));
            const int calls = 80 * 40, batch = 50;
            var answered = 0;
            for (var i = 0; i < calls; i += batch)
            {
                await Task.WhenAll(Enumerable.Range(0, batch).Select(async _ =>
                {
                    try
                    {
                        await client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray());
                        Interlocked.Increment(ref answered);
                    }
                    catch (HiveRpcClient.UpstreamRateLimitedException) { }
                }));
                Interlocked.Add(ref now, 1000 * batch / 80);
            }

            var upstream = stubs.Sum(x => x.Ok + x.Limited);
            Assert.True(upstream < 1.15 * calls, $"{upstream} upstream requests for {calls} calls");
            // A floor against collapse only: the share answered is capped by
            // the nodes (half here) and dips on a loaded runner.
            Assert.True(answered > 0.4 * calls, $"answered {answered} of {calls}");
        }
        finally
        {
            foreach (var x in stubs) await x.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnderOverload_ACallDrawsOneOverRate429_NotOnePerNode()
    {
        // Both nodes take five calls, then refuse. Once both are over their
        // rate, a call is sent over the rate to one of them. Its 429 moves the
        // call on to the other, which has not refused anything yet; that one's
        // 429 ends it. The next call meets two nodes that both refused
        // recently, and draws one 429, not two.
        int aLeft = 5, bLeft = 5;
        await using var a = new LimitedStub(() => Interlocked.Decrement(ref aLeft) >= 0);
        await using var b = new LimitedStub(() => Interlocked.Decrement(ref bLeft) >= 0);
        long now = 0;
        var client = new HiveRpcClient(new[] { a.Url, b.Url }, timeoutMs: 1000, failoverThreshold: 1, clock: () => now);
        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(await client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray()));
        }

        await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() =>
            client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray()));
        Assert.Equal(12, a.Ok + a.Limited + b.Ok + b.Limited);

        now += Pause + 500; // past both pauses, both 429s still recent
        for (var n = 0; n < 2; n++)
        {
            // Spend what rate came back, so both are over it again.
            for (var i = 0; i < 10; i++)
            {
                client.Health.Admit(n, allowRateLimitParked: false, NodeHealthTracker.RateUse.Overdraw);
                client.Health.EndAttempt(n);
            }
        }
        await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() =>
            client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray()));
        Assert.Equal(13, a.Ok + a.Limited + b.Ok + b.Limited);
    }

    [Fact]
    public async Task TheProbe_GoesToANodeParkedLongAgo_NotOneThatJustPaused()
    {
        // Node 1 refused repeatedly and sits on the ladder; its last 429 is
        // over 10s old and it has since recovered. Node 0 answered 429 a moment
        // ago and is paused for 1s, so its park lapses first. The probe must
        // still go to node 1, or a node that keeps pausing briefly takes every
        // probe and recovered nodes go unnoticed until their parks run out.
        await using var paused = new LimitedStub(() => false);
        await using var recovered = new LimitedStub(() => true);
        long now = 0;
        var client = new HiveRpcClient(new[] { paused.Url, recovered.Url }, timeoutMs: 1000, failoverThreshold: 1, clock: () => now);
        client.Health.RecordRateLimited(1, null, sentAtMs: now);          // 1s pause
        now = 1_500; client.Health.RecordRateLimited(1, null, sentAtMs: now);  // 10s
        now = 11_600; client.Health.RecordRateLimited(1, null, sentAtMs: now); // 20s, until 31.6s
        now = 21_900; client.Health.RecordRateLimited(0, null, sentAtMs: now); // 1s pause
        now = 22_000;
        Assert.True(client.Health.RateLimitedForMs(0) < client.Health.RateLimitedForMs(1));

        Assert.NotNull(await client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray()));
        Assert.Equal(1, recovered.Ok);
        Assert.Equal(0, paused.Limited);
    }

    /// <summary>A node without the v2 method: `unknown method` for it, and
    /// <paramref name="v1"/> decides the v1 fallback's status.</summary>
    private sealed class OldMethodStub : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<int> _v1;
        private readonly Action _onV2;
        public string Url { get; }
        public int V2, V1;

        public OldMethodStub(Func<int> v1, Action? onV2 = null)
        {
            _v1 = v1;
            _onV2 = onV2 ?? (() => { });
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
                string req;
                using (var reader = new StreamReader(ctx.Request.InputStream)) req = await reader.ReadToEndAsync();
                int status;
                string body;
                if (req.Contains("get_post_v2"))
                {
                    Interlocked.Increment(ref V2);
                    _onV2();
                    status = 200;
                    body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32602,\"message\":\"Assert Exception:unknown method: bridge_apiget_post_v2\"}}";
                }
                else
                {
                    Interlocked.Increment(ref V1);
                    status = _v1();
                    body = status == 200 ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"v\":1}}" : "rate limited";
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentLength64 = bytes.Length;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { /* client moved on */ }
            }
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }

    private static Task<JsonNode?> GetPostV2(HiveRpcClient client) =>
        client.CallMethod("bridge.get_post_v2", new JsonObject { ["author"] = "a", ["permlink"] = "p" },
            fallback: ("bridge.get_post", new JsonObject { ["author"] = "a", ["permlink"] = "p" }, "bridge.*_v2"));

    [Fact]
    public async Task TheSameNodeFallback_SpendsItsOwnUnitOfRate()
    {
        // `unknown method` then the v1 fallback: two requests to the node, so
        // two units of its rate, not one.
        await using var node = new OldMethodStub(() => 200);
        long now = 0;
        var client = new HiveRpcClient(new[] { node.Url }, timeoutMs: 1000, failoverThreshold: 1, clock: () => now);

        Assert.NotNull(await GetPostV2(client));
        Assert.Equal(1, node.V2);
        Assert.Equal(1, node.V1);

        var left = 0;
        while (client.Health.Admit(0, allowRateLimitParked: false, NodeHealthTracker.RateUse.Require) == NodeHealthTracker.Admission.Admitted)
        {
            client.Health.EndAttempt(0);
            left++;
        }
        Assert.Equal(3, left); // a bucket of 5, less the two requests
    }

    [Fact]
    public async Task AFallbacksRateLimit_IsTimedByTheFallbackItself()
    {
        // Between the v2 answer and the fallback, another call's 429 halves the
        // node's rate and pauses it; the pause is over by the time the fallback
        // goes out. The fallback is sent after that decrease, so its own 429 is
        // new news and halves again; timed by the first request it would read
        // as part of the earlier burst.
        HiveRpcClient client = null!;
        long now = 0;
        await using var node = new OldMethodStub(() => 429, onV2: () =>
        {
            now = 100;
            client.Health.RecordRateLimited(0, null, sentAtMs: 100);
            now = 100 + Pause + 100;
        });
        client = new HiveRpcClient(new[] { node.Url }, timeoutMs: 1000, failoverThreshold: 1, clock: () => now);
        var start = client.HealthSnapshot()[0]!["rate_per_s"]!.GetValue<double>();

        await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() => GetPostV2(client));

        Assert.Equal(start / 4, client.HealthSnapshot()[0]!["rate_per_s"]!.GetValue<double>(), 1);
    }

    [Fact]
    public async Task AFallback_IsNotSentIntoAParkThatBeganMeanwhile()
    {
        // Another call's 429 parks the node between the v2 answer and the
        // fallback. Sending the fallback anyway would draw a 429 that counts as
        // new, halving the rate again and lengthening the park.
        HiveRpcClient client = null!;
        long now = 0;
        await using var node = new OldMethodStub(() => 200, onV2: () =>
        {
            now = 100;
            client.Health.RecordRateLimited(0, null, sentAtMs: 100);
            now = 150;
        });
        client = new HiveRpcClient(new[] { node.Url }, timeoutMs: 1000, failoverThreshold: 1, clock: () => now);
        var start = client.HealthSnapshot()[0]!["rate_per_s"]!.GetValue<double>();

        await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() => GetPostV2(client));

        Assert.Equal(0, node.V1);
        Assert.Equal(start / 2, client.HealthSnapshot()[0]!["rate_per_s"]!.GetValue<double>(), 1);
        Assert.Equal(Pause - 50, client.Health.RateLimitedForMs(0));
    }

    [Fact]
    public async Task ARefusedCall_CountsTheNodesItLeftOut_InTheAdvisedWait()
    {
        // Both nodes refused at 0 and paused 1s. At 1.5s both are over their
        // rate; the call sends over the rate to one, which refuses again and
        // goes to the 10s step. The other was left out only for having
        // refused 1.5s ago and is not parked: it is tried again once that
        // refusal is 2s old, in 500ms, so that is the advice, not 10s.
        await using var a = new LimitedStub(() => false);
        await using var b = new LimitedStub(() => false);
        long now = 0;
        var client = new HiveRpcClient(new[] { a.Url, b.Url }, timeoutMs: 1000, failoverThreshold: 1, clock: () => now);
        await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() =>
            client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray()));

        now = 1_500;
        for (var n = 0; n < 2; n++)
        {
            for (var i = 0; i < 10; i++)
            {
                client.Health.Admit(n, allowRateLimitParked: false, NodeHealthTracker.RateUse.Overdraw);
                client.Health.EndAttempt(n);
            }
        }
        var ex = await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() =>
            client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray()));

        Assert.Equal(3, a.Limited + b.Limited);
        Assert.Equal(500, ex.RetryAfterMs);
    }

    [Fact]
    public async Task AWhollyThrottledPool_AfterTheFirstPause_ClimbsTheLadder()
    {
        // A first 429 pauses each node briefly. If every node 429s again right
        // after, without answering anything, it is refusing us: the next park
        // is the 10s step, and a lookup inside it costs one probe.
        await using var a = new LimitedStub(() => false);
        await using var b = new LimitedStub(() => false);
        await using var c = new LimitedStub(() => false);
        long now = 0;
        var client = new HiveRpcClient(new[] { a.Url, b.Url, c.Url }, timeoutMs: 500, failoverThreshold: 1, clock: () => now);
        int Hits() => a.Limited + b.Limited + c.Limited;

        await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() => client.Call("condenser_api", "get_accounts", new JsonArray()));
        Assert.Equal(3, Hits());

        now += Pause + 500; // past the first pauses
        await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() => client.Call("condenser_api", "get_accounts", new JsonArray()));
        Assert.Equal(6, Hits());

        now += 2_000; // inside the 10s step
        await Assert.ThrowsAsync<HiveRpcClient.UpstreamRateLimitedException>(() => client.Call("condenser_api", "get_accounts", new JsonArray()));
        Assert.Equal(7, Hits());
    }

    [Fact]
    public void RateLimitsForCallsSentBeforeTheDecrease_DoNotHalveOrParkAgain()
    {
        // Slow calls sent together come back 429 over several seconds, past
        // any fixed hold. They predate the decrease the first one caused.
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        var start = tracker.Snapshot()[0].RatePerS;

        tracker.RecordRateLimited(0, null, sentAtMs: 0);
        Assert.Equal(start / 2, tracker.Snapshot()[0].RatePerS, 3);
        Assert.Equal(Pause, tracker.RateLimitedForMs(0));

        foreach (var at in new long[] { Pause + 500, 2 * Pause })
        {
            now = at;
            tracker.RecordRateLimited(0, null, sentAtMs: 0);
            Assert.Equal(start / 2, tracker.Snapshot()[0].RatePerS, 3);
            Assert.Equal(0, tracker.RateLimitedForMs(0));
        }

        // A call sent after the decrease that still draws a 429 is new news.
        now = 2 * Pause + 100;
        tracker.RecordRateLimited(0, null, sentAtMs: 2 * Pause);
        Assert.Equal(start / 4, tracker.Snapshot()[0].RatePerS, 3);
        Assert.Equal(10_000, tracker.RateLimitedForMs(0));
    }

    [Fact]
    public void OverRate_CountsOnlyCallsSentOverTheRate_NotProbes()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        tracker.RecordRateLimited(0, 1); // empties the bucket

        tracker.Admit(0, allowRateLimitParked: true); // a probe: spends, not counted
        tracker.EndAttempt(0);
        Assert.Equal(0, tracker.Snapshot()[0].OverRate);

        Assert.Equal(NodeHealthTracker.Admission.OverRate,
            tracker.Admit(0, allowRateLimitParked: true, NodeHealthTracker.RateUse.Require));
        Assert.Equal(NodeHealthTracker.Admission.Admitted,
            tracker.Admit(0, allowRateLimitParked: true, NodeHealthTracker.RateUse.Overdraw));
        tracker.EndAttempt(0);
        Assert.Equal(1, tracker.Snapshot()[0].OverRate);
    }

    [Fact]
    public void SentLastSecond_CountsExactlyOneAlignedSecond()
    {
        long now = 0;
        var tracker = new NodeHealthTracker(1, () => now, learnRates: true);
        void Send(long at)
        {
            now = at;
            tracker.Admit(0, allowRateLimitParked: false);
            tracker.EndAttempt(0);
        }

        Send(100); Send(500); Send(900);   // second 0
        Send(1_700); Send(1_950);          // second 1
        now = 2_300;
        Assert.Equal(2, tracker.Snapshot()[0].SentLastS);

        now = 4_100; // second 2 (empty) and 3 have passed since
        Assert.Equal(0, tracker.Snapshot()[0].SentLastS);
    }

    [Fact]
    public async Task TheStatsShowEachNodesLearnedRate()
    {
        await using var only = new LimitedStub(() => true);
        var client = new HiveRpcClient(new[] { only.Url }, timeoutMs: 1000, failoverThreshold: 1);
        await client.Call("condenser_api", "get_dynamic_global_properties", new JsonArray());

        var node = client.HealthSnapshot()[0]!;

        Assert.True(node["rate_per_s"]!.GetValue<double>() > 0);
        Assert.NotNull(node["sent_last_s"]);
        Assert.NotNull(node["over_rate"]);
    }
}
