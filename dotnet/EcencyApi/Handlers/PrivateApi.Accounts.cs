using System.Text.Json;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/private-api.ts lines 901-1003: streak-freeze
/// endpoints, signup client-IP/Turnstile helpers, and the account-create routes.
/// </summary>
public static partial class PrivateApi
{
    // Streak Freeze buys/spends Points, so the username is taken from the authenticated
    // token (validateCode), never from the request body.
    public static async Task StreakFreeze(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await RequireAuthedUsername(ctx, body);
        if (username == null)
        {
            return;
        }
        await Upstream.Pipe(
            ApiClient.ApiRequest($"users/{username}/streak-freeze", HttpMethod.Get), ctx);
    }

    public static async Task StreakFreezeBuy(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await RequireAuthedUsername(ctx, body);
        if (username == null)
        {
            return;
        }

        // Require the idempotency key: a spend must never reach ePoints without it (a
        // missing key drops from the payload and removes double-charge protection on retry).
        var idempotencyKey = body.Str("idempotency_key");
        if (idempotencyKey == null || idempotencyKey.Trim().Length == 0)
        {
            await ctx.SendText(400, "idempotency_key is required");
            return;
        }

        var payload = new JsonObject { ["idempotency_key"] = idempotencyKey };
        await Upstream.Pipe(
            ApiClient.ApiRequest($"users/{username}/streak-freeze", HttpMethod.Post, null, payload), ctx);
    }

    /// <summary>
    /// Resolve the originating client IP for the account-create endpoints from the
    /// proxy-set X-Real-IP header. X-Forwarded-For is not used here because it can
    /// carry client-supplied values; there is no X-Forwarded-For fallback. Returns ''
    /// when the header is absent.
    /// </summary>
    public static string SignupClientIp(HttpContext ctx)
    {
        var realIp = ctx.Request.Headers["x-real-ip"];
        var first = realIp.Count > 0 ? realIp[0] : null;
        return string.IsNullOrEmpty(first) ? "" : first;
    }

    /// <summary>
    /// Server-side Cloudflare Turnstile verification -- the single captcha verifier for the
    /// account-create and paid-account-create routes. Returns true ONLY on a confirmed-human
    /// token; fails CLOSED on a missing secret/token, provider error, or rejection (a network
    /// blip must never open the gate). The secret is server-side only (config.turnstileSecret).
    /// </summary>
    private static async Task<bool> VerifyTurnstile(JsonNode? token, string ip)
    {
        var secret = Config.TurnstileSecret;
        var tokenStr = token is JsonValue tv && tv.TryGetValue<string>(out string? ts) ? ts : null;
        if (string.IsNullOrEmpty(secret) || tokenStr == null || tokenStr.Trim().Length == 0)
        {
            return false;
        }

        try
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("secret", secret),
                new("response", tokenStr.Trim()),
            };
            if (ip.Length != 0)
            {
                form.Add(new("remoteip", ip));
            }

            var data = await PostTurnstileForm(form);
            return data is JsonObject obj
                && obj.TryGetPropertyValue("success", out var success)
                && success is JsonValue sv
                && sv.TryGetValue<bool>(out bool b)
                && b;
        }
        catch (Exception err)
        {
            Console.WriteLine($"verifyTurnstile: siteverify error {err.Message}");
            return false;
        }
    }

    /// <summary>
    /// Documented exception to the shared-infra rule: the TS uses a bare axios.post
    /// with a form-encoded body (baseApiRequest always sends JSON), so this posts the
    /// form via Upstream.Http directly with the same 8000ms timeout and axios's
    /// default 2xx-only / parse-lenient response semantics. Returns the parsed JSON
    /// body, or null when the body wasn't a JSON document (axios would keep the raw
    /// string, whose .success is undefined). Non-2xx statuses and timeouts throw,
    /// matching axios's rejection into the caller's fail-closed catch.
    /// </summary>
    private static async Task<JsonNode?> PostTurnstileForm(List<KeyValuePair<string, string>> form)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post, "https://challenges.cloudflare.com/turnstile/v0/siteverify")
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var cts = new CancellationTokenSource(8000);
        HttpResponseMessage resp;
        try
        {
            resp = await Upstream.Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new Exception("timeout of 8000ms exceeded", e);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception($"Request failed with status code {(int)resp.StatusCode}");
            }

            if (text.Length == 0)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Turnstile gate for account creation. Returns true when the request must be REJECTED.
    /// Enforced for every request by default (config.captchaMode "hard"); an operator can set
    /// "off" as an emergency break-glass, since verification otherwise fails closed.
    /// </summary>
    private static async Task<bool> AccountCaptchaRejected(JsonNode? token, string ip)
    {
        if (Config.CaptchaMode == "off")
        {
            return false;
        }
        return !(await VerifyTurnstile(token, ip));
    }

    public static async Task CreateAccount(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var ip = SignupClientIp(ctx);

        // Single server-side Turnstile gate, enforced by default. vapi is the sole verifier, so the
        // (single-use) token is consumed here and never forwarded downstream.
        if (await AccountCaptchaRejected(body.Field("captcha_token"), ip))
        {
            await ctx.SendJson(406, new JsonObject
            {
                ["code"] = 113,
                ["message"] = "Please complete the verification and try again.",
            });
            return;
        }

        var headers = new List<KeyValuePair<string, string>> { new("X-Real-IP-V", ip) };

        // { username, email, referral }: keys present in the request body are forwarded
        // (null included); absent keys are omitted like JSON.stringify drops undefined.
        var payload = new JsonObject();
        if (body.ContainsKey("username"))
        {
            payload["username"] = body["username"]?.DeepClone();
        }
        if (body.ContainsKey("email"))
        {
            payload["email"] = body["email"]?.DeepClone();
        }
        if (body.ContainsKey("referral"))
        {
            payload["referral"] = body["referral"]?.DeepClone();
        }

        // On-chain account creation/broadcast can take longer than the default.
        await Upstream.Pipe(
            ApiClient.ApiRequest("signup/account-create", HttpMethod.Post, headers, payload, null, 30000), ctx);
    }

    public static async Task CreateAccountFriend(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var headers = new List<KeyValuePair<string, string>> { new("X-Real-IP-V", SignupClientIp(ctx)) };

        var payload = new JsonObject();
        if (body.ContainsKey("username"))
        {
            payload["username"] = body["username"]?.DeepClone();
        }
        if (body.ContainsKey("email"))
        {
            payload["email"] = body["email"]?.DeepClone();
        }
        if (body.ContainsKey("friend"))
        {
            payload["friend"] = body["friend"]?.DeepClone();
        }

        // On-chain account creation/broadcast can take longer than the default.
        await Upstream.Pipe(
            ApiClient.ApiRequest("signup/account-create-friend", HttpMethod.Post, headers, payload, null, 30000), ctx);
    }
}
