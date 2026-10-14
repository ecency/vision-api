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

    /// <summary>The upstream call, replaceable so tests can observe it without a network.</summary>
    internal static Func<string, HttpMethod, JsonNode?, IEnumerable<KeyValuePair<string, string?>>?, Task<UpstreamResponse>>
        ListenUpstream = (endpoint, method, payload, query) =>
            ApiClient.ApiRequest(endpoint, method, null, payload, query);

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
        if (ListenString(body, "code") != null)
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

        await Upstream.Pipe(ListenUpstream("listen-status", HttpMethod.Get, null, query), ctx);
    }

    /// <summary>
    /// Unlock a post's audio. `us` comes only from the validated code: upstream charges
    /// whoever `us` names. idempotency_key is forwarded as sent, so a retried unlock is
    /// answered as a replay instead of charging twice.
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
        var data = new JsonObject
        {
            ["us"] = username,
        };
        MiscCopyIfPresent(data, body, "author", "permlink", "idempotency_key", "updated");
        await Upstream.Pipe(ListenUpstream("listen", HttpMethod.Post, data, null), ctx);
    }
}
