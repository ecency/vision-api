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
    private int _currentIndex;
    private int _seq;

    public HiveRpcClient(string[] nodes, int timeoutMs = 2000)
    {
        _nodes = nodes;
        _timeoutMs = timeoutMs;
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

        // walk the ring starting from the sticky index; 2 attempts per node
        for (var n = 0; n < _nodes.Length; n++)
        {
            var nodeIndex = (Volatile.Read(ref _currentIndex) + n) % _nodes.Length;
            var node = _nodes[nodeIndex];

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var result = await CallNode(node, body);
                    Volatile.Write(ref _currentIndex, nodeIndex);
                    return result;
                }
                catch (RpcException)
                {
                    // dhive surfaces RPC errors without failover
                    Volatile.Write(ref _currentIndex, nodeIndex);
                    throw;
                }
                catch (Exception e)
                {
                    lastError = e;
                }
            }
        }

        throw lastError ?? new InvalidOperationException("no RPC nodes configured");
    }

    private async Task<JsonNode?> CallNode(string node, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, node)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var cts = new CancellationTokenSource(_timeoutMs);
        using var resp = await Upstream.Http.SendAsync(req, cts.Token);

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"RPC node {node} returned {(int)resp.StatusCode}");
        }

        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        var parsed = JsonNode.Parse(text);

        if (parsed is JsonObject obj && obj.TryGetPropertyValue("error", out var error) && error != null)
        {
            var message = error["message"]?.GetValue<string>() ?? error.ToJsonString();
            throw new RpcException(message);
        }

        return parsed?["result"];
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

/// <summary>The two client instances the Node service builds (node lists differ).</summary>
public static class HiveClients
{
    /// <summary>private-api.ts client (no hapi.ecency.com; used by validateCode).</summary>
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

    /// <summary>hive-explorer.ts client (prefers our own node first).</summary>
    public static readonly HiveRpcClient Explorer = new(new[]
    {
        "https://hapi.ecency.com",
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
