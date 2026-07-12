using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/private-api.ts — leaderboard, referrals,
/// curation, promoted entries, comment history, waves endpoints, points and
/// quests (the read-only private-API pass-through group).
/// </summary>
public static partial class PrivateApi
{
    public static async Task Leaderboard(HttpContext ctx)
    {
        // :duration(day|week|month) — the regex constraint lives in the route table.
        var duration = ctx.Request.RouteValues["duration"]?.ToString();
        await Upstream.Pipe(ApiClient.ApiRequest($"leaderboard?duration={duration}", HttpMethod.Get), ctx);
    }

    public static async Task Referrals(HttpContext ctx)
    {
        var username = ctx.Request.RouteValues["username"]?.ToString();
        var maxId = ctx.Request.Query["max_id"];

        var u = $"referrals/{username}?size=20";
        // JS `if (max_id)`: absent or single empty string is falsy; an array
        // (duplicate params) is truthy and interpolates comma-joined.
        if (maxId.Count > 1 || (maxId.Count == 1 && !string.IsNullOrEmpty(maxId[0])))
        {
            u += $"&max_id={maxId.ToString()}";
        }

        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task ReferralsStats(HttpContext ctx)
    {
        var username = ctx.Request.RouteValues["username"]?.ToString();
        var u = $"referrals/{username}/stats";
        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task Curation(HttpContext ctx)
    {
        var duration = ctx.Request.RouteValues["duration"]?.ToString();
        await Upstream.Pipe(ApiClient.ApiRequest($"curation?duration={duration}", HttpMethod.Get), ctx);
    }

    public static async Task PromotedEntries(HttpContext ctx)
    {
        // const { limit = '200', short_content = '0' } = req.query;
        // Destructuring defaults only apply when the key is ABSENT — a present
        // empty string stays "" and parseInt("") is NaN, exactly like Node.
        var limitSv = ctx.Request.Query["limit"];
        var shortSv = ctx.Request.Query["short_content"];
        var limitStr = limitSv.Count == 0 ? "200" : limitSv.ToString();
        var shortStr = shortSv.Count == 0 ? "0" : shortSv.ToString();

        var limitNum = FeedsParseInt(limitStr);
        var shortNum = FeedsParseInt(shortStr);

        // GetPromotedEntries takes ints; JS forwards NaN / out-of-int-range
        // doubles verbatim to the upstream. Map NaN -> 0 and clamp (parity gap
        // only for degenerate inputs; see port notes).
        var limit = double.IsNaN(limitNum) ? 0 : (int)Math.Clamp(limitNum, int.MinValue, int.MaxValue);
        var shortContent = double.IsNaN(shortNum) ? 0 : (int)Math.Clamp(shortNum, int.MinValue, int.MaxValue);

        var posts = await ApiClient.GetPromotedEntries(limit, shortContent);
        await ctx.SendJson(200, posts);
    }

    public static async Task CommentHistory(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var author = FeedsBodyInterp(body, "author");
        var permlink = FeedsBodyInterp(body, "permlink");

        var u = $"comment-history/{author}/{permlink}";
        // onlyMeta === '1' — strict equality: only the string "1" qualifies.
        if (body.Str("onlyMeta") == "1")
        {
            u += "?only_meta=1";
        }

        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task WavesTags(HttpContext ctx)
    {
        var container = FeedsQueryInterp(ctx.Request.Query["container"]);
        var tag = FeedsQueryInterp(ctx.Request.Query["tag"]);
        var u = $"waves/tags?container={container}&tag={tag}";
        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task WavesAccount(HttpContext ctx)
    {
        var container = FeedsQueryInterp(ctx.Request.Query["container"]);
        var username = FeedsQueryInterp(ctx.Request.Query["username"]);
        var u = $"waves/account?container={container}&username={username}";
        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task WavesFollowing(HttpContext ctx)
    {
        var container = FeedsQueryInterp(ctx.Request.Query["container"]);
        var username = FeedsQueryInterp(ctx.Request.Query["username"]);
        var u = $"waves/following?container={container}&username={username}";
        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task WavesTrendingTags(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var parts = new List<string>();

        // container is optional: omit it for combined trending tags across all
        // containers (do not forward a literal "undefined").
        FeedsSetSingle(parts, "container", q["container"]);
        FeedsSetSingle(parts, "hours", q["hours"]);
        FeedsSetSingle(parts, "days", q["days"]);

        var u = $"waves/trending/tags?{string.Join("&", parts)}";
        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task WavesTrendingAuthors(HttpContext ctx)
    {
        var container = FeedsQueryInterp(ctx.Request.Query["container"]);
        var u = $"waves/trending/authors?container={container}";
        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task WavesFeed(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var parts = new List<string>();

        // These are single-value; ignore array input (duplicate params) so a
        // String([...]) never forwards a comma-joined value to the backend.
        FeedsSetSingle(parts, "limit", q["limit"]);
        FeedsSetSingle(parts, "cursor", q["cursor"]);
        FeedsSetSingle(parts, "tag", q["tag"]);
        FeedsSetSingle(parts, "following", q["following"]);
        FeedsSetSingle(parts, "author", q["author"]);
        FeedsSetSingle(parts, "observer", q["observer"]);

        // container is optional and repeatable (omit for the full combined feed).
        FeedsAppendRepeatable(parts, "container", q["container"]);

        var u = $"waves/feed?{string.Join("&", parts)}";
        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task WavesShorts(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var parts = new List<string>();

        // Single-value params; ignore array (duplicate) input as wavesFeed does.
        FeedsSetSingle(parts, "limit", q["limit"]);
        FeedsSetSingle(parts, "cursor", q["cursor"]);
        FeedsSetSingle(parts, "tag", q["tag"]);
        FeedsSetSingle(parts, "author", q["author"]);
        FeedsSetSingle(parts, "observer", q["observer"]);

        // container is optional and repeatable (omit for the full combined shorts feed).
        FeedsAppendRepeatable(parts, "container", q["container"]);

        var u = $"waves/shorts?{string.Join("&", parts)}";
        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task Points(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = FeedsBodyInterp(body, "username");
        await Upstream.Pipe(ApiClient.ApiRequest($"users/{username}", HttpMethod.Get), ctx);
    }

    public static async Task PointList(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = FeedsBodyInterp(body, "username");

        var u = $"users/{username}/points?size=50";
        if (JsJson.IsTruthy(body.Field("type")))
        {
            u += $"&type={FeedsBodyInterp(body, "type")}";
        }

        await Upstream.Pipe(ApiClient.ApiRequest(u, HttpMethod.Get), ctx);
    }

    public static async Task Quests(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = FeedsBodyInterp(body, "username");
        await Upstream.Pipe(ApiClient.ApiRequest($"users/{username}/quests", HttpMethod.Get), ctx);
    }

    // ---------------------------------------------------------------------
    // Helpers replicating Express/JS query and template-literal semantics.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Template-literal interpolation of a req.query value: absent key
    /// interpolates as the literal "undefined"; duplicate params (an array in
    /// qs) interpolate comma-joined, which StringValues.ToString() matches.
    /// </summary>
    private static string FeedsQueryInterp(StringValues sv) =>
        sv.Count == 0 ? "undefined" : sv.ToString();

    /// <summary>
    /// URLSearchParams `if (v &amp;&amp; !Array.isArray(v)) params.set(key, String(v))`:
    /// forward only a single, truthy (non-empty) value.
    /// </summary>
    private static void FeedsSetSingle(List<string> parts, string key, StringValues sv)
    {
        if (sv.Count == 1 && !string.IsNullOrEmpty(sv[0]))
        {
            parts.Add($"{FeedsFormUrlEncode(key)}={FeedsFormUrlEncode(sv[0]!)}");
        }
    }

    /// <summary>
    /// Repeatable param: an array appends every element (no truthiness filter,
    /// matching the TS forEach), a single truthy value appends once, absent or
    /// single-empty appends nothing.
    /// </summary>
    private static void FeedsAppendRepeatable(List<string> parts, string key, StringValues sv)
    {
        if (sv.Count > 1)
        {
            foreach (var v in sv)
            {
                parts.Add($"{FeedsFormUrlEncode(key)}={FeedsFormUrlEncode(v ?? "")}");
            }
        }
        else if (sv.Count == 1 && !string.IsNullOrEmpty(sv[0]))
        {
            parts.Add($"{FeedsFormUrlEncode(key)}={FeedsFormUrlEncode(sv[0]!)}");
        }
    }

    /// <summary>
    /// WHATWG application/x-www-form-urlencoded serializer, byte-exact with
    /// URLSearchParams.toString(): keep [A-Za-z0-9*-._], space -> '+',
    /// everything else percent-encoded (uppercase hex) over UTF-8 bytes.
    /// </summary>
    private static string FeedsFormUrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b == 0x20)
            {
                sb.Append('+');
            }
            else if ((b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z')
                     || b == '*' || b == '-' || b == '.' || b == '_')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Template-literal interpolation of a destructured req.body field:
    /// absent -> "undefined", null -> "null", JS String() conversion otherwise.
    /// </summary>
    private static string FeedsBodyInterp(JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var node) ? FeedsJsToString(node) : "undefined";

    private static string FeedsJsToString(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject:
                return "[object Object]";
            case JsonArray arr:
                // Array.prototype.toString: elements joined with ",",
                // null/undefined elements become "".
                return string.Join(",", arr.Select(e => e switch
                {
                    null => "",
                    JsonObject => "[object Object]",
                    _ => FeedsJsToString(e),
                }));
            case JsonValue v when JsVal.TryGetStringLenient(v, out var s):
                return s;
            case JsonValue v when v.TryGetValue<bool>(out var b):
                return b ? "true" : "false";
            default:
                // JSON numbers: JSON.stringify(n) equals String(n) for finite numbers.
                return JsJson.Stringify(node);
        }
    }

    /// <summary>
    /// JS parseInt(str) semantics (radix unspecified): skip leading whitespace,
    /// optional sign, optional 0x/0X hex prefix, take leading digits; NaN when
    /// no digits were consumed. Returns double so NaN is representable.
    /// </summary>
    private static double FeedsParseInt(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        var sign = 1.0;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            if (s[i] == '-')
            {
                sign = -1.0;
            }
            i++;
        }

        var radix = 10;
        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
        {
            radix = 16;
            i += 2;
        }

        var value = 0.0;
        var any = false;
        while (i < s.Length)
        {
            var c = s[i];
            int d;
            if (c >= '0' && c <= '9')
            {
                d = c - '0';
            }
            else if (radix == 16 && c >= 'a' && c <= 'f')
            {
                d = c - 'a' + 10;
            }
            else if (radix == 16 && c >= 'A' && c <= 'F')
            {
                d = c - 'A' + 10;
            }
            else
            {
                break;
            }

            any = true;
            value = value * radix + d;
            i++;
        }

        return any ? sign * value : double.NaN;
    }
}
