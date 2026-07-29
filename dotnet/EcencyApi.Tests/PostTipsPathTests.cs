using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The tips handlers build an upstream path by interpolating caller-supplied
/// author and permlink values. Route values arrive percent-decoded and body
/// values are arbitrary strings, so anything structural left unescaped is
/// re-parsed when the string becomes a Uri — and the upstream call carries this
/// service's credentials, so a redirected path is a real problem, not a cosmetic
/// one.
/// </summary>
public class PostTipsPathTests
{
    [Fact]
    public void RealAuthorsAndPermlinksAreUnchanged()
    {
        // Hive names and permlinks are unreserved characters; escaping must be a
        // no-op for them or this would change every live request.
        Assert.Equal(
            "post-tips/good-karma/my-post-title-2026",
            PrivateApi.PostTipsPath("good-karma", "my-post-title-2026"));
        Assert.Equal(
            "post-tips/user.name/a_b-c.d~e",
            PrivateApi.PostTipsPath("user.name", "a_b-c.d~e"));
    }

    [Theory]
    // A slash would add path segments and address a different resource.
    [InlineData("a/b", "p")]
    [InlineData("a", "p/q")]
    // A question mark would truncate the path and turn the rest into a query.
    [InlineData("a?x=1", "p")]
    [InlineData("a", "p?x=1")]
    // A hash would truncate the path at a fragment.
    [InlineData("a#f", "p")]
    // Dots inside a longer segment are ordinary characters, not structure.
    [InlineData("x..y", "p")]
    public void StructuralCharactersCannotEscapeTheirSegment(string author, string permlink)
    {
        var path = PrivateApi.PostTipsPath(author, permlink);
        Assert.NotNull(path);

        // Exactly three segments: the prefix plus one each for author and permlink.
        Assert.Equal(3, path!.Split('/').Length);
        Assert.StartsWith("post-tips/", path);
        Assert.DoesNotContain("?", path);
        Assert.DoesNotContain("#", path);

        // And the whole thing still resolves to a path under post-tips rather
        // than somewhere else on the upstream host.
        var resolved = new Uri(new Uri("https://upstream.invalid/api/"), path);
        Assert.StartsWith("/api/post-tips/", resolved.AbsolutePath);
        Assert.Equal("", resolved.Query);
    }

    [Theory]
    [InlineData("..", "p")]
    [InlineData("a", "..")]
    [InlineData(".", "p")]
    [InlineData("a", ".")]
    public void DotSegmentsAreRejectedRatherThanEscaped(string author, string permlink)
    {
        // Escaping cannot neutralise these: `.` and `..` are unreserved so
        // EscapeDataString passes them through, and Uri decodes `%2E` back to `.`
        // before removing dot segments, so hand-encoding them resolves upward too.
        // Verified against Uri directly:
        Assert.Equal("/api/p", new Uri(new Uri("https://upstream.invalid/api/"), "post-tips/../p").AbsolutePath);
        Assert.Equal("/api/p", new Uri(new Uri("https://upstream.invalid/api/"), "post-tips/%2E%2E/p").AbsolutePath);

        Assert.Null(PrivateApi.PostTipsPath(author, permlink));
    }

    [Fact]
    public void EmptyAndMissingValuesStayInsideTheirSegment()
    {
        // Missing route params and absent body keys interpolate as "undefined";
        // both must remain a single inert segment.
        Assert.Equal("post-tips/undefined/undefined", PrivateApi.PostTipsPath("undefined", "undefined"));
        Assert.Equal(3, PrivateApi.PostTipsPath("", "")!.Split('/').Length);
    }
}
