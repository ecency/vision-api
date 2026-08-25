using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/private-api.ts lines 1704-2033: activity tracking,
/// newsletter, market data, reports, reblogs/tips, chats/channels, wallets,
/// games, purchase orders, active proposal and AI endpoints.
/// </summary>
public static partial class PrivateApi
{
    // --- local helpers (Misc-prefixed to avoid collisions across partial-class chunks) ---

    /// <summary>JS template-literal semantics for a JsonNode value: strings raw,
    /// numbers via Number::toString, true/false, JSON null -> "null",
    /// arrays join with "," (null elements -> ""), objects -> "[object Object]".</summary>
    private static string MiscJsString(JsonNode? node)
    {
        if (node == null)
        {
            return "null";
        }
        if (node is JsonArray arr)
        {
            return string.Join(",", arr.Select(item => item == null ? "" : MiscJsString(item)));
        }
        if (node is JsonValue v)
        {
            if (JsVal.TryGetStringLenient(v, out var s))
            {
                return s;
            }
            if (v.TryGetValue<bool>(out var b))
            {
                return b ? "true" : "false";
            }
            // numbers: String(n) === JSON.stringify(n)
            return JsJson.Stringify(node);
        }
        return "[object Object]";
    }

    /// <summary>`${req.body.key}` — absent key (undefined) interpolates as "undefined".</summary>
    private static string MiscBodyInterp(JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) ? MiscJsString(v) : "undefined";

    /// <summary>`${req.params.name}` — missing route param (undefined) -> "undefined".</summary>
    private static string MiscRouteParam(HttpContext ctx, string name) =>
        ctx.Request.RouteValues[name]?.ToString() ?? "undefined";

    /// <summary>Destructure-and-rebuild payload semantics: a key is added only when it
    /// was present in the request body (absent == undefined == omitted by JSON.stringify;
    /// present-with-null stays null).</summary>
    private static void MiscCopyIfPresent(JsonObject data, JsonObject body, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (body.TryGetPropertyValue(key, out var v))
            {
                data[key] = v?.DeepClone();
            }
        }
    }

    // --- handlers ---

    public static async Task Activities(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var ty = body.Field("ty");
        // ty === 10 (strict: JSON number equal to 10)
        var tyIsTen = ty is JsonValue tyVal && tyVal.TryGetValue<double>(out var tyNum) && tyNum == 10;

        string? reservedAnchor = null;

        if (tyIsTen)
        {
            // Keyed on the account, not the caller's address: see CheckinGate for why
            // an address-keyed window makes accounts behind one address compete for a
            // single check-in slot, plus why the read, the decision and the write
            // have to be one step.
            var decision = CheckinGate.DecideAndReserve(
                username, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (!decision.Forward)
            {
                // A repeat inside the window: ack it and drop it. The anchor stays
                // where it is; moving it here would push it past this account's next
                // scheduled check-in, which would then be absorbed as well.
                await ctx.SendJson(201, new JsonObject());
                return;
            }

            reservedAnchor = decision.StampToStore;
        }

        var pipeJson = new JsonObject
        {
            ["us"] = username,
        };
        // { us, ty } object literal: ty key survives JSON.stringify only when present
        if (body.TryGetPropertyValue("ty", out var tyRaw))
        {
            pipeJson["ty"] = tyRaw?.DeepClone();
        }
        var bl = body.Field("bl");
        if (JsJson.IsTruthy(bl))
        {
            pipeJson["bl"] = bl!.DeepClone();
        }
        var tx = body.Field("tx");
        if (JsJson.IsTruthy(tx))
        {
            pipeJson["tx"] = tx!.DeepClone();
        }

        // The anchor is claimed before the call, which is what closes the burst
        // race. If the check-in then never reached the backend, give it back
        // rather than absorb this account's next attempt on the strength of one
        // that never landed.
        var upstreamStarted = false;
        try
        {
            // ApiRequest builds the auth headers eagerly and throws on a
            // misconfigured deployment, so the request can fail before Pipe is
            // ever entered. That is a check-in the backend never saw.
            var upstream = ApiClient.ApiRequest("usr-activity", HttpMethod.Post, null, pipeJson);
            upstreamStarted = true;
            await Upstream.Pipe(upstream, ctx);
        }
        finally
        {
            // Pipe maps a transport failure to 504/500, so a 5xx is the "never
            // reached the backend" set, as is a request that never started. An
            // upstream 4xx is a deliberate rejection that a retry would not
            // change, so it keeps the anchor. So does a backend answer that only
            // failed on the way back to a client that went away: the check-in
            // landed, and SendLikeExpress sets the upstream status before it
            // writes, so the status still reports that here. The release has to
            // sit in a finally, because Pipe can throw out of the write itself.
            if (reservedAnchor != null && (!upstreamStarted || ctx.Response.StatusCode >= 500))
            {
                CheckinGate.Release(username, reservedAnchor);
            }
        }
    }

    public static async Task SubscribeNewsletter(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var data = new JsonObject();
        MiscCopyIfPresent(data, body, "email");
        await Upstream.Pipe(ApiClient.ApiRequest("newsletter/subscribe", HttpMethod.Post, null, data), ctx);
    }

    public static async Task MarketData(HttpContext ctx)
    {
        var fiat = MiscRouteParam(ctx, "fiat");
        var token = MiscRouteParam(ctx, "token");
        // `?fixed=${req.query.fixed}` — absent query value interpolates as "undefined"
        var fixedValues = ctx.Request.Query["fixed"];
        var fixedStr = fixedValues.Count == 0 ? "undefined" : fixedValues.ToString();
        await Upstream.Pipe(
            ApiClient.ApiRequest($"market-data/currency-rate/{fiat}/{token}?fixed={fixedStr}", HttpMethod.Get),
            ctx);
    }

    public static async Task MarketDataLatest(HttpContext ctx)
    {
        var currency = ctx.Request.Query["currency"].ToString();
        var queryString = currency.Length > 0 ? $"?currency={currency}" : "";
        await Upstream.Pipe(ApiClient.ApiRequest($"market-data/latest{queryString}", HttpMethod.Get), ctx);
    }

    public static async Task Report(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var type = body.Field("type");
        var author = body.Field("author");
        var permlink = body.Field("permlink");
        var reporter = body.Field("reporter");
        var notes = body.Field("notes");

        if (!JsJson.IsTruthy(type) || !JsJson.IsTruthy(author))
        {
            await ctx.SendText(400, "Missing required fields: type, author");
            return;
        }

        // type !== 'post' && type !== 'account' (strict string equality)
        var typeStr = body.Str("type");
        if (typeStr != "post" && typeStr != "account")
        {
            await ctx.SendText(400, "Invalid report type. Must be 'post' or 'account'");
            return;
        }

        if (typeStr == "post" && !JsJson.IsTruthy(permlink))
        {
            await ctx.SendText(400, "Missing required field: permlink for post reports");
            return;
        }

        var data = new JsonObject
        {
            ["type"] = type!.DeepClone(),
            ["author"] = author!.DeepClone(),
        };
        if (typeStr == "post")
        {
            data["permlink"] = permlink!.DeepClone();
        }
        data["reporter"] = JsJson.IsTruthy(reporter) ? reporter!.DeepClone() : (JsonNode)"anonymous";
        if (JsJson.IsTruthy(notes))
        {
            data["notes"] = notes!.DeepClone();
        }

        await Upstream.Pipe(ApiClient.ApiRequest("report", HttpMethod.Post, null, data), ctx);
    }

    /// <summary>
    /// Account-deletion acknowledgment stub. Hive accounts cannot be deleted
    /// on-chain; this endpoint satisfies the app-store account-deletion
    /// requirement by acknowledging the request. (The Node route table pointed
    /// this at the report handler, whose validation rejected the mobile payload
    /// with 400 — this wires the stub the code intended.)
    /// </summary>
    public static async Task RequestDelete(HttpContext ctx)
    {
        await ctx.SendJson(200, new JsonObject
        {
            ["status"] = 200,
            ["body"] = new JsonObject { ["status"] = "ok" },
        });
    }

    /// <summary>
    /// A segment URL resolution reads as structure rather than as a name.
    ///
    /// These cannot be escaped away: `.` and `..` are unreserved, so
    /// EscapeDataString passes them through, and percent-encoding them by hand
    /// does not help either because Uri decodes `%2E` back to `.` before it
    /// removes dot segments. Rejecting is the only option that holds.
    /// </summary>
    private static bool IsDotSegment(string value) => value is "." or "..";

    /// <summary>
    /// Upstream path for post tips, or null when a value cannot be expressed as
    /// a single path segment.
    ///
    /// Author and permlink are escaped as path segments. Route values arrive
    /// percent-decoded and body values are arbitrary strings, so a value carrying
    /// `/`, `?` or `#` would otherwise be re-parsed as URL structure once this
    /// string becomes a Uri — addressing a different upstream resource, with this
    /// service's credentials attached.
    ///
    /// Authors and permlinks are made of unreserved characters, which
    /// EscapeDataString leaves byte-identical, so real traffic is unaffected.
    /// </summary>
    public static string? PostTipsPath(string author, string permlink) =>
        IsDotSegment(author) || IsDotSegment(permlink)
            ? null
            : $"post-tips/{Uri.EscapeDataString(author)}/{Uri.EscapeDataString(permlink)}";

    public static async Task Tips(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var author = MiscBodyInterp(body, "author");
        var permlink = MiscBodyInterp(body, "permlink");
        var path = PostTipsPath(author, permlink);
        if (path == null)
        {
            await ctx.SendText(400, "Invalid author or permlink");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest(path, HttpMethod.Get), ctx);
    }

    /// <summary>
    /// GET twin of <see cref="Tips"/>, same upstream call and same body.
    ///
    /// Tips are a plain read keyed only by author/permlink, but the original
    /// endpoint is a POST, and a POST response is uncacheable by definition — so
    /// every mount refetched it. This variant is addressable by URL and carries a
    /// Cache-Control, which lets a client reuse it. The POST stays for clients
    /// that have not moved over.
    /// </summary>
    public static async Task TipsGet(HttpContext ctx)
    {
        var author = MiscRouteParam(ctx, "author");
        var permlink = MiscRouteParam(ctx, "permlink");
        var path = PostTipsPath(author, permlink);
        if (path == null)
        {
            await ctx.SendText(400, "Invalid author or permlink");
            return;
        }
        ctx.CacheWhenOk(CachePolicy.PostTips);
        await Upstream.Pipe(ApiClient.ApiRequest(path, HttpMethod.Get), ctx);
    }

    public static async Task GameGet(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var gameType = MiscBodyInterp(body, "game_type");
        await Upstream.Pipe(ApiClient.ApiRequest($"game/{username}?type={gameType}", HttpMethod.Get), ctx);
    }

    public static async Task GamePost(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var gameType = MiscBodyInterp(body, "game_type");
        var data = new JsonObject();
        MiscCopyIfPresent(data, body, "key");
        await Upstream.Pipe(
            ApiClient.ApiRequest($"game/{username}?type={gameType}", HttpMethod.Post, null, data), ctx);
    }

    public static async Task PurchaseOrder(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        // user !== 'ecency' (strict) — anything but the exact string requires auth
        if (body.Str("user") != "ecency")
        {
            var username = await ValidateCode(body);
            if (username == null)
            {
                await ctx.SendText(401, "Unauthorized");
                return;
            }
        }

        var data = new JsonObject();
        MiscCopyIfPresent(data, body, "platform", "product", "receipt", "user", "meta");
        // External payment/receipt validation can be slow.
        await Upstream.Pipe(
            ApiClient.ApiRequest("purchase-order", HttpMethod.Post, null, data, null, 30000), ctx);
    }

    public static async Task Chats(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest($"chats/{username}", HttpMethod.Get), ctx);
    }

    public static async Task ChatsAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var data = new JsonObject
        {
            ["username"] = username,
        };
        MiscCopyIfPresent(data, body, "key", "pubkey", "iv", "meta");
        await Upstream.Pipe(ApiClient.ApiRequest("chats", HttpMethod.Post, null, data), ctx);
    }

    public static async Task ChatsUpdate(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = MiscBodyInterp(body, "id");
        var data = new JsonObject();
        MiscCopyIfPresent(data, body, "key", "pubkey", "iv", "meta");
        await Upstream.Pipe(ApiClient.ApiRequest($"chats/{username}/{id}", HttpMethod.Put, null, data), ctx);
    }

    public static async Task ChatsPub(HttpContext ctx)
    {
        var username = MiscRouteParam(ctx, "username");
        await Upstream.Pipe(ApiClient.ApiRequest($"chats/pub/{username}", HttpMethod.Get), ctx);
    }

    public static async Task ChannelAdd(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var creator = await ValidateCode(body);
        if (creator == null || creator != "ecency")
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var data = new JsonObject
        {
            ["creator"] = creator,
        };
        MiscCopyIfPresent(data, body, "username", "channel_id", "meta");
        await Upstream.Pipe(ApiClient.ApiRequest("channel", HttpMethod.Post, null, data), ctx);
    }

    public static async Task ChannelGet(HttpContext ctx)
    {
        var username = MiscRouteParam(ctx, "username");
        await Upstream.Pipe(ApiClient.ApiRequest($"channel/{username}", HttpMethod.Get), ctx);
    }

    public static async Task ChannelsGet(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var data = new JsonObject();
        MiscCopyIfPresent(data, body, "users");
        await Upstream.Pipe(ApiClient.ApiRequest("channels", HttpMethod.Post, null, data), ctx);
    }

    public static async Task ChatsGet(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var data = new JsonObject();
        MiscCopyIfPresent(data, body, "users");
        await Upstream.Pipe(ApiClient.ApiRequest("chats/pubs", HttpMethod.Post, null, data), ctx);
    }

    public static async Task BotsGet(HttpContext ctx)
    {
        await ctx.SendJson(200, Constants.BotsJson());
    }

    public static async Task Wallets(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest($"wallets/{username}", HttpMethod.Get), ctx);
    }

    public static async Task WalletsAdd(HttpContext ctx)
    {
        // No auth in the Node handler — forwarded as-is.
        var body = await ctx.ReadBody();
        var data = new JsonObject();
        MiscCopyIfPresent(data, body, "username", "token", "address", "meta", "status");
        await Upstream.Pipe(ApiClient.ApiRequest("wallet", HttpMethod.Post, null, data), ctx);
    }

    public static async Task WalletsUpdate(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = MiscBodyInterp(body, "id");
        var data = new JsonObject
        {
            ["username"] = username,
        };
        MiscCopyIfPresent(data, body, "token", "address", "meta");
        await Upstream.Pipe(ApiClient.ApiRequest($"wallets/{username}/{id}", HttpMethod.Put, null, data), ctx);
    }

    public static async Task WalletsDelete(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var id = MiscBodyInterp(body, "id");
        await Upstream.Pipe(ApiClient.ApiRequest($"wallets/{username}/{id}", HttpMethod.Delete), ctx);
    }

    public static async Task WalletsExist(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var address = MiscBodyInterp(body, "address");
        var token = MiscBodyInterp(body, "token");
        await Upstream.Pipe(
            ApiClient.ApiRequest($"signup/exist-wallet-accounts?address={address}&token={token}", HttpMethod.Get),
            ctx);
    }

    public static async Task WalletsChkUser(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = MiscBodyInterp(body, "username");
        await Upstream.Pipe(
            ApiClient.ApiRequest($"signup/exist-wallet-user?username={username}", HttpMethod.Get), ctx);
    }

    public static async Task ProposalActive(HttpContext ctx)
    {
        // res.send(ACTIVE_PROPOSAL_META) — the constant { id: 336 } object.
        await ctx.SendJson(200, new JsonObject
        {
            ["id"] = Constants.ActiveProposalId,
        });
    }

    public static async Task AiGeneratePrice(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest("ai-image-price", HttpMethod.Get), ctx);
    }

    public static async Task AiGenerateImage(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var data = new JsonObject
        {
            ["us"] = username,
        };
        // idempotency_key lets a retry recover the same paid generation instead of
        // charging a second one; the upstream validates its format itself.
        MiscCopyIfPresent(data, body, "prompt", "aspect_ratio", "power", "idempotency_key");
        // AI image generation legitimately takes 10-60s+; keep it long.
        await Upstream.Pipe(
            ApiClient.ApiRequest("ai-image-generate", HttpMethod.Post, null, data, null, 120000), ctx);
    }

    /// <summary>
    /// Per-user AI image generation history (the upstream's last 20 successful
    /// generations). The username comes ONLY from the validated code: prompts are
    /// private to their author, so a body-supplied username would let any caller
    /// read someone else's generation history.
    /// </summary>
    public static async Task AiImagesHistory(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(
            ApiClient.ApiRequest($"users/{username}/ai-images", HttpMethod.Get), ctx);
    }

    public static async Task AiAssistPrice(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(ApiClient.ApiRequest($"ai-assist-price?us={username}", HttpMethod.Get), ctx);
    }

    public static async Task AiAssist(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        var data = new JsonObject
        {
            ["us"] = username,
        };
        MiscCopyIfPresent(data, body, "action", "text");
        // AI assist generation can take a long time; keep it long.
        await Upstream.Pipe(ApiClient.ApiRequest("ai-assist", HttpMethod.Post, null, data, null, 120000), ctx);
    }

    public static async Task AiTranscribePrice(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var username = await ValidateCode(body);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }
        await Upstream.Pipe(
            ApiClient.ApiRequest($"ai-transcribe-price?us={username}", HttpMethod.Get), ctx);
    }

    /// <summary>
    /// Dictation. Unlike every other private-api route this one carries a file, so the
    /// request is multipart/form-data rather than JSON and the auth code arrives as a
    /// form field instead of a JSON property.
    ///
    /// `us` is taken from the validated code and never from the client, matching
    /// AiAssist: the upstream bills whoever `us` names, so accepting it from the body
    /// would let a caller spend someone else's Points.
    /// </summary>
    public static async Task AiTranscribe(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType)
        {
            await ctx.SendText(400, "Expected multipart/form-data");
            return;
        }

        IFormCollection form;
        try
        {
            form = await ctx.Request.ReadFormAsync();
        }
        catch (Exception e)
        {
            // Malformed multipart, or a body past Kestrel's limit.
            Console.Error.WriteLine($"aiTranscribe(): unreadable form: {e.Message}");
            await ctx.SendText(400, "Bad Request");
            return;
        }

        // ValidateCode takes the JSON body shape, so lift the form field into it and
        // reuse the one implementation rather than growing a second auth path.
        var codeBody = new JsonObject { ["code"] = form["code"].ToString() };
        var username = await ValidateCode(codeBody);
        if (username == null)
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        var audio = form.Files.GetFile("audio");
        if (audio == null)
        {
            await ctx.SendText(400, "Missing audio");
            return;
        }

        await using var audioStream = audio.OpenReadStream();
        using var content = BuildTranscribeContent(
            username,
            form["duration_ms"].ToString(),
            form["idempotency_key"].ToString(),
            audioStream,
            audio.FileName,
            audio.ContentType);

        // Transcription is a vendor round trip on top of the upload; keep it long,
        // matching ai-assist and ai-image-generate.
        await Upstream.Pipe(ApiClient.ApiMultipartRequest("ai-transcribe", content, 120000), ctx);
    }

    /// <summary>
    /// Builds the upstream multipart body. Split out from the handler so the part it
    /// gets wrong-once-and-badly is testable: `us` must be the caller resolved from the
    /// signed code, never a value the client supplied, because upstream bills whoever
    /// `us` names.
    /// </summary>
    public static MultipartFormDataContent BuildTranscribeContent(
        string username,
        string durationMs,
        string? idempotencyKey,
        Stream audio,
        string? fileName,
        string? contentType)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(username), "us");
        content.Add(new StringContent(durationMs), "duration_ms");

        // Omit rather than send empty: upstream treats an empty key as absent anyway,
        // and sending "" would fail its [A-Za-z0-9_-]{8,64} validator with a 400.
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            content.Add(new StringContent(idempotencyKey), "idempotency_key");
        }

        var fileContent = new StreamContent(audio);
        // TryParse, not Parse. This value is client-controlled, and Parse throws
        // FormatException on malformed input -- which escapes the handler's
        // form-reading catch and surfaces as a 500 from the global middleware. A
        // label we cannot parse is not worth failing an otherwise valid upload over,
        // so it is dropped exactly like an absent one; upstream identifies the audio
        // from its contents regardless.
        if (!string.IsNullOrEmpty(contentType) &&
            System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsedType))
        {
            fileContent.Headers.ContentType = parsedType;
        }
        content.Add(fileContent, "audio", string.IsNullOrEmpty(fileName) ? "audio" : fileName);

        return content;
    }
}
