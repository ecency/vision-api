using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The notifications handler builds an upstream path by interpolating four
/// caller-supplied body values: the account name, the filter, a paging cursor and a
/// limit. Body values are arbitrary strings, so anything structural left unescaped is
/// re-parsed when the string becomes a Uri — and the upstream call carries this
/// service's credentials, so a redirected path is a real problem. Same reasoning as
/// PostTipsPathTests.
/// </summary>
public class NotificationsPathTests
{
    [Fact]
    public void RealRequestsAreUnchanged()
    {
        // Hive names, filter names, notification ids and integer limits are all
        // unreserved characters; escaping must be a no-op for them or this would
        // change every live request.
        Assert.Equal(
            "activities/good-karma",
            PrivateApi.NotificationsPath("good-karma", null, null, null, false));
        Assert.Equal(
            "follows/good-karma",
            PrivateApi.NotificationsPath("good-karma", "follows", null, null, false));
        Assert.Equal(
            "activities/user.name?since=f-179530372",
            PrivateApi.NotificationsPath("user.name", null, "f-179530372", null, false));
        Assert.Equal(
            "follows/good-karma?since=f-179530372&limit=50",
            PrivateApi.NotificationsPath("good-karma", "follows", "f-179530372", "50", false));
        Assert.Equal(
            "activities/good-karma?limit=50",
            PrivateApi.NotificationsPath("good-karma", null, null, "50", false));
    }

    [Theory]
    // A slash would add path segments and address a different resource. This is the
    // shape that made the nginx per-path allowlist load-bearing rather than routing
    // hygiene: `unread-count?x=` as a username reached a different upstream endpoint.
    [InlineData("a/b", null)]
    [InlineData("a", "b/c")]
    // A question mark would truncate the path and turn the rest into a query.
    [InlineData("a?x=1", null)]
    [InlineData("a", "b?x=1")]
    // A hash would truncate the path at a fragment.
    [InlineData("a#f", null)]
    [InlineData("a", "b#f")]
    public void StructuralCharactersCannotEscapeTheirSegment(string username, string? filter)
    {
        var path = PrivateApi.NotificationsPath(username, filter, null, null, false);

        Assert.NotNull(path);
        Assert.DoesNotContain("?x=1", path);
        Assert.DoesNotContain("#f", path);
        // The only separators left are the ones this builder wrote itself.
        Assert.Equal(1, path!.Split('/').Length - 1);
    }

    [Theory]
    // Dot segments cannot be fixed by escaping: Uri decodes %2E back to `.` before it
    // removes dot segments, so they have to be rejected outright.
    [InlineData(".", null)]
    [InlineData("..", null)]
    [InlineData("a", ".")]
    [InlineData("a", "..")]
    public void DotSegmentsAreRejected(string username, string? filter)
    {
        Assert.Null(PrivateApi.NotificationsPath(username, filter, null, null, false));
    }

    [Fact]
    public void QueryValuesCannotAddParameters()
    {
        // A cursor or limit carrying `&` would otherwise append parameters of its own.
        var path = PrivateApi.NotificationsPath("good-karma", null, "a&limit=999", null, false);
        Assert.Equal("activities/good-karma?since=a%26limit%3D999", path);

        var withLimit = PrivateApi.NotificationsPath("good-karma", null, null, "1&x=2", false);
        Assert.Equal("activities/good-karma?limit=1%26x%3D2", withLimit);
    }

    [Fact]
    public void LimitJoinsWithAmpersandOnlyWhenSinceIsPresent()
    {
        // Preserves the original branching: limit rides `&` when since is present and
        // `?` when it is not, so an existing client's paging URLs do not change shape.
        Assert.Equal(
            "activities/x?since=s&limit=10",
            PrivateApi.NotificationsPath("x", null, "s", "10", false));
        Assert.Equal(
            "activities/x?limit=10",
            PrivateApi.NotificationsPath("x", null, null, "10", false));
    }

    [Fact]
    public void FullScopeIsAppendedOnlyForASelfView()
    {
        // Omitting the flag is the SAFE direction: enotify defaults to chain-derived
        // activity only, so a cross-account view needs no parameter at all.
        Assert.Equal(
            "activities/good-karma",
            PrivateApi.NotificationsPath("good-karma", null, null, null, false));

        Assert.Equal(
            "activities/good-karma?scope=full",
            PrivateApi.NotificationsPath("good-karma", null, null, null, true));
    }

    [Fact]
    public void FullScopeJoinsCorrectlyWithExistingQueryValues()
    {
        Assert.Equal(
            "follows/good-karma?since=f-179530372&limit=50&scope=full",
            PrivateApi.NotificationsPath("good-karma", "follows", "f-179530372", "50", true));

        Assert.Equal(
            "activities/good-karma?limit=50&scope=full",
            PrivateApi.NotificationsPath("good-karma", null, null, "50", true));

        Assert.Equal(
            "activities/good-karma?since=s&scope=full",
            PrivateApi.NotificationsPath("good-karma", null, "s", null, true));
    }

    [Fact]
    public void ACallerCannotForgeTheScopeParameter()
    {
        // scope is decided by the handler from the validated code, never read from the
        // body. A value trying to smuggle its own parameter is escaped into a literal,
        // and the real one is appended last regardless.
        var path = PrivateApi.NotificationsPath("good-karma", null, "s&scope=full", null, false);

        Assert.Equal("activities/good-karma?since=s%26scope%3Dfull", path);
        Assert.DoesNotContain("&scope=full", path);
    }
}
