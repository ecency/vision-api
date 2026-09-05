using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The public desk reads are memoized and shared-cached by their normalized
/// upstream URL. Whitelisting decides what a stranger can make the backend
/// compute; the fixed order and dropped defaults decide how many distinct keys
/// a burst of equivalent requests turns into.
/// </summary>
public class CurationDeskQueryTests
{
    private static KeyValuePair<string, string>[] Q(params (string, string)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)).ToArray();

    private static string Feed(params (string, string)[] pairs) =>
        CurationDeskQuery.Endpoint("curation/desk/feed", CurationDeskQuery.NormalizeFeed(Q(pairs)));

    private static string Recommendations(params (string, string)[] pairs) =>
        CurationDeskQuery.Endpoint("curation/desk/recommendations", CurationDeskQuery.NormalizeRecommendations(Q(pairs)));

    [Fact]
    public void AnEmptyQueryIsTheBarePath()
    {
        Assert.Equal("curation/desk/feed", Feed());
        Assert.Equal("curation/desk/recommendations", Recommendations());
    }

    [Fact]
    public void UnknownParametersAreDropped()
    {
        Assert.Equal("curation/desk/feed",
            Feed(("x", "1"), ("order", "asc"), ("username", "alice"), ("code", "abc"), ("hide_reviewed", "0"), ("flagged", "1")));
    }

    [Fact]
    public void DefaultsAreDroppedSoTheyCollapseOntoTheBarePath()
    {
        Assert.Equal("curation/desk/feed",
            Feed(("limit", "25"), ("sort", "newest"), ("app", "all"), ("window", "all"), ("hide_curated", "1"),
                ("rep_min", "0"), ("rep_max", "100"), ("min_words", "0"), ("max_words", "50000"),
                ("has_images", "0"), ("new_authors", "0"), ("recommended", "0")));
    }

    [Theory]
    [InlineData("0", "limit=1")]
    [InlineData("-3", "limit=1")]
    [InlineData("1", "limit=1")]
    [InlineData("50", "limit=50")]
    [InlineData("999", "limit=50")]
    [InlineData("25", "")]
    [InlineData("abc", "")]
    [InlineData("1e1", "")]
    [InlineData("", "")]
    public void LimitIsClampedToItsRange(string given, string expected)
    {
        var url = Feed(("limit", given));
        Assert.Equal(expected.Length == 0 ? "curation/desk/feed" : "curation/desk/feed?" + expected, url);
    }

    [Fact]
    public void RangesAreClampedAndTheirNoOpBoundsDropped()
    {
        Assert.Equal("curation/desk/feed?rep_min=100", Feed(("rep_min", "150")));
        Assert.Equal("curation/desk/feed?rep_max=0", Feed(("rep_max", "-1")));
        Assert.Equal("curation/desk/feed?rep_min=40&rep_max=70", Feed(("rep_max", "70"), ("rep_min", "40")));
        Assert.Equal("curation/desk/feed", Feed(("min_words", "-5")));
        Assert.Equal("curation/desk/feed?min_words=50000", Feed(("min_words", "999999")));
        Assert.Equal("curation/desk/feed", Feed(("max_words", "999999")));
        Assert.Equal("curation/desk/feed?max_words=300", Feed(("max_words", "300")));
        Assert.Equal("curation/desk/feed", Feed(("rep_min", "high")));
    }

    [Fact]
    public void FlagsAreZeroOrOneOnly()
    {
        Assert.Equal("curation/desk/feed?has_images=1&new_authors=1&recommended=1",
            Feed(("has_images", "1"), ("new_authors", "1"), ("recommended", "1")));
        Assert.Equal("curation/desk/feed",
            Feed(("has_images", "true"), ("new_authors", "yes"), ("recommended", "2")));
        Assert.Equal("curation/desk/feed?hide_curated=0", Feed(("hide_curated", "0")));
        Assert.Equal("curation/desk/feed", Feed(("hide_curated", "false")));
    }

    [Fact]
    public void EnumsAcceptOnlyTheirAllowlist()
    {
        Assert.Equal("curation/desk/feed?sort=queue", Feed(("sort", "queue")));
        Assert.Equal("curation/desk/feed?sort=unique", Feed(("sort", "unique")));
        Assert.Equal("curation/desk/feed?view=new-authors", Feed(("view", "new-authors")));
        Assert.Equal("curation/desk/feed", Feed(("view", "excluded")));
        Assert.Equal("curation/desk/feed?app=peakd", Feed(("app", "peakd")));
        Assert.Equal("curation/desk/feed", Feed(("app", "hive")));
        Assert.Equal("curation/desk/feed?window=locked", Feed(("window", "locked")));
        Assert.Equal("curation/desk/feed", Feed(("window", "week")));
        Assert.Equal("curation/desk/feed", Feed(("sort", "Queue")));
    }

    [Fact]
    public void APublicRandomSortFallsBackToNewestAndItsSeedIsDropped()
    {
        Assert.Equal("curation/desk/feed", Feed(("sort", "random"), ("seed", "abcd1234")));
        Assert.Equal("curation/desk/feed", Feed(("seed", "abcd1234")));
        Assert.Equal("curation/desk/feed", Feed(("sort", "payout"), ("order", "desc")));
    }

    [Fact]
    public void UniqueImpliesRecommendedSoTheFlagIsRedundantThere()
    {
        Assert.Equal("curation/desk/feed?sort=unique", Feed(("sort", "unique"), ("recommended", "1")));
        Assert.Equal(Feed(("sort", "unique")), Feed(("sort", "unique"), ("recommended", "1")));
    }

    [Fact]
    public void CursorMustMatchTheOpaqueCursorGrammar()
    {
        Assert.Equal("curation/desk/feed?cursor=2026-09-05T10%3A00%3A00Z%3A123", Feed(("cursor", "2026-09-05T10:00:00Z:123")));
        Assert.Equal("curation/desk/feed?cursor=s%3Aabc_def.1%3A25", Feed(("cursor", "s:abc_def.1:25")));
        Assert.Equal("curation/desk/feed", Feed(("cursor", "a b")));
        Assert.Equal("curation/desk/feed", Feed(("cursor", "a/b")));
        Assert.Equal("curation/desk/feed", Feed(("cursor", "")));
        Assert.Equal("curation/desk/feed", Feed(("cursor", new string('a', 81))));
        Assert.Equal("curation/desk/feed?cursor=" + new string('a', 80), Feed(("cursor", new string('a', 80))));
    }

    [Fact]
    public void CommunityMustBeAHiveCommunityName()
    {
        Assert.Equal("curation/desk/feed?community=hive-125125", Feed(("community", "hive-125125")));
        Assert.Equal("curation/desk/feed?community=hive-12512", Feed(("community", "hive-12512")));
        Assert.Equal("curation/desk/feed", Feed(("community", "hive-1234")));
        Assert.Equal("curation/desk/feed", Feed(("community", "hive-1234567")));
        Assert.Equal("curation/desk/feed", Feed(("community", "photography")));
        Assert.Equal("curation/desk/feed", Feed(("community", "hive-125125/x")));
    }

    [Fact]
    public void ParametersAreEmittedInOneFixedOrderWhateverTheClientSent()
    {
        var a = Feed(("recommended", "1"), ("community", "hive-125125"), ("limit", "10"), ("view", "queue"),
            ("cursor", "c1"), ("window", "full"), ("app", "ecency"), ("sort", "queue"), ("has_images", "1"),
            ("rep_min", "30"), ("max_words", "800"), ("hide_curated", "0"), ("new_authors", "1"), ("rep_max", "75"),
            ("min_words", "100"));
        var b = Feed(("min_words", "100"), ("rep_max", "75"), ("new_authors", "1"), ("hide_curated", "0"),
            ("max_words", "800"), ("rep_min", "30"), ("has_images", "1"), ("sort", "queue"), ("app", "ecency"),
            ("window", "full"), ("cursor", "c1"), ("view", "queue"), ("limit", "10"), ("community", "hive-125125"),
            ("recommended", "1"));
        Assert.Equal(a, b);
        Assert.Equal(
            "curation/desk/feed?cursor=c1&limit=10&sort=queue&view=queue&app=ecency&community=hive-125125&window=full"
            + "&rep_min=30&rep_max=75&min_words=100&max_words=800&has_images=1&new_authors=1&recommended=1&hide_curated=0",
            a);

        // The order is the whitelist itself: nothing else can appear, and the
        // shared-cache key upstream of this service lists the same names.
        Assert.Equal(15, CurationDeskQuery.FeedOrder.Length);
        Assert.Equal(CurationDeskQuery.FeedOrder.Length, CurationDeskQuery.FeedOrder.Distinct().Count());
    }

    [Fact]
    public void ARepeatedKeyTakesItsFirstValue()
    {
        Assert.Equal("curation/desk/feed?limit=10", Feed(("limit", "10"), ("limit", "40")));
    }

    [Fact]
    public void RecommendationsAcceptOnlyCursorLimitAndTheirTwoSorts()
    {
        Assert.Equal("curation/desk/recommendations?sort=unique", Recommendations(("sort", "unique")));
        Assert.Equal("curation/desk/recommendations?sort=newest", Recommendations(("sort", "newest")));
        Assert.Equal("curation/desk/recommendations", Recommendations(("sort", "queue")));
        Assert.Equal("curation/desk/recommendations?cursor=abc&limit=50",
            Recommendations(("view", "all"), ("limit", "60"), ("cursor", "abc"), ("seed", "x")));
        Assert.Equal("curation/desk/recommendations?limit=10&sort=unique",
            Recommendations(("sort", "unique"), ("limit", "10")));
    }
}
