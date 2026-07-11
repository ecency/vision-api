using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/private-api.ts lines 1463-1703: points/promote/
/// boost/RC-delegation price+status endpoints and the Stripe payment routes.
/// </summary>
public static partial class PrivateApi
{
    public static async Task PointsClaim(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var data = new JsonObject { ["us"] = username };
        await Upstream.Pipe(ApiClient.ApiRequest("claim", HttpMethod.Put, null, data), ctx);
    }

    public static async Task PointsCalc(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var amount = PaymentsTemplateValue(body, "amount");
        await Upstream.Pipe(
            ApiClient.ApiRequest($"estm-calc?username={username}&amount={amount}", HttpMethod.Get), ctx);
    }

    public static async Task PromotePrice(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest("promote-price", HttpMethod.Get), ctx);
    }

    public static async Task PromotedPost(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var author = PaymentsTemplateValue(body, "author");
        var permlink = PaymentsTemplateValue(body, "permlink");
        await Upstream.Pipe(
            ApiClient.ApiRequest($"promoted-posts/{author}/{permlink}", HttpMethod.Get), ctx);
    }

    public static async Task BoostPlusPrice(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest("boost-plus-price", HttpMethod.Get), ctx);
    }

    public static async Task BoostedPlusAccount(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var account = PaymentsTemplateValue(body, "account");
        await Upstream.Pipe(
            ApiClient.ApiRequest($"boosted-plus-accounts/{account}", HttpMethod.Get), ctx);
    }

    public static async Task RcDelegationPrice(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest("rc-delegation-price", HttpMethod.Get), ctx);
    }

    public static async Task RcDelegationActive(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        // Self-only: always check the authenticated user's own active top-up.
        await Upstream.Pipe(
            ApiClient.ApiRequest($"rc-delegation-active/{username}", HttpMethod.Get), ctx);
    }

    public static async Task StripeCreateIntent(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        // Fail closed at the route (not a whole-service startup crash): never forward a money
        // request with an empty/absent shared secret.
        var secret = Config.StripeInternalSecret;
        if (string.IsNullOrEmpty(secret))
        {
            await ctx.SendText(503, "payments not configured");
            return;
        }
        // `user` is ALWAYS the authenticated caller (never client-supplied). sku/nonce/meta
        // are validated server-side by ePoints (amount + points come from its catalog, the
        // nonce is required for idempotency, the sku is points-only). The shared secret
        // authenticates this hop to ePoints; ePoints never trusts a client-sent price.
        // hosting_target is optional: on the hosting rail it activates a DIFFERENT tenant than
        // the buyer; ePoints validates it and ignores it on every non-hosting rail.
        // gift_recipient / gift_message (Points gift rail): the buyer pays and the Points are
        // credited to gift_recipient instead of the buyer; ePoints validates + verifies the
        // recipient exists before charging and ignores these on non-Points skus.

        // hosting_target is client-supplied and crosses into the trusted internal request.
        // Reject a malformed value at this boundary before forwarding (typeof guard first:
        // a non-string must never be coerced by the regex test and forwarded).
        var hostingTargetPresent = body.TryGetPropertyValue("hosting_target", out var hostingTargetNode);
        var hostingTarget = hostingTargetNode is JsonValue htv && htv.TryGetValue<string>(out var hts)
            ? hts
            : null;
        if (hostingTargetPresent && (hostingTarget == null || !PaymentsHiveNameRegex.IsMatch(hostingTarget)))
        {
            await ctx.SendText(400, "invalid hosting target");
            return;
        }
        // Same boundary guard for the gift recipient (typeof before regex); Hive names are
        // lowercase, so normalize typed input (trim + lowercase) before validating so a
        // recipient like "Alice" is accepted as "alice", and forward the canonical form.
        var giftRecipientPresent = body.TryGetPropertyValue("gift_recipient", out var giftRecipientNode);
        var giftRecipient = giftRecipientNode is JsonValue grv && grv.TryGetValue<string>(out var grs)
            ? grs.Trim().ToLowerInvariant()
            : null;
        if (giftRecipientPresent && (giftRecipient == null || !PaymentsHiveNameRegex.IsMatch(giftRecipient)))
        {
            await ctx.SendText(400, "invalid gift recipient");
            return;
        }
        var giftMessagePresent = body.TryGetPropertyValue("gift_message", out var giftMessageNode);
        var giftMessage = giftMessageNode is JsonValue gmv && gmv.TryGetValue<string>(out var gms)
            ? gms
            : null;
        if (giftMessagePresent && (giftMessage == null || giftMessage.Length > 200))
        {
            await ctx.SendText(400, "invalid gift message");
            return;
        }

        var headers = new[] { new KeyValuePair<string, string>("X-Internal-Secret", secret) };
        // JSON.stringify omits undefined: each optional key is forwarded only when it was
        // present in the request body (present-with-null is kept as null).
        var payload = new JsonObject { ["user"] = username };
        if (body.TryGetPropertyValue("sku", out var sku))
        {
            payload["sku"] = sku?.DeepClone();
        }
        if (body.TryGetPropertyValue("nonce", out var nonce))
        {
            payload["nonce"] = nonce?.DeepClone();
        }
        if (body.TryGetPropertyValue("meta", out var meta))
        {
            payload["meta"] = meta?.DeepClone();
        }
        if (hostingTargetPresent)
        {
            payload["hosting_target"] = hostingTarget;
        }
        if (giftRecipientPresent)
        {
            payload["gift_recipient"] = giftRecipient;
        }
        if (giftMessagePresent)
        {
            payload["gift_message"] = giftMessage;
        }
        await Upstream.Pipe(
            ApiClient.ApiRequest("stripe/create-intent", HttpMethod.Post, headers, payload, null, 20000), ctx);
    }

    public static async Task StripeOrderStatus(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var secret = Config.StripeInternalSecret;
        if (string.IsNullOrEmpty(secret))
        {
            await ctx.SendText(503, "payments not configured");
            return;
        }
        var paymentIntent = body.Str("payment_intent");
        if (paymentIntent == null || paymentIntent.Trim().Length == 0)
        {
            await ctx.SendText(400, "payment_intent required");
            return;
        }
        // Owner-scoped: ePoints filters by ?user, so a caller can only read its own order.
        var headers = new[] { new KeyValuePair<string, string>("X-Internal-Secret", secret) };
        await Upstream.Pipe(
            ApiClient.ApiRequest(
                $"stripe/order/{PaymentsEncodeUriComponent(paymentIntent.Trim())}?user={PaymentsEncodeUriComponent(username)}",
                HttpMethod.Get, headers, null, null, 20000),
            ctx);
    }

    public static async Task StripeCreateAccountIntent(HttpContext ctx)
    {
        // ANONYMOUS paid-account purchase: NO validateCode (the buyer has no Hive account yet).
        // The captcha is the human gate (ALWAYS enforced for this paid flow, independent of
        // CAPTCHA_MODE). The backend re-validates the username/email/availability and computes
        // the amount server-side before charging; this hop only authenticates with the shared
        // secret and forwards the request.
        var body = await ctx.ReadBody();
        var secret = Config.StripeInternalSecret;
        if (string.IsNullOrEmpty(secret))
        {
            await ctx.SendText(503, "payments not configured");
            return;
        }
        var ip = PaymentsSignupClientIp(ctx);
        // This route mints ONLY the accounts rail; never let it create a points order (which
        // the points route binds to an authenticated caller).
        var sku = body.Str("sku");
        if (sku == null || !sku.EndsWith("accounts", StringComparison.Ordinal))
        {
            await ctx.SendText(400, "invalid product");
            return;
        }
        // Validate cheap inputs BEFORE the single-use Turnstile check, so a fixable input error
        // (e.g. a missing username) never burns the user's one-time captcha token.
        var metaObj = body.Field("meta") as JsonObject;
        var metaUsername = metaObj?.Str("username");
        var username = metaUsername != null ? metaUsername.Trim().ToLowerInvariant() : "";
        if (username.Length == 0)
        {
            await ctx.SendText(400, "username required");
            return;
        }
        if (!await PaymentsVerifyTurnstile(body.Field("captcha_token"), ip))
        {
            await ctx.SendJson(406, new JsonObject
            {
                ["code"] = 113,
                ["message"] = "Please complete the verification and try again.",
            });
            return;
        }
        // `user` carries the new account name as the order identity; the backend re-derives +
        // re-validates it. Forward a NORMALIZED meta.username so meta and `user` agree. Forward
        // the client IP for backend-side rate-limiting / audit.
        var headers = new[]
        {
            new KeyValuePair<string, string>("X-Internal-Secret", secret),
            new KeyValuePair<string, string>("X-Real-IP-V", ip),
        };
        // meta is necessarily an object here (meta.username was a non-empty string), so the
        // TS `meta ? { ...meta, username } : meta` branch always spreads.
        var metaClone = (JsonObject)metaObj!.DeepClone();
        metaClone["username"] = username;
        var payload = new JsonObject { ["user"] = username, ["sku"] = sku };
        if (body.TryGetPropertyValue("nonce", out var nonce))
        {
            payload["nonce"] = nonce?.DeepClone();
        }
        payload["meta"] = metaClone;
        await Upstream.Pipe(
            ApiClient.ApiRequest("stripe/create-intent", HttpMethod.Post, headers, payload, null, 20000), ctx);
    }

    public static async Task StripeAccountStatus(HttpContext ctx)
    {
        // ANONYMOUS status poll: no Hive account yet, so no validateCode owner-binding. Scope by
        // the buyer-supplied username + payment_intent; the backend filters by ?user, so the
        // order resolves only when BOTH match what it created. Status is low-sensitivity.
        var body = await ctx.ReadBody();
        var secret = Config.StripeInternalSecret;
        if (string.IsNullOrEmpty(secret))
        {
            await ctx.SendText(503, "payments not configured");
            return;
        }
        var paymentIntent = body.Str("payment_intent");
        var pi = paymentIntent != null ? paymentIntent.Trim() : "";
        var usernameRaw = body.Str("username");
        var user = usernameRaw != null ? usernameRaw.Trim().ToLowerInvariant() : "";
        if (pi.Length == 0 || user.Length == 0)
        {
            await ctx.SendText(400, "payment_intent and username required");
            return;
        }
        var headers = new[] { new KeyValuePair<string, string>("X-Internal-Secret", secret) };
        await Upstream.Pipe(
            ApiClient.ApiRequest(
                $"stripe/order/{PaymentsEncodeUriComponent(pi)}?user={PaymentsEncodeUriComponent(user)}",
                HttpMethod.Get, headers, null, null, 20000),
            ctx);
    }

    public static async Task BoostOptions(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest("boost-options", HttpMethod.Get), ctx);
    }

    public static async Task BoostedPost(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var author = PaymentsTemplateValue(body, "author");
        var permlink = PaymentsTemplateValue(body, "permlink");
        await Upstream.Pipe(
            ApiClient.ApiRequest($"boosted-posts/{author}/{permlink}", HttpMethod.Get), ctx);
    }

    // --- helpers local to this chunk (prefixed to avoid partial-class collisions) ---

    // /^(?=.{3,16}$)[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*$/ — \z instead of $ because .NET's $
    // also matches before a trailing newline, which JS's does not.
    private static readonly Regex PaymentsHiveNameRegex =
        new(@"^(?=.{3,16}\z)[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*\z", RegexOptions.Compiled);

    /// <summary>
    /// signupClientIp(): originating client IP from the proxy-set X-Real-IP header.
    /// X-Forwarded-For is deliberately not used (client-suppliable). '' when absent.
    /// </summary>
    private static string PaymentsSignupClientIp(HttpContext ctx)
    {
        var realIp = ctx.Request.Headers["X-Real-IP"];
        var value = realIp.Count switch
        {
            0 => (string?)null,
            1 => realIp[0],
            // Node joins duplicate non-special headers with ", "
            _ => string.Join(", ", realIp.ToArray()),
        };
        return string.IsNullOrEmpty(value) ? "" : value;
    }

    /// <summary>
    /// verifyTurnstile(): server-side Cloudflare Turnstile verification. Returns true ONLY on
    /// a confirmed-human token; fails CLOSED on a missing secret/token, provider error, or
    /// rejection (a network blip must never open the gate).
    /// </summary>
    private static async Task<bool> PaymentsVerifyTurnstile(JsonNode? token, string ip)
    {
        var secret = Config.TurnstileSecret;
        var tokenStr = token is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
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
            if (ip.Length > 0)
            {
                form.Add(new("remoteip", ip));
            }
            using var req = new HttpRequestMessage(
                HttpMethod.Post, "https://challenges.cloudflare.com/turnstile/v0/siteverify")
            {
                Content = new FormUrlEncodedContent(form),
            };
            using var cts = new CancellationTokenSource(8000);
            using var resp = await Upstream.Http.SendAsync(
                req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                // axios' default validateStatus rejects non-2xx -> caught -> false
                Console.WriteLine(
                    $"verifyTurnstile: siteverify error Request failed with status code {(int)resp.StatusCode}");
                return false;
            }
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // axios keeps a non-JSON body as a raw string; .success is then undefined
                return false;
            }
            // r?.data?.success === true (strict: boolean true only)
            return parsed is JsonObject po
                && po.TryGetPropertyValue("success", out var success)
                && success is JsonValue sv
                && sv.TryGetValue<bool>(out var ok)
                && ok;
        }
        catch (Exception err)
        {
            Console.WriteLine($"verifyTurnstile: siteverify error {err.Message}");
            return false;
        }
    }

    /// <summary>encodeURIComponent(): Uri.EscapeDataString also escapes !'()* — undo those.</summary>
    private static string PaymentsEncodeUriComponent(string s) =>
        Uri.EscapeDataString(s)
            .Replace("%21", "!")
            .Replace("%27", "'")
            .Replace("%28", "(")
            .Replace("%29", ")")
            .Replace("%2A", "*");

    /// <summary>JS template-literal coercion for a body field: absent -> "undefined".</summary>
    private static string PaymentsTemplateValue(JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) ? PaymentsJsToString(v) : "undefined";

    /// <summary>String(x) coercion for a JSON value (null -> "null", array -> joined, ...).</summary>
    private static string PaymentsJsToString(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonArray arr:
                // Array.prototype.toString: comma-joined elements; null slots -> ""
                return string.Join(",", arr.Select(e => e == null ? "" : PaymentsJsToString(e)));
            case JsonObject:
                return "[object Object]";
            case JsonValue value:
                if (value.TryGetValue<string>(out var s))
                {
                    return s;
                }
                if (value.TryGetValue<bool>(out var b))
                {
                    return b ? "true" : "false";
                }
                // numbers: String(n) == JSON.stringify(n) for every finite double
                return JsJson.Stringify(node);
            default:
                return JsJson.Stringify(node);
        }
    }
}
