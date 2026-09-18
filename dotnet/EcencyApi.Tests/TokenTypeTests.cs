using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// A private-API session is a token issued to the Ecency app: a "code" the
/// apps sign with a key they hold, or the "posting" token HiveSigner gives for
/// one. Anything else signed by the same keys is refused before any key
/// lookup: another app's token, a sign-in proof typed "login", a signed
/// message with no type at all.
/// </summary>
public class TokenTypeTests
{
    private static JsonNode? Parse(string json) => JsonNode.Parse(json);

    [Theory]
    [InlineData("""{"type":"posting","app":"ecency.app"}""")]
    [InlineData("""{"type":"code","app":"ecency.app"}""")]
    [InlineData("""{"app":"ecency.app","type":"code"}""")]
    [InlineData("""{"type":"code","app":"ecency.app","extra":1}""")]
    public void EcencyCodesAndPostingTokensAreSessions(string signedMessage)
    {
        Assert.True(PrivateApi.IsEcencySession(Parse(signedMessage)));
    }

    [Theory]
    [InlineData("""{"type":"login","app":"ecency.app"}""")]
    [InlineData("""{"type":"login","app":"ecency.app","audience":"someapp://callback"}""")]
    [InlineData("""{"type":"offline","app":"ecency.app"}""")]
    [InlineData("""{"type":"refresh","app":"ecency.app"}""")]
    [InlineData("""{"type":"Posting","app":"ecency.app"}""")]
    [InlineData("""{"type":"posting","app":"another.app"}""")]
    [InlineData("""{"type":"code","app":"another.app"}""")]
    [InlineData("""{"type":"posting","app":"Ecency.app"}""")]
    [InlineData("""{"type":"posting"}""")]
    [InlineData("""{"app":"ecency.app"}""")]
    [InlineData("""{"message":"hello"}""")]
    [InlineData("""{"type":"posting","app":null}""")]
    [InlineData("""{"type":null,"app":"ecency.app"}""")]
    [InlineData("""{"type":1,"app":"ecency.app"}""")]
    [InlineData("""{"type":["posting"],"app":"ecency.app"}""")]
    [InlineData("""{"type":"posting","app":["ecency.app"]}""")]
    [InlineData("""["posting","ecency.app"]""")]
    [InlineData("\"posting\"")]
    [InlineData("null")]
    public void EverythingElseIsRefused(string signedMessage)
    {
        Assert.False(PrivateApi.IsEcencySession(Parse(signedMessage)));
    }
}
