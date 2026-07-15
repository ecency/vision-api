using System.Text;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// JSON-RPC client for Hive nodes with health-aware failover. The per-node
/// health state and pool ordering live in <see cref="NodeHealthTracker"/>
/// (shared with the Hive-Engine client); this class adds the Hive call
/// semantics:
///
///  - RPC-level errors (JSON error field) surface immediately without failover
///    (dhive semantics — an application error, not an unhealthy node).
///  - Overload statuses (429/502/503/504) advance to the next node immediately
///    instead of burning a same-node retry.
///
/// Not adopted (overkill at proxy call rates): request hedging, per-API failure
/// profiles, and head-block staleness checks.
/// </summary>
public sealed class HiveRpcClient
{
    private readonly string[] _nodes;
    private readonly int _timeoutMs;
    private readonly int _failoverThreshold;
    private readonly NodeHealthTracker _health;
    private int _seq;

    // timeoutMs 2000 / failoverThreshold 2 mirror the dhive Client options the
    // Node service constructed its clients with.
    public HiveRpcClient(string[] nodes, int timeoutMs = 2000, int failoverThreshold = 2)
    {
        _nodes = nodes;
        _timeoutMs = timeoutMs;
        _failoverThreshold = Math.Max(1, failoverThreshold);
        _health = new NodeHealthTracker(nodes.Length);
    }

    public sealed class RpcException : Exception
    {
        public RpcException(string message) : base(message) { }
    }

    private static long NowMs => Environment.TickCount64;

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
        // JsJson: a lone-surrogate username from a client token must serialize
        // (JSON.stringify semantics) instead of throwing in the writer.
        var body = JsJson.Stringify(request);

        Exception? lastError = null;

        foreach (var nodeIndex in _health.OrderedNodeIndices())
        {
            var node = _nodes[nodeIndex];

            for (var attempt = 0; attempt < _failoverThreshold; attempt++)
            {
                var started = NowMs;
                try
                {
                    var result = await CallNode(node, body);
                    _health.RecordSuccess(nodeIndex, NowMs - started);
                    return result;
                }
                catch (RpcException)
                {
                    // The node answered; the error is the application's. No
                    // failover (dhive semantics), and no failure mark.
                    _health.RecordSuccess(nodeIndex, NowMs - started);
                    throw;
                }
                catch (NodeUnavailableException e)
                {
                    lastError = e;
                    if (e.IsRateLimit)
                    {
                        _health.RecordRateLimited(nodeIndex, e.RetryAfterMs);
                    }
                    else
                    {
                        _health.RecordFailure(nodeIndex, NowMs - started);
                    }
                    if (e.AdvanceImmediately)
                    {
                        break; // don't retry a throttled/overloaded node
                    }
                }
                catch (Exception e)
                {
                    lastError = e;
                    _health.RecordFailure(nodeIndex, NowMs - started);
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
                    ? NodeHealthTracker.ParseRetryAfterMs(resp.Headers.TryGetValues("Retry-After", out var vals)
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
