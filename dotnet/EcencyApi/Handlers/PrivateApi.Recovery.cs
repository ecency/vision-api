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
    private sealed record RecoveryChainState(JsonObject? Account, JsonNode? OwnerHistory, DateTime? LastRecovery,
        DateTime HeadTime);

    /// <summary>recover_account is operation 25; the low filter word selects it alone.</summary>
    private const int RecoverAccountOpFilter = 1 << 25;

    private static async Task<RecoveryChainState> ReadRecoveryChain(string username)
    {
        var accounts = RecoveryRpc("get_accounts", new JsonArray(new JsonArray(username)));
        var ownerHistory = RecoveryRpc("get_owner_history", new JsonArray(username));
        var history = RecoveryRpc("get_account_history",
            new JsonArray(username, -1, 1000, RecoverAccountOpFilter, 0));
        var props = RecoveryRpc("get_dynamic_global_properties", new JsonArray());
        await Task.WhenAll(accounts, ownerHistory, history, props);

        var head = RecoveryRules.ParseChainTime(JsVal.Prop(props.Result, "time"))
            ?? throw new InvalidOperationException("head block time missing");
        var account = accounts.Result is JsonArray list && list.Count > 0 ? list[0] as JsonObject : null;
        return new RecoveryChainState(account, ownerHistory.Result,
            RecoveryRules.LatestRecoverAccountTime(history.Result), head);
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
            // Quiet on purpose: a node outage would otherwise log per request.
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

        if (resp.Status == 404)
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

        // Captcha before any chain read, so unsolved requests cost nothing upstream.
        var ip = SignupClientIp(ctx);
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
        if (!RecoveryRules.HasRecentOwnerChange(chain.OwnerHistory, chain.HeadTime, chain.LastRecovery))
        {
            await SendRecoveryError(ctx, 422, "no_recent_owner_change");
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

        if (signature == null || !RecoveryRules.ProofMatches(username, newOwnerPub!, nonce, signature, provenKeyPub!))
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

        var validUntil = RecoveryRules.ProvenKeyValidUntil(
            chain.OwnerHistory, provenKeyPub!, chain.HeadTime, chain.LastRecovery);
        if (validUntil == null)
        {
            await SendRecoveryError(ctx, 422, "key_not_recent");
            return;
        }

        var payload = new JsonObject
        {
            ["new_owner_pub"] = newOwnerPub,
            ["proven_key_pub"] = provenKeyPub,
            ["proven_key_valid_until"] = RecoveryRules.FormatChainTime(validUntil.Value),
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

        JsonNode? accounts;
        try
        {
            accounts = await RecoveryRpc("get_accounts", new JsonArray(new JsonArray(username)));
        }
        catch (Exception)
        {
            await SendRecoveryUnavailable(ctx);
            return;
        }
        var account = accounts is JsonArray list && list.Count > 0 ? list[0] : null;
        if (!RecoveryRules.IsSingleKeyAuthority(JsVal.Prop(account, "owner"), newOwnerPub))
        {
            await SendRecoveryError(ctx, 409, "not_recovered");
            return;
        }

        await Upstream.Pipe(RecoveryUpstream($"recovery-request/{requestId}/complete", HttpMethod.Post, null), ctx);
    }
}
