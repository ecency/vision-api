using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Curation desk gateway: /private-api/curation-desk/* -> curation/desk/* upstream.
///
/// Five public reads and eight signed writes. This service does three things
/// for the desk that the generic pipe handlers do not:
///
///  - every upstream call carries a shared secret header, reads included. The
///    desk backend answers nothing without it, so the memo, the cache policies
///    and the rate limits in front of this service cannot be skipped by calling
///    the backend directly. When the secret is not configured the routes fail
///    closed (503), the same way the payment routes do;
///  - public reads are whitelisted, clamped and emitted in one fixed order, so
///    every spelling of the same question collapses onto one memo entry and one
///    shared-cache key, and the answer is memoized as bytes for exactly the
///    s-maxage the response promises (single-flight per key, last-good on an
///    upstream error);
///  - writes resolve the caller from the signed code (memoized briefly, see
///    <see cref="RequireAuthedUsernameCached"/>) and forward only whitelisted
///    body fields under that username; a client-supplied username or code never
///    reaches the backend.
/// </summary>
public static partial class PrivateApi
{
    // ---- configuration seams -------------------------------------------------

    /// <summary>Header carrying the shared secret to the desk backend.</summary>
    internal const string DeskTokenHeader = "X-Desk-Internal-Token";

    /// <summary>
    /// The configured secret, or null when the desk is switched off. A static
    /// field rather than a Config read so tests can flip it; production reads it
    /// once at startup like every other setting.
    /// </summary>
    internal static string? DeskToken = string.IsNullOrWhiteSpace(Config.DeskInternalToken)
        ? null
        : Config.DeskInternalToken.Trim();

    /// <summary>
    /// The one upstream call every desk route goes through. Replaceable so tests
    /// can observe the request (path, method, headers, payload) without a network.
    /// </summary>
    internal static Func<string, HttpMethod, IEnumerable<KeyValuePair<string, string>>, JsonNode?, Task<UpstreamResponse>>
        DeskUpstream = (endpoint, method, headers, payload) => ApiClient.ApiRequest(endpoint, method, headers, payload);

    /// <summary>Signed-code validation, replaceable for tests (no chain RPC).</summary>
    internal static Func<JsonObject, Task<string?>> DeskValidateCode = ValidateCode;

    /// <summary>
    /// How long a successful code validation is remembered. A validation costs
    /// one uncached account lookup per call, and a curator on the desk sends a
    /// write every few seconds; 90 s keeps that to one lookup per curator per
    /// window while a posting-key rotation lags by at most this much. Failures
    /// are never remembered.
    /// </summary>
    internal static double DeskAuthMemoSeconds = 90;

    private const string DeskAuthMemoPrefix = "desk-auth:";

    private const string DeskNotConfigured = "curation desk not configured";

    // ---- public reads --------------------------------------------------------

    // GET /private-api/curation-desk/feed
    public static Task CurationDeskFeed(HttpContext ctx) =>
        ServeDeskRead(ctx,
            CurationDeskQuery.Endpoint("curation/desk/feed", CurationDeskQuery.NormalizeFeed(RawQuery(ctx))),
            CachePolicy.CurationDeskFeed);

    // GET /private-api/curation-desk/status
    public static Task CurationDeskStatus(HttpContext ctx) =>
        ServeDeskRead(ctx, "curation/desk/status", CachePolicy.CurationDeskStatus);

    // GET /private-api/curation-desk/roster
    public static Task CurationDeskRoster(HttpContext ctx) =>
        ServeDeskRead(ctx, "curation/desk/roster", CachePolicy.CurationDeskRoster);

    // GET /private-api/curation-desk/recommendations
    public static Task CurationDeskRecommendations(HttpContext ctx) =>
        ServeDeskRead(ctx,
            CurationDeskQuery.Endpoint("curation/desk/recommendations",
                CurationDeskQuery.NormalizeRecommendations(RawQuery(ctx))),
            CachePolicy.CurationDeskRecommendations);

    // GET /private-api/curation-desk/post/{author}/{permlink}
    public static async Task CurationDeskPost(HttpContext ctx)
    {
        var author = ctx.Request.RouteValues["author"]?.ToString() ?? "";
        var permlink = ctx.Request.RouteValues["permlink"]?.ToString() ?? "";
        var path = CurationDeskPostPath(author, permlink);
        if (path == null)
        {
            await ctx.SendText(400, "Invalid author or permlink");
            return;
        }
        await ServeDeskRead(ctx, path, CachePolicy.CurationDeskPost);
    }

    private static readonly Regex DeskAuthorPattern = new("^[a-z0-9.-]{3,16}$", RegexOptions.Compiled);
    private static readonly Regex DeskPermlinkPattern = new("^[a-z0-9-]{1,255}$", RegexOptions.Compiled);

    /// <summary>
    /// Upstream path for a single post, or null when either value is not a plain
    /// Hive name or permlink. Same reasoning as <see cref="PostTipsPath"/>: route
    /// values arrive percent-decoded, and a `/`, `?` or `#` left in place would be
    /// re-parsed as URL structure once the string becomes a Uri, with the desk
    /// secret attached to wherever it then points. The character classes already
    /// exclude every structural character; the escaping stays as a second fence
    /// and the dot-segment check as the one case escaping cannot fix.
    /// </summary>
    public static string? CurationDeskPostPath(string author, string permlink)
    {
        if (author is "." or ".." || permlink is "." or "..")
        {
            return null;
        }
        if (!DeskAuthorPattern.IsMatch(author) || !DeskPermlinkPattern.IsMatch(permlink))
        {
            return null;
        }
        return $"curation/desk/post/{Uri.EscapeDataString(author)}/{Uri.EscapeDataString(permlink)}";
    }

    private static IEnumerable<KeyValuePair<string, string>> RawQuery(HttpContext ctx)
    {
        foreach (var kv in ctx.Request.Query)
        {
            // A repeated key takes its first value, like Express `req.query` read
            // as a scalar; the normalizer sees each key once.
            var first = kv.Value.Count > 0 ? kv.Value[0] : null;
            if (first != null)
            {
                yield return new KeyValuePair<string, string>(kv.Key, first);
            }
        }
    }

    /// <summary>
    /// Serve one public desk read: memo hit, or a single-flight fill of the
    /// normalized endpoint. Cache-Control is attached for a 200 only.
    /// </summary>
    private static async Task ServeDeskRead(HttpContext ctx, string endpoint, string policy)
    {
        var token = DeskToken;
        if (token == null)
        {
            await ctx.SendText(503, DeskNotConfigured);
            return;
        }

        ctx.CacheWhenOk(policy);

        if (CurationDeskMemo.TryGetFresh(endpoint, out var hit, out var hitType))
        {
            await WriteBytes(ctx, 200, hitType, hit);
            return;
        }

        var gate = CurationDeskMemo.GateFor(endpoint);
        if (!await gate.WaitAsync(CurationDeskMemo.FillWait))
        {
            // Someone else's fill is taking longer than a whole upstream timeout.
            // Do not stack another one behind it; answer from what is known.
            await ServeLastGoodOr(ctx, endpoint, 504, "Upstream Timeout");
            return;
        }

        try
        {
            // The fill that held the gate may have landed while this one queued.
            if (CurationDeskMemo.TryGetFresh(endpoint, out hit, out hitType))
            {
                await WriteBytes(ctx, 200, hitType, hit);
                return;
            }

            UpstreamResponse r;
            try
            {
                r = await DeskUpstream(endpoint, HttpMethod.Get, DeskHeaders(token), null);
            }
            catch (UpstreamTimeoutException)
            {
                await ServeLastGoodOr(ctx, endpoint, 504, "Upstream Timeout");
                return;
            }
            catch (Exception)
            {
                await ServeLastGoodOr(ctx, endpoint, 500, "Server Error");
                return;
            }

            if (r.Status == 200 && r.Json is JsonObject or JsonArray)
            {
                var bytes = CurationDeskPublicPayload.ToPublicBytes(r);
                CurationDeskMemo.Store(endpoint, bytes, JsonContentType, CachePolicy.SharedMaxAge(policy));
                await WriteBytes(ctx, 200, JsonContentType, bytes);
                return;
            }

            if (r.Status >= 500)
            {
                // The backend is unwell; a body it last answered with is better
                // than its error page, and the error is not worth memoizing.
                if (CurationDeskMemo.TryGetLastGood(endpoint, out var stale, out var staleType))
                {
                    await WriteBytes(ctx, 200, staleType, stale);
                    return;
                }
            }

            // 4xx (an unknown post, a rejected token), a 200 that is not JSON, or
            // a 5xx with nothing to fall back on: pass through unmemoized, the
            // way Pipe would, so the client sees what the backend said.
            await Upstream.SendLikeExpress(ctx, r.Status, r.Json, r.RawText);
        }
        finally
        {
            gate.Release();
            CurationDeskMemo.ReleaseGate(endpoint, gate);
        }
    }

    private const string JsonContentType = "application/json; charset=utf-8";

    private static async Task ServeLastGoodOr(HttpContext ctx, string endpoint, int status, string text)
    {
        if (CurationDeskMemo.TryGetLastGood(endpoint, out var stale, out var staleType))
        {
            await WriteBytes(ctx, 200, staleType, stale);
            return;
        }
        await ctx.SendText(status, text);
    }

    private static async Task WriteBytes(HttpContext ctx, int status, string contentType, byte[] bytes)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        await ctx.Response.Body.WriteAsync(bytes);
    }

    private static List<KeyValuePair<string, string>> DeskHeaders(string token, string? clientIp = null)
    {
        var headers = new List<KeyValuePair<string, string>> { new(DeskTokenHeader, token) };
        if (clientIp != null)
        {
            headers.Add(new KeyValuePair<string, string>("X-Real-IP-V", clientIp));
        }
        return headers;
    }

    // ---- signed writes -------------------------------------------------------

    // POST /private-api/curation-desk/roster-feed
    public static Task CurationDeskRosterFeed(HttpContext ctx) =>
        ServeDeskWrite(ctx, CurationDeskWrites.RosterFeed);

    // POST /private-api/curation-desk/tick
    public static Task CurationDeskTick(HttpContext ctx) =>
        ServeDeskWrite(ctx, CurationDeskWrites.Tick);

    // POST /private-api/curation-desk/mark
    public static Task CurationDeskMark(HttpContext ctx) =>
        ServeDeskWrite(ctx, CurationDeskWrites.Mark);

    // POST /private-api/curation-desk/mark-clear
    public static Task CurationDeskMarkClear(HttpContext ctx) =>
        ServeDeskWrite(ctx, CurationDeskWrites.MarkClear);

    // POST /private-api/curation-desk/marks
    public static Task CurationDeskMarks(HttpContext ctx) =>
        ServeDeskWrite(ctx, CurationDeskWrites.Marks);

    // POST /private-api/curation-desk/cursor
    public static Task CurationDeskCursor(HttpContext ctx) =>
        ServeDeskWrite(ctx, CurationDeskWrites.Cursor);

    // POST /private-api/curation-desk/recommend-meta
    public static Task CurationDeskRecommendMeta(HttpContext ctx) =>
        ServeDeskWrite(ctx, CurationDeskWrites.RecommendMeta);

    // POST /private-api/curation-desk/recommendation-dismiss
    public static Task CurationDeskRecommendationDismiss(HttpContext ctx) =>
        ServeDeskWrite(ctx, CurationDeskWrites.RecommendationDismiss);

    /// <summary>
    /// Signed write: authenticate, fail closed when unconfigured, build the
    /// whitelisted payload under the validated username, pipe. Never cacheable.
    /// </summary>
    private static async Task ServeDeskWrite(HttpContext ctx, CurationDeskWrites.Route route)
    {
        var body = await ctx.ReadBody();
        var username = await RequireAuthedUsernameCached(ctx, body);
        if (username == null)
        {
            return;
        }

        var token = DeskToken;
        if (token == null)
        {
            await ctx.SendText(503, DeskNotConfigured);
            return;
        }

        var (payload, error) = CurationDeskWrites.Build(route, username, body);
        if (payload == null)
        {
            await ctx.SendText(400, error ?? "Invalid request");
            return;
        }

        // The client address rides along only where the backend uses it (the
        // recommendation meta ping). Same source as the signup path: the
        // proxy-set header, never a forwarded-for chain a client can extend.
        var headers = DeskHeaders(token, route.ForwardClientAddress ? SignupClientIp(ctx) : null);

        ctx.Response.Headers.CacheControl = "no-store";
        await Upstream.Pipe(DeskUpstream(route.UpstreamPath, HttpMethod.Post, headers, payload), ctx);
    }

    /// <summary>
    /// <see cref="RequireAuthedUsername"/> with a short memo of successful
    /// validations, keyed by the SHA-256 of the code. The validation itself is
    /// unchanged and still decides every miss; only its positive answer is
    /// remembered, for <see cref="DeskAuthMemoSeconds"/>. A failed validation is
    /// never stored, so a probe costs the same as before and a code that stops
    /// validating is refused on its next miss. Desk routes only.
    /// </summary>
    public static async Task<string?> RequireAuthedUsernameCached(HttpContext ctx, JsonObject body)
    {
        var code = body.Str("code");
        var memoKey = string.IsNullOrEmpty(code) ? null : DeskAuthMemoPrefix + Sha256Hex(code);

        if (memoKey != null && MemCache.Get<string>(memoKey) is { Length: > 0 } remembered)
        {
            return remembered;
        }

        var username = await DeskValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return null;
        }

        if (memoKey != null)
        {
            MemCache.Set(memoKey, username, DeskAuthMemoSeconds);
        }
        return username;
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Whitelist, clamp and order the query of the public desk reads.
///
/// Every accepted parameter has a fixed position and a default that is dropped,
/// so `?limit=25&amp;sort=newest&amp;x=1` and an empty query are the same
/// upstream URL, the same memo entry and the same shared-cache key. Unknown
/// names and unusable values are dropped, never errors: a public read should
/// answer the nearest sensible question rather than 400 on a typo.
/// </summary>
public static class CurationDeskQuery
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 50;
    public const int MaxWords = 50000;

    private static readonly Regex CursorPattern = new("^[A-Za-z0-9_.:-]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex CommunityPattern = new(@"^hive-\d{5,6}$", RegexOptions.Compiled);

    public static readonly IReadOnlySet<string> FeedSorts = new HashSet<string> { "queue", "newest", "unique" };
    public static readonly IReadOnlySet<string> RecommendationSorts = new HashSet<string> { "unique", "newest" };
    public static readonly IReadOnlySet<string> Views =
        new HashSet<string> { "queue", "latest", "new-authors", "recommended", "curated", "all" };
    public static readonly IReadOnlySet<string> Apps = new HashSet<string> { "all", "ecency", "peakd", "other" };
    public static readonly IReadOnlySet<string> Windows = new HashSet<string> { "full", "half", "eighth", "locked", "all" };

    /// <summary>
    /// Emission order of the feed parameters. Fixed so that the memo key and
    /// the shared-cache key are stable regardless of how a client orders them.
    /// </summary>
    public static readonly string[] FeedOrder =
    {
        "cursor", "limit", "sort", "view", "app", "community", "window", "rep_min", "rep_max",
        "min_words", "max_words", "has_images", "new_authors", "recommended", "hide_curated",
    };

    /// <summary>Route 1 (feed).</summary>
    public static List<KeyValuePair<string, string?>> NormalizeFeed(IEnumerable<KeyValuePair<string, string>> raw)
    {
        var q = First(raw);
        var kept = new Dictionary<string, string>();

        if (q.TryGetValue("cursor", out var cursor) && CursorPattern.IsMatch(cursor))
        {
            kept["cursor"] = cursor;
        }

        var limit = ClampInt(q, "limit", 1, MaxLimit);
        if (limit is { } l && l != DefaultLimit)
        {
            kept["limit"] = l.ToString();
        }

        // The public default is newest; an unknown sort (random and anything
        // else the roster feed may accept) falls back to it and so disappears.
        var sort = q.TryGetValue("sort", out var s) && FeedSorts.Contains(s) ? s : "newest";
        if (sort != "newest")
        {
            kept["sort"] = sort;
        }

        if (q.TryGetValue("view", out var view) && Views.Contains(view))
        {
            kept["view"] = view;
        }

        if (q.TryGetValue("app", out var app) && Apps.Contains(app) && app != "all")
        {
            kept["app"] = app;
        }

        if (q.TryGetValue("community", out var community) && CommunityPattern.IsMatch(community))
        {
            kept["community"] = community;
        }

        if (q.TryGetValue("window", out var window) && Windows.Contains(window) && window != "all")
        {
            kept["window"] = window;
        }

        // Range floors at their minimum and ceilings at their maximum select
        // everything, so they are the same question as leaving them out.
        if (ClampInt(q, "rep_min", 0, 100) is { } repMin && repMin != 0)
        {
            kept["rep_min"] = repMin.ToString();
        }
        if (ClampInt(q, "rep_max", 0, 100) is { } repMax && repMax != 100)
        {
            kept["rep_max"] = repMax.ToString();
        }
        if (ClampInt(q, "min_words", 0, MaxWords) is { } minWords && minWords != 0)
        {
            kept["min_words"] = minWords.ToString();
        }
        if (ClampInt(q, "max_words", 0, MaxWords) is { } maxWords && maxWords != MaxWords)
        {
            kept["max_words"] = maxWords.ToString();
        }

        if (Flag(q, "has_images") == true) kept["has_images"] = "1";
        if (Flag(q, "new_authors") == true) kept["new_authors"] = "1";

        // sort=unique already means "recommended posts only", so the explicit
        // flag adds nothing there and would only split the memo.
        if (Flag(q, "recommended") == true && sort != "unique") kept["recommended"] = "1";

        // hide_curated defaults to on; only switching it off says anything.
        if (Flag(q, "hide_curated") == false) kept["hide_curated"] = "0";

        return Ordered(kept, FeedOrder);
    }

    /// <summary>Route 4 (recommendations): cursor, limit, sort in unique|newest.</summary>
    public static List<KeyValuePair<string, string?>> NormalizeRecommendations(IEnumerable<KeyValuePair<string, string>> raw)
    {
        var q = First(raw);
        var kept = new Dictionary<string, string>();

        if (q.TryGetValue("cursor", out var cursor) && CursorPattern.IsMatch(cursor))
        {
            kept["cursor"] = cursor;
        }

        var limit = ClampInt(q, "limit", 1, MaxLimit);
        if (limit is { } l && l != DefaultLimit)
        {
            kept["limit"] = l.ToString();
        }

        if (q.TryGetValue("sort", out var sort) && RecommendationSorts.Contains(sort))
        {
            kept["sort"] = sort;
        }

        return Ordered(kept, new[] { "cursor", "limit", "sort" });
    }

    /// <summary>The upstream endpoint with the normalized query appended.</summary>
    public static string Endpoint(string path, IEnumerable<KeyValuePair<string, string?>> query) =>
        Upstream.AppendQuery(path, query);

    private static Dictionary<string, string> First(IEnumerable<KeyValuePair<string, string>> raw)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            q.TryAdd(key, value);
        }
        return q;
    }

    private static List<KeyValuePair<string, string?>> Ordered(Dictionary<string, string> kept, string[] order)
    {
        var list = new List<KeyValuePair<string, string?>>(kept.Count);
        foreach (var name in order)
        {
            if (kept.TryGetValue(name, out var value))
            {
                list.Add(new KeyValuePair<string, string?>(name, value));
            }
        }
        return list;
    }

    /// <summary>Integer within [min, max], clamped; null when absent or not an integer.</summary>
    private static int? ClampInt(Dictionary<string, string> q, string key, int min, int max)
    {
        if (!q.TryGetValue(key, out var raw)) return null;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }
        return Math.Clamp(value, min, max);
    }

    /// <summary>"1" -> true, "0" -> false, anything else -> null (dropped).</summary>
    private static bool? Flag(Dictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var raw) ? raw switch { "1" => true, "0" => false, _ => null } : null;
}

/// <summary>
/// Payload rules for the signed desk writes: which body keys reach the backend
/// and the few values this service refuses outright rather than forward.
/// </summary>
public static class CurationDeskWrites
{
    public sealed record Route(string UpstreamPath, string[] Keys, bool ForwardClientAddress = false);

    public static readonly IReadOnlySet<string> MarkStates =
        new HashSet<string> { "reviewed", "snoozed", "flagged", "noted" };
    public static readonly IReadOnlySet<string> CursorActions = new HashSet<string> { "advance", "rewind" };
    public static readonly IReadOnlySet<string> DismissActions = new HashSet<string> { "dismiss", "restore" };
    public static readonly IReadOnlySet<string> UaClasses = new HashSet<string> { "web", "mobile" };
    public static readonly IReadOnlySet<string> RosterSorts = new HashSet<string> { "queue", "newest", "unique", "random" };

    private static readonly Regex TrxIdPattern = new("^[0-9a-f]{40}$", RegexOptions.Compiled);

    public static readonly Route RosterFeed = new("curation/desk/roster-feed", new[]
    {
        "cursor", "limit", "view", "app", "community", "min_words", "sort", "seed", "window", "rep_min",
        "rep_max", "max_words", "has_images", "new_authors", "recommended", "flagged", "hide_curated",
        "hide_reviewed", "hide_snoozed",
    });

    public static readonly Route Tick = new("curation/desk/tick", new[] { "since", "need", "visible" });

    public static readonly Route Mark = new("curation/desk/marks",
        new[] { "author", "permlink", "state", "reason", "note", "snooze_until" });

    public static readonly Route MarkClear = new("curation/desk/marks/clear", new[] { "author", "permlink" });

    public static readonly Route Marks = new("curation/desk/marks/list", new[] { "state", "cursor", "limit" });

    public static readonly Route Cursor = new("curation/desk/cursors", new[] { "post_id", "action", "reason" });

    public static readonly Route RecommendMeta = new("curation/desk/recommendations/meta",
        new[] { "author", "permlink", "trx_id", "ua_class" }, ForwardClientAddress: true);

    public static readonly Route RecommendationDismiss = new("curation/desk/recommendations/dismiss",
        new[] { "author", "permlink", "action" });

    /// <summary>
    /// The upstream body: the validated username plus the route's whitelisted
    /// keys copied as the client sent them. `username` and `code` are never in
    /// a whitelist, so a forged identity cannot ride along. Returns no payload
    /// and a reason when a value the backend would only reject is caught here.
    /// </summary>
    public static (JsonObject? Payload, string? Error) Build(Route route, string username, JsonObject body)
    {
        var error = Validate(route, body);
        if (error != null)
        {
            return (null, error);
        }

        var payload = new JsonObject { ["username"] = username };
        foreach (var key in route.Keys)
        {
            if (key is "username" or "code")
            {
                continue;
            }
            CopyIfPresent(payload, body, key);
        }

        if (ReferenceEquals(route, RosterFeed))
        {
            // An unknown sort is not an error for a read: drop it and let the
            // backend apply its default, as the public feed does.
            var sort = body.Str("sort");
            if (sort == null || !RosterSorts.Contains(sort))
            {
                payload.Remove("sort");
            }
            // seed only means something to the random order; for any other sort
            // it is noise that would make two identical feeds look different.
            if (sort != "random")
            {
                payload.Remove("seed");
            }
            if (payload.Remove("limit") && body.Field("limit") is JsonValue limitValue
                && limitValue.TryGetValue<double>(out var limit))
            {
                payload["limit"] = Math.Clamp((int)limit, 1, CurationDeskQuery.MaxLimit);
            }
        }

        if (ReferenceEquals(route, RecommendMeta) && payload.ContainsKey("ua_class")
            && !(body.Str("ua_class") is { } ua && UaClasses.Contains(ua)))
        {
            payload.Remove("ua_class");
        }

        return (payload, null);
    }

    private static string? Validate(Route route, JsonObject body)
    {
        if (ReferenceEquals(route, Mark))
        {
            return RequireAuthorPermlink(body) ?? RequireOneOf(body, "state", MarkStates);
        }
        if (ReferenceEquals(route, MarkClear))
        {
            return RequireAuthorPermlink(body);
        }
        if (ReferenceEquals(route, Marks))
        {
            return body.ContainsKey("state") ? RequireOneOf(body, "state", MarkStates) : null;
        }
        if (ReferenceEquals(route, Cursor))
        {
            if (body.Field("post_id") is not JsonValue idValue
                || idValue.GetValueKind() is not (JsonValueKind.Number or JsonValueKind.String))
            {
                return "post_id required";
            }
            return RequireOneOf(body, "action", CursorActions);
        }
        if (ReferenceEquals(route, RecommendMeta))
        {
            var missing = RequireAuthorPermlink(body);
            if (missing != null) return missing;
            if (body.ContainsKey("trx_id"))
            {
                // Optional and informational, but a value that is not a
                // transaction id is a client bug worth surfacing, not storing.
                if (body.Str("trx_id") is not { } trx || !TrxIdPattern.IsMatch(trx))
                {
                    return "invalid trx_id";
                }
            }
            return null;
        }
        if (ReferenceEquals(route, RecommendationDismiss))
        {
            return RequireAuthorPermlink(body) ?? RequireOneOf(body, "action", DismissActions);
        }
        return null;
    }

    /// <summary>
    /// Copy a body field only when the key is present (absent == undefined, which
    /// JSON.stringify omits; a present null is kept), as the other passthroughs do.
    /// </summary>
    private static void CopyIfPresent(JsonObject target, JsonObject body, string key)
    {
        if (body.TryGetPropertyValue(key, out var value))
        {
            target[key] = value?.DeepClone();
        }
    }

    private static string? RequireAuthorPermlink(JsonObject body) =>
        RequireNonEmpty(body, "author") ?? RequireNonEmpty(body, "permlink");

    private static string? RequireNonEmpty(JsonObject body, string key) =>
        body.Str(key) is { Length: > 0 } ? null : $"{key} required";

    private static string? RequireOneOf(JsonObject body, string key, IReadOnlySet<string> allowed) =>
        body.Str(key) is { } value && allowed.Contains(value) ? null : $"invalid {key}";
}

/// <summary>
/// What a public desk response may carry. The backend is specified to omit
/// these already; this is the fence on this side of the boundary, so a backend
/// change that starts leaking a curator's identity or a hashed address into a
/// publicly cached body is stopped here rather than served for its s-maxage.
/// </summary>
public static class CurationDeskPublicPayload
{
    public static readonly IReadOnlySet<string> PrivateKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "set_by", "set_at", "active_curators", "trail_alerts", "note", "excluded_reason", "ip_hash", "key_id",
    };

    /// <summary>
    /// Remove every private key anywhere in the tree. Returns whether anything
    /// was removed, so an untouched body can be served as the bytes it came in.
    /// </summary>
    public static bool Strip(JsonNode? node)
    {
        var removed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToArray())
                {
                    if (PrivateKeys.Contains(key))
                    {
                        obj.Remove(key);
                        removed = true;
                    }
                    else
                    {
                        removed |= Strip(obj[key]);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    removed |= Strip(item);
                }
                break;
        }
        return removed;
    }

    /// <summary>
    /// The body to memoize and serve: the upstream bytes as received when they
    /// were already clean, otherwise the stripped tree re-serialized once.
    /// </summary>
    public static byte[] ToPublicBytes(UpstreamResponse r)
    {
        if (!Strip(r.Json))
        {
            return r.Bytes.Length > 0 ? r.Bytes : Encoding.UTF8.GetBytes(JsJson.Stringify(r.Json));
        }
        return Encoding.UTF8.GetBytes(JsJson.Stringify(r.Json));
    }
}

/// <summary>
/// Byte memo for the public desk reads, keyed by the normalized upstream
/// endpoint. Two bounded stores: the fresh one holds a body for the s-maxage of
/// its route, the last-good one holds the most recent 200 for longer so an
/// upstream error answers with something recent rather than an error page.
/// Bytes, not trees: a hit is a lookup and a write, whatever the read rate.
/// </summary>
public static class CurationDeskMemo
{
    /// <summary>Budget of each store. A feed page is tens of KB; this is thousands of them.</summary>
    internal const long BudgetBytes = 64L * 1024 * 1024;

    /// <summary>
    /// How long a last-good body stays eligible as a fallback. Long enough to
    /// ride out a backend restart, short enough that a stale feed does not
    /// outlive an outage by much.
    /// </summary>
    internal const int LastGoodTtlMs = 10 * 60 * 1000;

    /// <summary>
    /// How long a request waits for another request's fill of the same key. A
    /// fill is one upstream call, so this only bounds the case where that call
    /// is itself timing out; the waiter then answers from last-good or 504.
    /// </summary>
    internal static readonly TimeSpan FillWait = TimeSpan.FromMilliseconds(Upstream.DefaultTimeoutMs + 1000);

    internal static BytesCache Fresh = new(BudgetBytes);
    internal static BytesCache LastGood = new(BudgetBytes);

    /// <summary>
    /// One fill per key at a time. Gates are created on demand and dropped once
    /// released with nobody holding them, so a scan over many distinct keys does
    /// not leave a semaphore per key behind. A request that read a gate just
    /// before it was dropped can start a second fill; that costs one duplicate
    /// upstream call, not correctness.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public static bool TryGetFresh(string key, out byte[] bytes, out string contentType)
    {
        var hit = Fresh.TryGet(key, out bytes, out var tag);
        contentType = tag ?? "application/json; charset=utf-8";
        return hit;
    }

    public static bool TryGetLastGood(string key, out byte[] bytes, out string contentType)
    {
        var hit = LastGood.TryGet(key, out bytes, out var tag);
        contentType = tag ?? "application/json; charset=utf-8";
        return hit;
    }

    public static void Store(string key, byte[] bytes, string contentType, int ttlSeconds)
    {
        Fresh.Set(key, bytes, ttlSeconds * 1000, contentType);
        LastGood.Set(key, bytes, LastGoodTtlMs, contentType);
    }

    internal static SemaphoreSlim GateFor(string key) => Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    internal static void ReleaseGate(string key, SemaphoreSlim gate)
    {
        if (gate.CurrentCount == 1)
        {
            Gates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate));
        }
    }

    internal static void ResetForTests()
    {
        Fresh = new BytesCache(BudgetBytes);
        LastGood = new BytesCache(BudgetBytes);
        Gates.Clear();
    }
}
