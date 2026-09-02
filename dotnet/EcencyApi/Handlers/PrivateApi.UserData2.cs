using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/private-api.ts user-data endpoints:
/// schedules, favorites, fragments, decks and recoveries. All are
/// validateCode-gated passthroughs to the private API.
/// </summary>
public static partial class PrivateApi
{
    public static async Task Schedules(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(
            ApiClient.ApiRequest($"schedules/{username}", HttpMethod.Get, null, null, UserData2Js.Query(ctx)),
            ctx);
    }

    public static async Task SchedulesAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        // { username, permlink, title, body, meta, options, schedule, reblog: reblog ? 1 : 0 }
        // — destructured keys are only serialized when present in the request
        // body (JSON.stringify omits undefined); reblog is always 0/1.
        var data = new JsonObject { ["username"] = username };
        UserData2Js.CopyIfPresent(body, data, "permlink");
        UserData2Js.CopyIfPresent(body, data, "title");
        UserData2Js.CopyIfPresent(body, data, "body");
        UserData2Js.CopyIfPresent(body, data, "meta");
        UserData2Js.CopyIfPresent(body, data, "options");
        UserData2Js.CopyIfPresent(body, data, "schedule");
        data["reblog"] = JsJson.IsTruthy(body.Field("reblog")) ? 1 : 0;

        await Upstream.Pipe(ApiClient.ApiRequest("schedules", HttpMethod.Post, null, data), ctx);
    }

    public static async Task SchedulesDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = UserData2Js.TemplateField(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"schedules/{username}/{id}", HttpMethod.Delete), ctx);
    }

    public static async Task SchedulesMove(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = UserData2Js.TemplateField(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"schedules/{username}/{id}", HttpMethod.Put), ctx);
    }

    public static async Task Favorites(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(
            ApiClient.ApiRequest($"favorites/{username}", HttpMethod.Get, null, null, UserData2Js.Query(ctx)),
            ctx);
    }

    public static async Task FavoritesCheck(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var account = UserData2Js.TemplateField(body, "account");
        await Upstream.Pipe(ApiClient.ApiRequest($"isfavorite/{username}/{account}", HttpMethod.Get), ctx);
    }

    public static async Task FavoritesAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var data = new JsonObject { ["username"] = username };
        UserData2Js.CopyIfPresent(body, data, "account");
        await Upstream.Pipe(ApiClient.ApiRequest("favorite", HttpMethod.Post, null, data), ctx);
    }

    public static async Task FavoritesDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var account = UserData2Js.TemplateField(body, "account");
        await Upstream.Pipe(ApiClient.ApiRequest($"favoriteUser/{username}/{account}", HttpMethod.Delete), ctx);
    }

    /// <summary>
    /// The body's `tag`, or null when it is absent, not a string, or empty.
    ///
    /// The favorites handlers template a missing `account` into the literal
    /// "undefined". A tag must not go that way: "undefined", "null", "true" and
    /// "123" are all valid tag names upstream, so a request with no tag, or a
    /// non-string one, would check or delete a follow the caller never named.
    /// String extraction is lenient on purpose: JSON strings are arbitrary UTF-16.
    /// </summary>
    public static string? FavoriteTagField(JsonObject body)
    {
        if (!body.TryGetPropertyValue("tag", out var node) || node is not JsonValue value)
        {
            return null;
        }

        return JsVal.TryGetStringLenient(value, out var tag) && tag.Length > 0 ? tag : null;
    }

    /// <summary>
    /// Upstream path for a followed-tag check or removal, or null when a value cannot
    /// be expressed as a single path segment.
    ///
    /// The tag is a body string, so one carrying `/`, `?` or `#` would otherwise be
    /// re-parsed as URL structure once this string becomes a Uri, addressing a
    /// different upstream resource with this service's credentials attached. Same
    /// reasoning as NotificationsPath. Real tags are lowercase words with digits and
    /// hyphens, which EscapeDataString leaves byte-identical. A leading `#` a client
    /// may send is escaped rather than dropped: normalising the tag is the upstream's
    /// job, and it strips one.
    /// </summary>
    public static string? FavoriteTagPath(string action, string username, string tag)
    {
        if (IsDotSegment(username) || IsDotSegment(tag))
        {
            return null;
        }

        return $"{action}/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(tag)}";
    }

    public static async Task FavoriteTags(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(
            ApiClient.ApiRequest($"favorite-tags/{username}", HttpMethod.Get, null, null, UserData2Js.Query(ctx)),
            ctx);
    }

    public static async Task FavoriteTagsCheck(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var tag = FavoriteTagField(body);
        var path = tag == null ? null : FavoriteTagPath("isfavoritetag", username, tag);
        if (path == null)
        {
            await ctx.SendText(400, "Invalid tag");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest(path, HttpMethod.Get), ctx);
    }

    public static async Task FavoriteTagsAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var tag = FavoriteTagField(body);
        if (tag == null)
        {
            await ctx.SendText(400, "Invalid tag");
            return;
        }
        var data = new JsonObject { ["username"] = username, ["tag"] = tag };
        await Upstream.Pipe(ApiClient.ApiRequest("favorite-tag", HttpMethod.Post, null, data), ctx);
    }

    public static async Task FavoriteTagsDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var tag = FavoriteTagField(body);
        var path = tag == null ? null : FavoriteTagPath("favoriteTag", username, tag);
        if (path == null)
        {
            await ctx.SendText(400, "Invalid tag");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest(path, HttpMethod.Delete), ctx);
    }

    public static async Task Fragments(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(
            ApiClient.ApiRequest($"fragments/{username}", HttpMethod.Get, null, null, UserData2Js.Query(ctx)),
            ctx);
    }

    public static async Task FragmentsAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var data = new JsonObject { ["username"] = username };
        UserData2Js.CopyIfPresent(body, data, "title");
        UserData2Js.CopyIfPresent(body, data, "body");
        await Upstream.Pipe(ApiClient.ApiRequest("fragment", HttpMethod.Post, null, data), ctx);
    }

    public static async Task FragmentsUpdate(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = UserData2Js.TemplateField(body, "id");
        var data = new JsonObject();
        UserData2Js.CopyIfPresent(body, data, "title");
        UserData2Js.CopyIfPresent(body, data, "body");
        await Upstream.Pipe(ApiClient.ApiRequest($"fragments/{username}/{id}", HttpMethod.Put, null, data), ctx);
    }

    public static async Task FragmentsDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = UserData2Js.TemplateField(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"fragments/{username}/{id}", HttpMethod.Delete), ctx);
    }

    public static async Task Decks(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest($"decks/{username}", HttpMethod.Get), ctx);
    }

    public static async Task DecksAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var data = new JsonObject { ["username"] = username };
        UserData2Js.CopyIfPresent(body, data, "title");
        UserData2Js.CopyIfPresent(body, data, "settings");
        await Upstream.Pipe(ApiClient.ApiRequest("deck", HttpMethod.Post, null, data), ctx);
    }

    public static async Task DecksUpdate(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = UserData2Js.TemplateField(body, "id");
        var data = new JsonObject { ["username"] = username };
        UserData2Js.CopyIfPresent(body, data, "title");
        UserData2Js.CopyIfPresent(body, data, "settings");
        await Upstream.Pipe(ApiClient.ApiRequest($"decks/{username}/{id}", HttpMethod.Put, null, data), ctx);
    }

    public static async Task DecksDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = UserData2Js.TemplateField(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"decks/{username}/{id}", HttpMethod.Delete), ctx);
    }

    public static async Task Recoveries(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest($"recoveries/{username}", HttpMethod.Get), ctx);
    }

    public static async Task RecoveriesAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var data = new JsonObject { ["username"] = username };
        UserData2Js.CopyIfPresent(body, data, "email");
        UserData2Js.CopyIfPresent(body, data, "public_keys");
        await Upstream.Pipe(ApiClient.ApiRequest("recovery", HttpMethod.Post, null, data), ctx);
    }

    public static async Task RecoveriesUpdate(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = UserData2Js.TemplateField(body, "id");
        var data = new JsonObject { ["username"] = username };
        UserData2Js.CopyIfPresent(body, data, "email");
        UserData2Js.CopyIfPresent(body, data, "public_keys");
        await Upstream.Pipe(ApiClient.ApiRequest($"recoveries/{username}/{id}", HttpMethod.Put, null, data), ctx);
    }

    public static async Task RecoveriesDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = UserData2Js.TemplateField(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"recoveries/{username}/{id}", HttpMethod.Delete), ctx);
    }
}

/// <summary>
/// File-local helpers replicating the JS semantics these handlers rely on
/// (template-literal string coercion of destructured body fields, undefined-key
/// omission in payloads, req.query forwarding).
/// </summary>
file static class UserData2Js
{
    /// <summary>
    /// `${field}` for a destructured req.body field: absent key -> "undefined",
    /// explicit null -> "null", otherwise JS String() coercion.
    /// </summary>
    public static string TemplateField(JsonObject body, string key)
    {
        return body.TryGetPropertyValue(key, out var v) ? JsString(v) : "undefined";
    }

    private static string JsString(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject:
                return "[object Object]";
            case JsonArray arr:
                // Array.prototype.toString: join(","), null/undefined elements -> ""
                return string.Join(",", arr.Select(el => el == null ? "" : JsString(el)));
            case JsonValue v:
                if (JsVal.TryGetStringLenient(v, out var s)) return s;
                if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                // numbers: String(n) == JSON.stringify(n) for finite doubles
                return JsJson.Stringify(node);
            default:
                return node.ToJsonString();
        }
    }

    /// <summary>
    /// Add a payload key only when it exists in the request body (absent ==
    /// undefined == omitted by JSON.stringify; explicit null is kept).
    /// </summary>
    public static void CopyIfPresent(JsonObject src, JsonObject dst, string key)
    {
        if (src.TryGetPropertyValue(key, out var v))
        {
            dst[key] = v?.DeepClone();
        }
    }

    /// <summary>req.query forwarded as axios params.</summary>
    public static List<KeyValuePair<string, string?>> Query(HttpContext ctx)
    {
        var list = new List<KeyValuePair<string, string?>>();
        foreach (var kv in ctx.Request.Query)
        {
            foreach (var value in kv.Value)
            {
                list.Add(new KeyValuePair<string, string?>(kv.Key, value));
            }
        }
        return list;
    }
}
