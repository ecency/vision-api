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
/// The account is the validated code only, including when the body names someone else.
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
    // The body's spelling of the same account must not replace the code's account.
    [InlineData("Good-Karma")]
    [InlineData("GOOD-KARMA")]
    public void NamingYourOwnAccountStillServesTheCode(string requested)
    {
        var (username, fullScope) = PrivateApi.ResolveNotificationsTarget("good-karma", requested);

        Assert.Equal("good-karma", username);
        Assert.True(fullScope);
    }

    [Fact]
    public void ABodyNamingAnotherAccountDoesNotOverrideTheCode()
    {
        // Previously this returned someone-else (a restricted feed). The body does not
        // choose the account: the code's feed is served, not the one named here.
        var (username, fullScope) = PrivateApi.ResolveNotificationsTarget("good-karma", "someone-else");

        Assert.Equal("good-karma", username);
        Assert.NotEqual("someone-else", username);
        Assert.True(fullScope);
    }

    [Fact]
    public void ANearMissNameDoesNotSwitchTheAccount()
    {
        foreach (var other in new[] { "good-karm", "good-karma2", "ood-karma", " good-karma", "good_karma" })
        {
            var resolved = PrivateApi.ResolveNotificationsTarget("good-karma", other);
            Assert.Equal("good-karma", resolved.Username);
            Assert.True(resolved.FullScope);
        }
    }
}

/// <summary>
/// The notifications handler itself: a code for one account cannot be pointed at
/// another by the body's <c>user</c> field, and nothing is fetched when the code
/// does not validate.
/// </summary>
[Collection("notifications-auth")]
public class NotificationsHandlerTests : IDisposable
{
    private readonly CurationDeskTestSupport.Recorder _upstream = new();

    public NotificationsHandlerTests()
    {
        // `code` "as:alice" validates as alice; anything else is invalid.
        PrivateApi.NotificationsValidateCode = body =>
        {
            var code = body["code"]?.GetValue<string>();
            return Task.FromResult(code != null && code.StartsWith("as:", StringComparison.Ordinal) ? code[3..] : null);
        };
        PrivateApi.NotificationsUpstream = (endpoint, method, headers) =>
            _upstream.Handle(endpoint, method, headers ?? Array.Empty<KeyValuePair<string, string>>(), null);
    }

    public void Dispose()
    {
        PrivateApi.NotificationsValidateCode = PrivateApi.ValidateCode;
        PrivateApi.NotificationsUpstream =
            (endpoint, method, extraHeaders) =>
                EcencyApi.Infrastructure.ApiClient.ApiRequest(endpoint, method, extraHeaders);
    }

    [Fact]
    public async Task ACodeAloneFetchesThatAccountsCompleteFeed()
    {
        var ctx = CurationDeskTestSupport.Post(
            "/private-api/notifications",
            """{"code":"as:alice"}""");
        await PrivateApi.Notifications(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("activities/alice?scope=full", call.Endpoint);
        Assert.Equal(HttpMethod.Get, call.Method);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("Alice")]
    public async Task NamingTheCodesOwnAccountStillFetchesTheCode(string user)
    {
        var ctx = CurationDeskTestSupport.Post(
            "/private-api/notifications",
            $$"""{"code":"as:alice","user":"{{user}}"}""");
        await PrivateApi.Notifications(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("activities/alice?scope=full", call.Endpoint);
    }

    [Theory]
    [InlineData("victim")]
    [InlineData("victim/unread-count")]
    [InlineData("victim?x=1")]
    public async Task ABodyNamingAnotherAccountServesTheCodesAccount(string user)
    {
        var ctx = CurationDeskTestSupport.Post(
            "/private-api/notifications",
            $$"""{"code":"as:alice","user":"{{user}}","filter":"follows","limit":20}""");
        await PrivateApi.Notifications(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_upstream.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("follows/alice?limit=20&scope=full", call.Endpoint);
        Assert.DoesNotContain("victim", call.Endpoint);
    }

    [Fact]
    public async Task NamingAnAccountWithoutAValidCodeIsUnauthorized()
    {
        var ctx = CurationDeskTestSupport.Post(
            "/private-api/notifications",
            """{"code":"bogus","user":"victim"}""");
        await PrivateApi.Notifications(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Equal("Unauthorized", CurationDeskTestSupport.Body(ctx));
        Assert.Empty(_upstream.Calls);
    }
}

[CollectionDefinition("notifications-auth", DisableParallelization = true)]
public class NotificationsAuthCollection { }
