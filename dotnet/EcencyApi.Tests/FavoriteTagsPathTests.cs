using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The favorite-tag check and delete handlers interpolate a body-supplied tag into
/// an upstream path. A tag is an arbitrary string, so anything structural left
/// unescaped is re-parsed when the string becomes a Uri, and the upstream call
/// carries this service's credentials. Same reasoning as NotificationsPathTests.
/// </summary>
public class FavoriteTagsPathTests
{
    [Fact]
    public void RealRequestsAreUnchanged()
    {
        // Hive names and tags are unreserved characters; escaping must be a no-op for
        // them or this would change every live request.
        Assert.Equal(
            "isfavoritetag/good-karma/photography",
            PrivateApi.FavoriteTagPath("isfavoritetag", "good-karma", "photography"));
        Assert.Equal(
            "favoriteTag/user.name/contest-2026",
            PrivateApi.FavoriteTagPath("favoriteTag", "user.name", "contest-2026"));
    }

    [Fact]
    public void LeadingHashIsEscapedNotDropped()
    {
        // Raw, `#` would truncate the path at a fragment. Escaped, it reaches the
        // upstream, which normalises the tag and strips one leading hash itself.
        Assert.Equal(
            "isfavoritetag/good-karma/%23photography",
            PrivateApi.FavoriteTagPath("isfavoritetag", "good-karma", "#photography"));
    }

    [Fact]
    public void MissingTagIsTheLiteralUndefinedSegment()
    {
        // TemplateField renders an absent body field as "undefined", the same way the
        // favorites handlers do for a missing account; it stays one plain segment.
        Assert.Equal(
            "favoriteTag/good-karma/undefined",
            PrivateApi.FavoriteTagPath("favoriteTag", "good-karma", "undefined"));
    }

    [Theory]
    // A slash would add path segments and address a different resource.
    [InlineData("a", "b/c")]
    [InlineData("a/b", "c")]
    // A question mark would truncate the path and turn the rest into a query.
    [InlineData("a", "b?x=1")]
    // A hash would truncate the path at a fragment.
    [InlineData("a", "b#f")]
    // Whitespace and non-ASCII must not leak into the request line either.
    [InlineData("a", "b c")]
    [InlineData("a", "caf\u00e9")]
    public void StructuralCharactersCannotEscapeTheirSegment(string username, string tag)
    {
        var path = PrivateApi.FavoriteTagPath("isfavoritetag", username, tag);

        Assert.NotNull(path);
        Assert.DoesNotContain("?x=1", path);
        Assert.DoesNotContain("#f", path);
        Assert.DoesNotContain(" ", path);
        Assert.True(path!.All(c => c < 128));
        // The only separators left are the two this builder wrote itself.
        Assert.Equal(2, path.Split('/').Length - 1);
    }

    [Theory]
    // Dot segments cannot be fixed by escaping: Uri decodes %2E back to `.` before it
    // removes dot segments, so they have to be rejected outright.
    [InlineData(".", "photography")]
    [InlineData("..", "photography")]
    [InlineData("good-karma", ".")]
    [InlineData("good-karma", "..")]
    public void DotSegmentsAreRejected(string username, string tag)
    {
        Assert.Null(PrivateApi.FavoriteTagPath("isfavoritetag", username, tag));
    }
}
