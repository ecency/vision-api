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
            if (v.TryGetValue<string>(out var s))
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

        if (tyIsTen)
        {
            // req.headers['x-real-ip'] || req.connection.remoteAddress || req.headers['x-forwarded-for'] || ''
            var vip = ctx.Request.Headers["x-real-ip"].ToString();
            if (vip.Length == 0)
            {
                vip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            }
            if (vip.Length == 0)
            {
                vip = ctx.Request.Headers["x-forwarded-for"].ToString();
            }
            var identifier = vip;

            string? rec = null;
            try
            {
                rec = MemCache.Get<string>(identifier);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Console.Error.WriteLine("Cache get failed.");
            }

            if (!string.IsNullOrEmpty(rec))
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (double.TryParse(rec, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var recMs)
                    && nowMs - recMs < 900000)
                {
                    // Node sends 201 here WITHOUT returning; the cache write and the
                    // upstream usr-activity call below still run (pipe then skips the
                    // second response) — replicated as-is.
                    await ctx.SendJson(201, new JsonObject());
                }
                try
                {
                    MemCache.Set(identifier,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), 901);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    Console.Error.WriteLine("Cache set failed.");
                }
            }
            else
            {
                try
                {
                    MemCache.Set(identifier,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), 901);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    Console.Error.WriteLine("Cache set failed.");
                }
            }
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

        await Upstream.Pipe(ApiClient.ApiRequest("usr-activity", HttpMethod.Post, null, pipeJson), ctx);
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

    public static async Task Tips(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var author = MiscBodyInterp(body, "author");
        var permlink = MiscBodyInterp(body, "permlink");
        await Upstream.Pipe(ApiClient.ApiRequest($"post-tips/{author}/{permlink}", HttpMethod.Get), ctx);
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
        MiscCopyIfPresent(data, body, "prompt", "aspect_ratio", "power");
        // AI image generation legitimately takes 10-60s+; keep it long.
        await Upstream.Pipe(
            ApiClient.ApiRequest("ai-image-generate", HttpMethod.Post, null, data, null, 120000), ctx);
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
}
