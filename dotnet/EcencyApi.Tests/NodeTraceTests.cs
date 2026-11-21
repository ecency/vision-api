using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The node trace records, per node and per second, the upstream requests sent
/// and how each ended, and logs each node's minute as one line, so a node's
/// limiter can be fitted offline (its rate, its burst, whether it counts
/// refused requests).
/// </summary>
public class NodeTraceTests
{
    private const long T0 = 1_700_000_040_000; // a minute boundary (unix ms)

    private static (NodeTrace Trace, List<string> Lines, Action<long> SetNow) Make(params string[] nodes)
    {
        long now = T0;
        var lines = new List<string>();
        var trace = new NodeTrace(nodes, () => now, lines.Add, "ssr");
        return (trace, lines, t => now = t);
    }

    private static int[] Field(string line, string name)
    {
        var part = line.Split(' ').Single(p => p.StartsWith(name + "="));
        var value = part[(name.Length + 1)..];
        return value == "-" ? new int[60] : value.Split(',').Select(int.Parse).ToArray();
    }

    [Fact]
    public void AMinute_IsLoggedAsOneLinePerNode_WithEachSecondsCounts()
    {
        var (trace, lines, setNow) = Make("a.example", "b.example");
        trace.Record(0, T0 + 500, NodeTrace.Outcome.Answered);
        trace.Record(0, T0 + 900, NodeTrace.Outcome.RateLimited);
        trace.Record(0, T0 + 59_100, NodeTrace.Outcome.Failed);
        trace.Record(1, T0 + 3_000, NodeTrace.Outcome.Answered);

        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Flush();

        Assert.Equal(2, lines.Count);
        var a = lines.Single(l => l.Contains(" node=a.example "));
        Assert.StartsWith("node-trace pool=ssr ", a);
        Assert.Contains(" minute=" + T0 / 1000 + " ", a);
        var sent = Field(a, "sent");
        Assert.Equal(60, sent.Length);
        Assert.Equal(2, sent[0]);
        Assert.Equal(1, sent[59]);
        Assert.Equal(1, Field(a, "ok")[0]);
        Assert.Equal(1, Field(a, "rl")[0]);
        Assert.Equal(1, Field(a, "fail")[59]);
        Assert.Equal(1, Field(lines.Single(l => l.Contains(" node=b.example ")), "ok")[3]);
    }

    [Fact]
    public void AMinute_IsHeldBackUntilItsSlowRequestsCanHaveEnded()
    {
        var (trace, lines, setNow) = Make("a.example");
        trace.Record(0, T0 + 59_000, NodeTrace.Outcome.Answered);

        setNow(T0 + 60_000 + NodeTrace.GraceMs - 1);
        trace.Flush();
        Assert.Empty(lines);

        // A slow request sent in that minute ends now: still its minute.
        trace.Record(0, T0 + 59_500, NodeTrace.Outcome.Failed);
        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Flush();

        var line = Assert.Single(lines);
        Assert.Equal(2, Field(line, "sent")[59]);
    }

    [Fact]
    public void ARequestEndingAfterItsMinuteWasLogged_IsCountedAsLate_OnTheNextLine()
    {
        var (trace, lines, setNow) = Make("a.example");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Answered);
        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Flush();

        trace.Record(0, T0 + 2_000, NodeTrace.Outcome.Failed); // its minute is gone
        trace.Record(0, T0 + 61_000, NodeTrace.Outcome.Answered);
        setNow(T0 + 120_000 + NodeTrace.GraceMs);
        trace.Flush();

        Assert.Equal(2, lines.Count);
        Assert.Contains(" late=1", lines[1]);
        Assert.Contains(" late=0", lines[0]);
    }

    [Fact]
    public void AMinuteANodeWasNotSentAnything_IsNotLogged()
    {
        var (trace, lines, setNow) = Make("a.example", "b.example");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Answered);
        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Flush();

        Assert.Single(lines);
    }

    [Fact]
    public void RetryAfters_AreSummarisedPerMinute()
    {
        var (trace, lines, setNow) = Make("a.example");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.RateLimited, retryAfterMs: 3_000);
        trace.Record(0, T0 + 2_000, NodeTrace.Outcome.RateLimited, retryAfterMs: 1_000);
        trace.Record(0, T0 + 3_000, NodeTrace.Outcome.RateLimited);
        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Flush();

        Assert.Contains(" retry_after=2:1000:3000", Assert.Single(lines));
    }

    [Fact]
    public void Recording_NeverWrites_SoASlowLogCannotHoldUpARequest()
    {
        var (trace, lines, setNow) = Make("a.example");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Answered);
        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Record(0, T0 + 60_000 + NodeTrace.GraceMs, NodeTrace.Outcome.Answered);

        Assert.Empty(lines);
        trace.Flush();
        Assert.Single(lines);
    }

    [Fact]
    public async Task TheFlushTimer_LogsAnIdleNodesLastMinute()
    {
        long now = T0;
        var written = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trace = new NodeTrace(new[] { "a.example" }, () => Interlocked.Read(ref now), l => written.TrySetResult(l), "ssr");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Answered);
        Interlocked.Exchange(ref now, T0 + 60_000 + NodeTrace.GraceMs); // no request follows

        using (trace.StartFlushing(TimeSpan.FromMilliseconds(20)))
        {
            Assert.Contains(" node=a.example ", await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void WhileTheLogIsStalled_OnlyTheNewestMinutesAreKept()
    {
        var (trace, lines, setNow) = Make("a.example");
        for (var i = 0; i <= NodeTrace.MaxOpenMinutes; i++)
            trace.Record(0, T0 + i * 60_000L, NodeTrace.Outcome.Answered);
        setNow(T0 + (NodeTrace.MaxOpenMinutes + 1) * 60_000L + NodeTrace.GraceMs);
        trace.Flush();

        Assert.Equal(NodeTrace.MaxOpenMinutes, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains(" minute=" + T0 / 1000 + " "));
    }

    [Fact]
    public void ALineTheLogFailsOn_DoesNotLoseTheOthers()
    {
        long now = T0;
        var lines = new List<string>();
        var trace = new NodeTrace(new[] { "a.example", "b.example" }, () => now,
            l => { if (l.Contains("node=a.example")) throw new IOException("busy"); lines.Add(l); }, "ssr");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Answered);
        trace.Record(1, T0 + 1_000, NodeTrace.Outcome.Answered);
        now = T0 + 60_000 + NodeTrace.GraceMs;
        trace.Flush();

        Assert.Contains(" node=b.example ", Assert.Single(lines));
    }

    [Fact]
    public void Timeouts_5xxAndOtherFailures_AreCountedApart()
    {
        var (trace, lines, setNow) = Make("a.example");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Timeout);
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.ServerError);
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.ServerError);
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Failed);
        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Flush();

        var line = Assert.Single(lines);
        Assert.Equal(4, Field(line, "sent")[1]);
        Assert.Equal(1, Field(line, "timeout")[1]);
        Assert.Equal(2, Field(line, "e5xx")[1]);
        Assert.Equal(1, Field(line, "fail")[1]);
        Assert.Contains(" ok=- ", line);
    }

    [Fact]
    public void AnsweredLatency_IsSummedAndItsMaximumKept_AndInFlightItsMaximum()
    {
        var (trace, lines, setNow) = Make("a.example");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Answered, latencyMs: 100, inFlight: 3);
        trace.Record(0, T0 + 1_500, NodeTrace.Outcome.Answered, latencyMs: 400, inFlight: 7);
        trace.Record(0, T0 + 1_600, NodeTrace.Outcome.RateLimited, latencyMs: 9_000, inFlight: 2);
        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Flush();

        var line = Assert.Single(lines);
        Assert.Equal(500, Field(line, "ok_ms")[1]);
        Assert.Equal(400, Field(line, "max_ms")[1]);
        Assert.Equal(7, Field(line, "inflight")[1]);
    }

    [Fact]
    public void TheFirstLimiterHeadersOfTheMinute_AreKept_AsOneToken()
    {
        var (trace, lines, setNow) = Make("a.example", "b.example");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.RateLimited, headers: "server:nginx/1.2 (Ubuntu);ratelimit-limit:10");
        trace.Record(0, T0 + 2_000, NodeTrace.Outcome.RateLimited, headers: "server:other");
        trace.Record(1, T0 + 2_000, NodeTrace.Outcome.Answered);
        setNow(T0 + 60_000 + NodeTrace.GraceMs);
        trace.Flush();

        Assert.Contains(" hdr=server:nginx/1.2_(Ubuntu);ratelimit-limit:10 ", lines.Single(l => l.Contains("node=a.example")));
        Assert.Contains(" hdr=- ", lines.Single(l => l.Contains("node=b.example")));
    }

    [Fact]
    public void TheDefaultClock_IsUnixTime()
    {
        var clock = NodeTrace.MonotonicUnixMs();
        Assert.InRange(clock() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), -1_000, 1_000);
    }

    [Fact]
    public void AFailingLog_DoesNotReachTheCaller()
    {
        long now = T0;
        var trace = new NodeTrace(new[] { "a.example" }, () => now, _ => throw new IOException("stdout closed"), "ssr");
        trace.Record(0, T0 + 1_000, NodeTrace.Outcome.Answered);
        now = T0 + 60_000 + NodeTrace.GraceMs;

        trace.Flush(); // the log throws
    }
}
