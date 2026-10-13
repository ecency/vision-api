using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The image-check filter on promoted entries. The verdicts come from the waves
/// indexer; what can silently go wrong here is the rest: dropping the wrong
/// entry, treating an unusable answer as "nothing is flagged", or taking the
/// promoted list down when the indexer cannot be reached.
/// </summary>
[Collection("wave-nsfw")]
public class WaveNsfwTests : IDisposable
{
    private readonly Func<IReadOnlyList<string>, Task<UpstreamResponse>> _saved = WaveNsfw.Lookup;

    public void Dispose() => WaveNsfw.Lookup = _saved;

    // MemCache is process-wide and has no reset, so every test names its own waves.
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static JsonObject Post(string? author, string? permlink)
    {
        var o = new JsonObject();
        if (author != null) o["author"] = author;
        if (permlink != null) o["permlink"] = permlink;
        return o;
    }

    private static UpstreamResponse Answer(int status, JsonNode? json) => new()
    {
        Status = status,
        Json = json,
        Headers = new HttpResponseHeaders2(new HttpResponseMessage()),
    };

    private static UpstreamResponse Flagged(params string[] keys) =>
        Answer(200, new JsonObject { ["flagged"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()) });

    private static string?[] PermlinksOf(JsonArray arr) =>
        arr.Select(e => e is JsonObject o && o.TryGetPropertyValue("permlink", out var p) && p is JsonValue
            ? p.ToString()
            : null).ToArray();

    [Fact]
    public void KeysAreAuthorSlashPermlinkLowercasedAuthorAndDeduplicated()
    {
        var entries = new JsonArray(Post("Alice", "Wave-1"), Post("alice", "Wave-1"), Post("bob", "p"),
            Post(null, "p"), Post("carol", null), Post("", "p"), new JsonObject { ["author"] = 7, ["permlink"] = "p" });

        Assert.Equal(new[] { "alice/Wave-1", "bob/p" }, WaveNsfw.Keys(entries));
    }

    [Fact]
    public void TheLookupEndpointRepeatsPostAndEncodesEachKey()
    {
        Assert.Equal("waves/nsfw?post=alice%2Fwave-1&post=bob%2Fa%20b%26c",
            WaveNsfw.Endpoint(new[] { "alice/wave-1", "bob/a b&c" }));
    }

    [Fact]
    public void OnlyTheFlaggedEntryIsDropped()
    {
        var entries = new JsonArray(Post("alice", "clean"), Post("bob", "explicit"), Post("bob", "other"));

        var result = WaveNsfw.FilterFlagged(entries, WaveNsfw.ToSet(new[] { "bob/explicit" }));

        Assert.Equal(new[] { "clean", "other" }, PermlinksOf(result));
    }

    [Fact]
    public void FilteringNeverReachesTheCachedPromotedList()
    {
        // The filter detaches the entries it keeps from the array it is given. That
        // is only safe because the promoted list never hands out its cached array:
        // MemCache clones on write and again on every read. If either clone went
        // away, one filtered request would empty the cache for everyone after it.
        var key = "wave-nsfw-test-" + Tag();
        var original = new JsonArray(Post("alice", "clean"), Post("bob", "explicit"), Post("carol", "other"));
        MemCache.Set(key, original, 60);

        // the instance a cache-miss request keeps using after it stored the list
        var missCopy = WaveNsfw.FilterFlagged(original, WaveNsfw.ToSet(new[] { "bob/explicit" }));
        // the instance a cache-hit request is handed
        var hitCopy = WaveNsfw.FilterFlagged(MemCache.Get<JsonArray>(key)!, WaveNsfw.ToSet(new[] { "bob/explicit" }));

        Assert.Equal(new[] { "clean", "other" }, PermlinksOf(missCopy));
        Assert.Equal(new[] { "clean", "other" }, PermlinksOf(hitCopy));
        Assert.Equal(new[] { "clean", "explicit", "other" }, PermlinksOf(MemCache.Get<JsonArray>(key)!));
        MemCache.Del(key);
    }

    [Fact]
    public void NothingFlaggedLeavesTheListUntouched()
    {
        var entries = new JsonArray(Post("alice", "clean"));
        Assert.Same(entries, WaveNsfw.FilterFlagged(entries, WaveNsfw.ToSet(Array.Empty<string>())));
    }

    [Fact]
    public void AnEntryThatCannotBeKeyedIsKeptNotDropped()
    {
        // An unreadable shape is not a verdict, and dropping it would silently
        // shrink the promoted list.
        var entries = new JsonArray(Post(null, "explicit"), new JsonObject { ["author"] = new JsonObject() },
            Post("bob", "explicit"));

        var result = WaveNsfw.FilterFlagged(entries, WaveNsfw.ToSet(new[] { "bob/explicit" }));

        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData(500, "{\"flagged\":[]}")]
    [InlineData(200, "{\"error\":\"x\"}")]
    [InlineData(200, "{\"flagged\":\"all\"}")]
    [InlineData(200, "[]")]
    public void AnUnusableAnswerIsAFailureNotAnEmptyVerdict(int status, string json)
    {
        // Cached as "nothing is flagged", a 500 would switch the filter off.
        Assert.Throws<InvalidOperationException>(() => WaveNsfw.ReadFlagged(Answer(status, JsonNode.Parse(json))));
    }

    [Fact]
    public void RowsThatAreNotKeysAreSkipped()
    {
        var answer = Answer(200, JsonNode.Parse("{\"flagged\":[\"bob/p\",7,null,\"\",{\"a\":1}]}"));
        Assert.Equal(new[] { "bob/p" }, WaveNsfw.ReadFlagged(answer));
    }

    [Fact]
    public async Task TheFlaggedSetIsAskedOnceAndThenServedFromTheCache()
    {
        var t = Tag();
        var entries = new JsonArray(Post("alice", "clean-" + t), Post("bob", "explicit-" + t));
        var asked = new List<string[]>();
        WaveNsfw.Lookup = keys =>
        {
            asked.Add(keys.ToArray());
            return Task.FromResult(Flagged("bob/explicit-" + t));
        };

        var first = await WaveNsfw.GetFlagged(entries);
        var second = await WaveNsfw.GetFlagged(entries);

        Assert.Equal(new[] { "bob/explicit-" + t }, first);
        Assert.Equal(first, second);
        Assert.Single(asked);
        Assert.Equal(new[] { "alice/clean-" + t, "bob/explicit-" + t }, asked[0]);
    }

    [Fact]
    public async Task AnEmptyListAsksNothing()
    {
        WaveNsfw.Lookup = _ => throw new Xunit.Sdk.XunitException("no lookup for no keys");
        Assert.Empty(await WaveNsfw.GetFlagged(new JsonArray(Post(null, null))));
    }

    [Fact]
    public async Task AFailedLookupFallsBackToTheLastGoodAnswerAndNeverThrows()
    {
        var t = Tag();
        var entries = new JsonArray(Post("alice", "clean-" + t), Post("bob", "explicit-" + t));
        WaveNsfw.Lookup = _ => Task.FromResult(Flagged("bob/explicit-" + t));
        await WaveNsfw.GetFlagged(entries);

        // The promoted list changed (a new cache key) and the indexer is down.
        var later = new JsonArray(Post("bob", "explicit-" + t), Post("carol", "new-" + t));
        var calls = 0;
        WaveNsfw.Lookup = _ =>
        {
            calls++;
            throw new HttpRequestException("indexer unreachable");
        };

        var flagged = await WaveNsfw.GetFlagged(later);
        var again = await WaveNsfw.GetFlagged(later);

        Assert.Contains("bob/explicit-" + t, flagged);
        Assert.Equal(new[] { "new-" + t }, PermlinksOf(WaveNsfw.FilterFlagged(later, flagged)));
        // The failure is held, so a dead indexer is not asked on every request.
        Assert.Equal(1, calls);
        Assert.Equal(flagged, again);
    }

    [Fact]
    public async Task ALongListIsCheckedInFullAcrossSeveralLookups()
    {
        var t = Tag();
        var entries = new JsonArray();
        for (var i = 0; i < WaveNsfw.MaxPosts + 5; i++)
        {
            entries.Add(Post("a" + i, "p-" + t));
        }
        var last = "a" + (WaveNsfw.MaxPosts + 4) + "/p-" + t;
        var sizes = new List<int>();
        WaveNsfw.Lookup = keys =>
        {
            sizes.Add(keys.Count);
            return Task.FromResult(keys.Contains(last) ? Flagged(last) : Flagged());
        };

        var flagged = await WaveNsfw.GetFlagged(entries);

        Assert.Equal(new[] { WaveNsfw.MaxPosts, 5 }, sizes);
        Assert.Equal(new[] { last }, flagged);
    }
}
