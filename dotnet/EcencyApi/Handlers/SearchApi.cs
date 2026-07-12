using System.Text.Json.Nodes;

using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/search-api.ts — thin proxies to the search API,
/// forwarding selected body fields with the search token as Authorization.
/// </summary>
public static class SearchApi
{
    private static KeyValuePair<string, string>[] AuthHeaders() =>
        new[] { KeyValuePair.Create("Authorization", Config.SearchApiToken) };

    /// <summary>
    /// TS object literal from destructured body fields ({q, sort, ...}):
    /// JSON.stringify drops undefined (absent) keys but keeps null — copy the
    /// key only when it was present in the request body.
    /// </summary>
    private static void CopyIfPresent(JsonObject body, JsonObject payload, string key)
    {
        if (body.TryGetPropertyValue(key, out var v))
        {
            payload[key] = v?.DeepClone();
        }
    }

    /// <summary>"if (field) payload.field = field" — add only when JS-truthy.</summary>
    private static void CopyIfTruthy(JsonObject body, JsonObject payload, string key)
    {
        if (body.TryGetPropertyValue(key, out var v) && JsJson.IsTruthy(v))
        {
            payload[key] = v!.DeepClone();
        }
    }

    /// <summary>
    /// `${field}` template-literal coercion for body fields interpolated into
    /// upstream URLs: absent -> "undefined", null -> "null", etc.
    /// </summary>
    private static string JsTemplate(JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) ? JsToString(v) : "undefined";

    private static string JsToString(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject:
                return "[object Object]";
            case JsonArray arr:
                // Array.prototype.toString: elements joined with ",", null -> ""
                return string.Join(",", arr.Select(e => e == null ? "" : JsToString(e)));
            case JsonValue v:
                if (JsVal.TryGetStringLenient(v, out var s)) return s;
                if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                return JsJson.Stringify(v);
            default:
                return "undefined";
        }
    }

    public static async Task Search(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var url = $"{Config.SearchApiAddr}/search";

        var payload = new JsonObject();
        CopyIfPresent(body, payload, "q");
        CopyIfPresent(body, payload, "sort");
        CopyIfPresent(body, payload, "hide_low");

        CopyIfTruthy(body, payload, "since");
        CopyIfTruthy(body, payload, "scroll_id");
        CopyIfTruthy(body, payload, "votes");
        CopyIfTruthy(body, payload, "include_nsfw");

        // Search can be moderately slow; allow more than the default.
        await Upstream.Pipe(
            Upstream.BaseApiRequest(url, HttpMethod.Post, AuthHeaders(), payload, timeoutMs: 15000), ctx);
    }

    public static async Task SearchFollower(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var url = $"{Config.SearchApiAddr}/search-follower/{JsTemplate(body, "following")}";

        var payload = new JsonObject();
        CopyIfPresent(body, payload, "q");

        await Upstream.Pipe(
            Upstream.BaseApiRequest(url, HttpMethod.Post, AuthHeaders(), payload, timeoutMs: 15000), ctx);
    }

    public static async Task SearchFollowing(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var url = $"{Config.SearchApiAddr}/search-following/{JsTemplate(body, "follower")}";

        var payload = new JsonObject();
        CopyIfPresent(body, payload, "q");

        await Upstream.Pipe(
            Upstream.BaseApiRequest(url, HttpMethod.Post, AuthHeaders(), payload, timeoutMs: 15000), ctx);
    }

    public static async Task SearchAccount(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var url = $"{Config.SearchApiAddr}/search-account";

        var payload = new JsonObject();
        CopyIfPresent(body, payload, "q");
        CopyIfPresent(body, payload, "limit");
        CopyIfPresent(body, payload, "random");

        await Upstream.Pipe(
            Upstream.BaseApiRequest(url, HttpMethod.Post, AuthHeaders(), payload, timeoutMs: 15000), ctx);
    }

    public static async Task SearchTag(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var url = $"{Config.SearchApiAddr}/search-tag";

        var payload = new JsonObject();
        CopyIfPresent(body, payload, "q");
        CopyIfPresent(body, payload, "limit");
        CopyIfPresent(body, payload, "random");

        await Upstream.Pipe(
            Upstream.BaseApiRequest(url, HttpMethod.Post, AuthHeaders(), payload, timeoutMs: 15000), ctx);
    }

    public static async Task SearchPath(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var url = $"{Config.SearchApiAddr}/search-path";

        var payload = new JsonObject();
        CopyIfPresent(body, payload, "q");

        await Upstream.Pipe(
            Upstream.BaseApiRequest(url, HttpMethod.Post, AuthHeaders(), payload, timeoutMs: 15000), ctx);
    }

    public static async Task SearchSimilar(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var url = $"{Config.SearchApiAddr}/similar";

        var payload = new JsonObject();
        CopyIfPresent(body, payload, "author");
        CopyIfPresent(body, payload, "permlink");

        CopyIfTruthy(body, payload, "title");
        CopyIfTruthy(body, payload, "body");
        CopyIfTruthy(body, payload, "tags");
        CopyIfTruthy(body, payload, "since");
        CopyIfTruthy(body, payload, "include_nsfw");

        await Upstream.Pipe(
            Upstream.BaseApiRequest(url, HttpMethod.Post, AuthHeaders(), payload, timeoutMs: 15000), ctx);
    }
}
