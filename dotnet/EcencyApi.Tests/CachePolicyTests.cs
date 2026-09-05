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
    public static TheoryData<string> AllPolicies() =>
        new()
        {
            CachePolicy.ProMembers, CachePolicy.Announcements, CachePolicy.PostTips,
            CachePolicy.CurationDeskFeed, CachePolicy.CurationDeskStatus, CachePolicy.CurationDeskRoster,
            CachePolicy.CurationDeskRecommendations, CachePolicy.CurationDeskPost,
        };

    public static TheoryData<string, int> DeskPolicies() =>
        new()
        {
            { CachePolicy.CurationDeskFeed, 30 },
            { CachePolicy.CurationDeskStatus, 15 },
            { CachePolicy.CurationDeskRoster, 600 },
            { CachePolicy.CurationDeskRecommendations, 30 },
            { CachePolicy.CurationDeskPost, 15 },
        };

    [Theory]
    [MemberData(nameof(AllPolicies))]
    public void EveryPolicyIsPubliclyCacheableWithAMaxAge(string policy)
    {
        Assert.StartsWith("public, max-age=", policy);
        Assert.DoesNotContain("no-store", policy);
        Assert.DoesNotContain("private", policy);
    }

    [Theory]
    [MemberData(nameof(AllPolicies))]
    public void APolicyOnlyAppliesToASuccessfulResponse(string policy)
    {
        Assert.Equal(policy, CachePolicy.ForStatus(200, policy));

        // Pipe() turns upstream transport failures into these after the handler
        // has already attached the policy. Caching one would keep a healthy
        // endpoint broken for the whole max-age.
        Assert.Null(CachePolicy.ForStatus(504, policy));
        Assert.Null(CachePolicy.ForStatus(500, policy));

        // Upstream error passthroughs must not be cached either.
        Assert.Null(CachePolicy.ForStatus(404, policy));
        Assert.Null(CachePolicy.ForStatus(401, policy));
        Assert.Null(CachePolicy.ForStatus(429, policy));
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
    public void DeskPoliciesRevalidateInTheBrowserAndAreSharedForTheirSMaxAge(string policy, int sMaxAge)
    {
        // max-age=0 makes every browser poll revalidate; s-maxage is what shared
        // caches and the in-process memo hold the body for.
        Assert.Equal(0, MaxAge(policy));
        Assert.Equal(sMaxAge, CachePolicy.SharedMaxAge(policy));
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
