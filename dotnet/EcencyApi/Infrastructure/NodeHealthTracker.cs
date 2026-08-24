using System.Globalization;

namespace EcencyApi.Infrastructure;

/// <summary>
/// How expensive one upstream call is, for latency accounting only.
///
/// Cost is bimodal across the reads this service makes: point reads answer in a
/// few hundred milliseconds from nearly every node, while feed-shaped queries
/// cost several times that and vary by an order of magnitude BETWEEN nodes. With
/// a single latency profile per node the ranking is learned from whichever class
/// dominates by count (the cheap one) and then used to pick a node for the other.
/// That is how a node quick on point reads and slow on feed queries ends up
/// leading the pool for feed queries too.
///
/// Only *how fast* a node is differs by class. Whether it is answering at all
/// (consecutive failures, rate-limit parking, failure parking, half-open
/// admission) stays node-wide: a node that is not answering is not answering for
/// any class.
/// </summary>
public enum CallClass
{
    /// <summary>Point reads. The default, so a client that makes one shape of
    /// call keeps exactly one profile per node, as before.</summary>
    Cheap = 0,

    /// <summary>Feed-shaped queries: seconds where a point read takes
    /// milliseconds. Node-dependent in a way point reads are not.</summary>
    Heavy = 1,
}

/// <summary>
/// Per-node health bookkeeping shared by the upstream RPC clients (Hive and
/// Hive-Engine), adopting the proven design of @ecency/sdk's NodeHealthTracker
/// (simplified for a proxy that makes a handful of call shapes):
///
///  - Per-node health state: consecutive failures, rate-limit parking, and a
///    latency EWMA used to order the pool best-first.
///  - 429 parks a node for the server's Retry-After when present, else an
///    escalating window (10s doubling to 60s); the escalation streak resets
///    after 120s without a throttle. Parked nodes sort last.
///  - A node with a recent failure (30s window) is deprioritized behind clean
///    nodes, so one bad response moves traffic away without banning the node.
///  - Healthy nodes are ordered by the latency EWMA of the call class being
///    ordered (alpha 0.3, trusted after 3 samples, stale after 5 minutes); a
///    node that class has not proven scores a neutral prior (1s), so an unknown
///    node is explored before a proven-slow one. Config order breaks ties, so
///    cold start behaves exactly like the configured list.
/// </summary>
public sealed class NodeHealthTracker
{
    private const int RateLimitBaseMs = 10_000;
    private const int RateLimitMaxMs = 60_000;
    private const int RateLimitStreakResetMs = 120_000;
    private const int RecentFailureWindowMs = 30_000;
    private const double LatencyEwmaAlpha = 0.3;
    private const int LatencyMinSamples = 3;
    private const int LatencyMaxAgeMs = 5 * 60_000;
    // Score for a node whose profile for the class being ordered is not trusted
    // yet, so an unexplored node is tried before a proven-slow one. ONE prior for
    // both classes, deliberately:
    //  - It must stay below the caller's per-node timeout, or it stops separating
    //    anything. Every latency a client can observe is bounded by that timeout,
    //    so a prior above it is never exceeded by a real measurement: the first
    //    node to reach LatencyMinSamples would outrank every untried node forever
    //    and no other node would ever be sampled.
    //  - The alternative of scoring an unproven class from the node's OTHER class
    //    is worse than it looks: the two are on different scales (a heavy query
    //    costs several times a point read on the same node), so every
    //    heavy-unproven node would outrank every heavy-proven one.
    private const double LatencyUnprovenPriorMs = 1_000;
    private const int SlowFailureFloorMs = 2_000;
    // A node that fails this many times in a row is parked (30s, doubling to
    // 120s; a success clears the streak) and is not tried while any other node
    // is available. Without this a node that never answers keeps its
    // "unexplored" standing: only 429s parked, and a failure demoted for 30s at
    // most, so each time the leading nodes hiccupped the dead node took the
    // whole in-flight burst at the full per-node timeout (observed as dozens
    // of half-open connects at once to one unreachable node).
    private const int FailureParkThreshold = 3;
    private const int FailureParkBaseMs = 30_000;
    private const int FailureParkMaxMs = 120_000;
    // Under concurrency "consecutive" is not "sequential": with hundreds of
    // calls in flight, three overlapping timeouts of a heavy query satisfy the
    // count while the node is answering everything else. Parking therefore
    // also needs the node to have never answered, or to be failing most of its
    // recent calls (an EWMA of the failure fraction, alpha 0.1).
    private const double FailureRateAlpha = 0.1;
    private const double FailureRateParkFloor = 0.5;

    // The class is an index into each node's latency array, so CallClass values
    // must stay contiguous from zero.
    private static readonly int CallClassCount = Enum.GetValues<CallClass>().Length;

    /// <summary>One node's measured latency for one call class.</summary>
    private sealed class LatencyProfile
    {
        public double? EwmaMs;
        public int SampleCount;
        public long UpdatedAtMs;
    }

    private sealed class NodeHealth
    {
        public int ConsecutiveFailures;
        public long LastFailureAtMs;
        public long RateLimitedUntilMs;
        public int RateLimitStreak;
        public long LastRateLimitAtMs;
        public long FailureParkedUntilMs;
        public int FailureParkStreak;
        // Recent failure fraction (1 = every recent call failed).
        public double FailureRate;
        // Hard failures only (timeouts, refusals, bad answers), never 429s: a
        // throttled node is responsive and has its own parking.
        public int ConsecutiveHardFailures;
        // Attempts currently in flight against this node (a gauge, for the
        // half-open rule below).
        public int InFlight;
        // Latency is the one thing kept per call class; see CallClass.
        public readonly LatencyProfile[] Latency =
            Enumerable.Range(0, CallClassCount).Select(_ => new LatencyProfile()).ToArray();
        // Lifetime counters, for the stats endpoint.
        public long Calls, Successes, Failures, Timeouts, RateLimits;
    }

    /// <summary>One node's latency for one call class, as reported by
    /// <see cref="Snapshot"/>; one entry per <see cref="CallClass"/>, in enum order.</summary>
    public sealed record ClassLatencyView(CallClass Class, double? EwmaMs, int Samples);

    /// <summary>One node's health as reported by <see cref="Snapshot"/>.</summary>
    public sealed record NodeView(
        int Index, long Calls, long Successes, long Failures, long Timeouts, long RateLimits,
        IReadOnlyList<ClassLatencyView> Latency, int ConsecutiveFailures,
        bool RecentFailure, long RateLimitedForMs, long FailureParkedForMs, double FailureRate);

    private readonly NodeHealth[] _health;
    private readonly object _lock = new();
    private readonly Func<long> _clock;

    public NodeHealthTracker(int nodeCount, Func<long>? clock = null)
    {
        _health = new NodeHealth[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            _health[i] = new NodeHealth();
        }
        _clock = clock ?? (() => Environment.TickCount64);
    }

    private long NowMs => _clock();

    // ---- health bookkeeping (lock-guarded; contention is negligible) ------

    /// <param name="callClass">Which latency profile the sample belongs to.
    /// Required, not defaulted: a call site that forgets it would silently file a
    /// heavy measurement under the cheap profile and still compile and pass.</param>
    public void RecordSuccess(int nodeIndex, double elapsedMs, CallClass callClass)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            h.Calls++;
            h.Successes++;
            h.FailureRate *= 1 - FailureRateAlpha;
            h.ConsecutiveFailures = 0;
            h.ConsecutiveHardFailures = 0;
            h.RateLimitStreak = 0;
            h.FailureParkStreak = 0;
            // A node that just answered is not parked, whatever the deadline said:
            // an all-parked pool offers every node, and the one that recovers must
            // not be pushed out again by a stale deadline the moment another
            // node's park lapses.
            h.FailureParkedUntilMs = 0;
            RecordLatency(h, callClass, elapsedMs);
        }
    }

    /// <param name="timedOut">The attempt ran into the client's per-node timeout.
    /// That IS a latency sample whatever the timeout is set to; the floor below
    /// only tells instant refusals (a down node is not "slow") from slow 5xx.</param>
    /// <returns>True when this failure parked the node (or it was already
    /// parked): the caller should not retry it, a same-node retry would only
    /// add another timeout and lengthen the park.</returns>
    /// <param name="callClass">Which latency profile the sample belongs to. The
    /// failure itself is node-wide health either way.</param>
    public bool RecordFailure(int nodeIndex, double elapsedMs, CallClass callClass, bool timedOut = false)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            var now = NowMs;
            h.Calls++;
            h.Failures++;
            if (timedOut) h.Timeouts++;
            h.FailureRate = h.FailureRate * (1 - FailureRateAlpha) + FailureRateAlpha;
            h.ConsecutiveFailures++;
            h.ConsecutiveHardFailures++;
            h.LastFailureAtMs = now;
            if (timedOut)
            {
                // A timeout says "at least this slow". Floored at the unproven
                // prior so a short client timeout cannot rank a node that never
                // answered ahead of nodes that were never tried.
                RecordLatency(h, callClass, Math.Max(elapsedMs, LatencyUnprovenPriorMs + 1));
            }
            // Only a slow failure is a latency statement; an instant refusal says
            // the node is down, not that it is slow. Under a per-node timeout
            // below this floor the branch cannot be reached at all, so every
            // failure sample that class records is the censored timeout above.
            else if (elapsedMs >= SlowFailureFloorMs)
            {
                RecordLatency(h, callClass, elapsedMs);
            }
            var notAnswering = h.Successes == 0 || h.FailureRate >= FailureRateParkFloor;
            if (h.ConsecutiveHardFailures >= FailureParkThreshold && notAnswering)
            {
                var parkMs = Math.Min(FailureParkBaseMs << Math.Min(h.FailureParkStreak, 2), FailureParkMaxMs);
                h.FailureParkedUntilMs = now + parkMs;
                h.FailureParkStreak++;
            }
            return h.FailureParkedUntilMs > now;
        }
    }

    public void RecordRateLimited(int nodeIndex, int? retryAfterMs)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            var now = NowMs;
            h.Calls++;
            h.RateLimits++;
            h.ConsecutiveFailures++;
            h.LastFailureAtMs = now;
            if (now - h.LastRateLimitAtMs > RateLimitStreakResetMs)
            {
                h.RateLimitStreak = 0;
            }
            var parkMs = retryAfterMs
                         ?? Math.Min(RateLimitBaseMs << Math.Min(h.RateLimitStreak, 3), RateLimitMaxMs);
            h.RateLimitedUntilMs = now + parkMs;
            h.RateLimitStreak++;
            h.LastRateLimitAtMs = now;
        }
    }

    private void RecordLatency(NodeHealth h, CallClass callClass, double elapsedMs)
    {
        var now = NowMs;
        var p = h.Latency[(int)callClass];
        // A stale profile restarts from scratch so an idle process re-learns
        // instead of ranking on old data. Per class: a class that has not been
        // called in a while is unproven again, which is exploration, not a
        // penalty: the node keeps its other profile and all of its health.
        if (p.UpdatedAtMs > 0 && now - p.UpdatedAtMs > LatencyMaxAgeMs)
        {
            p.EwmaMs = null;
            p.SampleCount = 0;
        }
        p.EwmaMs = p.EwmaMs is { } prev
            ? LatencyEwmaAlpha * elapsedMs + (1 - LatencyEwmaAlpha) * prev
            : elapsedMs;
        p.SampleCount++;
        p.UpdatedAtMs = now;
    }

    /// <summary>
    /// Node indices ordered best-first: unparked nodes sorted by
    /// (recent-failure tier, latency score, config index); rate-limit-parked
    /// nodes appended last as a final resort. A node parked for consecutive
    /// failures is left out altogether while any other node is available (it
    /// was not answering; a throttled node might), and probed once its park
    /// lapses. When every node is failure-parked all are offered, so a pool
    /// that is entirely down degrades to "try them" rather than "try nothing".
    ///
    /// Only the latency score depends on <paramref name="callClass"/>; every
    /// tier above it is node-wide, so the two classes agree on which nodes are
    /// usable at all and differ only in the order of the usable ones.
    /// </summary>
    public List<int> OrderedNodeIndices(CallClass callClass)
    {
        lock (_lock)
        {
            var now = NowMs;
            var ranked = Enumerable.Range(0, _health.Length)
                .Select(i =>
                {
                    var h = _health[i];
                    var parked = h.RateLimitedUntilMs > now;
                    var dead = h.FailureParkedUntilMs > now;
                    var recentFailure = h.ConsecutiveFailures > 0
                                        && now - h.LastFailureAtMs < RecentFailureWindowMs;
                    var p = h.Latency[(int)callClass];
                    var latencyUsable = p.EwmaMs is not null
                                        && p.SampleCount >= LatencyMinSamples
                                        && now - p.UpdatedAtMs <= LatencyMaxAgeMs;
                    var score = latencyUsable ? p.EwmaMs!.Value : LatencyUnprovenPriorMs;
                    return (Index: i, Parked: parked, Dead: dead, RecentFailure: recentFailure, Score: score);
                })
                .ToList();
            if (ranked.Any(x => !x.Dead))
            {
                ranked.RemoveAll(x => x.Dead);
            }
            return ranked
                .OrderBy(x => x.Parked)
                .ThenBy(x => x.RecentFailure)
                .ThenBy(x => x.Score)
                .ThenBy(x => x.Index)
                .Select(x => x.Index)
                .ToList();
        }
    }

    /// <summary>
    /// Admission at attempt time, because the ordering a call holds was taken
    /// when the call started and can be stale by the time it reaches this node:
    /// a node parked since then is skipped, and a node just out of a park is
    /// half-open, admitting one probe at a time, so a burst of concurrent calls
    /// that all hold it in their lists cannot all probe it at once. Either
    /// rule yields when no other node could take the attempt. Pair with
    /// <see cref="EndAttempt"/>.
    /// </summary>
    public bool TryBeginAttempt(int nodeIndex)
    {
        lock (_lock)
        {
            var now = NowMs;
            var h = _health[nodeIndex];
            var parked = h.FailureParkedUntilMs > now;
            var halfOpenBusy = !parked && h.FailureParkStreak > 0 && h.InFlight > 0;
            if (parked || halfOpenBusy)
            {
                var othersAvailable = false;
                for (var j = 0; j < _health.Length && !othersAvailable; j++)
                {
                    if (j == nodeIndex) continue;
                    var o = _health[j];
                    var oParked = o.FailureParkedUntilMs > now;
                    var oBusy = !oParked && o.FailureParkStreak > 0 && o.InFlight > 0;
                    othersAvailable = !oParked && !oBusy;
                }
                if (othersAvailable) return false;
            }
            h.InFlight++;
            return true;
        }
    }

    public void EndAttempt(int nodeIndex)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            if (h.InFlight > 0) h.InFlight--;
        }
    }

    /// <summary>Per-node state and lifetime counters, for the stats endpoint.</summary>
    public List<NodeView> Snapshot()
    {
        lock (_lock)
        {
            var now = NowMs;
            return Enumerable.Range(0, _health.Length).Select(i =>
            {
                var h = _health[i];
                var latency = Enumerable.Range(0, CallClassCount)
                    .Select(c => new ClassLatencyView((CallClass)c, h.Latency[c].EwmaMs, h.Latency[c].SampleCount))
                    .ToList();
                return new NodeView(i, h.Calls, h.Successes, h.Failures, h.Timeouts, h.RateLimits,
                    latency, h.ConsecutiveFailures,
                    h.ConsecutiveFailures > 0 && now - h.LastFailureAtMs < RecentFailureWindowMs,
                    Math.Max(0, h.RateLimitedUntilMs - now), Math.Max(0, h.FailureParkedUntilMs - now),
                    h.FailureRate);
            }).ToList();
        }
    }

    /// <summary>Retry-After: delta-seconds or an HTTP date (RFC 9110).</summary>
    public static int? ParseRetryAfterMs(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var t = header.Trim();
        if (long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            // Clamp before converting: a huge delta-seconds must saturate, not
            // overflow into a negative park window.
            return seconds >= 0 ? (int)(Math.Min(seconds, int.MaxValue / 1000L) * 1000) : null;
        }
        if (DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
        {
            var ms = (when - DateTimeOffset.UtcNow).TotalMilliseconds;
            return ms > 0 ? (int)Math.Min(ms, int.MaxValue) : 0;
        }
        return null;
    }
}
