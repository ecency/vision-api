using System.Text.Json;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/private-api.ts lines 1004-1257: notifications,
/// devices, images, drafts, bookmarks and support-settings passthroughs.
/// </summary>
public static partial class PrivateApi
{
    /// <summary>
    /// Upstream path for the notifications feed, or null when a value cannot be
    /// expressed as a single path segment.
    ///
    /// Every caller-supplied value is escaped. These are arbitrary body strings, so
    /// one carrying `/`, `?` or `#` would otherwise be re-parsed as URL structure
    /// once this string becomes a Uri, addressing a different upstream resource with
    /// this service's credentials attached. Same reasoning as PostTipsPath.
    ///
    /// Hive account names, filter names, notification ids and integer limits are all
    /// unreserved characters, which EscapeDataString leaves byte-identical, so real
    /// traffic is unaffected.
    /// </summary>
    public static string? NotificationsPath(
        string username, string? filter, string? since, string? limit, bool fullScope)
    {
        if (IsDotSegment(username) || (filter != null && IsDotSegment(filter)))
        {
            return null;
        }

        var u = filter != null
            ? $"{Uri.EscapeDataString(filter)}/{Uri.EscapeDataString(username)}"
            : $"activities/{Uri.EscapeDataString(username)}";

        var query = new List<string>();

        if (since != null)
        {
            query.Add($"since={Uri.EscapeDataString(since)}");
        }

        if (limit != null)
        {
            query.Add($"limit={Uri.EscapeDataString(limit)}");
        }

        // Opts in to the complete feed. enotify defaults to chain-derived activity only,
        // so omitting this is the safe direction: a cross-account view, or any request
        // that never reaches this handler, gets the restricted feed.
        if (fullScope)
        {
            query.Add("scope=full");
        }

        return query.Count == 0 ? u : $"{u}?{string.Join("&", query)}";
    }

    // POST ^/private-api/notifications$
    public static async Task Notifications(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        var user = body.Field("user");

        // A valid code is required. This guard used to accept a bare `user` field in
        // place of one, so an unauthenticated caller could name any account and be
        // served its notifications.
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        // Decks builds a notifications column for an arbitrary account and sends that
        // name here alongside the signed-in user's own code (vision-web
        // deck-notifications-column.tsx passes settings.username). That stays supported:
        // notifications are largely public data and the column exists for that reason.
        //
        // But the feed also carries Ecency-only activity that is not public, notably
        // favorites and bookmarks, which reveal who a user follows and what they saved,
        // plus Points transfers and the various streaks and aggregates. So a request for
        // SOMEONE ELSE's notifications is left at enotify's restricted default, and only
        // a self-view asks for scope=full. This service is the only layer that can make
        // that call, because it is the only one that has validated who is asking.
        // Only a caller asking for their OWN notifications gets the complete feed.
        var fullScope = true;

        if (JsJson.IsTruthy(user))
        {
            var requested = UserData1Helpers.Template(user);
            fullScope = string.Equals(requested, username, StringComparison.OrdinalIgnoreCase);
            username = requested;
        }

        var filter = body.Field("filter");
        var since = body.Field("since");
        var limit = body.Field("limit");

        var u = NotificationsPath(
            username,
            JsJson.IsTruthy(filter) ? UserData1Helpers.Template(filter) : null,
            JsJson.IsTruthy(since) ? UserData1Helpers.Template(since) : null,
            JsJson.IsTruthy(limit) ? UserData1Helpers.Template(limit) : null,
            fullScope);

        if (u == null)
        {
            await ctx.SendText(400, "Invalid user or filter");
            return;
        }

        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    // GET ^/private-api/pub-notifications/:username
    public static async Task PublicUnreadNotifications(HttpContext ctx)
    {
        var username = ctx.Request.RouteValues["username"]?.ToString();

        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(400, "Missing username");
            return;
        }

        await Upstream.Pipe(ApiClient.ApiRequest($"activities/{username}/unread-count", HttpMethod.Get), ctx);
    }

    /* Login required endpoints */

    // POST ^/private-api/notifications/unread$
    public static async Task UnreadNotifications(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        await Upstream.Pipe(ApiClient.ApiRequest($"activities/{username}/unread-count", HttpMethod.Get), ctx);
    }

    // POST ^/private-api/notifications/mark$
    public static async Task MarkNotifications(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var id = body.Field("id");
        var data = new JsonObject();

        if (JsJson.IsTruthy(id))
        {
            data["id"] = id!.DeepClone();
        }

        await Upstream.Pipe(ApiClient.ApiRequest($"activities/{username}", HttpMethod.Put, null, data), ctx);
    }

    // POST ^/private-api/register-device$
    public static async Task RegisterDevice(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var authedUsername = await ValidateCode(body);
        if (string.IsNullOrEmpty(authedUsername))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        // Payload takes the fields from the request body, not the authed user.
        var data = new JsonObject();
        UserData1Helpers.CopyIfPresent(data, body, "username");
        UserData1Helpers.CopyIfPresent(data, body, "token");
        UserData1Helpers.CopyIfPresent(data, body, "system");
        UserData1Helpers.CopyIfPresent(data, body, "allows_notify");
        UserData1Helpers.CopyIfPresent(data, body, "notify_types");
        await Upstream.Pipe(ApiClient.ApiRequest("rgstrmbldvc/", HttpMethod.Post, null, data), ctx);
    }

    // POST ^/private-api/detail-device$
    public static async Task DetailDevice(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var authedUsername = await ValidateCode(body);
        if (string.IsNullOrEmpty(authedUsername))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var username = UserData1Helpers.TemplateOf(body, "username");
        var token = UserData1Helpers.TemplateOf(body, "token");
        await Upstream.Pipe(ApiClient.ApiRequest($"mbldvcdtl/{username}/{token}", HttpMethod.Get), ctx);
    }

    // POST ^/private-api/images$
    public static async Task Images(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        await Upstream.Pipe(
            ApiClient.ApiRequest($"images/{username}", HttpMethod.Get, null, null, UserData1Helpers.ExpressQuery(ctx)),
            ctx);
    }

    // POST ^/private-api/images-delete$
    public static async Task ImagesDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var id = UserData1Helpers.TemplateOf(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"images/{username}/{id}", HttpMethod.Delete), ctx);
    }

    // POST ^/private-api/images-add$
    public static async Task ImagesAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var data = new JsonObject { ["username"] = username };
        if (body.TryGetPropertyValue("url", out var url))
        {
            data["image_url"] = url?.DeepClone();
        }
        await Upstream.Pipe(ApiClient.ApiRequest("image", HttpMethod.Post, null, data), ctx);
    }

    // POST ^/private-api/drafts$
    public static async Task Drafts(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        await Upstream.Pipe(
            ApiClient.ApiRequest($"drafts/{username}", HttpMethod.Get, null, null, UserData1Helpers.ExpressQuery(ctx)),
            ctx);
    }

    // POST ^/private-api/drafts-add$
    public static async Task DraftsAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var data = new JsonObject { ["username"] = username };
        UserData1Helpers.CopyIfPresent(data, body, "title");
        UserData1Helpers.CopyIfPresent(data, body, "body");
        UserData1Helpers.CopyIfPresent(data, body, "tags");
        UserData1Helpers.CopyIfPresent(data, body, "meta");
        await Upstream.Pipe(ApiClient.ApiRequest("draft", HttpMethod.Post, null, data), ctx);
    }

    // POST ^/private-api/drafts-update$
    public static async Task DraftsUpdate(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var id = UserData1Helpers.TemplateOf(body, "id");
        var data = new JsonObject { ["username"] = username };
        UserData1Helpers.CopyIfPresent(data, body, "title");
        UserData1Helpers.CopyIfPresent(data, body, "body");
        UserData1Helpers.CopyIfPresent(data, body, "tags");
        UserData1Helpers.CopyIfPresent(data, body, "meta");
        await Upstream.Pipe(ApiClient.ApiRequest($"drafts/{username}/{id}", HttpMethod.Put, null, data), ctx);
    }

    // POST ^/private-api/drafts-delete$
    public static async Task DraftsDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var id = UserData1Helpers.TemplateOf(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"drafts/{username}/{id}", HttpMethod.Delete), ctx);
    }

    // POST ^/private-api/bookmarks$
    public static async Task Bookmarks(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        await Upstream.Pipe(
            ApiClient.ApiRequest($"bookmarks/{username}", HttpMethod.Get, null, null, UserData1Helpers.ExpressQuery(ctx)),
            ctx);
    }

    // POST ^/private-api/bookmarks-add$
    public static async Task BookmarksAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var data = new JsonObject { ["username"] = username };
        UserData1Helpers.CopyIfPresent(data, body, "author");
        UserData1Helpers.CopyIfPresent(data, body, "permlink");
        data["chain"] = "steem";
        await Upstream.Pipe(ApiClient.ApiRequest("bookmark", HttpMethod.Post, null, data), ctx);
    }

    // POST ^/private-api/bookmarks-delete$
    public static async Task BookmarksDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var id = UserData1Helpers.TemplateOf(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"bookmarks/{username}/{id}", HttpMethod.Delete), ctx);
    }

    /// <summary>
    /// Parse and validate the support-settings payload. Both fields must be integers
    /// within 0..100 (booleans, strings and floats are rejected). Returns the parsed
    /// pair, or null when the payload is invalid. Exported for unit tests in the TS
    /// source; kept public here for the same reason (not wired to any route itself).
    /// Values stay doubles because JS Number.isInteger accepts any integral number.
    /// </summary>
    public static (double BeneficiaryPercent, double CurationPercent)? ParseSupportSettingsPayload(JsonNode? body)
    {
        JsonNode? beneficiaryPercent = null;
        JsonNode? curationPercent = null;
        if (body is JsonObject obj)
        {
            obj.TryGetPropertyValue("beneficiary_percent", out beneficiaryPercent);
            obj.TryGetPropertyValue("curation_percent", out curationPercent);
        }

        if (!UserData1Helpers.IsPercent(beneficiaryPercent, out var beneficiary) ||
            !UserData1Helpers.IsPercent(curationPercent, out var curation))
        {
            return null;
        }
        return (beneficiary, curation);
    }

    // Support settings are per-user opt-ins, so the username is taken from the
    // authenticated token (validateCode), never from the request body.
    // POST ^/private-api/support-settings$
    public static async Task SupportSettings(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await RequireAuthedUsername(ctx, body);
        if (username == null)
        {
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest($"support-settings/{username}", HttpMethod.Get), ctx);
    }

    // POST ^/private-api/support-settings-update$
    public static async Task SupportSettingsUpdate(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await RequireAuthedUsername(ctx, body);
        if (username == null)
        {
            return;
        }

        var parsed = ParseSupportSettingsPayload(body);
        if (parsed == null)
        {
            await ctx.SendText(400,
                "beneficiary_percent and curation_percent must be integers between 0 and 100");
            return;
        }

        var (beneficiaryPercent, curationPercent) = parsed.Value;
        var payload = new JsonObject
        {
            ["username"] = username,
            ["beneficiary_percent"] = beneficiaryPercent,
            ["curation_percent"] = curationPercent,
        };
        await Upstream.Pipe(
            ApiClient.ApiRequest($"support-settings/{username}", HttpMethod.Put, null, payload),
            ctx);
    }
}

/// <summary>File-local helpers (JS template-literal/undefined semantics).</summary>
file static class UserData1Helpers
{
    /// <summary>JS template-literal interpolation (`${v}`) for a JSON value.</summary>
    public static string Template(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject:
                return "[object Object]";
            case JsonArray arr:
                // Array.prototype.toString: elements joined by ",", holes/null -> ""
                return string.Join(",", arr.Select(item => item == null ? "" : Template(item)));
            default:
                if (node is JsonValue jv && JsVal.TryGetStringLenient(jv, out var s))
                {
                    return s;
                }
                // numbers and booleans stringify identically to String(v)
                return JsJson.Stringify(node);
        }
    }

    /// <summary>Template interpolation of a body field; absent key == undefined -> "undefined".</summary>
    public static string TemplateOf(JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) ? Template(v) : "undefined";

    /// <summary>
    /// Copy a destructured body field into a payload only when the key is present
    /// (absent == undefined, which JSON.stringify omits; present null is kept).
    /// </summary>
    public static void CopyIfPresent(JsonObject target, JsonObject body, string key)
    {
        if (body.TryGetPropertyValue(key, out var v))
        {
            target[key] = v?.DeepClone();
        }
    }

    /// <summary>
    /// req.query -> axios params: single values as key=value, repeated keys the
    /// way axios serializes qs arrays (key[]=v per element).
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string?>> ExpressQuery(HttpContext ctx)
    {
        var list = new List<KeyValuePair<string, string?>>();
        foreach (var kv in ctx.Request.Query)
        {
            if (kv.Value.Count > 1)
            {
                foreach (var v in kv.Value)
                {
                    list.Add(new KeyValuePair<string, string?>($"{kv.Key}[]", v));
                }
            }
            else
            {
                list.Add(new KeyValuePair<string, string?>(kv.Key, kv.Value.ToString()));
            }
        }
        return list;
    }

    /// <summary>typeof v === "number" &amp;&amp; Number.isInteger(v) &amp;&amp; v &gt;= 0 &amp;&amp; v &lt;= 100.</summary>
    public static bool IsPercent(JsonNode? v, out double value)
    {
        value = 0;
        if (v is not JsonValue jv)
        {
            return false;
        }

        double d;
        if (jv.TryGetValue<JsonElement>(out var el))
        {
            if (el.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
            d = el.GetDouble();
        }
        else if (jv.TryGetValue<int>(out var i))
        {
            d = i;
        }
        else if (jv.TryGetValue<long>(out var l))
        {
            d = l;
        }
        else if (jv.TryGetValue<double>(out var dd))
        {
            d = dd;
        }
        else if (jv.TryGetValue<decimal>(out var m))
        {
            d = (double)m;
        }
        else
        {
            return false;
        }

        if (double.IsNaN(d) || double.IsInfinity(d) || Math.Floor(d) != d)
        {
            return false; // Number.isInteger rejects non-integral values
        }
        if (d < 0 || d > 100)
        {
            return false;
        }
        value = d;
        return true;
    }
}
