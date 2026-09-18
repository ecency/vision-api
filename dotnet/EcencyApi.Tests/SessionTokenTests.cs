using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using NBitcoin.Secp256k1;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// ValidateCode end to end, with real signatures: only a code or posting token
/// issued to the Ecency app is a session, whether the user's own key signed it
/// or @hivesigner did when it exchanged a code, and the rest is refused before
/// any account is read.
/// </summary>
[Collection("session-token")]
public class SessionTokenTests : IDisposable
{
    private static readonly ECPrivKey Key = HiveCrypto.FromLogin("alice", "test-password");
    private static readonly string Pub = HiveCrypto.PublicKeyFromLogin("alice", "test-password");
    // The key @hivesigner signs the tokens it exchanges codes for.
    private static readonly ECPrivKey HsKey = HiveCrypto.FromLogin("hivesigner", "test-password");
    private static readonly string HsPub = HiveCrypto.PublicKeyFromLogin("hivesigner", "test-password");
    private readonly List<string?> _reads = new();

    public SessionTokenTests()
    {
        PrivateApi.ResetHivesignerCache();
        PrivateApi.ValidationAccounts = names =>
        {
            var name = names.Single();
            _reads.Add(name);
            var account = new JsonObject
            {
                ["name"] = name,
                ["posting"] = new JsonObject
                {
                    ["key_auths"] = new JsonArray(new JsonArray(name == "hivesigner" ? HsPub : Pub, 1)),
                },
            };
            return Task.FromResult<JsonArray?>(new JsonArray(account));
        };
    }

    public void Dispose()
    {
        PrivateApi.ValidationAccounts = names => HiveClients.Default.GetAccounts(names);
        PrivateApi.ResetHivesignerCache();
    }

    /// <summary>A request body carrying `signedMessage` for alice, signed by `key`.</summary>
    private static JsonObject Body(string signedMessage, ECPrivKey? key = null)
    {
        var message = new JsonObject
        {
            ["signed_message"] = JsonNode.Parse(signedMessage),
            ["authors"] = new JsonArray("alice"),
            ["timestamp"] = 1726650000,
        };
        var digest = HiveCrypto.Sha256Utf8(JsJson.Stringify(message));
        message["signatures"] = new JsonArray(HiveCrypto.Sign(key ?? Key, digest));
        return new JsonObject { ["code"] = B64u.Encode(JsJson.Stringify(message)) };
    }

    [Theory]
    [InlineData("""{"type":"posting","app":"ecency.app"}""")]
    [InlineData("""{"type":"code","app":"ecency.app"}""")]
    public async Task EcencyTokensAreSessions(string signedMessage)
    {
        Assert.Equal("alice", await PrivateApi.ValidateCode(Body(signedMessage)));
        Assert.Equal(new[] { "alice" }, _reads);
    }

    [Fact]
    public async Task APostingTokenHivesignerExchangedIsASession()
    {
        // What web and mobile hold: HiveSigner's posting token for the Ecency
        // app, signed by @hivesigner rather than by the user's own key.
        var body = Body("""{"type":"posting","app":"ecency.app"}""", HsKey);
        Assert.Equal("alice", await PrivateApi.ValidateCode(body));
        Assert.Equal(new[] { "alice", "hivesigner" }, _reads);
    }

    [Fact]
    public async Task HivesignerSignedTokensForOtherAppsAreNot()
    {
        var body = Body("""{"type":"posting","app":"another.app"}""", HsKey);
        Assert.Null(await PrivateApi.ValidateCode(body));
        Assert.Empty(_reads);
    }

    [Theory]
    [InlineData("""{"message":"hello"}""")]
    [InlineData("""{"type":"login","app":"ecency.app"}""")]
    [InlineData("""{"type":"posting","app":"another.app"}""")]
    public async Task OtherSignedMessagesAreNot(string signedMessage)
    {
        Assert.Null(await PrivateApi.ValidateCode(Body(signedMessage)));
        Assert.Empty(_reads);
    }
}

[CollectionDefinition("session-token", DisableParallelization = true)]
public class SessionTokenCollection
{
}
