using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Microsoft.AspNetCore.Http;
using NBitcoin.Secp256k1;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// A session code whose keys could not be read is a server error, not a refused
/// session: the request answers 503 through the pipeline and the handler goes no
/// further. A code the chain does answer for (unknown account, wrong key) is
/// still a 401. Clients sign the user out on 401, so a node outage answered as
/// 401 signed everyone out.
/// </summary>
[Collection("session-token")]
public class TokenCheckUnavailableTests : IDisposable
{
    private static readonly ECPrivKey Key = HiveCrypto.FromLogin("alice", "test-password");
    private static readonly string Pub = HiveCrypto.PublicKeyFromLogin("alice", "test-password");
    private static readonly ECPrivKey HsKey = HiveCrypto.FromLogin("hivesigner", "test-password");
    private static readonly string HsPub = HiveCrypto.PublicKeyFromLogin("hivesigner", "test-password");
    private static readonly ECPrivKey OtherKey = HiveCrypto.FromLogin("mallory", "test-password");

    public TokenCheckUnavailableTests() => PrivateApi.ResetHivesignerCache();

    public void Dispose()
    {
        PrivateApi.ValidationAccounts = names => HiveClients.Default.GetAccounts(names);
        PrivateApi.ResetHivesignerCache();
    }

    private static JsonObject Account(string name, string pub) => new()
    {
        ["name"] = name,
        ["posting"] = new JsonObject { ["key_auths"] = new JsonArray(new JsonArray(pub, 1)) },
    };

    private static JsonArray Known(string? name) =>
        new(Account(name!, name == "hivesigner" ? HsPub : Pub));

    private static string Code(ECPrivKey key)
    {
        var message = new JsonObject
        {
            ["signed_message"] = new JsonObject { ["type"] = "code", ["app"] = "ecency.app" },
            ["authors"] = new JsonArray("alice"),
            ["timestamp"] = 1726650000,
        };
        var digest = HiveCrypto.Sha256Utf8(JsJson.Stringify(message));
        message["signatures"] = new JsonArray(HiveCrypto.Sign(key, digest));
        return B64u.Encode(JsJson.Stringify(message));
    }

    private static async Task<(int Status, string Body, string RetryAfter)> Run(
        RequestDelegate handler, JsonObject body)
    {
        var ctx = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();

        await ServerErrors.Handle(ctx, () => handler(ctx));

        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, text, ctx.Response.Headers.RetryAfter.ToString());
    }

    private static Task<(int Status, string Body, string RetryAfter)> Unread(ECPrivKey key) =>
        Run(PrivateApi.UnreadNotifications, new JsonObject { ["code"] = Code(key) });

    // A spend: reaching the upstream would charge Points.
    private static Task<(int Status, string Body, string RetryAfter)> StreakFreezeBuy(ECPrivKey key) =>
        Run(PrivateApi.StreakFreezeBuy,
            new JsonObject { ["code"] = Code(key), ["idempotency_key"] = "k-1" });

    private static void AssertUnavailable((int Status, string Body, string RetryAfter) r)
    {
        Assert.Equal(503, r.Status);
        // Only the 503 was written: the handler stopped before any upstream call.
        Assert.Equal("Service Unavailable", r.Body);
        Assert.Equal(ServerErrors.RetryAfterSeconds.ToString(), r.RetryAfter);
    }

    [Fact]
    public async Task ANodeFailureIsUnavailableNotUnauthorized()
    {
        PrivateApi.ValidationAccounts = _ => throw new HttpRequestException("node down");

        AssertUnavailable(await Unread(Key));
        AssertUnavailable(await StreakFreezeBuy(Key));
    }

    [Fact]
    public async Task ATimeoutIsUnavailable()
    {
        PrivateApi.ValidationAccounts = _ => throw new TaskCanceledException("timed out");

        AssertUnavailable(await Unread(Key));
    }

    [Fact]
    public async Task ANonIterableAnswerIsUnavailable()
    {
        PrivateApi.ValidationAccounts = _ => Task.FromResult<JsonArray?>(null);

        AssertUnavailable(await Unread(Key));
        AssertUnavailable(await StreakFreezeBuy(Key));
    }

    [Fact]
    public async Task AnAccountWithoutPostingKeysIsAGarbageAnswer()
    {
        PrivateApi.ValidationAccounts = _ =>
            Task.FromResult<JsonArray?>(new JsonArray(new JsonObject { ["name"] = "alice" }));

        AssertUnavailable(await Unread(Key));
    }

    [Fact]
    public async Task AFailedHivesignerReadWithNothingCachedIsUnavailable()
    {
        // alice's own key does not match, so @hivesigner's keys decide; they cannot be read.
        PrivateApi.ValidationAccounts = names =>
            names.Single() == "hivesigner"
                ? throw new HttpRequestException("node down")
                : Task.FromResult<JsonArray?>(Known("alice"));

        AssertUnavailable(await Unread(HsKey));
        AssertUnavailable(await StreakFreezeBuy(HsKey));
    }

    [Fact]
    public async Task AGarbageHivesignerAnswerIsUnavailableAndNotCached()
    {
        PrivateApi.ValidationAccounts = names =>
            Task.FromResult<JsonArray?>(names.Single() == "hivesigner"
                ? new JsonArray(new JsonObject { ["name"] = "hivesigner" })
                : Known("alice"));
        AssertUnavailable(await Unread(HsKey));

        // The node recovers: the next check reads @hivesigner again.
        PrivateApi.ValidationAccounts = names => Task.FromResult<JsonArray?>(Known(names.Single()));
        Assert.Equal("alice", await PrivateApi.ValidateCode(new JsonObject { ["code"] = Code(HsKey) }));
    }

    [Fact]
    public async Task AFailedHivesignerRefreshKeepsTheStaleKeys()
    {
        PrivateApi.ValidationAccounts = names => Task.FromResult<JsonArray?>(Known(names.Single()));
        Assert.Equal("alice", await PrivateApi.ValidateCode(new JsonObject { ["code"] = Code(HsKey) }));

        // Due for refresh, still inside the stale grace.
        PrivateApi.AgeHivesignerCache(PrivateApi.HiveSignerStaleMaxMs - 60_000);
        PrivateApi.ValidationAccounts = names =>
            names.Single() == "hivesigner"
                ? throw new HttpRequestException("node down")
                : Task.FromResult<JsonArray?>(Known("alice"));

        Assert.Equal("alice", await PrivateApi.ValidateCode(new JsonObject { ["code"] = Code(HsKey) }));
    }

    private static Func<IEnumerable<string?>, Task<JsonArray?>> HivesignerDown => names =>
        names.Single() == "hivesigner"
            ? throw new HttpRequestException("node down")
            : Task.FromResult<JsonArray?>(Known("alice"));

    [Fact]
    public async Task AMismatchAgainstStaleKeysAfterAFailedRefreshIsUnavailable()
    {
        // Cached @hivesigner keys that no longer sign (HiveSigner rotated its key).
        PrivateApi.ValidationAccounts = names =>
            Task.FromResult<JsonArray?>(names.Single() == "hivesigner"
                ? new JsonArray(Account("hivesigner", Pub.Replace(Pub[^1], Pub[^1] == 'A' ? 'B' : 'A')))
                : Known("alice"));
        Assert.Null(await PrivateApi.ValidateCode(new JsonObject { ["code"] = Code(HsKey) }));

        // The refresh that would have read the new key fails: no verdict either way.
        PrivateApi.AgeHivesignerCache(PrivateApi.HiveSignerStaleMaxMs - 60_000);
        PrivateApi.ValidationAccounts = HivesignerDown;
        AssertUnavailable(await Unread(HsKey));
    }

    [Fact]
    public async Task StaleHivesignerKeysPastTheGraceAreNotUsed()
    {
        PrivateApi.ValidationAccounts = names => Task.FromResult<JsonArray?>(Known(names.Single()));
        Assert.Equal("alice", await PrivateApi.ValidateCode(new JsonObject { ["code"] = Code(HsKey) }));

        PrivateApi.AgeHivesignerCache(PrivateApi.HiveSignerStaleMaxMs + 60_000);
        PrivateApi.ValidationAccounts = HivesignerDown;
        AssertUnavailable(await Unread(HsKey));
    }

    [Theory]
    [InlineData("[[123, 1]]")]
    [InlineData("[[\"\", 1]]")]
    [InlineData("[[null, 1]]")]
    public async Task AnAccountKeyThatIsNotAKeyIsAGarbageAnswer(string keyAuths)
    {
        PrivateApi.ValidationAccounts = _ => Task.FromResult<JsonArray?>(new JsonArray(new JsonObject
        {
            ["name"] = "alice",
            ["posting"] = new JsonObject { ["key_auths"] = JsonNode.Parse(keyAuths) },
        }));

        AssertUnavailable(await Unread(Key));
    }

    [Theory]
    [InlineData("[[123, 1]]")]
    [InlineData("[]")]
    public async Task AnUnusableHivesignerKeyListIsUnavailableAndNotCached(string keyAuths)
    {
        PrivateApi.ValidationAccounts = names =>
            Task.FromResult<JsonArray?>(names.Single() == "hivesigner"
                ? new JsonArray(new JsonObject
                {
                    ["name"] = "hivesigner",
                    ["posting"] = new JsonObject { ["key_auths"] = JsonNode.Parse(keyAuths) },
                })
                : Known("alice"));
        AssertUnavailable(await Unread(HsKey));

        PrivateApi.ValidationAccounts = names => Task.FromResult<JsonArray?>(Known(names.Single()));
        Assert.Equal("alice", await PrivateApi.ValidateCode(new JsonObject { ["code"] = Code(HsKey) }));
    }

    [Fact]
    public async Task AnAccountWhosePostingIsHeldByAccountsOnlyIsStillChecked()
    {
        // No posting keys of its own is a real account shape (account_auths only),
        // not a garbage answer: @hivesigner's keys still decide.
        PrivateApi.ValidationAccounts = names =>
            Task.FromResult<JsonArray?>(names.Single() == "hivesigner"
                ? Known("hivesigner")
                : new JsonArray(new JsonObject
                {
                    ["name"] = "alice",
                    ["posting"] = new JsonObject { ["key_auths"] = new JsonArray() },
                }));

        Assert.Equal("alice", await PrivateApi.ValidateCode(new JsonObject { ["code"] = Code(HsKey) }));
        Assert.Equal((401, "Unauthorized", ""), await Unread(OtherKey));
    }

    [Fact]
    public void TheUnavailableLogNamesTheCauseButNotTheToken()
    {
        var timeout = PrivateApi.UnavailableLog("account read failed", new TaskCanceledException("timed out"));
        var transport = PrivateApi.UnavailableLog("account read failed", new HttpRequestException("refused"));

        Assert.Contains("TaskCanceledException", timeout.Line);
        Assert.Contains("timed out", timeout.Line);
        Assert.Contains("HttpRequestException", transport.Line);
        // One line per reason and cause type, however the messages vary.
        Assert.NotEqual(timeout.Key, transport.Key);
        Assert.Equal(transport.Key,
            PrivateApi.UnavailableLog("account read failed", new HttpRequestException("other")).Key);
    }

    [Fact]
    public async Task AWrongSignatureIsStillUnauthorized()
    {
        PrivateApi.ValidationAccounts = names => Task.FromResult<JsonArray?>(Known(names.Single()));

        Assert.Equal((401, "Unauthorized", ""), await Unread(OtherKey));
        Assert.Equal((401, "Unauthorized", ""), await StreakFreezeBuy(OtherKey));
    }

    [Fact]
    public async Task AnUnknownAccountIsStillUnauthorized()
    {
        // The chain answered: there is no such account.
        PrivateApi.ValidationAccounts = _ => Task.FromResult<JsonArray?>(new JsonArray());

        Assert.Equal((401, "Unauthorized", ""), await Unread(Key));
        Assert.Equal((401, "Unauthorized", ""), await StreakFreezeBuy(Key));
    }

    [Fact]
    public async Task AnRpcErrorIsTheNodesAnswerAndStaysUnauthorized()
    {
        // An RPC-level error is the node answering the call (HiveRpcClient does
        // not fail over on it), so it is not an outage.
        PrivateApi.ValidationAccounts = _ => throw new HiveRpcClient.RpcException("bad name");

        Assert.Equal((401, "Unauthorized", ""), await Unread(Key));
    }

    [Fact]
    public async Task AMalformedCodeIsStillUnauthorized()
    {
        PrivateApi.ValidationAccounts = _ => throw new HttpRequestException("never read");

        Assert.Equal((401, "Unauthorized", ""),
            await Run(PrivateApi.UnreadNotifications, new JsonObject { ["code"] = "not-a-token" }));
    }
}
