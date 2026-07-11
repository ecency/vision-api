using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// JSON-RPC client for Hive nodes with health-aware failover, adopting the
/// proven design of @ecency/sdk's NodeHealthTracker (simplified for a proxy
/// that makes a handful of call shapes):
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
///  - RPC-level errors (JSON error field) surface immediately without failover
///    (dhive semantics — an application error, not an unhealthy node).
///
/// Not adopted (overkill at proxy call rates): request hedging, per-API failure
/// profiles, and head-block staleness checks.
/// </summary>
public sealed class HiveRpcClient
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

    private readonly string[] _nodes;
    private readonly int _timeoutMs;
    private readonly int _failoverThreshold;
    private readonly NodeHealth[] _health;
    private readonly object _lock = new();
    private int _seq;

    // timeoutMs 2000 / failoverThreshold 2 mirror the dhive Client options the
    // Node service constructed its clients with.
    public HiveRpcClient(string[] nodes, int timeoutMs = 2000, int failoverThreshold = 2)
    {
        _nodes = nodes;
        _timeoutMs = timeoutMs;
        _failoverThreshold = Math.Max(1, failoverThreshold);
        _health = new NodeHealth[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            _health[i] = new NodeHealth();
        }
    }

    public sealed class RpcException : Exception
    {
        public RpcException(string message) : base(message) { }
    }

    private static long NowMs => Environment.TickCount64;

    // ---- health bookkeeping (lock-guarded; contention is negligible) ------

    private void RecordSuccess(int nodeIndex, double elapsedMs)
    {
        lock (_lock)
        {
            var h = _health[nodeIndex];
            h.ConsecutiveFailures = 0;
            h.RateLimitStreak = 0;
            RecordLatency(h, elapsedMs);
        }
    }

    private void RecordFailure(int nodeIndex, double elapsedMs)
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

    private void RecordRateLimited(int nodeIndex, int? retryAfterMs)
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
    private List<int> OrderedNodeIndices()
    {
        lock (_lock)
        {
            var now = NowMs;
            return Enumerable.Range(0, _nodes.Length)
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

    // ---- calls -------------------------------------------------------------

    public async Task<JsonNode?> Call(string api, string method, JsonNode @params)
    {
        var request = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _seq),
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["params"] = new JsonArray(api, method, @params),
        };
        var body = request.ToJsonString();

        Exception? lastError = null;

        foreach (var nodeIndex in OrderedNodeIndices())
        {
            var node = _nodes[nodeIndex];

            for (var attempt = 0; attempt < _failoverThreshold; attempt++)
            {
                var started = NowMs;
                try
                {
                    var result = await CallNode(node, body);
                    RecordSuccess(nodeIndex, NowMs - started);
                    return result;
                }
                catch (RpcException)
                {
                    // The node answered; the error is the application's. No
                    // failover (dhive semantics), and no failure mark.
                    RecordSuccess(nodeIndex, NowMs - started);
                    throw;
                }
                catch (NodeUnavailableException e)
                {
                    lastError = e;
                    if (e.IsRateLimit)
                    {
                        RecordRateLimited(nodeIndex, e.RetryAfterMs);
                    }
                    else
                    {
                        RecordFailure(nodeIndex, NowMs - started);
                    }
                    if (e.AdvanceImmediately)
                    {
                        break; // don't retry a throttled/overloaded node
                    }
                }
                catch (Exception e)
                {
                    lastError = e;
                    RecordFailure(nodeIndex, NowMs - started);
                }
            }
        }

        // Every node exhausted — surface the last transport error (dhive throws
        // after cycling the whole list).
        throw lastError ?? new InvalidOperationException("no RPC nodes configured");
    }

    /// <summary>A node is unhealthy/unreachable; try the next one. Distinct from
    /// RpcException (a real RPC-level error that must not fail over).</summary>
    private sealed class NodeUnavailableException : Exception
    {
        public bool AdvanceImmediately { get; }
        public bool IsRateLimit { get; }
        public int? RetryAfterMs { get; }
        public Exception? Cause { get; private set; }

        public NodeUnavailableException(string message, bool advanceImmediately,
            bool isRateLimit = false, int? retryAfterMs = null) : base(message)
        {
            AdvanceImmediately = advanceImmediately;
            IsRateLimit = isRateLimit;
            RetryAfterMs = retryAfterMs;
        }

        public NodeUnavailableException WithInner(Exception inner) { Cause = inner; return this; }
    }

    // Overload statuses mean "this node is throttling/failing at the edge" —
    // skip it now rather than burning a retry that will fail the same way.
    private static bool IsOverloadStatus(int status) => status is 429 or 502 or 503 or 504;

    /// <summary>Retry-After: delta-seconds or an HTTP date (RFC 9110).</summary>
    private static int? ParseRetryAfterMs(string? header)
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

    private async Task<JsonNode?> CallNode(string node, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, node)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var cts = new CancellationTokenSource(_timeoutMs);
        HttpResponseMessage resp;
        try
        {
            resp = await Upstream.Http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new NodeUnavailableException($"RPC node {node} timed out", advanceImmediately: false).WithInner(e);
        }
        catch (HttpRequestException e)
        {
            throw new NodeUnavailableException($"RPC node {node} unreachable: {e.Message}", advanceImmediately: false).WithInner(e);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var retryAfter = status == 429
                    ? ParseRetryAfterMs(resp.Headers.TryGetValues("Retry-After", out var vals)
                        ? vals.FirstOrDefault()
                        : null)
                    : null;
                throw new NodeUnavailableException(
                    $"RPC node {node} returned {status}",
                    advanceImmediately: IsOverloadStatus(status),
                    isRateLimit: status == 429,
                    retryAfterMs: retryAfter);
            }

            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new NodeUnavailableException($"RPC node {node} returned non-JSON", advanceImmediately: false).WithInner(e);
            }

            if (parsed is JsonObject obj && obj.TryGetPropertyValue("error", out var error) && error != null)
            {
                var message = error["message"]?.GetValue<string>() ?? error.ToJsonString();
                throw new RpcException(message);
            }

            return parsed?["result"];
        }
    }

    // ---- typed helpers matching the dhive calls the handlers make --------

    public async Task<JsonArray?> GetAccounts(IEnumerable<string?> names)
    {
        var nameArr = new JsonArray();
        foreach (var n in names)
        {
            // A null name serializes to JSON null (matches dhive: getAccounts([undefined])
            // -> params [null]). Hive's get_accounts(["null"]) is a real account (@null);
            // get_accounts([null]) is empty — so this distinction is load-bearing.
            nameArr.Add(n is null ? null : JsonValue.Create(n));
        }
        var result = await Call("condenser_api", "get_accounts", new JsonArray(nameArr));
        return result as JsonArray;
    }

    public Task<JsonNode?> GetDynamicGlobalProperties() =>
        Call("condenser_api", "get_dynamic_global_properties", new JsonArray());
}

/// <summary>
/// The shared RPC client. The Node service built two dhive Clients
/// (private-api.ts and hive-explorer.ts, the latter with hapi.ecency.com
/// first) — that node has since been decommissioned, so both collapse into
/// one client with a single shared health state.
/// </summary>
public static class HiveClients
{
    public static readonly HiveRpcClient Default = new(new[]
    {
        "https://api.hive.blog",
        "https://techcoderx.com",
        "https://api.deathwing.me",
        "https://rpc.mahdiyari.info",
        "https://hive-api.arcange.eu",
        "https://api.openhive.network",
        "https://hiveapi.actifit.io",
        "https://hive-api.3speak.tv",
        "https://api.syncad.com",
        "https://api.c0ff33a.uk",
    });
}
