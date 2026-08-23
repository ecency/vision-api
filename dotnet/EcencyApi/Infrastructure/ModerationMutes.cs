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

    /// <summary>
    /// How long a failed refresh is held before trying again. Short, so a blip
    /// costs one interval of stale filtering, but not zero: caching the failure
    /// is what stops every request behind the gate from running its own full
    /// node-failover sweep.
    /// </summary>
    private const double FailureTtlSeconds = 30;

    /// <summary>
    /// How long a request waits for someone else's refresh before answering from
    /// what it already has. A normal refresh is one RPC round trip, so waiters
    /// get the real list; this only bounds the pathological case where the whole
    /// node pool is timing out and the refresh takes tens of seconds.
    /// </summary>
    private static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(2);

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
    /// One refresh at a time. Without this, every request arriving after the TTL
    /// lapses starts its own paging loop, so a burst turns one refresh into as
    /// many RPC conversations as there are concurrent promoted-entries requests.
    /// The waiters re-read the cache and take the winner's result.
    /// </summary>
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

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

        if (!await RefreshGate.WaitAsync(RefreshWait))
        {
            // A refresh is already running and is taking far longer than one RPC
            // round trip. Queueing behind it would hand that latency to a
            // promoted-entries request, so answer from the fallback instead.
            return ToSet(MemCache.Get<string[]>(LastGoodCacheKey) ?? Array.Empty<string>());
        }

        try
        {
            // Someone else may have refreshed while this request queued.
            cached = MemCache.Get<string[]>(CacheKey);
            if (cached != null)
            {
                return ToSet(cached);
            }

            return ToSet(await Refresh());
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task<string[]> Refresh()
    {
        try
        {
            var names = await Fetch();
            MemCache.Set(CacheKey, names, TtlSeconds);

            // Only a list with something in it is worth falling back to. An empty
            // one is served live (unmuting everyone must take effect) but must not
            // overwrite the fallback, or one empty answer would turn every later
            // failure into no filtering at all.
            if (names.Length > 0)
            {
                MemCache.Set(LastGoodCacheKey, names);
            }

            return names;
        }
        catch (Exception)
        {
            // Deliberately silent: this runs on the promoted-entries request path
            // and the service keeps its logs quiet there (CLAUDE.md, "No hot-path
            // logging"). The caching below is what makes the failure survivable.
            var fallback = MemCache.Get<string[]>(LastGoodCacheKey) ?? Array.Empty<string>();

            // Cache the failure, including the empty one. Without this the gate
            // above turns a node outage into something worse than no gate at all:
            // each queued request waits out the one ahead of it and then runs its
            // own full failover sweep, so the Nth caller pays N times the timeout
            // budget. Caching lets every waiter answer immediately and puts one
            // retry on the clock instead of one per request.
            MemCache.Set(CacheKey, fallback, FailureTtlSeconds);
            return fallback;
        }
    }

    private static async Task<string[]> Fetch()
    {
        var names = new List<string>();
        var start = "";

        for (var page = 0; page < MaxPages; page++)
        {
            // Validate the shape at the client, so a node answering 200 with
            // something that is not a row array fails over to another one and,
            // if none can answer, throws. Without this an unusable answer read
            // as "no more rows" and the caller cached an empty mute list --
            // filtering silently off, with nothing anywhere saying so.
            var result = await Rpc.Call("condenser_api", "get_following",
                new JsonArray(Account, start, "ignore", PageSize),
                validateResult: r => r is JsonArray);

            var rows = (JsonArray)result!;
            if (rows.Count == 0)
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

    /// <summary>
    /// Read one string property the lenient way. `GetValue&lt;string&gt;()` throws on
    /// a lone-surrogate escape, which JSON.parse accepts and Hive nodes do emit;
    /// letting that throw here would fail a promoted-entries request, or abort a
    /// mute-list refresh, over one malformed account name.
    /// </summary>
    private static string? ReadString(JsonNode? owner, string property) =>
        owner is JsonObject o
        && o.TryGetPropertyValue(property, out var node)
        && node is JsonValue value
        && JsVal.TryGetStringLenient(value, out var s)
            ? s
            : null;

    internal static List<string> ReadFollowing(JsonArray rows)
    {
        var names = new List<string>();
        foreach (var row in rows)
        {
            var name = ReadString(row, "following");
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
            var author = ReadString(entry, "author");

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
