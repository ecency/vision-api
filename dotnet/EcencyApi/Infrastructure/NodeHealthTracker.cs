using System.Globalization;

namespace EcencyApi.Infrastructure;

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
///  - Healthy nodes are ordered by latency EWMA (alpha 0.3, trusted after 3
///    samples, stale after 5 minutes); unproven nodes score a neutral prior
///    (1s) so an unknown node is explored before a proven-slow one. Config
///    order breaks ties, so cold start behaves exactly like the configured list.
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
    private const double LatencyUnprovenPriorMs = 1_000;
    private const int SlowFailureFloorMs = 2_000;
    // A node that fails this many times in a row is parked (30s, doubling to
    // 120s; a success clears the streak) and is not tried while any other node
    // is available. Without this a node that never answers keeps its
    // "unexplored" standing: only 429s parked, and a failure demoted for 30s at
    // most, so each time the leading nodes hiccupped the dead node took the
    // whole in-flight burst at the full per-node timeout (observed: 83
    // half-open connects at once to one unreachable node).
    private const int FailureParkThreshold = 3;
    private const int FailureParkBaseMs = 30_000;
    private const int FailureParkMaxMs = 120_000;

    private sealed class NodeHealth
    {
        public int ConsecutiveFailures;
        public long LastFailureAtMs;
        public long RateLimitedUntilMs;
        public int RateLimitStreak;
        public long LastRateLimitAtMs;
        public long FailureParkedUntilMs;
        public int FailureParkStreak;
        public double? EwmaLatencyMs;
        public int LatencySampleCount;
        public long LatencyUpdatedAtMs;
        // Lifetime counters, for the stats endpoint.
        public long Calls, Successes, Failures, Timeouts, RateLimits;
    }

    /// <summary>One node's health as reported by <see cref="Snapshot"/>.</summary>
    public sealed record NodeView(
        int Index, long Calls, long Successes, long Failures, long Timeouts, long RateLimits,
        double? EwmaLatencyMs, int LatencySamples, int ConsecutiveFailures,
        bool RecentFailure, long RateLimitedForMs, long FailureParkedForMs);

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

    public void RecordSuccess(int nodeIndex, double elapsedMs)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            h.Calls++;
            h.Successes++;
            h.ConsecutiveFailures = 0;
            h.RateLimitStreak = 0;
            h.FailureParkStreak = 0;
            RecordLatency(h, elapsedMs);
        }
    }

    /// <param name="timedOut">The attempt ran into the client's per-node timeout.
    /// That IS a latency sample whatever the timeout is set to; the floor below
    /// only tells instant refusals (a down node is not "slow") from slow 5xx.</param>
    public void RecordFailure(int nodeIndex, double elapsedMs, bool timedOut = false)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            var now = NowMs;
            h.Calls++;
            h.Failures++;
            if (timedOut) h.Timeouts++;
            h.ConsecutiveFailures++;
            h.LastFailureAtMs = now;
            if (timedOut || elapsedMs >= SlowFailureFloorMs)
            {
                RecordLatency(h, elapsedMs);
            }
            if (h.ConsecutiveFailures >= FailureParkThreshold)
            {
                var parkMs = Math.Min(FailureParkBaseMs << Math.Min(h.FailureParkStreak, 2), FailureParkMaxMs);
                h.FailureParkedUntilMs = now + parkMs;
                h.FailureParkStreak++;
            }
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

    private void RecordLatency(NodeHealth h, double elapsedMs)
    {
        var now = NowMs;
        // A stale profile restarts from scratch so an idle process re-learns
        // instead of ranking on old data.
        if (h.LatencyUpdatedAtMs > 0 && now - h.LatencyUpdatedAtMs > LatencyMaxAgeMs)
        {
            h.EwmaLatencyMs = null;
            h.LatencySampleCount = 0;
        }
        h.EwmaLatencyMs = h.EwmaLatencyMs is { } prev
            ? LatencyEwmaAlpha * elapsedMs + (1 - LatencyEwmaAlpha) * prev
            : elapsedMs;
        h.LatencySampleCount++;
        h.LatencyUpdatedAtMs = now;
    }

    /// <summary>
    /// Node indices ordered best-first: unparked nodes sorted by
    /// (recent-failure tier, latency score, config index); rate-limit-parked
    /// nodes appended last as a final resort. A node parked for consecutive
    /// failures is left out altogether while any other node is available (it
    /// was not answering; a throttled node might), and probed once its park
    /// lapses. When every node is failure-parked all are offered, so a pool
    /// that is entirely down degrades to "try them" rather than "try nothing".
    /// </summary>
    public List<int> OrderedNodeIndices()
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
                    var latencyUsable = h.EwmaLatencyMs is not null
                                        && h.LatencySampleCount >= LatencyMinSamples
                                        && now - h.LatencyUpdatedAtMs <= LatencyMaxAgeMs;
                    var score = latencyUsable ? h.EwmaLatencyMs!.Value : LatencyUnprovenPriorMs;
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

    /// <summary>Per-node state and lifetime counters, for the stats endpoint.</summary>
    public List<NodeView> Snapshot()
    {
        lock (_lock)
        {
            var now = NowMs;
            return Enumerable.Range(0, _health.Length).Select(i =>
            {
                var h = _health[i];
                return new NodeView(i, h.Calls, h.Successes, h.Failures, h.Timeouts, h.RateLimits,
                    h.EwmaLatencyMs, h.LatencySampleCount, h.ConsecutiveFailures,
                    h.ConsecutiveFailures > 0 && now - h.LastFailureAtMs < RecentFailureWindowMs,
                    Math.Max(0, h.RateLimitedUntilMs - now), Math.Max(0, h.FailureParkedUntilMs - now));
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
