using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The cache policies are a public contract: `public` lets shared caches store a
/// response, and the max-age is how long a wrong one would stay wrong. Pin both
/// the opt-in rule and the shape of each policy.
/// </summary>
public class CachePolicyTests
{
    // Keyed by route: several routes share a policy string, and a theory row
    // that repeats its arguments is a duplicate test id that xUnit reports as a
    // skip rather than running.
    public static TheoryData<string, string> AllPolicies() =>
        new()
        {
            { "pro-members", CachePolicy.ProMembers },
            { "announcements", CachePolicy.Announcements },
            { "post-tips", CachePolicy.PostTips },
            { "desk-feed", CachePolicy.CurationDeskFeed },
            { "desk-status", CachePolicy.CurationDeskStatus },
            { "desk-roster", CachePolicy.CurationDeskRoster },
            { "desk-recommendations", CachePolicy.CurationDeskRecommendations },
            { "desk-post", CachePolicy.CurationDeskPost },
            { "desk-recommender", CachePolicy.CurationDeskRecommender },
        };

    public static TheoryData<string, string, int> DeskPolicies() =>
        new()
        {
            { "desk-feed", CachePolicy.CurationDeskFeed, 30 },
            { "desk-status", CachePolicy.CurationDeskStatus, 15 },
            { "desk-roster", CachePolicy.CurationDeskRoster, 600 },
            { "desk-recommendations", CachePolicy.CurationDeskRecommendations, 30 },
            { "desk-post", CachePolicy.CurationDeskPost, 15 },
            { "desk-recommender", CachePolicy.CurationDeskRecommender, 60 },
        };

    [Theory]
    [MemberData(nameof(AllPolicies))]
    public void EveryPolicyIsPubliclyCacheableWithAMaxAge(string route, string policy)
    {
        Assert.True(policy.StartsWith("public, max-age=", StringComparison.Ordinal), route + ": " + policy);
        Assert.DoesNotContain("no-store", policy);
        Assert.DoesNotContain("private", policy);
    }

    [Theory]
    [MemberData(nameof(AllPolicies))]
    public void APolicyOnlyAppliesToASuccessfulResponse(string route, string policy)
    {
        Assert.Equal(policy, CachePolicy.ForStatus(200, policy));

        // Pipe() turns upstream transport failures into 504/500 after the
        // handler has already attached the policy, and an upstream error
        // passthrough keeps its own status. Caching either would keep a healthy
        // endpoint broken for the whole max-age.
        foreach (var status in new[] { 504, 500, 404, 401, 429 })
        {
            Assert.True(CachePolicy.ForStatus(status, policy) == null, route + " " + status);
        }
    }

    [Fact]
    public void AnnouncementsOutliveTheOtherPolicies()
    {
        // Announcements are a compile-time constant, so they can only change on a
        // deploy; the proxied endpoints track data that moves independently.
        Assert.True(MaxAge(CachePolicy.Announcements) > MaxAge(CachePolicy.ProMembers));
        Assert.True(MaxAge(CachePolicy.ProMembers) > MaxAge(CachePolicy.PostTips));
    }

    [Theory]
    [MemberData(nameof(DeskPolicies))]
    public void DeskPoliciesRevalidateInTheBrowserAndAreSharedForTheirSMaxAge(string route, string policy, int sMaxAge)
    {
        // max-age=0 makes every browser poll revalidate; s-maxage is what shared
        // caches and the in-process memo hold the body for.
        Assert.True(MaxAge(policy) == 0, route);
        Assert.True(CachePolicy.SharedMaxAge(policy) == sMaxAge, route);
        Assert.DoesNotContain("stale-while-revalidate", policy);
    }

    [Theory]
    [MemberData(nameof(DeskPolicies))]
    public void AnAgedPolicyOffersOnlyTheRestOfTheSharedWindow(string route, string policy, int sMaxAge)
    {
        // A body served from the in-process memo has already spent part of its
        // life there; handing a shared cache a fresh window would let the two
        // layers serve one answer for a lifetime each, in series.
        Assert.Equal(policy, CachePolicy.Aged(policy, 0));
        Assert.Equal(sMaxAge - 1, CachePolicy.SharedMaxAge(CachePolicy.Aged(policy, 1)));

        foreach (var age in new[] { 1, sMaxAge - 1, sMaxAge, sMaxAge + 1, sMaxAge * 10 })
        {
            var aged = CachePolicy.Aged(policy, age);
            var remaining = CachePolicy.SharedMaxAge(aged);

            // Floored at a second, never longer than what is left, and still a
            // policy of the same shape.
            Assert.True(remaining >= 1, route + " " + age);
            Assert.True(remaining <= Math.Max(1, sMaxAge - age), route + " " + age);
            Assert.StartsWith("public, max-age=0, s-maxage=", aged);
            Assert.Null(CachePolicy.ForStatus(504, aged));
        }
    }

    [Theory]
    [MemberData(nameof(DeskPolicies))]
    public void TheStalePolicyIsShortAndNeverLongerThanTheRouteItself(string route, string policy, int sMaxAge)
    {
        // Served because the upstream call failed: still cacheable, so a burst
        // does not all queue behind a struggling backend, but only for seconds.
        var stale = CachePolicy.Stale(policy);
        Assert.Equal("public, max-age=0, s-maxage=" + CachePolicy.StaleSharedMaxAge, stale);
        Assert.True(CachePolicy.SharedMaxAge(stale) < sMaxAge, route);
        Assert.Null(CachePolicy.ForStatus(500, stale));
    }

    [Fact]
    public void ShorteningAPolicyLeavesItsOtherDirectivesAlone()
    {
        // A policy whose shared window is its max-age gains an s-maxage rather
        // than having what browsers were told rewritten under them.
        var aged = CachePolicy.Aged(CachePolicy.ProMembers, 100);
        Assert.StartsWith(CachePolicy.ProMembers, aged);
        Assert.Contains("stale-while-revalidate=3600", aged);
        Assert.Equal(500, CachePolicy.SharedMaxAge(aged));
    }

    [Fact]
    public void SharedMaxAgeFallsBackToMaxAgeWhenAPolicyHasNoSharedDirective()
    {
        Assert.Equal(600, CachePolicy.SharedMaxAge(CachePolicy.ProMembers));
        Assert.Equal(60, CachePolicy.SharedMaxAge(CachePolicy.PostTips));
        Assert.Throws<ArgumentException>(() => CachePolicy.SharedMaxAge("public"));
    }

    private static int MaxAge(string policy)
    {
        var token = policy.Split(',').Select(p => p.Trim())
            .Single(p => p.StartsWith("max-age=", StringComparison.Ordinal));
        return int.Parse(token["max-age=".Length..]);
    }
}
