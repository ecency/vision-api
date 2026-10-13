using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// The image check's verdicts for waves, as kept by the waves indexer.
///
/// The indexer leaves a wave with a detected explicit picture out of every
/// shared feed it serves. Promoted entries never go through it, so without this
/// a flagged wave could still be bought into the most prominent slot of the
/// feed, the same hole the moderation mute list had before ModerationMutes.
///
/// Asked from the indexer rather than decided here: the threshold and the
/// stored verdicts live in one place. Fails open, and to the last answer it
/// had: a filter that cannot load must not take the promoted list down.
/// </summary>
public static class WaveNsfw
{
    private const string CachePrefix = "wave-nsfw-flagged:";

    /// <summary>Survives a failed lookup. Never expires on purpose.</summary>
    private const string LastGoodCacheKey = "wave-nsfw-flagged-last-good";

    /// <summary>A verdict lands seconds after a wave is posted; this bounds how long a
    /// just-flagged promoted wave stays up.</summary>
    private const double TtlSeconds = 60;

    /// <summary>Holds a failure so a dead indexer costs one short call per interval,
    /// not one per promoted-entries request.</summary>
    private const double FailureTtlSeconds = 30;

    /// <summary>This sits on the promoted-entries request path, so it is short.</summary>
    private const int TimeoutMs = 2000;

    /// <summary>The indexer answers at most this many posts per call.</summary>
    public const int MaxPosts = 100;

    /// <summary>Replaceable for tests. Returns the indexer's answer for these keys.</summary>
    internal static Func<IReadOnlyList<string>, Task<UpstreamResponse>> Lookup = DefaultLookup;

    private static Task<UpstreamResponse> DefaultLookup(IReadOnlyList<string> keys) =>
        ApiClient.ApiRequest(Endpoint(keys), HttpMethod.Get, timeoutMs: TimeoutMs);

    /// <summary>`waves/nsfw?post=a/p&amp;post=b/q`, each key form-encoded.</summary>
    internal static string Endpoint(IEnumerable<string> keys) =>
        "waves/nsfw?" + string.Join("&", keys.Select(k => "post=" + Uri.EscapeDataString(k)));

    /// <summary>
    /// `author/permlink` for every entry that has both, deduplicated. Entries
    /// with an unreadable author or permlink are skipped here and therefore kept
    /// by the filter.
    /// </summary>
    internal static List<string> Keys(JsonArray entries)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = Key(entry);
            if (key != null && seen.Add(key))
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    internal static string? Key(JsonNode? entry)
    {
        var author = ReadString(entry, "author");
        var permlink = ReadString(entry, "permlink");
        return string.IsNullOrEmpty(author) || string.IsNullOrEmpty(permlink)
            ? null
            : author.ToLowerInvariant() + "/" + permlink;
    }

    /// <summary>Lenient on purpose, see ModerationMutes.ReadString.</summary>
    private static string? ReadString(JsonNode? owner, string property) =>
        owner is JsonObject o
        && o.TryGetPropertyValue(property, out var node)
        && node is JsonValue value
        && JsVal.TryGetStringLenient(value, out var s)
            ? s
            : null;

    /// <summary>
    /// The flagged subset of these entries. Empty when there is nothing to ask,
    /// and the last good answer when the indexer cannot be reached.
    /// </summary>
    public static async Task<HashSet<string>> GetFlagged(JsonArray entries)
    {
        var keys = Keys(entries);
        if (keys.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // The promoted list is the same for every caller between its own
        // refreshes, so one entry per distinct list is one entry in practice.
        var cacheKey = CachePrefix + string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal));
        var cached = MemCache.Get<string[]>(cacheKey);
        if (cached != null)
        {
            return ToSet(cached);
        }

        string[] flagged;
        try
        {
            // One call per MaxPosts keys, so a long promoted list is checked in
            // full instead of being filtered only as far as the first chunk.
            var found = new List<string>();
            foreach (var chunk in keys.Chunk(MaxPosts))
            {
                found.AddRange(ReadFlagged(await Lookup(chunk)));
            }
            flagged = found.ToArray();
            MemCache.Set(cacheKey, flagged, TtlSeconds);
            MemCache.Set(LastGoodCacheKey, flagged);
        }
        catch (Exception)
        {
            // Deliberately silent (CLAUDE.md, "No hot-path logging"). The last
            // good answer may name waves that are no longer promoted; that is
            // harmless, the filter only drops what is in both sets.
            flagged = MemCache.Get<string[]>(LastGoodCacheKey) ?? Array.Empty<string>();
            MemCache.Set(cacheKey, flagged, FailureTtlSeconds);
        }

        return ToSet(flagged);
    }

    /// <summary>
    /// `{"flagged": ["author/permlink", ...]}`. Anything else throws, so an
    /// indexer that answers 200 with an unusable body is a failure (fall back),
    /// not an empty verdict cached as "nothing is flagged".
    /// </summary>
    internal static string[] ReadFlagged(UpstreamResponse response)
    {
        if (response.Status != 200 || response.Json is not JsonObject body
            || !body.TryGetPropertyValue("flagged", out var node) || node is not JsonArray rows)
        {
            throw new InvalidOperationException("unusable wave nsfw lookup answer");
        }

        var flagged = new List<string>();
        foreach (var row in rows)
        {
            if (row is JsonValue value && JsVal.TryGetStringLenient(value, out var key) && !string.IsNullOrEmpty(key))
            {
                flagged.Add(key);
            }
        }
        return flagged.ToArray();
    }

    internal static HashSet<string> ToSet(IEnumerable<string> keys) => new(keys, StringComparer.Ordinal);

    /// <summary>
    /// Drop the flagged entries. Returns a new array; an entry whose author or
    /// permlink cannot be read is kept, an unreadable shape is not a verdict.
    /// </summary>
    public static JsonArray FilterFlagged(JsonArray entries, ISet<string> flagged)
    {
        if (flagged.Count == 0)
        {
            return entries;
        }

        var kept = new JsonArray();
        foreach (var entry in entries.ToArray())
        {
            var key = Key(entry);
            if (key != null && flagged.Contains(key))
            {
                continue;
            }

            // A node can only live in one parent, so detach before re-parenting.
            entry?.Parent?.AsArray().Remove(entry);
            kept.Add(entry);
        }

        return kept;
    }
}
