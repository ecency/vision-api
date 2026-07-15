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

    private sealed class NodeHealth
    {
        public int ConsecutiveFailures;
        public long LastFailureAtMs;
        public long RateLimitedUntilMs;
        public int RateLimitStreak;
        public long LastRateLimitAtMs;
        public double? EwmaLatencyMs;
        public int LatencySampleCount;
        public long LatencyUpdatedAtMs;
    }

    private readonly NodeHealth[] _health;
    private readonly object _lock = new();

    public NodeHealthTracker(int nodeCount)
    {
        _health = new NodeHealth[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            _health[i] = new NodeHealth();
        }
    }

    private static long NowMs => Environment.TickCount64;

    // ---- health bookkeeping (lock-guarded; contention is negligible) ------

    public void RecordSuccess(int nodeIndex, double elapsedMs)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            h.ConsecutiveFailures = 0;
            h.RateLimitStreak = 0;
            RecordLatency(h, elapsedMs);
        }
    }

    public void RecordFailure(int nodeIndex, double elapsedMs)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            h.ConsecutiveFailures++;
            h.LastFailureAtMs = NowMs;
            // A genuinely slow failure (timeout / slow 5xx) is also a latency
            // signal; an instant refusal is not (a *down* node isn't "slow").
            if (elapsedMs >= SlowFailureFloorMs)
            {
                RecordLatency(h, elapsedMs);
            }
        }
    }

    public void RecordRateLimited(int nodeIndex, int? retryAfterMs)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            var now = NowMs;
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

    private static void RecordLatency(NodeHealth h, double elapsedMs)
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
    /// (recent-failure tier, latency score, config index); parked
    /// (rate-limited) nodes appended last as a final resort. A recovered node
    /// re-enters the healthy tiers as soon as its windows lapse.
    /// </summary>
    public List<int> OrderedNodeIndices()
    {
        lock (_lock)
        {
            var now = NowMs;
            return Enumerable.Range(0, _health.Length)
                .Select(i =>
                {
                    var h = _health[i];
                    var parked = h.RateLimitedUntilMs > now;
                    var recentFailure = h.ConsecutiveFailures > 0
                                        && now - h.LastFailureAtMs < RecentFailureWindowMs;
                    var latencyUsable = h.EwmaLatencyMs is not null
                                        && h.LatencySampleCount >= LatencyMinSamples
                                        && now - h.LatencyUpdatedAtMs <= LatencyMaxAgeMs;
                    var score = latencyUsable ? h.EwmaLatencyMs!.Value : LatencyUnprovenPriorMs;
                    return (Index: i, Parked: parked, RecentFailure: recentFailure, Score: score);
                })
                .OrderBy(x => x.Parked)
                .ThenBy(x => x.RecentFailure)
                .ThenBy(x => x.Score)
                .ThenBy(x => x.Index)
                .Select(x => x.Index)
                .ToList();
        }
    }

    /// <summary>Retry-After: delta-seconds or an HTTP date (RFC 9110).</summary>
    public static int? ParseRetryAfterMs(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var t = header.Trim();
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds >= 0 ? seconds * 1000 : null;
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
