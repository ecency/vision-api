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
    public void TheModerationAccountIsEcency()
    {
        // Pinned: this account name is the whole control surface. A typo here
        // would read as "nobody is muted" with no error anywhere.
        Assert.Equal("ecency", ModerationMutes.Account);
    }
}
