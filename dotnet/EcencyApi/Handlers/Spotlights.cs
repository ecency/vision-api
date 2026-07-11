using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/spotlights.ts.
///
/// Feature Spotlight content - single source of truth for the "did you know?" cards.
/// Edit this array (set start/end or bump weight) to rotate the spotlight. The server
/// filters by the start/end window only; guestsOnly/path/dismiss are applied client-side
/// (web + mobile). Wire shape must stay in sync with @ecency/sdk (types/spotlight.ts) -
/// clients parse exactly this shape.
///
/// id          - stable slug, e.g. "waves-2026-w23"; used as the dismiss key, never reuse
/// feature     - which feature this promotes (waves|polls|points|communities|...), for gating
/// title       - card title
/// description - card body
/// icon        - optional icon name or i.ecency.com image url
/// button_text - actionable button text
/// button_link - internal route ("/waves") or external url the button opens
/// path        - which path(s) it should show on, supports regex on location; omit = everywhere
/// guestsOnly  - show only to signed-out visitors; omit = logged-in users only
/// platforms   - which platform(s) may show it: "web" (website) and/or "mobile" (mobile app). omit = both
/// start       - ISO date (UTC); do not show before this. Bare "YYYY-MM-DD" = that day at 00:00Z
/// end         - ISO date (UTC); inclusive - bare "YYYY-MM-DD" shows through the END of that day. Omit = never expires
/// weight      - higher wins when multiple are active
/// locales     - optional per-language overrides for title/description/button_text
/// </summary>
public static class Spotlights
{
    /// <summary>Fresh copy of the spotlights array (wire shape identical to the TS export).</summary>
    public static JsonArray Json() => new()
    {
        // Guest funnel: shown only to signed-out visitors (guestsOnly). These never expire and
        // descend in weight, so a guest sees the top hook first and the next one each time they
        // dismiss. Logged-in users never match a guestsOnly card, so there is no overlap with the
        // educational cards below.
        new JsonObject
        {
            ["id"] = "guest-signup",
            ["feature"] = "signup",
            ["title"] = "New to Ecency?",
            ["description"] = "Create your free account and start earning rewards for everything you post.",
            ["button_text"] = "Create account",
            ["button_link"] = "/signup",
            ["guestsOnly"] = true,
            ["start"] = "2026-06-05",
            ["weight"] = 10,
        },
        new JsonObject
        {
            ["id"] = "guest-earn",
            ["feature"] = "signup",
            ["title"] = "Get paid to post",
            ["description"] = "On Ecency the rewards go to creators, not the platform. Your posts, your earnings.",
            ["button_text"] = "Join free",
            ["button_link"] = "/signup",
            ["guestsOnly"] = true,
            ["start"] = "2026-06-05",
            ["weight"] = 9,
        },
        new JsonObject
        {
            ["id"] = "guest-own",
            ["feature"] = "signup",
            ["title"] = "Own your content",
            ["description"] = "No ads, no gatekeepers. Your account lives on the blockchain, and it is yours.",
            ["button_text"] = "Get started",
            ["button_link"] = "/signup",
            ["guestsOnly"] = true,
            ["start"] = "2026-06-05",
            ["weight"] = 8,
        },

        // ---- Jun 5 - 18 ----
        new JsonObject
        {
            ["id"] = "web-waves-2026-06",
            ["feature"] = "waves",
            ["title"] = "Have you tried Waves?",
            ["description"] = "Share short posts and jump into the conversation on Hive in seconds.",
            ["button_text"] = "Open Waves",
            ["button_link"] = "/waves",
            ["platforms"] = new JsonArray { "web" },
            ["start"] = "2026-06-05",
            ["end"] = "2026-06-18",
            ["weight"] = 10,
        },
        new JsonObject
        {
            ["id"] = "mob-trending-2026-06",
            ["feature"] = "discover",
            ["title"] = "See what's trending",
            ["description"] = "Tap into the most popular posts on Hive right now.",
            ["button_text"] = "View trending",
            ["button_link"] = "/trending",
            ["platforms"] = new JsonArray { "mobile" },
            ["start"] = "2026-06-05",
            ["end"] = "2026-06-18",
            ["weight"] = 10,
        },

        // ---- Jun 19 - Jul 2 ----
        new JsonObject
        {
            ["id"] = "communities-2026-06",
            ["feature"] = "communities",
            ["title"] = "Find your community",
            ["description"] = "Join topic-based communities and meet people who share your interests.",
            ["button_text"] = "Browse communities",
            ["button_link"] = "/communities",
            ["start"] = "2026-06-19",
            ["end"] = "2026-07-02",
            ["weight"] = 10,
        },

        // ---- Jul 3 - 16 ----
        new JsonObject
        {
            ["id"] = "web-decks-2026-07",
            ["feature"] = "decks",
            ["title"] = "Power up with Decks",
            ["description"] = "A customizable multi-column dashboard. Track tags, communities, and notifications side by side.",
            ["button_text"] = "Open Decks",
            ["button_link"] = "/decks",
            ["platforms"] = new JsonArray { "web" },
            ["start"] = "2026-07-03",
            ["end"] = "2026-07-16",
            ["weight"] = 10,
        },
        new JsonObject
        {
            ["id"] = "mob-hot-2026-07",
            ["feature"] = "discover",
            ["title"] = "Catch what's hot",
            ["description"] = "See the posts heating up across Hive.",
            ["button_text"] = "View hot",
            ["button_link"] = "/hot",
            ["platforms"] = new JsonArray { "mobile" },
            ["start"] = "2026-07-03",
            ["end"] = "2026-07-16",
            ["weight"] = 10,
        },

        // ---- Jul 17 - 30 ----
        new JsonObject
        {
            ["id"] = "wallet-2026-07",
            ["feature"] = "wallet",
            ["title"] = "Your wallet, at a glance",
            ["description"] = "Check your HIVE, HBD, Hive Power and Points, then claim your rewards.",
            ["button_text"] = "Open wallet",
            ["button_link"] = "/wallet",
            ["start"] = "2026-07-17",
            ["end"] = "2026-07-30",
            ["weight"] = 10,
        },

        // ---- Jul 31 - Aug 13 ----
        new JsonObject
        {
            ["id"] = "search-2026-08",
            ["feature"] = "search",
            ["title"] = "Find people and topics to follow",
            ["description"] = "Search posts, tags, and creators across Hive, and follow the ones you love.",
            ["button_text"] = "Start searching",
            ["button_link"] = "/search",
            ["start"] = "2026-07-31",
            ["end"] = "2026-08-13",
            ["weight"] = 10,
        },

        // ---- Aug 14 - 27 ----
        new JsonObject
        {
            ["id"] = "web-points-2026-08",
            ["feature"] = "points",
            ["title"] = "Earn Ecency Points",
            ["description"] = "Get rewarded for posting and engaging, then spend Points to boost your reach.",
            ["button_text"] = "Explore perks",
            ["button_link"] = "/perks",
            ["platforms"] = new JsonArray { "web" },
            ["start"] = "2026-08-14",
            ["end"] = "2026-08-27",
            ["weight"] = 10,
        },
        new JsonObject
        {
            ["id"] = "mob-bookmarks-2026-08",
            ["feature"] = "bookmarks",
            ["title"] = "Save it for later",
            ["description"] = "Bookmark posts you want to come back to, they are always one tap away.",
            ["button_text"] = "Open bookmarks",
            ["button_link"] = "/bookmarks",
            ["platforms"] = new JsonArray { "mobile" },
            ["start"] = "2026-08-14",
            ["end"] = "2026-08-27",
            ["weight"] = 10,
        },
    };

    private static readonly Regex BareDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    /// <summary>isBareDate from private-api.ts getSpotlight: /^\d{4}-\d{2}-\d{2}$/.</summary>
    internal static bool IsBareDate(string d) => BareDate.IsMatch(d);

    /// <summary>
    /// JS `new Date(s).getTime()` equivalent: milliseconds since epoch, NaN when unparsable.
    /// Bare "YYYY-MM-DD" parses as UTC midnight, like the JS Date ISO short form.
    /// </summary>
    internal static double ParseDateMs(string d)
    {
        if (IsBareDate(d))
        {
            return DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                ? (day - DateTime.UnixEpoch).TotalMilliseconds
                : double.NaN;
        }

        return DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds()
            : double.NaN;
    }
}

public static partial class PrivateApi
{
    // GET ^/private-api/spotlights$
    public static async Task GetSpotlight(HttpContext ctx)
    {
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Bare ISO dates ("YYYY-MM-DD") parse as UTC midnight. `start` is inclusive from the
        // beginning of its day; `end` is inclusive through the END of its day, so an item with
        // end "2026-06-30" still shows throughout June 30 (UTC). A malformed date fails open
        // (the item stays visible) rather than silently hiding a live spotlight.
        const double dayMs = 86_400_000;

        var active = new JsonArray();
        foreach (var node in Spotlights.Json())
        {
            var s = (JsonObject)node!;
            var start = s["start"]?.GetValue<string>();
            var end = s["end"]?.GetValue<string>();

            var startMs = !string.IsNullOrEmpty(start) ? Spotlights.ParseDateMs(start) : double.NaN;
            var endMs = !string.IsNullOrEmpty(end) ? Spotlights.ParseDateMs(end) : double.NaN;
            var endBoundary = !string.IsNullOrEmpty(end) && Spotlights.IsBareDate(end)
                ? endMs + dayMs - 1
                : endMs;

            var startsOk = string.IsNullOrEmpty(start) || double.IsNaN(startMs) || startMs <= now;
            var endsOk = string.IsNullOrEmpty(end) || double.IsNaN(endMs) || endBoundary >= now;

            if (startsOk && endsOk)
            {
                active.Add(s.DeepClone());
            }
        }

        await ctx.SendJson(200, active);
    }
}
