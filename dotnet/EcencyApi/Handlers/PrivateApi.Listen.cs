using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Listen: natural-voice audio of a post that a reader unlocks with Points. The audio
/// is generated asynchronously upstream; the client unlocks once, then polls the status
/// until its tracks are ready.
/// </summary>
public static partial class PrivateApi
{
    internal static Func<JsonObject, Task<string?>> ListenValidateCode = ValidateCode;

    internal static Func<string?> ListenSecret = () => Config.ListenProxySecret;

    /// <summary>The upstream call, replaceable so tests can observe it without a network.</summary>
    internal static Func<string, HttpMethod, IEnumerable<KeyValuePair<string, string>>?, JsonNode?,
            IEnumerable<KeyValuePair<string, string?>>?, Task<UpstreamResponse>>
        ListenUpstream = (endpoint, method, headers, payload, query) =>
            ApiClient.ApiRequest(endpoint, method, headers, payload, query);

    /// <summary>
    /// The secret that lets the upstream trust the reader this proxy names. Without it
    /// configured, nothing that names a reader is sent (503), rather than letting the
    /// upstream refuse it as forged.
    /// </summary>
    private static KeyValuePair<string, string>[]? ListenSecretHeaders()
    {
        var secret = ListenSecret();
        return string.IsNullOrEmpty(secret)
            ? null
            : new[] { new KeyValuePair<string, string>("X-Internal-Secret", secret) };
    }

    private static string? ListenString(JsonObject body, string key) =>
        body[key] is JsonValue v && JsVal.TryGetStringLenient(v, out var s) && s.Length > 0 ? s : null;

    /// <summary>
    /// State of a post's audio. Anonymous readers may ask (the bar shows "log in to
    /// listen"); with a code, the reader is resolved from it and the answer says whether
    /// they unlocked the post and, if so, carries signed track URLs. A code that does not
    /// validate is a 401 rather than a silent anonymous answer, so the client refreshes
    /// its token instead of showing a paid post as locked.
    /// </summary>
    public static async Task ListenStatus(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        string? username = null;
        // Any `code` property, even empty or not a string, is an attempt to be a reader:
        // only its absence means anonymous.
        if (body.TryGetPropertyValue("code", out var codeNode) && codeNode != null)
        {
            username = await ListenValidateCode(body);
            if (username == null)
            {
                await ctx.SendText(401, "Unauthorized");
                return;
            }
        }

        var author = ListenString(body, "author");
        var permlink = ListenString(body, "permlink");
        if (author == null || permlink == null)
        {
            await ctx.SendText(400, "Bad Request");
            return;
        }

        var query = new List<KeyValuePair<string, string?>>
        {
            new("author", author),
            new("permlink", permlink),
        };
        if (username != null) query.Add(new("us", username));
        var updated = ListenString(body, "updated");
        if (updated != null) query.Add(new("updated", updated));

        KeyValuePair<string, string>[]? headers = null;
        if (username != null)
        {
            headers = ListenSecretHeaders();
            if (headers == null)
            {
                Console.Error.WriteLine("listenStatus(): LISTEN_PROXY_SECRET not configured");
                await ctx.SendText(503, "Service Unavailable");
                return;
            }
        }

        await Upstream.Pipe(ListenUpstream("listen-status", HttpMethod.Get, headers, null, query), ctx);
    }

    /// <summary>
    /// Unlock a post's audio. `us` comes only from the validated code: upstream charges
    /// whoever `us` names. idempotency_key is required and forwarded as sent, so a
    /// retried unlock is answered as a replay instead of charging twice. Only the known
    /// fields are forwarded: this request spends Points, and nothing else a client puts
    /// in the body should reach the upstream.
    /// </summary>
    public static async Task Listen(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ListenValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        if (ListenString(body, "author") == null || ListenString(body, "permlink") == null
            || string.IsNullOrWhiteSpace(ListenString(body, "idempotency_key")))
        {
            await ctx.SendText(400, "Bad Request");
            return;
        }
        var headers = ListenSecretHeaders();
        if (headers == null)
        {
            Console.Error.WriteLine("listen(): LISTEN_PROXY_SECRET not configured");
            await ctx.SendText(503, "Service Unavailable");
            return;
        }
        var data = new JsonObject
        {
            ["us"] = username,
        };
        MiscCopyIfPresent(data, body, "author", "permlink", "idempotency_key", "updated");
        await Upstream.Pipe(ListenUpstream("listen", HttpMethod.Post, headers, data, null), ctx);
    }
}
