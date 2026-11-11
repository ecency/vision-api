using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Self-serve account recovery. The request state lives in the private API; this layer
/// gates each step on the chain (recovery account, owner history, the proven key) and
/// verifies the owner-key proof, so nothing reaches the private API that the chain
/// would not accept. Internal row fields (the nonce in particular) are read here and
/// never returned to the client.
/// </summary>
public static partial class PrivateApi
{
    private const string RecoveryAccountName = "ecency";
    private const string RecoveryCaptchaAction = "recover";

    // Seams for tests: the private API, the chain and the captcha check.
    internal static Func<string, HttpMethod, JsonNode?, Task<UpstreamResponse>> RecoveryUpstream = DefaultRecoveryUpstream;
    internal static Func<string, JsonArray, Task<JsonNode?>> RecoveryRpc = DefaultRecoveryRpc;
    internal static Func<JsonNode?, string, Task<bool>> RecoveryCaptchaRejected = DefaultRecoveryCaptchaRejected;

    internal static void ResetRecoverySeams()
    {
        RecoveryUpstream = DefaultRecoveryUpstream;
        RecoveryRpc = DefaultRecoveryRpc;
        RecoveryCaptchaRejected = DefaultRecoveryCaptchaRejected;
    }

    private static Task<UpstreamResponse> DefaultRecoveryUpstream(string endpoint, HttpMethod method, JsonNode? payload) =>
        ApiClient.ApiRequest(endpoint, method, null, payload);

    private static Task<bool> DefaultRecoveryCaptchaRejected(JsonNode? token, string ip) =>
        AccountCaptchaRejected(token, ip, RecoveryCaptchaAction);

    private static Task<JsonNode?> DefaultRecoveryRpc(string method, JsonArray @params) =>
        HiveClients.Default.Call("condenser_api", method, @params, method switch
        {
            "get_accounts" => HiveRpcClient.IsAccountArray,
            "get_dynamic_global_properties" => r => r is JsonObject,
            _ => r => r is JsonArray,
        });

    /// <summary>The account and the owner-change facts the recovery rules need.</summary>
    private sealed record RecoveryChainState(JsonObject? Account, List<RecoveryRules.OwnerHistoryEntry> OwnerHistory,
        DateTime? LastRecovery, DateTime HeadTime);

    private static async Task<RecoveryChainState> ReadRecoveryChain(string username)
    {
        var account = RecoveryRead("get_accounts", new JsonArray(new JsonArray(username)), username, r =>
        {
            var found = r is JsonArray list && list.Count > 0 ? list[0] as JsonObject : null;
            // The latest recovery comes with the account; a field that cannot be read is a read failure.
            return (Account: found, LastRecovery: found == null ? null : RecoveryRules.LastAccountRecovery(found));
        });
        var ownerHistory = RecoveryRead("get_owner_history", new JsonArray(username), username,
            RecoveryRules.ReadOwnerHistory);
        var head = RecoveryRead("get_dynamic_global_properties", new JsonArray(), username,
            r => RecoveryRules.ParseChainTime(JsVal.Prop(r, "time")) ?? throw new FormatException("head block time missing"));
        await Task.WhenAll(account, ownerHistory, head);

        return new RecoveryChainState(account.Result.Account, ownerHistory.Result, account.Result.LastRecovery,
            head.Result);
    }

    /// <summary>
    /// One chain read and the reading of its answer. A failure of either is logged (the method
    /// and the cause only) and rethrown.
    /// </summary>
    private static async Task<T> RecoveryRead<T>(string method, JsonArray @params, string username,
        Func<JsonNode?, T> read)
    {
        try
        {
            return read(await RecoveryRpc(method, @params));
        }
        catch (Exception e)
        {
            var (key, line) = RecoveryReadFailureLog(method, e);
            LogRecoveryFailure(key, line);
            throw;
        }
    }

    /// <summary>
    /// The log line for a failed recovery chain read, and the key it is written once under:
    /// the method, the exception type and a fixed category. Never the message, which can carry
    /// node-supplied text.
    /// </summary>
    internal static (string Key, string Line) RecoveryReadFailureLog(string method, Exception e)
    {
        var type = e.GetType().Name;
        var category = e switch
        {
            OperationCanceledException or TimeoutException => "timeout",
            HttpRequestException => "http_error",
            // The RPC client's own node failure type (unreachable, bad status, bad body).
            _ when type == "NodeUnavailableException" => "http_error",
            HiveRpcClient.RpcException => "rpc_error",
            FormatException or System.Text.Json.JsonException => "parse_error",
            _ => "other",
        };
        return ($"recovery {method} [{type}]", $"warn: recovery chain read failed: {method} ({type}, {category})");
    }

    // Written once per method and cause type, at most a few lines per process: a node outage
    // fails every request the same way and must not log per request.
    private const int MaxRecoveryLogLines = 16;
    private static int _recoveryLogLines;
    private static readonly ConcurrentDictionary<string, byte> RecoveryLogSeen = new();

    private static void LogRecoveryFailure(string key, string line)
    {
        if (Volatile.Read(ref _recoveryLogLines) >= MaxRecoveryLogLines || !RecoveryLogSeen.TryAdd(key, 0)) return;
        if (Interlocked.Increment(ref _recoveryLogLines) > MaxRecoveryLogLines) return;
        Console.Error.WriteLine(line);
    }

    private static Task SendRecoveryError(HttpContext ctx, int status, string error) =>
        ctx.SendJson(status, new JsonObject { ["error"] = error });

    private static async Task SendRecoveryUnavailable(HttpContext ctx)
    {
        ctx.Response.Headers.RetryAfter = ServerErrors.RetryAfterSeconds.ToString();
        await SendRecoveryError(ctx, 503, "unavailable");
    }

    /// <summary>Chain read for a recovery step; answers 503 and returns null when it fails.</summary>
    private static async Task<RecoveryChainState?> TryReadRecoveryChain(HttpContext ctx, string username)
    {
        try
        {
            return await ReadRecoveryChain(username);
        }
        catch (Exception)
        {
            // Already logged by RecoveryRead.
            await SendRecoveryUnavailable(ctx);
            return null;
        }
    }

    private static bool IsEcencyRecovered(JsonObject account) =>
        JsVal.Prop(account, "recovery_account") is JsonValue rv
        && JsVal.TryGetStringLenient(rv, out var ra)
        && ra == RecoveryAccountName;

    /// <summary>
    /// The internal row for a request. Returns null after answering the client: 404 when
    /// the private API does not know it, the private API's own answer for any other
    /// non-200, and 504/500 like Pipe on a transport failure.
    /// </summary>
    private static async Task<JsonObject?> FetchRecoveryRow(HttpContext ctx, string requestId)
    {
        UpstreamResponse resp;
        try
        {
            resp = await RecoveryUpstream($"recovery-request/{requestId}", HttpMethod.Get, null);
        }
        catch (Exception e)
        {
            await Upstream.Pipe(Task.FromException<UpstreamResponse>(e), ctx);
            return null;
        }

        // An expired row is gone as far as the client is concerned.
        if (resp.Status == 404 || (resp.Status == 200 && resp.Json is JsonObject r && r.Str("status") == "expired"))
        {
            await SendRecoveryError(ctx, 404, "not_found");
            return null;
        }
        if (resp.Status != 200 || resp.Json is not JsonObject row)
        {
            await Upstream.SendLikeExpress(ctx, resp.Status, resp.Json, resp.RawText);
            return null;
        }
        return row;
    }

    private static string? NormalizedUsername(JsonObject body)
    {
        var username = body.Str("username")?.Trim().ToLowerInvariant();
        return HiveNames.IsAccountName(username) ? username : null;
    }

    public static async Task RecoveryStart(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = NormalizedUsername(body);
        if (username == null)
        {
            await SendRecoveryError(ctx, 400, "invalid_username");
            return;
        }

        // The private API limits requests per client address, so one is always required.
        var ip = SignupClientIp(ctx);
        if (ip.Length == 0)
        {
            await SendRecoveryError(ctx, 400, "invalid_request");
            return;
        }

        // Captcha before any chain read, so unsolved requests cost nothing upstream.
        if (await RecoveryCaptchaRejected(body.Field("captcha_token"), ip))
        {
            await SendRecoveryError(ctx, 403, "captcha");
            return;
        }

        var chain = await TryReadRecoveryChain(ctx, username);
        if (chain == null)
        {
            return;
        }
        if (chain.Account == null)
        {
            await SendRecoveryError(ctx, 404, "not_found");
            return;
        }
        if (!IsEcencyRecovered(chain.Account))
        {
            await SendRecoveryError(ctx, 422, "not_ecency_recovery");
            return;
        }
        switch (RecoveryRules.CheckOwnerHistory(chain.OwnerHistory, chain.HeadTime, chain.LastRecovery))
        {
            case RecoveryRules.Verdict.NoRecentOwnerChange:
                await SendRecoveryError(ctx, 422, "no_recent_owner_change");
                return;
            case RecoveryRules.Verdict.KeyNotSufficient:
                await SendRecoveryError(ctx, 422, "key_not_sufficient");
                return;
        }

        var payload = new JsonObject { ["username"] = username, ["ip"] = ip };
        await Upstream.Pipe(RecoveryUpstream("recovery-request/start", HttpMethod.Post, payload), ctx);
    }

    public static async Task RecoveryProve(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var requestId = body.Str("request_id");
        var username = NormalizedUsername(body);
        var newOwnerPub = body.Str("new_owner_pub");
        var provenKeyPub = body.Str("proven_key_pub");
        var signature = body.Str("signature");

        if (!RecoveryRules.IsRequestId(requestId) || username == null
            || !HiveCrypto.IsPublicKey(newOwnerPub) || !HiveCrypto.IsPublicKey(provenKeyPub)
            || newOwnerPub == provenKeyPub)
        {
            await SendRecoveryError(ctx, 400, "invalid_request");
            return;
        }
        // Checked before any other call; a proof of another length is never decoded.
        if (signature is not { Length: HiveCrypto.SignatureHexLength })
        {
            await SendRecoveryError(ctx, 400, "invalid_proof");
            return;
        }

        var row = await FetchRecoveryRow(ctx, requestId!);
        if (row == null)
        {
            return;
        }
        var nonce = row.Str("nonce");
        if (row.Str("status") != "started" || row.Str("username") != username || string.IsNullOrEmpty(nonce))
        {
            await SendRecoveryError(ctx, 409, "bad_state");
            return;
        }

        if (!RecoveryRules.ProofMatches(username, newOwnerPub!, nonce, signature, provenKeyPub!))
        {
            await SendRecoveryError(ctx, 400, "invalid_proof");
            return;
        }

        var chain = await TryReadRecoveryChain(ctx, username);
        if (chain == null)
        {
            return;
        }
        if (chain.Account == null)
        {
            await SendRecoveryError(ctx, 404, "not_found");
            return;
        }
        if (!IsEcencyRecovered(chain.Account))
        {
            await SendRecoveryError(ctx, 422, "not_ecency_recovery");
            return;
        }
        if (RecoveryRules.AuthorityKeys(JsVal.Prop(chain.Account, "owner")).Contains(newOwnerPub!))
        {
            await SendRecoveryError(ctx, 400, "invalid_request");
            return;
        }

        var (verdict, validUntil) = RecoveryRules.CheckProvenKey(
            chain.OwnerHistory, provenKeyPub!, chain.HeadTime, chain.LastRecovery);
        if (verdict == RecoveryRules.Verdict.KeyNotSufficient)
        {
            await SendRecoveryError(ctx, 422, "key_not_sufficient");
            return;
        }
        if (validUntil == null)
        {
            await SendRecoveryError(ctx, 422, "key_not_recent");
            return;
        }

        // Only narrows which saved emails may be used, so a failed or unreadable read sends
        // null (signup email only) instead of failing the request. The filtered read scans a
        // bounded slice of recent history; an older key-setting op is simply not found.
        DateTime? ownerSince;
        try
        {
            ownerSince = await RecoveryRead("get_account_history",
                new JsonArray(username, -1, 1000, RecoveryRules.OwnerOpsFilter, 0), username,
                r => RecoveryRules.OwnerSince(r, username, provenKeyPub!, validUntil.Value));
        }
        catch (Exception)
        {
            ownerSince = null;
        }

        var payload = new JsonObject
        {
            ["new_owner_pub"] = newOwnerPub,
            ["proven_key_pub"] = provenKeyPub,
            ["proven_key_valid_until"] = RecoveryRules.FormatChainTime(validUntil.Value),
            ["signature"] = signature,
            ["owner_since"] = ownerSince == null ? null : RecoveryRules.FormatChainTime(ownerSince.Value),
        };
        await Upstream.Pipe(RecoveryUpstream($"recovery-request/{requestId}/prove", HttpMethod.Post, payload), ctx);
    }

    public static async Task RecoveryVerifyCode(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var requestId = body.Str("request_id");
        var code = body.Field("code") is JsonValue cv && JsVal.TryGetStringLenient(cv, out var c) ? c : null;
        if (!RecoveryRules.IsRequestId(requestId) || !RecoveryRules.IsCode(code))
        {
            await SendRecoveryError(ctx, 400, "invalid_request");
            return;
        }

        var payload = new JsonObject { ["code"] = code };
        await Upstream.Pipe(RecoveryUpstream($"recovery-request/{requestId}/verify", HttpMethod.Post, payload), ctx);
    }

    public static async Task RecoveryStatus(HttpContext ctx)
    {
        var requestId = ctx.Request.Query["request_id"].ToString();
        if (!RecoveryRules.IsRequestId(requestId))
        {
            await SendRecoveryError(ctx, 400, "invalid_request");
            return;
        }

        await Upstream.Pipe(RecoveryUpstream($"recovery-request/{requestId}/status", HttpMethod.Get, null), ctx);
    }

    public static async Task RecoveryComplete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var requestId = body.Str("request_id");
        if (!RecoveryRules.IsRequestId(requestId))
        {
            await SendRecoveryError(ctx, 400, "invalid_request");
            return;
        }

        var row = await FetchRecoveryRow(ctx, requestId!);
        if (row == null)
        {
            return;
        }
        var username = row.Str("username");
        var newOwnerPub = row.Str("new_owner_pub");
        if (row.Str("status") != "requested" || !HiveNames.IsAccountName(username) || string.IsNullOrEmpty(newOwnerPub))
        {
            await SendRecoveryError(ctx, 409, "bad_state");
            return;
        }

        JsonNode? account;
        try
        {
            account = await RecoveryRead("get_accounts", new JsonArray(new JsonArray(username)), username!,
                r => r is JsonArray list && list.Count > 0 ? list[0] : null);
        }
        catch (Exception)
        {
            await SendRecoveryUnavailable(ctx);
            return;
        }
        if (!RecoveryRules.IsRecoveredOwner(JsVal.Prop(account, "owner"), newOwnerPub))
        {
            await SendRecoveryError(ctx, 409, "not_recovered");
            return;
        }

        await Upstream.Pipe(RecoveryUpstream($"recovery-request/{requestId}/complete", HttpMethod.Post, null), ctx);
    }
}
