using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The moderation mute filter on promoted entries. The list is fetched from
/// chain, but the parts that can silently break a feed are pure: an empty or
/// unreadable list must leave the feed alone, and a match must remove exactly
/// the muted author's entries and nothing else.
/// </summary>
public class ModerationMutesTests
{
    private static long TotalRpcCalls() =>
        ModerationMutes.Rpc.HealthSnapshot().Sum(n => n!["calls"]!.GetValue<long>());

    private static JsonArray Entries(params string?[] authors)
    {
        var arr = new JsonArray();
        foreach (var a in authors)
        {
            var o = new JsonObject { ["permlink"] = "p-" + (a ?? "none") };
            if (a != null)
            {
                o["author"] = a;
            }
            arr.Add(o);
        }
        return arr;
    }

    private static string?[] AuthorsOf(JsonArray arr) =>
        arr.Select(e => e is JsonObject o && o.TryGetPropertyValue("author", out var a)
            ? a?.GetValue<string>()
            : null).ToArray();

    [Fact]
    public void AnEmptyMuteListLeavesTheFeedUntouched()
    {
        var entries = Entries("alice", "bob");
        var result = ModerationMutes.FilterMutedAuthors(entries, ModerationMutes.ToSet(Array.Empty<string>()));
        Assert.Equal(new[] { "alice", "bob" }, AuthorsOf(result));
    }

    [Fact]
    public void MutedAuthorsAreDroppedAndTheRestKeptInOrder()
    {
        var entries = Entries("alice", "spammer", "bob", "spammer", "carol");
        var result = ModerationMutes.FilterMutedAuthors(entries, ModerationMutes.ToSet(new[] { "spammer" }));
        Assert.Equal(new[] { "alice", "bob", "carol" }, AuthorsOf(result));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        // Hive account names are lowercase, but nothing here guarantees the two
        // sides were normalized by the same code path.
        var entries = Entries("Spammer");
        var result = ModerationMutes.FilterMutedAuthors(entries, ModerationMutes.ToSet(new[] { "spammer" }));
        Assert.Empty(result);
    }

    [Fact]
    public void AnEntryWithNoAuthorIsKept()
    {
        // An unreadable shape is not evidence of anything; dropping it would
        // shrink the feed for a reason nobody could see.
        var entries = Entries("alice", null);
        var result = ModerationMutes.FilterMutedAuthors(entries, ModerationMutes.ToSet(new[] { "spammer" }));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilteringEveryEntryYieldsAnEmptyArrayNotNull()
    {
        var entries = Entries("spammer", "spammer");
        var result = ModerationMutes.FilterMutedAuthors(entries, ModerationMutes.ToSet(new[] { "spammer" }));
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadFollowingTakesTheFollowingNamesAndSkipsUnusableRows()
    {
        var rows = new JsonArray(
            new JsonObject { ["follower"] = "ecency", ["following"] = "spammer", ["what"] = new JsonArray("ignore") },
            new JsonObject { ["follower"] = "ecency" },
            new JsonObject { ["follower"] = "ecency", ["following"] = "" },
            new JsonObject { ["follower"] = "ecency", ["following"] = "phisher", ["what"] = new JsonArray("ignore") });

        Assert.Equal(new[] { "spammer", "phisher" }, ModerationMutes.ReadFollowing(rows));
    }

    [Fact]
    public void AnAuthorThatCannotBeReadAsAStringDoesNotThrow()
    {
        // GetValue<string>() throws on a lone-surrogate escape, which JSON.parse
        // accepts and Hive nodes do emit. Throwing here would fail the whole
        // promoted-entries request over one malformed name.
        var entries = new JsonArray(
            new JsonObject { ["author"] = 42 },
            new JsonObject { ["author"] = JsonValue.Create((string?)null) },
            new JsonObject { ["author"] = new JsonObject() },
            new JsonObject { ["author"] = "spammer" });

        var result = ModerationMutes.FilterMutedAuthors(entries, ModerationMutes.ToSet(new[] { "spammer" }));

        // The three unreadable ones survive; only the match is dropped.
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void AFollowingThatCannotBeReadAsAStringIsSkippedNotFatal()
    {
        // Same reasoning on the refresh side: one malformed row must not abort
        // the mute-list refresh and leave the feed unfiltered.
        var rows = new JsonArray(
            new JsonObject { ["following"] = 7 },
            new JsonObject { ["following"] = new JsonArray("nested") },
            new JsonObject { ["following"] = "spammer" });

        Assert.Equal(new[] { "spammer" }, ModerationMutes.ReadFollowing(rows));
    }

    [Fact]
    public async Task AFailedRefreshIsCachedSoQueuedRequestsDoNotEachRetry()
    {
        // The refresh gate serializes callers. Without caching the failure, a
        // dead node pool makes that worse than no gate at all: each queued
        // request waits out the one ahead of it and then runs its own full
        // failover sweep, so the Nth caller pays N times the timeout budget.
        var original = ModerationMutes.Rpc;
        MemCache.Del("moderation-muted-authors");
        MemCache.Del("moderation-muted-authors-last-good");

        // Port 9 (discard) refuses immediately, so this measures the code path
        // rather than a real network timeout.
        ModerationMutes.Rpc = new HiveRpcClient(
            new[] { "http://127.0.0.1:9/" }, timeoutMs: 250, failoverThreshold: 1);

        try
        {
            var first = await ModerationMutes.Get();
            Assert.Empty(first);

            // Count attempts rather than elapsed time: a refused connection
            // fails in microseconds, so a timing assertion passes just as
            // happily whether or not the failure was cached.
            var callsAfterFirst = TotalRpcCalls();

            var followers = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => ModerationMutes.Get()));

            Assert.All(followers, f => Assert.Empty(f));
            Assert.Equal(callsAfterFirst, TotalRpcCalls());
        }
        finally
        {
            ModerationMutes.Rpc = original;
            MemCache.Del("moderation-muted-authors");
            MemCache.Del("moderation-muted-authors-last-good");
        }
    }

    [Fact]
    public async Task AFailedRefreshFallsBackToTheLastListSeen()
    {
        var original = ModerationMutes.Rpc;
        MemCache.Del("moderation-muted-authors");

        // Stand in for a previously successful fetch.
        MemCache.Set("moderation-muted-authors-last-good", new[] { "spammer" });
        ModerationMutes.Rpc = new HiveRpcClient(
            new[] { "http://127.0.0.1:9/" }, timeoutMs: 250, failoverThreshold: 1);

        try
        {
            // Stale filtering, not no filtering: an unreachable pool must not be
            // a way for a muted account back into the feed.
            var muted = await ModerationMutes.Get();
            Assert.Equal(new[] { "spammer" }, muted.OrderBy(x => x));
        }
        finally
        {
            ModerationMutes.Rpc = original;
            MemCache.Del("moderation-muted-authors");
            MemCache.Del("moderation-muted-authors-last-good");
        }
    }

    [Fact]
    public void TheModerationAccountIsEcency()
    {
        // Pinned: this account name is the whole control surface. A typo here
        // would read as "nobody is muted" with no error anywhere.
        Assert.Equal("ecency", ModerationMutes.Account);
    }
}
