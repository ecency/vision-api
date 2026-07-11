using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// In-process cache mirroring node-cache semantics as used by src/server/cache.ts:
/// stdTTL 0 (entries without a TTL never expire), lazy expiry on read plus a
/// periodic sweep (checkperiod 600). node-cache clones values on get/set
/// (useClones: true), which the Node code relies on — e.g. getPromotedEntries
/// shuffles the array it gets back without corrupting the cached copy — so
/// JsonNode values are deep-cloned on both store and read.
/// </summary>
public static class MemCache
{
    private sealed record Entry(object Value, long ExpiresAtMs);

    private static readonly ConcurrentDictionary<string, Entry> Store = new();

    private static readonly Timer Sweeper = new(_ => Sweep(), null,
        TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(600));

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <param name="ttlSeconds">0 = never expires (node-cache stdTTL semantics)</param>
    public static void Set(string key, object value, double ttlSeconds = 0)
    {
        var stored = value is JsonNode node ? node.DeepClone() : value;
        var expires = ttlSeconds > 0 ? NowMs + (long)(ttlSeconds * 1000) : long.MaxValue;
        Store[key] = new Entry(stored, expires);
    }

    public static T? Get<T>(string key) where T : class
    {
        if (!Store.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAtMs <= NowMs)
        {
            Store.TryRemove(key, out _);
            return null;
        }

        if (entry.Value is JsonNode node)
        {
            return node.DeepClone() as T;
        }

        return entry.Value as T;
    }

    public static void Del(string key) => Store.TryRemove(key, out _);

    private static void Sweep()
    {
        var now = NowMs;
        foreach (var kv in Store)
        {
            if (kv.Value.ExpiresAtMs <= now)
            {
                Store.TryRemove(kv.Key, out _);
            }
        }
    }
}
