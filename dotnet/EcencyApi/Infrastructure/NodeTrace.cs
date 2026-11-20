namespace EcencyApi.Infrastructure;

/// <summary>
/// Records, per node and per second, the upstream requests the pool sent and
/// how each ended, and logs each node's minute as one line:
///
/// <c>node-trace pool=ssr node=api.example minute=&lt;unix s&gt; sent=… ok=… rl=… timeout=… e5xx=… fail=… ok_ms=… max_ms=… inflight=… retry_after=n:min:max hdr=… late=n</c>
///
/// Each count field holds 60 comma-separated values, by the second the request
/// was sent (when a limiter counts it), or `-` when all are 0. `ok_ms` and
/// `max_ms` are the summed and the longest latency of answered requests;
/// `inflight` the most requests in flight to the node when one was sent.
/// `hdr` is the first 429 or 5xx's limiter headers that minute, if any.
///
/// It changes nothing in routing. It exists to fit each node's limiter offline
/// from what it does under real load: its sustained rate, the burst it allows
/// after a rest, whether it delays rather than refuses, whether it limits
/// concurrency rather than rate, and what it says about itself.
///
/// A minute is logged once its slowest requests can have ended
/// (<see cref="GraceMs"/> after it closes). A request that ends later still is
/// counted as `late` on the node's next line. A minute a node was sent nothing
/// is not logged.
/// </summary>
public sealed class NodeTrace
{
    public enum Outcome { Answered, RateLimited, Timeout, ServerError, Failed }

    /// <summary>Longer than any per-node timeout the pool uses.</summary>
    public const long GraceMs = 15_000;

    private const int MaxHeaderSample = 300;

    private sealed class Minute
    {
        public readonly int[] Sent = new int[60], Ok = new int[60], RateLimited = new int[60],
            Timeout = new int[60], ServerError = new int[60], Failed = new int[60],
            OkMs = new int[60], MaxMs = new int[60], InFlight = new int[60];
        public int RetryAfters;
        public int RetryAfterMin = int.MaxValue, RetryAfterMax;
        public string? Headers;
    }

    private readonly object _lock = new();
    private readonly string[] _names;
    private readonly Func<long> _unixMs;
    private readonly Action<string> _log;
    private readonly string _poolName;
    // Per node, the minutes (unix minute index) not yet logged.
    private readonly Dictionary<long, Minute>[] _open;
    private readonly int[] _late;
    private long _loggedThrough = long.MinValue;

    public NodeTrace(IReadOnlyList<string> nodeNames, Func<long> unixMs, Action<string> log, string poolName)
    {
        _names = nodeNames.ToArray();
        _unixMs = unixMs;
        _log = log;
        _poolName = poolName;
        _open = _names.Select(_ => new Dictionary<long, Minute>()).ToArray();
        _late = new int[_names.Length];
    }

    /// <summary>
    /// Unix ms read once and advanced by the monotonic tick count: a wall-clock
    /// step (NTP) cannot send requests back into minutes already logged, or
    /// log minutes early.
    /// </summary>
    public static Func<long> MonotonicUnixMs()
    {
        var anchorUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var anchorTick = Environment.TickCount64;
        return () => anchorUnix + (Environment.TickCount64 - anchorTick);
    }

    private static long MinuteOf(long ms) => (long)Math.Floor(ms / 60_000.0);

    /// <summary>One upstream request to the node, sent at <paramref name="sentAtMs"/> (unix ms).</summary>
    /// <param name="latencyMs">How long it took; recorded for answered requests.</param>
    /// <param name="inFlight">Requests in flight to the node when it was sent, itself included.</param>
    /// <param name="headers">The limiter's headers on a 429 or 5xx, if any.</param>
    public void Record(int nodeIndex, long sentAtMs, Outcome outcome, int? retryAfterMs = null,
        int latencyMs = 0, int inFlight = 0, string? headers = null)
    {
        List<string>? due = null;
        try
        {
            lock (_lock)
            {
                var minute = MinuteOf(sentAtMs);
                if (minute <= _loggedThrough)
                {
                    _late[nodeIndex]++;
                }
                else
                {
                    var open = _open[nodeIndex];
                    if (!open.TryGetValue(minute, out var m)) open[minute] = m = new Minute();
                    var s = (int)((sentAtMs - minute * 60_000) / 1000);
                    m.Sent[s]++;
                    m.InFlight[s] = Math.Max(m.InFlight[s], inFlight);
                    switch (outcome)
                    {
                        case Outcome.Answered:
                            m.Ok[s]++;
                            m.OkMs[s] += latencyMs;
                            m.MaxMs[s] = Math.Max(m.MaxMs[s], latencyMs);
                            break;
                        case Outcome.RateLimited: m.RateLimited[s]++; break;
                        case Outcome.Timeout: m.Timeout[s]++; break;
                        case Outcome.ServerError: m.ServerError[s]++; break;
                        default: m.Failed[s]++; break;
                    }
                    if (retryAfterMs is { } wait)
                    {
                        m.RetryAfters++;
                        m.RetryAfterMin = Math.Min(m.RetryAfterMin, wait);
                        m.RetryAfterMax = Math.Max(m.RetryAfterMax, wait);
                    }
                    if (m.Headers == null && !string.IsNullOrEmpty(headers)) m.Headers = Sanitize(headers);
                }
                due = TakeDue();
            }
        }
        catch
        {
            // The trace is best effort: never the caller's failure.
        }
        Write(due);
    }

    /// <summary>Logs the minutes that are due. Recording does this too.</summary>
    public void Flush()
    {
        List<string>? due;
        lock (_lock) due = TakeDue();
        Write(due);
    }

    // One token on a space-separated line, bounded.
    private static string Sanitize(string headers)
    {
        var s = new string(headers.Select(c => char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c).ToArray());
        return s.Length > MaxHeaderSample ? s[..MaxHeaderSample] : s;
    }

    private static string Field(int[] values) => values.All(v => v == 0) ? "-" : string.Join(',', values);

    // Under the lock.
    private List<string>? TakeDue()
    {
        var through = MinuteOf(_unixMs() - GraceMs) - 1;
        if (through <= _loggedThrough) return null;
        List<string>? lines = null;
        for (var i = 0; i < _names.Length; i++)
        {
            var open = _open[i];
            foreach (var minute in open.Keys.Where(k => k <= through).OrderBy(k => k).ToList())
            {
                var m = open[minute];
                open.Remove(minute);
                var ra = m.RetryAfters == 0 ? "0" : $"{m.RetryAfters}:{m.RetryAfterMin}:{m.RetryAfterMax}";
                (lines ??= new()).Add(
                    $"node-trace pool={_poolName} node={_names[i]} minute={minute * 60}"
                    + $" sent={Field(m.Sent)} ok={Field(m.Ok)} rl={Field(m.RateLimited)}"
                    + $" timeout={Field(m.Timeout)} e5xx={Field(m.ServerError)} fail={Field(m.Failed)}"
                    + $" ok_ms={Field(m.OkMs)} max_ms={Field(m.MaxMs)} inflight={Field(m.InFlight)}"
                    + $" retry_after={ra} hdr={m.Headers ?? "-"} late={_late[i]}");
                _late[i] = 0;
            }
        }
        _loggedThrough = through;
        return lines;
    }

    // Outside the lock: a slow or broken log must not hold up or fail a call.
    private void Write(List<string>? lines)
    {
        if (lines == null) return;
        try
        {
            foreach (var line in lines) _log(line);
        }
        catch
        {
            // The trace is best effort.
        }
    }
}
