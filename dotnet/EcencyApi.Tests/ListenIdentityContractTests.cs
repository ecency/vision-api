using System.Net;
using System.Net.Sockets;
using System.Text;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The contract between this service and ePoints, end to end through the real
/// ApiClient and HTTP stack: ePoints trusts the reader named in a Listen call only
/// when the request carries <see cref="ApiClient.IdentityHeader"/> with the value it
/// holds as VAPI_UPSTREAM_AUTH. A stand-in upstream enforces exactly that rule, and
/// PRIVATE_API_AUTH is configured the way the README documents it.
/// </summary>
[Collection("listen")]
public class ListenIdentityContractTests : IDisposable
{
    private const string Identity = "contract-identity-value";

    private readonly HttpListener _upstream = new();
    private readonly List<(string Path, string? Query, string? Identity)> _seen = new();
    private readonly Func<string> _authBefore = ApiClient.AuthSource;
    private readonly Func<string> _addrBefore = ApiClient.AddrSource;

    public ListenIdentityContractTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _upstream.Prefixes.Add($"http://127.0.0.1:{port}/");
        _upstream.Start();
        _ = Serve();
        ApiClient.AddrSource = () => $"http://127.0.0.1:{port}";
        PrivateApi.ListenValidateCode = body =>
            Task.FromResult(body["code"]?.GetValue<string>() == "as:alice" ? "alice" : null);
    }

    public void Dispose()
    {
        ApiClient.AuthSource = _authBefore;
        ApiClient.AddrSource = _addrBefore;
        PrivateApi.ListenValidateCode = PrivateApi.ValidateCode;
        _upstream.Stop();
        _upstream.Close();
    }

    /// <summary>ePoints' rule (callers.require_caller('vapi')): 403 unless the identity matches.</summary>
    private async Task Serve()
    {
        while (_upstream.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _upstream.GetContextAsync(); }
            catch (Exception) { return; }
            var sent = ctx.Request.Headers[ApiClient.IdentityHeader];
            lock (_seen) _seen.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Url.Query, sent));
            var ok = sent == Identity;
            ctx.Response.StatusCode = ok ? 200 : 403;
            ctx.Response.ContentType = "application/json";
            var body = Encoding.UTF8.GetBytes(ok ? """{"cost":50,"unlocked":true}""" : """{"error":"forbidden"}""");
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    }

    private static string Bundle(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task DocumentedConfigurationIsAcceptedUpstream()
    {
        ApiClient.AuthSource = () =>
            Bundle($$"""{"User-Agent":"EcencyVision/1.0.0","{{ApiClient.IdentityHeader}}":"{{Identity}}"}""");
        Assert.True(ApiClient.HasIdentity());

        var ctx = CurationDeskTestSupport.Post("/private-api/listen-status",
            """{"code":"as:alice","author":"noelyss","permlink":"plaza"}""");
        await PrivateApi.ListenStatus(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var seen = Assert.Single(_seen);
        Assert.Equal("/listen-status", seen.Path);
        Assert.Contains("us=alice", seen.Query);
        Assert.Equal(Identity, seen.Identity);
    }

    [Fact]
    public async Task UnlockIsAcceptedUpstreamWithTheIdentity()
    {
        ApiClient.AuthSource = () => Bundle($$"""{"{{ApiClient.IdentityHeader}}":"{{Identity}}"}""");

        var ctx = CurationDeskTestSupport.Post("/private-api/listen",
            """{"code":"as:alice","author":"noelyss","permlink":"plaza","idempotency_key":"abcd1234efgh"}""");
        await PrivateApi.Listen(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(Identity, Assert.Single(_seen).Identity);
    }

    [Theory]
    [InlineData("""{"Authorization":"token"}""")]
    [InlineData("""{"X-Vapi-Auth":""}""")]
    [InlineData("""{"X-Vapi-Auth":"some-other-value"}""")]
    public async Task WithoutTheMatchingIdentityTheReaderIsRefused(string bundle)
    {
        ApiClient.AuthSource = () => Bundle(bundle);

        var ctx = CurationDeskTestSupport.Post("/private-api/listen",
            """{"code":"as:alice","author":"noelyss","permlink":"plaza","idempotency_key":"abcd1234efgh"}""");
        await PrivateApi.Listen(ctx);

        Assert.Equal(403, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("""{"Authorization":"token"}""", false)]
    [InlineData("""{"X-Vapi-Auth":"  "}""", false)]
    [InlineData("""{"x-vapi-auth":"value"}""", true)]
    [InlineData("not base64 json", false)]
    public void StartupCheckFlagsAMissingIdentity(string bundle, bool expected)
    {
        ApiClient.AuthSource = () => bundle == "not base64 json" ? bundle : Bundle(bundle);
        Assert.Equal(expected, ApiClient.HasIdentity());
    }
}
