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
        };

    public static TheoryData<string, string, int> DeskPolicies() =>
        new()
        {
            { "desk-feed", CachePolicy.CurationDeskFeed, 30 },
            { "desk-status", CachePolicy.CurationDeskStatus, 15 },
            { "desk-roster", CachePolicy.CurationDeskRoster, 600 },
            { "desk-recommendations", CachePolicy.CurationDeskRecommendations, 30 },
            { "desk-post", CachePolicy.CurationDeskPost, 15 },
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
