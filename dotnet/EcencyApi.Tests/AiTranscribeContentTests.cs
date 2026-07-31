using System.Text;
using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The transcription route is the only private-api endpoint that forwards a file, so it
/// is the only one building a multipart body by hand. Two things about that body have
/// consequences beyond a failed request:
///
/// `us` decides whose Points upstream burns. It has to be the caller resolved from the
/// signed code; taking it from the request would let anyone spend anyone else's balance.
///
/// The multipart boundary is generated, so a Content-Type set anywhere else silently
/// makes the body unparseable upstream and surfaces only as a confusing 400.
/// </summary>
public class AiTranscribeContentTests
{
    private static MultipartFormDataContent Build(
        string username = "good-karma",
        string durationMs = "30000",
        string? idem = "abcd1234efgh",
        string? fileName = "clip.m4a",
        string? contentType = "audio/mp4")
    {
        var audio = new MemoryStream(Encoding.UTF8.GetBytes("fake audio bytes"));
        return PrivateApi.BuildTranscribeContent(
            username, durationMs, idem, audio, fileName, contentType);
    }

    private static async Task<string> Render(MultipartFormDataContent content)
    {
        return await content.ReadAsStringAsync();
    }

    [Fact]
    public async Task UsIsTheAuthenticatedCallerNotAClientValue()
    {
        using var content = Build(username: "good-karma");
        var body = await Render(content);

        Assert.Contains("name=us", body.Replace("\"", ""));
        Assert.Contains("good-karma", body);
    }

    [Fact]
    public async Task CarriesDurationAndIdempotencyKey()
    {
        using var content = Build(durationMs: "45000", idem: "abcd1234efgh");
        var body = await Render(content);

        Assert.Contains("45000", body);
        Assert.Contains("abcd1234efgh", body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OmitsAnEmptyIdempotencyKeyRatherThanSendingBlank(string? idem)
    {
        // Upstream validates the key against [A-Za-z0-9_-]{8,64}; a blank one is a 400,
        // whereas an absent one is simply an un-deduplicated request.
        using var content = Build(idem: idem);
        var body = await Render(content);

        Assert.DoesNotContain("idempotency_key", body);
    }

    [Fact]
    public async Task SendsTheAudioPartWithItsFilename()
    {
        using var content = Build(fileName: "clip.m4a");
        var body = await Render(content);

        Assert.Contains("name=audio", body.Replace("\"", ""));
        Assert.Contains("clip.m4a", body);
        Assert.Contains("fake audio bytes", body);
    }

    [Fact]
    public async Task FallsBackToAFilenameWhenTheClientSendsNone()
    {
        using var content = Build(fileName: null);
        var body = await Render(content);

        Assert.Contains("name=audio", body.Replace("\"", ""));
    }

    [Fact]
    public void ContentTypeCarriesAGeneratedBoundary()
    {
        using var content = Build();

        var mediaType = content.Headers.ContentType;
        Assert.NotNull(mediaType);
        Assert.Equal("multipart/form-data", mediaType!.MediaType);

        var boundary = mediaType.Parameters
            .FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(boundary);
        Assert.False(string.IsNullOrWhiteSpace(boundary!.Value));
    }

    [Fact]
    public async Task TolerantOfAMissingAudioContentType()
    {
        // expo-audio and MediaRecorder do not always label the part.
        using var content = Build(contentType: null);
        var body = await Render(content);

        Assert.Contains("fake audio bytes", body);
    }

    [Theory]
    // A bare token with no subtype, a trailing separator, and a broken parameter --
    // all things a client can put in a multipart part header.
    [InlineData("audio")]
    [InlineData("audio/")]
    [InlineData("audio/mp4;")]
    [InlineData("audio/mp4; codecs=")]
    [InlineData("not a media type at all")]
    [InlineData("///")]
    public async Task AMalformedContentTypeIsDroppedRatherThanThrowing(string contentType)
    {
        // MediaTypeHeaderValue.Parse throws FormatException on these, and the throw
        // would escape the handler's form-reading catch and become a 500 from the
        // global middleware. The upload itself is fine, so the label is dropped and
        // the request proceeds.
        var ex = Record.Exception(() =>
        {
            using var content = Build(contentType: contentType);
        });
        Assert.Null(ex);

        using var built = Build(contentType: contentType);
        var body = await Render(built);
        Assert.Contains("fake audio bytes", body);
    }

    [Fact]
    public async Task AValidContentTypeIsStillForwarded()
    {
        // The permissive path above must not have made this a no-op.
        using var content = Build(contentType: "audio/mp4");
        var body = await Render(content);

        Assert.Contains("audio/mp4", body);
    }
}
