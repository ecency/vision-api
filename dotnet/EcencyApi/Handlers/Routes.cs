using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// 1:1 port of the route table in src/server/index.tsx. Every route maps to the
/// same path and method as the Express original; handlers are static methods on
/// the SearchApi / AuthApi / WalletApi / PrivateApi partial classes.
///
/// Routing parity verified against the live Node image:
///  - declared routes are EXACT matches even without a trailing '$' (path-to-regexp
///    0.1.7 fully anchors them);
///  - :param matches a single path segment;
///  - unmatched GET/HEAD -> 200 + the template page;
///  - unmatched other methods -> 404 + Express finalhandler "Cannot METHOD /path".
/// </summary>
public static class Routes
{
    public static void Map(WebApplication app)
    {
        // ---- Search Api ----
        app.MapPost("/search-api/search", SearchApi.Search);
        app.MapPost("/search-api/search-follower", SearchApi.SearchFollower);
        app.MapPost("/search-api/search-following", SearchApi.SearchFollowing);
        app.MapPost("/search-api/search-account", SearchApi.SearchAccount);
        app.MapPost("/search-api/search-tag", SearchApi.SearchTag);
        app.MapPost("/search-api/search-path", SearchApi.SearchPath);
        app.MapPost("/search-api/similar", SearchApi.SearchSimilar);

        // ---- Auth Api ----
        app.MapPost("/auth-api/hs-token-refresh", AuthApi.HsTokenRefresh);
        app.MapPost("/auth-api/hs-token-create", AuthApi.HsTokenCreate);

        // ---- Wallet Api ----
        app.MapPost("/wallet-api/portfolio", WalletApi.Portfolio);
        app.MapPost("/wallet-api/portfolio-v2", WalletApi.PortfolioV2);
        app.MapPost("/private-api/engine-api", WalletApi.Eapi);
        app.MapGet("/private-api/engine-reward-api/{username}", WalletApi.Erewardapi);
        app.MapGet("/private-api/engine-chart-api", WalletApi.Echartapi);
        app.MapGet("/private-api/engine-account-history", WalletApi.EngineAccountHistory);

        // ---- Private Api (GET) ----
        app.MapGet("/private-api/public/bots", PrivateApi.BotsGet);
        app.MapGet("/private-api/received-vesting/{username}", PrivateApi.ReceivedVesting);
        app.MapGet("/private-api/received-rc/{username}", PrivateApi.ReceivedRC);
        app.MapGet("/private-api/rewarded-communities", PrivateApi.RewardedCommunities);
        app.MapGet("/private-api/pro-members", PrivateApi.ProMembers);
        app.MapGet("/private-api/balance/{chain}/{address}", PrivateApi.Balance);
        app.MapPost("/private-api/rpc/{chain}", PrivateApi.ChainRpc);
        app.MapGet("/private-api/leaderboard/{duration:regex(^(day|week|month)$)}", PrivateApi.Leaderboard);
        app.MapGet("/private-api/curation/{duration:regex(^(day|week|month)$)}", PrivateApi.Curation);
        app.MapGet("/private-api/promoted-entries", PrivateApi.PromotedEntries);
        // market-data/latest (1 segment) and market-data/{fiat}/{token} (2 segments)
        // never collide by arity; register both like the Node table.
        app.MapGet("/private-api/market-data/{fiat}/{token}", PrivateApi.MarketData);
        app.MapGet("/private-api/market-data/latest", PrivateApi.MarketDataLatest);
        app.MapGet("/private-api/referrals/{username}", PrivateApi.Referrals);
        app.MapGet("/private-api/referrals/{username}/stats", PrivateApi.ReferralsStats);
        app.MapGet("/private-api/announcements", PrivateApi.GetAnnouncement);
        app.MapGet("/private-api/spotlights", PrivateApi.GetSpotlight);
        app.MapGet("/private-api/chats-pub/{username}", PrivateApi.ChatsPub);
        app.MapGet("/private-api/channel/{username}", PrivateApi.ChannelGet);
        app.MapGet("/private-api/proposal/active", PrivateApi.ProposalActive);
        app.MapGet("/private-api/pub-notifications/{username}", PrivateApi.PublicUnreadNotifications);
        app.MapGet("/private-api/waves/feed", PrivateApi.WavesFeed);
        app.MapGet("/private-api/waves/shorts", PrivateApi.WavesShorts);
        app.MapGet("/private-api/waves/tags", PrivateApi.WavesTags);
        app.MapGet("/private-api/waves/account", PrivateApi.WavesAccount);
        app.MapGet("/private-api/waves/following", PrivateApi.WavesFollowing);
        app.MapGet("/private-api/waves/trending/tags", PrivateApi.WavesTrendingTags);
        app.MapGet("/private-api/waves/trending/authors", PrivateApi.WavesTrendingAuthors);

        // ---- Private Api (POST) ----
        app.MapPost("/private-api/comment-history", PrivateApi.CommentHistory);
        app.MapPost("/private-api/points", PrivateApi.Points);
        app.MapPost("/private-api/point-list", PrivateApi.PointList);
        app.MapPost("/private-api/quests", PrivateApi.Quests);
        app.MapPost("/private-api/streak-freeze", PrivateApi.StreakFreeze);
        app.MapPost("/private-api/streak-freeze/buy", PrivateApi.StreakFreezeBuy);
        app.MapPost("/private-api/account-create", PrivateApi.CreateAccount);
        app.MapPost("/private-api/account-create-friend", PrivateApi.CreateAccountFriend);
        app.MapPost("/private-api/subscribe", PrivateApi.SubscribeNewsletter);
        app.MapPost("/private-api/notifications", PrivateApi.Notifications);
        app.MapPost("/private-api/report", PrivateApi.Report);
        app.MapPost("/private-api/request-delete", PrivateApi.Report);
        app.MapPost("/private-api/post-reblogs", PrivateApi.Reblogs);
        app.MapPost("/private-api/post-reblog-count", PrivateApi.ReblogCount);
        app.MapPost("/private-api/post-tips", PrivateApi.Tips);
        app.MapPost("/private-api/chats-get", PrivateApi.ChatsGet);
        app.MapPost("/private-api/channels-get", PrivateApi.ChannelsGet);
        app.MapPost("/private-api/wallets-add", PrivateApi.WalletsAdd);
        app.MapPost("/private-api/wallets-chkuser", PrivateApi.WalletsChkUser);
        app.MapPost("/private-api/wallets", PrivateApi.Wallets);
        app.MapPost("/private-api/wallets-update", PrivateApi.WalletsUpdate);
        app.MapPost("/private-api/wallets-delete", PrivateApi.WalletsDelete);
        app.MapPost("/private-api/wallets-exist", PrivateApi.WalletsExist);
        app.MapPost("/private-api/notifications/unread", PrivateApi.UnreadNotifications);
        app.MapPost("/private-api/notifications/mark", PrivateApi.MarkNotifications);
        app.MapPost("/private-api/register-device", PrivateApi.RegisterDevice);
        app.MapPost("/private-api/detail-device", PrivateApi.DetailDevice);
        app.MapPost("/private-api/images", PrivateApi.Images);
        app.MapPost("/private-api/images-delete", PrivateApi.ImagesDelete);
        app.MapPost("/private-api/images-add", PrivateApi.ImagesAdd);
        app.MapPost("/private-api/drafts", PrivateApi.Drafts);
        app.MapPost("/private-api/drafts-add", PrivateApi.DraftsAdd);
        app.MapPost("/private-api/drafts-update", PrivateApi.DraftsUpdate);
        app.MapPost("/private-api/drafts-delete", PrivateApi.DraftsDelete);
        app.MapPost("/private-api/bookmarks", PrivateApi.Bookmarks);
        app.MapPost("/private-api/bookmarks-add", PrivateApi.BookmarksAdd);
        app.MapPost("/private-api/bookmarks-delete", PrivateApi.BookmarksDelete);
        app.MapPost("/private-api/support-settings", PrivateApi.SupportSettings);
        app.MapPost("/private-api/support-settings-update", PrivateApi.SupportSettingsUpdate);
        app.MapPost("/private-api/schedules", PrivateApi.Schedules);
        app.MapPost("/private-api/schedules-add", PrivateApi.SchedulesAdd);
        app.MapPost("/private-api/schedules-delete", PrivateApi.SchedulesDelete);
        app.MapPost("/private-api/schedules-move", PrivateApi.SchedulesMove);
        app.MapPost("/private-api/favorites", PrivateApi.Favorites);
        app.MapPost("/private-api/favorites-check", PrivateApi.FavoritesCheck);
        app.MapPost("/private-api/favorites-add", PrivateApi.FavoritesAdd);
        app.MapPost("/private-api/favorites-delete", PrivateApi.FavoritesDelete);
        app.MapPost("/private-api/fragments", PrivateApi.Fragments);
        app.MapPost("/private-api/fragments-add", PrivateApi.FragmentsAdd);
        app.MapPost("/private-api/fragments-update", PrivateApi.FragmentsUpdate);
        app.MapPost("/private-api/fragments-delete", PrivateApi.FragmentsDelete);
        app.MapPost("/private-api/decks", PrivateApi.Decks);
        app.MapPost("/private-api/decks-add", PrivateApi.DecksAdd);
        app.MapPost("/private-api/decks-update", PrivateApi.DecksUpdate);
        app.MapPost("/private-api/decks-delete", PrivateApi.DecksDelete);
        app.MapPost("/private-api/recoveries", PrivateApi.Recoveries);
        app.MapPost("/private-api/recoveries-add", PrivateApi.RecoveriesAdd);
        app.MapPost("/private-api/recoveries-update", PrivateApi.RecoveriesUpdate);
        app.MapPost("/private-api/recoveries-delete", PrivateApi.RecoveriesDelete);
        app.MapPost("/private-api/points-claim", PrivateApi.PointsClaim);
        app.MapPost("/private-api/points-calc", PrivateApi.PointsCalc);
        app.MapPost("/private-api/promote-price", PrivateApi.PromotePrice);
        app.MapPost("/private-api/promoted-post", PrivateApi.PromotedPost);
        app.MapPost("/private-api/boost-plus-price", PrivateApi.BoostPlusPrice);
        app.MapPost("/private-api/boosted-plus-account", PrivateApi.BoostedPlusAccount);
        app.MapPost("/private-api/rc-delegation-price", PrivateApi.RcDelegationPrice);
        app.MapPost("/private-api/rc-delegation-active", PrivateApi.RcDelegationActive);
        app.MapPost("/private-api/stripe-create-intent", PrivateApi.StripeCreateIntent);
        app.MapPost("/private-api/stripe-order-status", PrivateApi.StripeOrderStatus);
        app.MapPost("/private-api/stripe-account-intent", PrivateApi.StripeCreateAccountIntent);
        app.MapPost("/private-api/stripe-account-status", PrivateApi.StripeAccountStatus);
        app.MapPost("/private-api/boost-options", PrivateApi.BoostOptions);
        app.MapPost("/private-api/boosted-post", PrivateApi.BoostedPost);
        app.MapPost("/private-api/ai-generate-price", PrivateApi.AiGeneratePrice);
        app.MapPost("/private-api/ai-generate-image", PrivateApi.AiGenerateImage);
        app.MapPost("/private-api/ai-assist-price", PrivateApi.AiAssistPrice);
        app.MapPost("/private-api/ai-assist", PrivateApi.AiAssist);
        app.MapPost("/private-api/usr-activity", PrivateApi.Activities);
        app.MapPost("/private-api/get-game", PrivateApi.GameGet);
        app.MapPost("/private-api/post-game", PrivateApi.GamePost);
        app.MapPost("/private-api/purchase-order", PrivateApi.PurchaseOrder);
        app.MapPost("/private-api/chats", PrivateApi.Chats);
        app.MapPost("/private-api/chats-add", PrivateApi.ChatsAdd);
        app.MapPost("/private-api/chats-update", PrivateApi.ChatsUpdate);
        app.MapPost("/private-api/channel-add", PrivateApi.ChannelAdd);

        // ---- Health check for docker swarm ----
        app.MapGet("/healthcheck.json", async ctx =>
        {
            await ctx.SendJson(200, new JsonObject
            {
                ["status"] = 200,
                ["body"] = new JsonObject { ["status"] = "ok" },
            });
        });

        // ---- Fallback ----
        // GET/HEAD -> the template page (Express .get("*", fallbackHandler)).
        // Everything else -> Express's default finalhandler 404.
        app.MapFallback(async ctx =>
        {
            var method = ctx.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                if (!HttpMethods.IsHead(method))
                {
                    await ctx.Response.WriteAsync(TemplateHtml);
                }
                return;
            }

            var message = $"Cannot {method} {EscapeHtml(EncodeUrl(ctx.Request.Path.Value ?? "/"))}";
            var html =
                "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n" +
                "<title>Error</title>\n</head>\n<body>\n<pre>" + message + "</pre>\n</body>\n</html>\n";
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html);
        });
    }

    /// <summary>
    /// Exact render of template.tsx (renderToString(&lt;App/&gt;) -> "Hello there! This is
    /// Vision API."). Byte-for-byte with the Node output (496 bytes); parity-verified.
    /// </summary>
    private const string TemplateHtml =
        "<!DOCTYPE html>\n" +
        "            <html lang=\"en\">\n" +
        "            <head>\n" +
        "                <meta charset=\"utf-8\" />\n" +
        "                <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n" +
        "                <link rel=\"icon\" href=\"/favicon.png\" />\n" +
        "                <meta name=\"theme-color\" content=\"#000000\" />\n" +
        "                <title>Ecency Api</title>\n" +
        "            </head>\n" +
        "            <body>\n" +
        "                <div id=\"root\">Hello there! This is Vision API.</div>\n" +
        "            </body>\n" +
        "        </html>";

    /// <summary>
    /// Port of the `encodeurl` npm package (used by Express finalhandler): encode
    /// characters outside its allow-list, but leave already-percent-encoded
    /// sequences intact. ctx.Request.Path is URL-decoded, so this re-encodes it.
    /// </summary>
    private static string EncodeUrl(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var ch in path)
        {
            // encodeurl's ENCODE_CHARS_REGEXP leaves these unescaped:
            //   A-Za-z0-9 and  - _ . ! ~ * ' ( )  ; / ? : @ & = + $ , # [ ] %
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')'
                or ';' or '/' or '?' or ':' or '@' or '&' or '=' or '+' or '$'
                or ',' or '#' or '[' or ']' or '%')
            {
                sb.Append(ch);
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes(ch.ToString()))
                {
                    sb.Append('%').Append(((int)b).ToString("X2"));
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Port of the `escape-html` npm package (Express finalhandler).</summary>
    private static string EscapeHtml(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("&quot;"); break;
                case '&': sb.Append("&amp;"); break;
                case '\'': sb.Append("&#39;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
