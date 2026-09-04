using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// A HiveSigner-style message typed "login" proves who signed it and nothing
/// more: HiveSigner answers /api/me for one and refuses it everywhere else, and
/// the Ecency clients never send one (wallet logins send "code", HiveSigner
/// issues "posting" for the scopes they request). Token validation refuses it
/// before any key lookup, and leaves every other shape to the signature checks.
/// </summary>
public class TokenTypeTests
{
    private static JsonNode? Parse(string json) => JsonNode.Parse(json);

    [Fact]
    public void LoginTypedMessageIsRefused()
    {
        Assert.True(PrivateApi.IsLoginOnlyMessage(Parse("""{"type":"login","app":"ecency.app"}""")));
        Assert.True(PrivateApi.IsLoginOnlyMessage(
            Parse("""{"type":"login","app":"ecency.app","audience":"honeyback://hive"}""")));
    }

    [Theory]
    [InlineData("""{"type":"code","app":"ecency.app"}""")]
    [InlineData("""{"type":"posting","app":"ecency.app"}""")]
    [InlineData("""{"type":"offline","app":"ecency.app"}""")]
    [InlineData("""{"type":"refresh","app":"ecency.app"}""")]
    [InlineData("""{"type":"Login","app":"ecency.app"}""")]
    [InlineData("""{"app":"ecency.app"}""")]
    [InlineData("""{"type":null}""")]
    [InlineData("""{"type":1}""")]
    [InlineData("""{"type":["login"]}""")]
    [InlineData("""["login"]""")]
    [InlineData("null")]
    public void EverythingElseIsLeftToTheSignatureChecks(string signedMessage)
    {
        Assert.False(PrivateApi.IsLoginOnlyMessage(Parse(signedMessage)));
    }
}
