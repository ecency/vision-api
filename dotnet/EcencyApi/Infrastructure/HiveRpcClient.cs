using System.Text;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Minimal dhive-Client equivalent: JSON-RPC over HTTP against a preference-
/// ordered node list with sticky failover (timeout 2000ms, failoverThreshold 2 —
/// two failed attempts on a node advance to the next; RPC-level errors surface
/// immediately and do not fail over, matching dhive).
/// </summary>
public sealed class HiveRpcClient
{
    private readonly string[] _nodes;
    private readonly int _timeoutMs;
    private readonly int _failoverThreshold;
    private int _currentIndex;
    private int _seq;

    // timeoutMs 2000 / failoverThreshold 2 mirror the dhive Client options the
    // Node service constructs its two clients with.
    public HiveRpcClient(string[] nodes, int timeoutMs = 2000, int failoverThreshold = 2)
    {
        _nodes = nodes;
        _timeoutMs = timeoutMs;
        _failoverThreshold = Math.Max(1, failoverThreshold);
    }

    public sealed class RpcException : Exception
    {
        public RpcException(string message) : base(message) { }
    }

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

        // Walk the ring starting from the sticky index. Transient failures
        // (timeout / network / 5xx) get up to `failoverThreshold` attempts on a
        // node before advancing; rate-limit / overload responses (429, 503, 502,
        // 504) advance IMMEDIATELY — a throttled node will not recover in the
        // few ms a local retry would take, so retrying it just wastes budget.
        // On success the node becomes sticky so later calls prefer it.
        for (var n = 0; n < _nodes.Length; n++)
        {
            var nodeIndex = (Volatile.Read(ref _currentIndex) + n) % _nodes.Length;
            var node = _nodes[nodeIndex];

            for (var attempt = 0; attempt < _failoverThreshold; attempt++)
            {
                try
                {
                    var result = await CallNode(node, body);
                    Volatile.Write(ref _currentIndex, nodeIndex);
                    return result;
                }
                catch (RpcException)
                {
                    // dhive surfaces RPC-level errors without failover (a real
                    // application error, not an unhealthy node).
                    Volatile.Write(ref _currentIndex, nodeIndex);
                    throw;
                }
                catch (NodeUnavailableException e)
                {
                    lastError = e;
                    if (e.AdvanceImmediately) break; // don't retry a throttled/overloaded node
                }
                catch (Exception e)
                {
                    lastError = e;
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
        public Exception? Cause { get; private set; }
        public NodeUnavailableException(string message, bool advanceImmediately) : base(message)
            => AdvanceImmediately = advanceImmediately;
        public NodeUnavailableException WithInner(Exception inner) { Cause = inner; return this; }
    }

    // Statuses that mean "this node is throttling/overloaded" — skip it now
    // rather than burning a retry that will also be throttled.
    private static bool IsOverloadStatus(int status) =>
        status is 429 or 502 or 503 or 504;

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
            // Timed out — the node is slow; retry once then advance.
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
                throw new NodeUnavailableException(
                    $"RPC node {node} returned {status}", advanceImmediately: IsOverloadStatus(status));
            }

            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException e)
            {
                // A node that returns non-JSON (e.g. an HTML error page) is unhealthy.
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
/// one client with a single sticky failover state.
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
