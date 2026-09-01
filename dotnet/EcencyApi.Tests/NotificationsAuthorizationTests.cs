using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The authorization decision for /private-api/notifications, which had two defects:
/// a request could satisfy the guard with a `user` field and no valid code at all, and a
/// valid code was then overridden by that field anyway.
///
/// Kept separate from path construction because these are the rules that decide whose
/// data is served, and a regression here is silent rather than a visible break.
/// </summary>
public class NotificationsAuthorizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutAValidCodeTheRequestIsUnauthorized(string? validated)
    {
        // No code at all.
        var (username, fullScope) = PrivateApi.ResolveNotificationsTarget(validated, null);
        Assert.Null(username);
        Assert.False(fullScope);

        // THE BYPASS: naming an account used to be accepted in place of a code.
        var named = PrivateApi.ResolveNotificationsTarget(validated, "victim");
        Assert.Null(named.Username);
        Assert.False(named.FullScope);
    }

    [Fact]
    public void AValidCodeAloneServesThatAccountsCompleteFeed()
    {
        var (username, fullScope) = PrivateApi.ResolveNotificationsTarget("good-karma", null);

        Assert.Equal("good-karma", username);
        Assert.True(fullScope);
    }

    [Theory]
    [InlineData("good-karma")]
    // Hive names are lowercase, but the comparison must not hinge on that.
    [InlineData("Good-Karma")]
    [InlineData("GOOD-KARMA")]
    public void NamingYourOwnAccountIsStillASelfView(string requested)
    {
        var (username, fullScope) = PrivateApi.ResolveNotificationsTarget("good-karma", requested);

        Assert.Equal(requested, username);
        Assert.True(fullScope);
    }

    [Fact]
    public void NamingAnotherAccountIsServedTheRestrictedFeed()
    {
        // Still permitted: Decks builds notification columns for arbitrary accounts and
        // notifications are largely public. It just does not unlock the complete feed.
        var (username, fullScope) = PrivateApi.ResolveNotificationsTarget("good-karma", "someone-else");

        Assert.Equal("someone-else", username);
        Assert.False(fullScope);
    }

    [Fact]
    public void OnlyASelfViewEverSetsFullScope()
    {
        // The property that matters: for any requested account other than the validated
        // one, fullScope is false. A near-miss must not slip through.
        foreach (var other in new[] { "good-karm", "good-karma2", "ood-karma", " good-karma", "good_karma" })
        {
            Assert.False(
                PrivateApi.ResolveNotificationsTarget("good-karma", other).FullScope,
                other);
        }
    }
}
