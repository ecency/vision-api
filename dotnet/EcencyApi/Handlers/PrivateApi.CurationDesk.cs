using System.Globalization;
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
/// Six public reads and eight signed writes. This service does three things
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
///    upstream error). A body served from that memo says how old it is and
///    offers shared caches only the rest of its window, so the two layers do
///    not each hold it for a full lifetime in series;
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

    // The one-line handlers here and under "signed writes" return the delegate's
    // Task instead of awaiting it: they do nothing after the call, so an async
    // state machine per request would be pure overhead and Routes.cs only needs
    // a Task back. Handlers that do work of their own stay `async`.

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
        // The unconfigured answer comes first, before this route looks at
        // anything the caller sent. A dark desk answers 503 on every route the
        // same way; answering 400 here instead would make this one route report
        // on its own path grammar while the other five report nothing.
        if (DeskToken == null)
        {
            await ctx.SendText(503, DeskNotConfigured);
            return;
        }

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

    // GET /private-api/curation-desk/recommender/{username}
    public static async Task CurationDeskRecommender(HttpContext ctx)
    {
        // Same order as the post route: while the desk is dark every route
        // answers 503, before this one looks at the name it was given.
        if (DeskToken == null)
        {
            await ctx.SendText(503, DeskNotConfigured);
            return;
        }

        var username = ctx.Request.RouteValues["username"]?.ToString() ?? "";
        var path = CurationDeskRecommenderPath(username);
        if (path == null)
        {
            await ctx.SendText(400, "Invalid username");
            return;
        }
        await ServeDeskRead(ctx, path, CachePolicy.CurationDeskRecommender);
    }

    // \A and \z, not ^ and $: in .NET `$` also matches before a trailing
    // newline, so "good-karma\n" would pass a `$`-anchored name check and
    // travel into the upstream path.
    private static readonly Regex DeskAuthorPattern = new(@"\A[a-z0-9.-]{3,16}\z", RegexOptions.Compiled);
    private static readonly Regex DeskPermlinkPattern = new(@"\A[a-z0-9-]{1,255}\z", RegexOptions.Compiled);

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

    /// <summary>
    /// Upstream path for one recommender's public scorecard, or null when the
    /// value is not a plain Hive name. The name travels in the path, so it goes
    /// through the same fence as <see cref="CurationDeskPostPath"/>: the name
    /// grammar first, escaping as a second fence, then the dot-segment check for
    /// the one case escaping cannot fix. The route takes no query parameters, so
    /// every spelling of the question about one name is a single memo entry and a
    /// single shared-cache key.
    /// </summary>
    public static string? CurationDeskRecommenderPath(string username) =>
        username is "." or ".." || !DeskAuthorPattern.IsMatch(username)
            ? null
            : $"curation/desk/recommenders/{Uri.EscapeDataString(username)}";

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
    /// normalized endpoint.
    ///
    /// Nothing is written to the client while the per-key gate is held. The gate
    /// exists to collapse concurrent fills of one key onto one upstream call, so
    /// a fill only computes the public bytes and stores them; what to send is
    /// kept in locals, the gate is released in the finally, and the response is
    /// written after it. Writing under the gate would make every reader of a key
    /// wait for the slowest reader's socket to drain rather than for the upstream
    /// call, which is the one thing the gate is meant to share.
    /// </summary>
    private static async Task ServeDeskRead(HttpContext ctx, string endpoint, string policy)
    {
        var token = DeskToken;
        if (token == null)
        {
            await ctx.SendText(503, DeskNotConfigured);
            return;
        }

        if (CurationDeskMemo.TryGetFresh(endpoint, out var hit, out var hitType, out var hitAge))
        {
            await SendPublicJson(ctx, policy, hitType, hit, hitAge);
            return;
        }

        // Filled under the gate, sent after it: bytes to serve, an error to
        // answer with, or the upstream response to pass through.
        byte[]? bytes = null;
        string? bytesType = null;
        // How long this service has held those bytes; a fill made here is new.
        var bytesAge = 0;
        var errorStatus = 0;
        string? errorText = null;
        UpstreamResponse? passthrough = null;

        // Held for the whole block, so the gate cannot be dropped and replaced
        // between being handed out and being waited on.
        var gate = CurationDeskMemo.GateFor(endpoint);
        var entered = false;
        var gateTimedOut = false;

        try
        {
            entered = await gate.Semaphore.WaitAsync(CurationDeskMemo.FillWait);
            if (!entered)
            {
                // Someone else's fill is taking longer than a whole upstream
                // timeout. Do not stack another one behind it; answer from what
                // is known, once the gate has been handed back.
                gateTimedOut = true;
            }
            // The fill that held the gate may have landed while this one queued.
            else if (CurationDeskMemo.TryGetFresh(endpoint, out var fresh, out var freshType, out var freshAge))
            {
                bytes = fresh;
                bytesType = freshType;
                bytesAge = freshAge;
            }
            else
            {
                UpstreamResponse? r = null;
                try
                {
                    r = await DeskUpstream(endpoint, HttpMethod.Get, DeskHeaders(token), null);
                }
                catch (UpstreamTimeoutException)
                {
                    (errorStatus, errorText) = (504, "Upstream Timeout");
                }
                catch (Exception)
                {
                    (errorStatus, errorText) = (500, "Server Error");
                }

                if (r != null && r.Status == 200 && r.Json is JsonObject or JsonArray)
                {
                    bytes = CurationDeskPublicPayload.ToPublicBytes(r);
                    bytesType = JsonContentType;
                    CurationDeskMemo.Store(endpoint, bytes, JsonContentType, CachePolicy.SharedMaxAge(policy));
                }
                else
                {
                    passthrough = r;
                }
            }
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }
            CurationDeskMemo.ReleaseGate(endpoint, gate);
        }

        if (gateTimedOut)
        {
            await ServeLastGoodOr(ctx, endpoint, policy, 504, "Upstream Timeout");
            return;
        }

        if (bytes != null)
        {
            await SendPublicJson(ctx, policy, bytesType!, bytes, bytesAge);
            return;
        }

        if (errorText != null)
        {
            await ServeLastGoodOr(ctx, endpoint, policy, errorStatus, errorText);
            return;
        }

        var response = passthrough!;

        // A 5xx, or a 200 whose body is not a JSON object or array (an error
        // page, a redirect body, a bare string): either way the backend is not
        // answering the question this route asks, so a body it did answer with
        // is better than passing that on, and neither is worth memoizing.
        if ((response.Status >= 500 || response.Status == 200)
            && CurationDeskMemo.TryGetLastGood(endpoint, out var stale, out var staleType, out var staleAge))
        {
            await SendStaleJson(ctx, policy, staleType, stale, staleAge);
            return;
        }

        // 4xx (an unknown post, a rejected token), or nothing to fall back on:
        // pass through the way Pipe would, unmemoized and with no Cache-Control
        // of ours, so the client sees what the backend said. An error body is
        // still a public body this service emits, so it goes through the same
        // fence as a served one.
        CurationDeskPublicPayload.Strip(response.Json);
        await Upstream.SendLikeExpress(ctx, response.Status, response.Json, response.RawText);
    }

    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>
    /// Send a JSON body this service holds (a fresh fill, or a memo hit that is
    /// already <paramref name="ageSeconds"/> old) with the route's cache policy.
    /// Only these bodies are publicly cacheable: an upstream passthrough carries
    /// whatever the backend answered, which may be an error page or a body meant
    /// for one caller, so it never gets a Cache-Control of ours.
    ///
    /// A hit goes out with the rest of its window rather than a new one, so the
    /// memo and the caches downstream expire the same body at the same moment
    /// instead of holding it for one lifetime each in series. The remaining
    /// lifetime is the only freshness signal sent: an Age header on top of an
    /// already shortened s-maxage would be subtracted a second time by a cache
    /// that honours both, leaving the body stale on arrival.
    /// </summary>
    private static async Task SendPublicJson(HttpContext ctx, string policy, string contentType, byte[] bytes, int ageSeconds)
    {
        ctx.CacheWhenOk(CachePolicy.Aged(policy, ageSeconds));
        await WriteBytes(ctx, 200, contentType, bytes);
    }

    /// <summary>
    /// Send a last-good body: a real answer from the backend, but one kept
    /// because the call that should have replaced it failed. It carries a short
    /// window instead of the route's own, so an upstream that comes back is
    /// picked up within a poll or two rather than at the end of a full one.
    /// </summary>
    private static async Task SendStaleJson(HttpContext ctx, string policy, string contentType, byte[] bytes, int ageSeconds)
    {
        // the body's real age is not advertised: the short window alone is the
        // freshness, and an Age older than it would make the answer stale at once
        _ = ageSeconds;
        ctx.CacheWhenOk(CachePolicy.Stale(policy));
        await WriteBytes(ctx, 200, contentType, bytes);
    }

    private static async Task ServeLastGoodOr(HttpContext ctx, string endpoint, string policy, int status, string text)
    {
        if (CurationDeskMemo.TryGetLastGood(endpoint, out var stale, out var staleType, out var staleAge))
        {
            await SendStaleJson(ctx, policy, staleType, stale, staleAge);
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
        // Before anything else: a dark desk answers 503 whoever is asking, so
        // validating first would spend one chain lookup per request on a route
        // that cannot do any work. The answer reveals nothing a reader of the
        // public routes cannot see, which answer 503 unauthenticated too.
        var token = DeskToken;
        if (token == null)
        {
            await ctx.SendText(503, DeskNotConfigured);
            return;
        }

        var body = await ctx.ReadBody();
        var username = await RequireAuthedUsernameCached(ctx, body);
        if (username == null)
        {
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

    // Anchored with \A and \z (a `$` would also match before a trailing
    // newline) and written with explicit digit classes: .NET's `\d` matches
    // every Unicode decimal digit, so `hive-\d{5,6}` accepts Arabic-Indic or
    // Devanagari digits that name no community here.
    private static readonly Regex CursorPattern = new(@"\A[A-Za-z0-9_.:-]{1,80}\z", RegexOptions.Compiled);
    private static readonly Regex CommunityPattern = new(@"\Ahive-[0-9]{5,6}\z", RegexOptions.Compiled);

    /// <summary>Random-order seed: one per browser session, roster feed only.</summary>
    private static readonly Regex SeedPattern = new(@"\A[a-z0-9]{8,16}\z", RegexOptions.Compiled);

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

    /// <summary>Integer within [min, max], clamped; null when absent or not a number.</summary>
    private static int? ClampInt(Dictionary<string, string> q, string key, int min, int max) =>
        q.TryGetValue(key, out var raw) ? ClampValue(raw, min, max) : null;

    /// <summary>
    /// <see cref="ClampInt"/> for a value that did not come from a query string:
    /// the roster feed reads the same names out of a JSON body and must clamp
    /// them the same way, or the two feeds answer different questions.
    ///
    /// Parsed as a double rather than an int so that a value outside the int
    /// range still clamps into the range: `limit=99999999999` asks for as many
    /// rows as there are, and 50 is the right answer to that, while dropping it
    /// would silently serve the default page size instead. Only text that is not
    /// a plain signed integer is refused, so `1e6`, `12.5` and `abc` are dropped
    /// and fall back to the default the same way they did before.
    /// </summary>
    public static int? ClampValue(string? raw, int min, int max)
    {
        if (raw == null
            || !double.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value))
        {
            return null;
        }
        return (int)Math.Clamp(value, min, max);
    }

    /// <summary>Opaque paging cursor grammar; shared with the roster feed body.</summary>
    public static bool IsCursor(string? value) => value != null && CursorPattern.IsMatch(value);

    /// <summary>A `hive-NNNNN` community name; shared with the roster feed body.</summary>
    public static bool IsCommunity(string? value) => value != null && CommunityPattern.IsMatch(value);

    /// <summary>A random-order seed; the roster feed is the only route that takes one.</summary>
    public static bool IsSeed(string? value) => value != null && SeedPattern.IsMatch(value);

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

    /// <summary>
    /// Views the roster feed takes: the public ones plus `excluded`, which is
    /// the only place an excluded row is ever listed.
    /// </summary>
    public static readonly IReadOnlySet<string> RosterViews =
        new HashSet<string>(CurationDeskQuery.Views, StringComparer.Ordinal) { "excluded" };

    /// <summary>How many post ids one tick may name per list.</summary>
    public const int MaxTickIds = 100;

    // \A and \z for the same reason as the name patterns above: `$` would let
    // a trailing newline through.
    private static readonly Regex TrxIdPattern = new(@"\A[0-9a-f]{40}\z", RegexOptions.Compiled);

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
            NormalizeRosterFeed(payload, body);
        }

        if (ReferenceEquals(route, Tick))
        {
            // The backend caps both lists at this many ids; truncating here
            // keeps a client bug from turning one tick into thousands of
            // primary-key probes on the way to the same 400.
            Truncate(payload, "need", MaxTickIds);
            Truncate(payload, "visible", MaxTickIds);
        }

        if (ReferenceEquals(route, RecommendMeta) && payload.ContainsKey("ua_class")
            && !(body.Str("ua_class") is { } ua && UaClasses.Contains(ua)))
        {
            payload.Remove("ua_class");
        }

        return (payload, null);
    }

    /// <summary>
    /// The roster feed carries the filter names of the public feed, so it gets
    /// the public feed's value rules: an out-of-range number is clamped rather
    /// than forwarded, and a value outside an allowlist or a pattern is dropped
    /// so the backend applies its default. Both feeds then answer the same
    /// question for the same request, and a typo cannot ask for a query plan
    /// nobody sized.
    /// </summary>
    private static void NormalizeRosterFeed(JsonObject payload, JsonObject body)
    {
        // An unknown sort is not an error for a read: drop it and let the
        // backend apply its default, as the public feed does.
        var sort = body.Str("sort");
        if (sort == null || !RosterSorts.Contains(sort))
        {
            payload.Remove("sort");
        }

        // seed only means something to the random order; for any other sort it
        // is noise that would make two identical feeds look different, and a
        // seed outside the grammar is not a seed the backend can hash with.
        if (sort != "random" || !CurationDeskQuery.IsSeed(body.Str("seed")))
        {
            payload.Remove("seed");
        }

        KeepAllowed(payload, "view", RosterViews);
        KeepAllowed(payload, "app", CurationDeskQuery.Apps);
        KeepAllowed(payload, "window", CurationDeskQuery.Windows);
        KeepMatching(payload, "cursor", CurationDeskQuery.IsCursor);
        KeepMatching(payload, "community", CurationDeskQuery.IsCommunity);

        Clamp(payload, "limit", 1, CurationDeskQuery.MaxLimit);
        Clamp(payload, "rep_min", 0, 100);
        Clamp(payload, "rep_max", 0, 100);
        Clamp(payload, "min_words", 0, CurationDeskQuery.MaxWords);
        Clamp(payload, "max_words", 0, CurationDeskQuery.MaxWords);
    }

    /// <summary>Drop a field whose value is not one of <paramref name="allowed"/>.</summary>
    private static void KeepAllowed(JsonObject payload, string key, IReadOnlySet<string> allowed)
    {
        if (payload.ContainsKey(key) && !(JsVal.AsString(payload[key]) is { } value && allowed.Contains(value)))
        {
            payload.Remove(key);
        }
    }

    /// <summary>Drop a field whose value does not match <paramref name="matches"/>.</summary>
    private static void KeepMatching(JsonObject payload, string key, Func<string?, bool> matches)
    {
        if (payload.ContainsKey(key) && !matches(JsVal.AsString(payload[key])))
        {
            payload.Remove(key);
        }
    }

    /// <summary>
    /// Clamp a whole-number field into [min, max], accepting the number or its
    /// string spelling; a value that is neither is dropped rather than forwarded.
    ///
    /// These names count rows, reputations and words, so only an integral value
    /// is one of them: `1.9` is not "1", it is a client sending something else,
    /// and truncating it would forward a filter nobody asked for. Strings go
    /// through the query-string parser, so a body and a query string accept the
    /// same spellings. A number is judged by its value, not its spelling, since
    /// JSON parsing keeps no spelling: `1e6` and `1000000` are the same number
    /// and clamp to the same bound, exactly as `1000000` does in a query string.
    /// </summary>
    private static void Clamp(JsonObject payload, string key, int min, int max)
    {
        if (!payload.ContainsKey(key))
        {
            return;
        }
        var node = payload[key];
        int? value = JsVal.AsNumber(node) is { } number
            ? (double.IsInteger(number) ? (int?)Math.Clamp(number, min, max) : null)
            : CurationDeskQuery.ClampValue(JsVal.AsString(node), min, max);
        if (value is { } clamped)
        {
            payload[key] = clamped;
        }
        else
        {
            payload.Remove(key);
        }
    }

    /// <summary>Keep at most <paramref name="max"/> elements of an array field.</summary>
    private static void Truncate(JsonObject payload, string key, int max)
    {
        if (payload[key] is not JsonArray array || array.Count <= max)
        {
            return;
        }
        var kept = new JsonArray();
        for (var i = 0; i < max; i++)
        {
            kept.Add(array[i]?.DeepClone());
        }
        payload[key] = kept;
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
    /// <summary>Budget of each store (DESK_MEMO_BYTES; 64 MiB by default).</summary>
    internal static readonly long BudgetBytes = Config.DeskMemoBytes;

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

    /// <summary>Wall clock in milliseconds behind the fill times below.</summary>
    internal static readonly Func<long> SystemClock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// The clock the fill times are read from. Replaceable so a test can age an
    /// entry rather than wait for it; production never moves it.
    /// </summary>
    internal static Func<long> NowMs = SystemClock;

    /// <summary>
    /// One fill of one key at a time. A reader takes the key's gate, waits on
    /// its semaphore, fills and hands the gate back.
    ///
    /// Users are counted rather than inferred from the semaphore: a reader that
    /// has been handed the gate but has not reached its wait yet is a user, and
    /// dropping the entry under it (the semaphore looks free, because that
    /// reader has not taken it) would let the next reader create a replacement
    /// and fill the same key beside it. Two fills of one key can then finish out
    /// of order and store the older answer last.
    /// </summary>
    internal sealed class Gate
    {
        /// <summary>Admission to the fill; one holder at a time.</summary>
        internal readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>Readers holding this gate. Guarded by <see cref="GateLock"/>.</summary>
        internal int Users;
    }

    private static readonly Dictionary<string, Gate> Gates = new(StringComparer.Ordinal);
    private static readonly object GateLock = new();

    /// <summary>
    /// A stored entry carries when it was filled as well as what it is, so a hit
    /// can say how much of its shared window is left. Both travel in the one tag
    /// the byte cache keeps beside the bytes, so they are evicted together and
    /// no side table can outlive or contradict an entry.
    /// </summary>
    private const char TagSeparator = '|';

    private const string DefaultContentType = "application/json; charset=utf-8";

    public static bool TryGetFresh(string key, out byte[] bytes, out string contentType, out int ageSeconds) =>
        Read(Fresh, key, out bytes, out contentType, out ageSeconds);

    public static bool TryGetLastGood(string key, out byte[] bytes, out string contentType, out int ageSeconds) =>
        Read(LastGood, key, out bytes, out contentType, out ageSeconds);

    public static void Store(string key, byte[] bytes, string contentType, int ttlSeconds)
    {
        var tag = NowMs().ToString(CultureInfo.InvariantCulture) + TagSeparator + contentType;
        Fresh.Set(key, bytes, ttlSeconds * 1000, tag);
        LastGood.Set(key, bytes, LastGoodTtlMs, tag);
    }

    private static bool Read(BytesCache cache, string key, out byte[] bytes, out string contentType, out int ageSeconds)
    {
        var hit = cache.TryGet(key, out bytes, out var tag);
        var split = tag?.IndexOf(TagSeparator) ?? -1;
        contentType = split >= 0 ? tag![(split + 1)..] : tag ?? DefaultContentType;
        ageSeconds = split > 0 && long.TryParse(tag.AsSpan(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out var filledAtMs)
            ? (int)Math.Clamp((NowMs() - filledAtMs) / 1000, 0, int.MaxValue)
            : 0;
        return hit;
    }

    /// <summary>
    /// The gate of a key, counting this caller as one of its users. Every call
    /// must be paired with a <see cref="ReleaseGate"/>, whether or not the
    /// caller went on to take the semaphore.
    /// </summary>
    internal static Gate GateFor(string key)
    {
        lock (GateLock)
        {
            if (!Gates.TryGetValue(key, out var gate))
            {
                gate = new Gate();
                Gates[key] = gate;
            }
            gate.Users++;
            return gate;
        }
    }

    /// <summary>
    /// Give a gate back. The entry is dropped only when the last user leaves and
    /// the key still maps to this same gate, so a scan over many distinct keys
    /// leaves no semaphore per key behind while no in-flight reader ever loses
    /// the gate it is about to wait on.
    /// </summary>
    internal static void ReleaseGate(string key, Gate gate)
    {
        lock (GateLock)
        {
            if (--gate.Users > 0)
            {
                return;
            }
            if (Gates.TryGetValue(key, out var current) && ReferenceEquals(current, gate))
            {
                Gates.Remove(key);
            }
        }
    }

    /// <summary>Gates currently held, for the tests that pin the cleanup.</summary>
    internal static int GateCount
    {
        get { lock (GateLock) { return Gates.Count; } }
    }

    internal static void ResetForTests()
    {
        Fresh = new BytesCache(BudgetBytes);
        LastGood = new BytesCache(BudgetBytes);
        NowMs = SystemClock;
        lock (GateLock)
        {
            Gates.Clear();
        }
    }
}
