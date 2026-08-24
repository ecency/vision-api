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
/// Latency is tracked per call class (see <see cref="CallClass"/>) because the
/// reads made through this client are bimodal in cost. Failure state is not: it
/// stays node-wide.
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
        : this(nodes, timeoutMs, failoverThreshold, null)
    {
    }

    /// <param name="clock">Test seam for the health tracker's notion of time.</param>
    internal HiveRpcClient(string[] nodes, int timeoutMs, int failoverThreshold, Func<long>? clock)
    {
        _nodes = nodes;
        _timeoutMs = timeoutMs;
        _failoverThreshold = Math.Max(1, failoverThreshold);
        _health = new NodeHealthTracker(nodes.Length, clock);
    }

    public IReadOnlyList<string> Nodes => _nodes;

    /// <summary>
    /// Per-node health for an internal stats endpoint: which node carries the
    /// traffic, which one times out, which one is parked. Hosts only; these are
    /// the public node names, nothing about this deployment.
    /// </summary>
    public JsonArray HealthSnapshot()
    {
        var arr = new JsonArray();
        foreach (var v in _health.Snapshot())
        {
            var node = new JsonObject
            {
                ["node"] = Uri.TryCreate(_nodes[v.Index], UriKind.Absolute, out var u) ? u.Host : _nodes[v.Index],
                ["calls"] = v.Calls,
                ["ok"] = v.Successes,
                ["failures"] = v.Failures,
                ["timeouts"] = v.Timeouts,
                ["rate_limited"] = v.RateLimits,
            };
            // One EWMA per call class (see CallClass): the pool is ordered from
            // the profile of the class being called, so both must be readable to
            // tell "this node is slow" from "this node is slow at feed queries".
            // ewma_ms/samples stay the cheap class, which is what they have always
            // reported in practice, since cheap calls dominate by count.
            var cheap = v.Latency.First(l => l.Class == CallClass.Cheap);
            var heavy = v.Latency.First(l => l.Class == CallClass.Heavy);
            node["ewma_ms"] = cheap.EwmaMs is { } ce ? JsonValue.Create(Math.Round(ce, 1)) : null;
            node["samples"] = cheap.Samples;
            node["heavy_ewma_ms"] = heavy.EwmaMs is { } he ? JsonValue.Create(Math.Round(he, 1)) : null;
            node["heavy_samples"] = heavy.Samples;
            node["consecutive_failures"] = v.ConsecutiveFailures;
            node["recent_failure"] = v.RecentFailure;
            node["rate_limited_for_ms"] = v.RateLimitedForMs;
            node["parked_for_ms"] = v.FailureParkedForMs;
            node["failure_rate"] = Math.Round(v.FailureRate, 3);
            arr.Add(node);
        }
        return arr;
    }

    public sealed class RpcException : Exception
    {
        public RpcException(string message) : base(message) { }
    }

    private static long NowMs => Environment.TickCount64;

    /// <summary>How many nodes may be consulted to satisfy a soft result
    /// preference before the first well-formed answer is accepted as-is.</summary>
    private const int MaxPreferenceProbes = 2;

    // ---- calls -------------------------------------------------------------

    /// <param name="validateResult">Optional shape check for the RPC result. A node
    /// can return 200 with valid JSON that carries neither an error nor a usable
    /// result (observed in production as multi-hour windows of get_accounts
    /// yielding no array); without validation that response counts as a SUCCESS,
    /// so the health tracker keeps the poisoned node ranked first for the whole
    /// window. A failed check is treated as node failure and fails over.</param>
    /// <param name="preferResult">Optional *soft* check: the result is well-formed
    /// but this node cannot serve the caller's needs. Unlike validateResult this is
    /// not a health signal — the node is fine for other calls — so it is neither
    /// retried nor marked unhealthy; we simply move on and keep its answer. If no
    /// node satisfies the preference, the first such answer is returned rather than
    /// throwing, so the caller is never worse off than without the preference.
    ///
    /// Exists because some Hive nodes serve accounts with account metadata stripped:
    /// balances and reputation are correct, posting_json_metadata is empty. That is a
    /// valid 200 with a usable array, so shape validation passes and the latency EWMA
    /// keeps such a node ranked first — silently blanking every metadata-derived
    /// feature (portfolio engine/chain token visibility) with no error and no log.</param>
    /// <param name="callClass">Which latency profile this call's timings belong
    /// to. That profile is also the one the pool is ordered from for this call.
    /// Defaults to Cheap, so a caller that makes one shape of call keeps exactly
    /// one profile per node.</param>
    public Task<JsonNode?> Call(string api, string method, JsonNode @params,
        Func<JsonNode?, bool>? validateResult = null,
        Func<JsonNode?, bool>? preferResult = null,
        CallClass callClass = CallClass.Cheap)
    {
        // The legacy `call` envelope the Node service always sent. hived resolves
        // it for its own APIs (condenser_api, database_api); hivemind's `bridge`
        // is not one of them, so bridge reads must use CallMethod.
        var request = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _seq),
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["params"] = new JsonArray(api, method, @params),
        };
        return Send(request, method, validateResult, preferResult, callClass);
    }

    /// <summary>
    /// The modern JSON-RPC form, `"method": "bridge.get_post"` with the params
    /// as given, which jussi/HAF route to hived or hivemind by prefix. Needed
    /// for every hivemind (`bridge`) read; works for condenser_api too.
    /// </summary>
    public Task<JsonNode?> CallMethod(string qualifiedMethod, JsonNode @params,
        Func<JsonNode?, bool>? validateResult = null,
        Func<JsonNode?, bool>? preferResult = null,
        CallClass callClass = CallClass.Cheap)
    {
        var request = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _seq),
            ["jsonrpc"] = "2.0",
            ["method"] = qualifiedMethod,
            ["params"] = @params,
        };
        return Send(request, qualifiedMethod, validateResult, preferResult, callClass);
    }

    private async Task<JsonNode?> Send(JsonObject request, string method,
        Func<JsonNode?, bool>? validateResult,
        Func<JsonNode?, bool>? preferResult,
        CallClass callClass)
    {
        // JsJson: a lone-surrogate username from a client token must serialize
        // (JSON.stringify semantics) instead of throwing in the writer.
        var body = JsJson.Stringify(request);

        Exception? lastError = null;
        JsonNode? unpreferred = null;
        var haveUnpreferred = false;
        var unpreferredCount = 0;

        foreach (var nodeIndex in _health.OrderedNodeIndices(callClass))
        {
            var node = _nodes[nodeIndex];

            for (var attempt = 0; attempt < _failoverThreshold; attempt++)
            {
                // The ordering above is a snapshot; admission is decided now.
                if (!_health.TryBeginAttempt(nodeIndex)) break;
                var started = NowMs;
                try
                {
                    var result = await CallNode(node, body);
                    if (validateResult != null && !validateResult(result))
                    {
                        // Don't burn a same-node retry: a node serving malformed
                        // 200s keeps serving them (observed for hours at a time).
                        throw new NodeUnavailableException(
                            $"RPC node {node} returned unusable {method} result",
                            advanceImmediately: true);
                    }
                    // The node is healthy either way — record the success before
                    // deciding whether its answer is the one we wanted.
                    _health.RecordSuccess(nodeIndex, NowMs - started, callClass);
                    if (preferResult != null && !preferResult(result))
                    {
                        // Keep the first such answer as the floor and try the next
                        // node; same-node retry would return the same thing.
                        if (!haveUnpreferred) { unpreferred = result; haveUnpreferred = true; }
                        // Bounded on purpose. Roughly an eighth of active accounts
                        // genuinely carry no metadata, and for those NO node can
                        // satisfy the preference — probing the whole pool every time
                        // would multiply RPC load on a common case to route around a
                        // rare one. One alternative is enough to get past a single
                        // metadata-stripping node, which is all this guards against.
                        if (++unpreferredCount >= MaxPreferenceProbes) return unpreferred;
                        break;
                    }
                    return result;
                }
                catch (RpcException)
                {
                    // The node answered; the error is the application's. No
                    // failover (dhive semantics), and no failure mark.
                    _health.RecordSuccess(nodeIndex, NowMs - started, callClass);
                    // ...but if we only came to this node to improve on an answer we
                    // already hold, its error belongs to the optional probe, not to
                    // the caller's request. Rethrowing here would fail a call that
                    // would have succeeded without the preference.
                    if (haveUnpreferred) return unpreferred;
                    throw;
                }
                catch (NodeUnavailableException e)
                {
                    lastError = e;
                    if (e.IsRateLimit)
                    {
                        _health.RecordRateLimited(nodeIndex, e.RetryAfterMs);
                    }
                    else if (_health.RecordFailure(nodeIndex, NowMs - started, callClass, e.IsTimeout))
                    {
                        break; // this failure parked the node: no same-node retry
                    }
                    if (e.AdvanceImmediately)
                    {
                        break; // don't retry a throttled/overloaded node
                    }
                }
                catch (Exception e)
                {
                    lastError = e;
                    if (_health.RecordFailure(nodeIndex, NowMs - started, callClass))
                    {
                        break;
                    }
                }
                finally
                {
                    _health.EndAttempt(nodeIndex);
                }
            }
        }

        // No node satisfied the preference, but one answered well-formed: that is
        // the normal outcome when the preference is genuinely unsatisfiable (an
        // account really has no metadata), so return it instead of failing.
        if (haveUnpreferred) return unpreferred;

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
        public bool IsTimeout { get; }
        public int? RetryAfterMs { get; }
        public Exception? Cause { get; private set; }

        public NodeUnavailableException(string message, bool advanceImmediately,
            bool isRateLimit = false, int? retryAfterMs = null, bool isTimeout = false) : base(message)
        {
            AdvanceImmediately = advanceImmediately;
            IsRateLimit = isRateLimit;
            RetryAfterMs = retryAfterMs;
            IsTimeout = isTimeout;
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
            throw new NodeUnavailableException($"RPC node {node} timed out", advanceImmediately: false, isTimeout: true).WithInner(e);
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
        var result = await Call("condenser_api", "get_accounts", new JsonArray(nameArr),
            IsAccountArray,
            HasAnyAccountMetadata);
        return result as JsonArray;
    }

    /// <summary>
    /// A usable get_accounts result: an array whose entries are account objects or
    /// JSON null (an unknown account). A node answering with scalar entries passes a
    /// bare "is an array" check but yields nothing readable downstream — metadata
    /// reads off it come back empty, which silently blanks portfolio token
    /// visibility exactly like a metadata-stripping node. Treat it as node failure.
    /// </summary>
    internal static bool IsAccountArray(JsonNode? result)
    {
        if (result is not JsonArray accounts) return false;

        foreach (var account in accounts)
        {
            if (account is not null and not JsonObject) return false;
        }

        return true;
    }

    /// <summary>
    /// True when at least one returned account carries a non-empty
    /// posting_json_metadata. Nodes that strip account metadata answer with a
    /// well-formed array whose entries have it blank; portfolio token visibility
    /// is derived from that field, so such an answer silently reads as "this user
    /// enabled nothing". Soft preference, not a health signal: an account that
    /// genuinely has no metadata produces the same shape, and after every node
    /// declines the caller still gets the response.
    /// </summary>
    internal static bool HasAnyAccountMetadata(JsonNode? result)
    {
        if (result is not JsonArray accounts || accounts.Count == 0) return true;

        var sawAccount = false;
        foreach (var account in accounts)
        {
            if (account is not JsonObject) continue;
            sawAccount = true;
            var meta = JsVal.AsString(JsVal.Prop(account, "posting_json_metadata"));
            if (!string.IsNullOrEmpty(meta)) return true;
        }

        // An all-null array (unknown account) has nothing to prefer either way.
        return !sawAccount;
    }

    public Task<JsonNode?> GetDynamicGlobalProperties() =>
        Call("condenser_api", "get_dynamic_global_properties", new JsonArray(),
            r => r is JsonObject);
}

/// <summary>
/// The shared RPC client. The Node service built two dhive Clients
/// (private-api.ts and hive-explorer.ts, the latter with hapi.ecency.com
/// first) — that node has since been decommissioned, so both collapse into
/// one client with a single shared health state.
/// </summary>
public static class HiveClients
{
    // techcoderx.com and hiveapi.actifit.io are deliberately absent: both serve
    // accounts with posting_json_metadata stripped (balances correct, metadata
    // empty). They are fast, so the latency EWMA ranked them first and the
    // portfolio engine/chain layers came back empty for everyone. GetAccounts
    // also routes around such a node at runtime, but keeping them out of the pool
    // means correctness here does not depend on that fallback firing.
    // hive-api.arcange.eu and hive-api.3speak.tv are absent for the same reason
    // as each other: neither completes a TCP connect from any host this service
    // runs on (SYN, no answer, on 443 and on 80), so every attempt cost the full
    // per-node timeout and, in bursts, took every in-flight fill with it. A node
    // that answers nothing is worse for the pool than a slow one, because the
    // health tracker learns latency from answers and has none to learn from: it
    // parks after three consecutive failures, the park lapses, it is probed
    // again. While unparked it sits in config order, which is where any
    // unproven latency profile starts.
    public static readonly IReadOnlyList<string> DefaultNodes = new[]
    {
        "https://api.hive.blog",
        "https://api.deathwing.me",
        "https://rpc.mahdiyari.info",
        "https://api.openhive.network",
        "https://api.syncad.com",
        "https://api.c0ff33a.uk",
    };

    public static readonly HiveRpcClient Default = new(DefaultNodes.ToArray());
}
