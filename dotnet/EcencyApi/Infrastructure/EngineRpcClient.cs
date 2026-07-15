using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Hive-Engine /contracts client with the same health-aware node failover as
/// HiveRpcClient (shared <see cref="NodeHealthTracker"/>). Replaces the ported
/// Node behavior — a sticky randomly-picked node plus one blind random retry —
/// which intermittently blanked engine data whenever the random pick landed on
/// a dead or flaky node twice in a row.
/// </summary>
public sealed class EngineRpcClient
{
    private readonly string[] _nodes;
    private readonly KeyValuePair<string, string>[] _headers;
    private readonly NodeHealthTracker _health;

    /// <param name="nodes">Base node URLs; "/contracts" is appended per call.</param>
    public EngineRpcClient(string[] nodes, KeyValuePair<string, string>[] headers)
    {
        _nodes = nodes;
        _headers = headers;
        _health = new NodeHealthTracker(nodes.Length);
    }

    private static long NowMs => Environment.TickCount64;

    /// <summary>
    /// JSON-RPC find query requiring a "result" array in the response. The
    /// portfolio queries are fixed shapes that always yield an array (possibly
    /// empty) on a healthy node, so an error payload or non-JSON body is a node
    /// problem, not an application error — mark it failed and try the next.
    /// The per-attempt timeout is short because these calls run inside the
    /// portfolioV2 leg budget; healthy engine nodes answer well under it.
    /// </summary>
    public async Task<JsonArray> Find(JsonNode payload, int perAttemptTimeoutMs = 2000)
    {
        Exception? lastError = null;

        foreach (var nodeIndex in _health.OrderedNodeIndices())
        {
            var node = _nodes[nodeIndex];
            var started = NowMs;
            try
            {
                var resp = await Upstream.BaseApiRequest($"{node}/contracts", HttpMethod.Post,
                    _headers, payload, null, perAttemptTimeoutMs);

                if (resp.Status == 429)
                {
                    _health.RecordRateLimited(nodeIndex,
                        NodeHealthTracker.ParseRetryAfterMs(resp.Headers.Get("Retry-After")));
                    lastError = new Exception($"engine node {node} rate-limited (429)");
                    continue;
                }
                if (resp.Status is < 200 or >= 300)
                {
                    _health.RecordFailure(nodeIndex, NowMs - started);
                    lastError = new Exception($"engine node {node} returned {resp.Status}");
                    continue;
                }
                if (resp.Json?["result"] is JsonArray result)
                {
                    _health.RecordSuccess(nodeIndex, NowMs - started);
                    return result;
                }
                _health.RecordFailure(nodeIndex, NowMs - started);
                lastError = new Exception($"engine node {node} returned no result array");
            }
            catch (Exception e) // timeout, DNS failure, connection refused
            {
                _health.RecordFailure(nodeIndex, NowMs - started);
                lastError = e;
            }
        }

        throw lastError ?? new InvalidOperationException("no engine nodes configured");
    }

    /// <summary>
    /// Raw /contracts passthrough for the engine-api proxy route. Fails over
    /// only on transport errors and overload statuses (429/5xx) — any other
    /// response belongs to the caller's query and pipes as-is. If every tried
    /// node overloads, the last upstream response is still returned so the
    /// client sees what the pool said; a pool of pure transport failures
    /// rethrows for pipe()'s 504/500 split. Attempts are capped: the wallet
    /// clients calling this route wait synchronously, and a pool-wide outage
    /// shouldn't multiply the full per-attempt timeout by the pool size.
    /// </summary>
    public async Task<UpstreamResponse> ContractsRaw(JsonNode? payload,
        int perAttemptTimeoutMs = Upstream.DefaultTimeoutMs, int maxAttempts = 3)
    {
        Exception? lastError = null;
        UpstreamResponse? lastResponse = null;
        var attempts = 0;

        foreach (var nodeIndex in _health.OrderedNodeIndices())
        {
            if (attempts++ >= maxAttempts) break;
            var node = _nodes[nodeIndex];
            var started = NowMs;
            try
            {
                var resp = await Upstream.BaseApiRequest($"{node}/contracts", HttpMethod.Post,
                    _headers, payload, null, perAttemptTimeoutMs);

                if (resp.Status == 429)
                {
                    _health.RecordRateLimited(nodeIndex,
                        NodeHealthTracker.ParseRetryAfterMs(resp.Headers.Get("Retry-After")));
                    lastResponse = resp;
                    continue;
                }
                if (resp.Status >= 500)
                {
                    _health.RecordFailure(nodeIndex, NowMs - started);
                    lastResponse = resp;
                    continue;
                }

                _health.RecordSuccess(nodeIndex, NowMs - started);
                return resp;
            }
            catch (Exception e)
            {
                _health.RecordFailure(nodeIndex, NowMs - started);
                lastError = e;
            }
        }

        if (lastResponse != null) return lastResponse;
        throw lastError ?? new InvalidOperationException("no engine nodes configured");
    }
}
