using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// /private-api/register-device and /private-api/detail-device act on a push
/// registration, which belongs to the account the code was issued for.
/// </summary>
public class DeviceAuthorizationTests
{
    [Theory]
    [InlineData("good-karma")]
    // Hive names are lowercase, but the comparison must not hinge on that.
    [InlineData("Good-Karma")]
    [InlineData("GOOD-KARMA")]
    public void TheCodesOwnAccountIsAccepted(string requested)
    {
        Assert.True(PrivateApi.IsOwnDeviceRequest("good-karma", requested));
    }

    [Theory]
    [InlineData("someone-else")]
    [InlineData("good-karm")]
    [InlineData("good-karma2")]
    [InlineData(" good-karma")]
    [InlineData("good_karma")]
    public void AnyOtherAccountIsRefused(string requested)
    {
        Assert.False(PrivateApi.IsOwnDeviceRequest("good-karma", requested));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingUsernameIsRefused(string? requested)
    {
        Assert.False(PrivateApi.IsOwnDeviceRequest("good-karma", requested));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutAValidatedCodeNothingIsAccepted(string? validated)
    {
        Assert.False(PrivateApi.IsOwnDeviceRequest(validated, "good-karma"));
        Assert.False(PrivateApi.IsOwnDeviceRequest(validated, validated));
    }
}

/// <summary>
/// The device handlers themselves: a code for one account cannot register or read a
/// device row under another, and nothing reaches upstream when it tries.
/// </summary>
[Collection("device-auth")]
public class DeviceHandlerTests : IDisposable
{
    private readonly CurationDeskTestSupport.Recorder _upstream = new();

    public DeviceHandlerTests()
    {
        // `code` "as:alice" validates as alice; anything else is invalid.
        PrivateApi.DeviceValidateCode = body =>
        {
            var code = body["code"]?.GetValue<string>();
            return Task.FromResult(code != null && code.StartsWith("as:", StringComparison.Ordinal) ? code[3..] : null);
        };
        PrivateApi.DeviceUpstream = (endpoint, method, payload) =>
            _upstream.Handle(endpoint, method, Array.Empty<KeyValuePair<string, string>>(), payload);
    }

    public void Dispose()
    {
        PrivateApi.DeviceValidateCode = PrivateApi.ValidateCode;
        PrivateApi.DeviceUpstream = (endpoint, method, payload) =>
            EcencyApi.Infrastructure.ApiClient.ApiRequest(endpoint, method, null, payload);
    }

    private static string Register(string code, string? username) =>
        username == null
            ? $$"""{"code":"{{code}}","token":"t1","system":"fcm-ios","allows_notify":1,"notify_types":[1]}"""
            : $$"""{"code":"{{code}}","username":"{{username}}","token":"t1","system":"fcm-ios","allows_notify":1,"notify_types":[1]}""";

    [Theory]
    [InlineData("alice")]
    [InlineData("Alice")]
    public async Task RegisteringTheCodesOwnAccountIsForwarded(string username)
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/register-device", Register("as:alice", username));
        await PrivateApi.RegisterDevice(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("rgstrmbldvc/", call.Endpoint);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(username, call.Payload?["username"]?.GetValue<string>());
        Assert.Equal("t1", call.Payload?["token"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("bob")]
    [InlineData(null)]
    public async Task RegisteringAnotherAccountIsRefused(string? username)
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/register-device", Register("as:alice", username));
        await PrivateApi.RegisterDevice(ctx);

        Assert.Equal(403, ctx.Response.StatusCode);
        Assert.Empty(_upstream.Calls);
    }

    [Fact]
    public async Task RegisteringWithoutAValidCodeIsUnauthorized()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/register-device", Register("bogus", "alice"));
        await PrivateApi.RegisterDevice(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Empty(_upstream.Calls);
    }

    [Fact]
    public async Task ReadingTheCodesOwnDeviceIsForwarded()
    {
        var ctx = CurationDeskTestSupport.Post(
            "/private-api/detail-device", """{"code":"as:alice","username":"alice","token":"t1"}""");
        await PrivateApi.DetailDevice(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("mbldvcdtl/alice/t1", call.Endpoint);
        Assert.Equal(HttpMethod.Get, call.Method);
    }

    [Fact]
    public async Task ReadingAnotherAccountsDeviceIsRefused()
    {
        var ctx = CurationDeskTestSupport.Post(
            "/private-api/detail-device", """{"code":"as:alice","username":"bob","token":"t1"}""");
        await PrivateApi.DetailDevice(ctx);

        Assert.Equal(403, ctx.Response.StatusCode);
        Assert.Empty(_upstream.Calls);
    }
}

[CollectionDefinition("device-auth", DisableParallelization = true)]
public class DeviceAuthCollection { }
