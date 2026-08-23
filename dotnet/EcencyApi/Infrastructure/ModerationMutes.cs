using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Ecency's on-chain moderation mute list.
///
/// Muting an account from the moderation account is how spam and phishing are
/// kept out of the waves feeds; esync applies that list to every waves query it
/// serves. Promoted entries never go through esync, so without this they were
/// the one surface a muted account could still reach an audience through — and
/// the most prominent one, since a promoted card is a paid placement.
///
/// Read straight from chain rather than from another service so this holds even
/// if the indexer is behind, and cached because the list changes only when a
/// moderator acts on it.
/// </summary>
public static class ModerationMutes
{
    /// <summary>The account whose mutes are treated as platform-wide.</summary>
    public const string Account = "ecency";

    private const string CacheKey = "moderation-muted-authors";

    /// <summary>
    /// Survives a failed refresh, so an unreachable node degrades to the list we
    /// last saw rather than to no filtering at all. Never expires on purpose.
    /// </summary>
    private const string LastGoodCacheKey = "moderation-muted-authors-last-good";

    private const double TtlSeconds = 300;

    /// <summary>condenser_api.get_following caps a single response at 1000 rows.</summary>
    private const int PageSize = 1000;

    /// <summary>
    /// Bounds the paging loop. 20 pages is 20k muted accounts, far past any real
    /// list, so a node that stops advancing the cursor truncates rather than
    /// looping forever.
    /// </summary>
    private const int MaxPages = 20;

    /// <summary>Replaceable for tests (loopback stub nodes).</summary>
    internal static HiveRpcClient Rpc = HiveClients.Default;

    /// <summary>
    /// The muted accounts, cached. Returns an empty set rather than throwing:
    /// a moderation filter that cannot load must not take a feed down with it.
    /// </summary>
    public static async Task<HashSet<string>> Get()
    {
        var cached = MemCache.Get<string[]>(CacheKey);
        if (cached != null)
        {
            return ToSet(cached);
        }

        try
        {
            var names = await Fetch();
            MemCache.Set(CacheKey, names, TtlSeconds);
            MemCache.Set(LastGoodCacheKey, names);
            return ToSet(names);
        }
        catch (Exception e)
        {
            Console.WriteLine($"warn: failed to fetch moderation mutes {e.Message}");

            // Re-arm the short TTL with the stale list so a node outage does not
            // put an RPC call on every promoted-entries request for its duration.
            var lastGood = MemCache.Get<string[]>(LastGoodCacheKey);
            if (lastGood != null)
            {
                MemCache.Set(CacheKey, lastGood, TtlSeconds);
                return ToSet(lastGood);
            }

            return ToSet(Array.Empty<string>());
        }
    }

    private static async Task<string[]> Fetch()
    {
        var names = new List<string>();
        var start = "";

        for (var page = 0; page < MaxPages; page++)
        {
            var result = await Rpc.Call("condenser_api", "get_following",
                new JsonArray(Account, start, "ignore", PageSize));

            if (result is not JsonArray rows || rows.Count == 0)
            {
                break;
            }

            var pageNames = ReadFollowing(rows);

            // `start` is exclusive on Hive, so a page should not repeat the
            // cursor. Drop it anyway: against a node treating it as inclusive
            // this would re-append the same account until the page cap.
            if (pageNames.Count > 0 && pageNames[0] == start)
            {
                pageNames.RemoveAt(0);
            }

            if (pageNames.Count == 0)
            {
                break;
            }

            names.AddRange(pageNames);

            if (rows.Count < PageSize)
            {
                break;
            }

            start = pageNames[^1];
        }

        return names.ToArray();
    }

    internal static List<string> ReadFollowing(JsonArray rows)
    {
        var names = new List<string>();
        foreach (var row in rows)
        {
            var name = row is JsonObject o && o.TryGetPropertyValue("following", out var f)
                ? f?.GetValue<string>()
                : null;
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }
        return names;
    }

    internal static HashSet<string> ToSet(IEnumerable<string> names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drop entries authored by a muted account. Returns a new array; entries
    /// with no readable author are kept, since an unreadable shape is not
    /// evidence of anything and dropping it would silently shrink the feed.
    /// </summary>
    public static JsonArray FilterMutedAuthors(JsonArray entries, ISet<string> muted)
    {
        if (muted.Count == 0)
        {
            return entries;
        }

        var kept = new JsonArray();
        foreach (var entry in entries.ToArray())
        {
            var author = entry is JsonObject o && o.TryGetPropertyValue("author", out var a)
                ? a?.GetValue<string>()
                : null;

            if (author != null && muted.Contains(author))
            {
                continue;
            }

            // A node can only live in one parent, and these come from a cache
            // clone we own, so detach before re-parenting into the result.
            entry?.Parent?.AsArray().Remove(entry);
            kept.Add(entry);
        }

        return kept;
    }
}
