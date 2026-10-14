using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// Listen bills whoever <c>us</c> names, so the proxy must take it only from the
/// validated code and must forward the client's <c>idempotency_key</c> (a retried
/// unlock is then a replay, not a second charge). The status route is also open to
/// anonymous readers.
/// </summary>
[Collection("listen")]
public class ListenHandlerTests : IDisposable
{
    private sealed record ListenCall(string Endpoint, HttpMethod Method, JsonNode? Payload,
        List<KeyValuePair<string, string?>> Query);

    private readonly List<ListenCall> _calls = new();

    public ListenHandlerTests()
    {
        // `code` "as:alice" validates as alice; anything else is invalid.
        PrivateApi.ListenValidateCode = body =>
        {
            string? code = null;
            if (body["code"] is JsonValue codeValue && JsVal.TryGetStringLenient(codeValue, out var codeStr))
                code = codeStr;
            return Task.FromResult(code != null && code.StartsWith("as:", StringComparison.Ordinal) ? code[3..] : null);
        };
        PrivateApi.ListenUpstream = (endpoint, method, payload, query) =>
        {
            _calls.Add(new ListenCall(endpoint, method, payload?.DeepClone(), query?.ToList() ?? new()));
            return Task.FromResult(CurationDeskTestSupport.JsonResponse(200, "{}"));
        };
    }

    public void Dispose()
    {
        PrivateApi.ListenValidateCode = PrivateApi.ValidateCode;
        PrivateApi.ListenUpstream = (endpoint, method, payload, query) =>
            ApiClient.ApiRequest(endpoint, method, null, payload, query);
    }

    private static string? Q(ListenCall call, string key) =>
        call.Query.FirstOrDefault(kv => kv.Key == key).Value;

    [Fact]
    public async Task UnlockTakesTheReaderFromTheCodeAndForwardsTheKey()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/listen", """
            {"code":"as:alice","us":"mallory","author":"noelyss","permlink":"plaza","idempotency_key":"abcd1234efgh","updated":"2026-10-13T01:04:12"}
            """);
        await PrivateApi.Listen(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_calls);
        Assert.Equal("listen", call.Endpoint);
        Assert.Equal(HttpMethod.Post, call.Method);
        var payload = Assert.IsType<JsonObject>(call.Payload);
        Assert.Equal("alice", payload["us"]?.GetValue<string>());
        Assert.Equal("noelyss", payload["author"]?.GetValue<string>());
        Assert.Equal("plaza", payload["permlink"]?.GetValue<string>());
        Assert.Equal("abcd1234efgh", payload["idempotency_key"]?.GetValue<string>());
        Assert.Equal("2026-10-13T01:04:12", payload["updated"]?.GetValue<string>());
        Assert.False(payload.ContainsKey("code"));
    }

    [Fact]
    public async Task UnlockWithoutAValidCodeIsRefused()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/listen", """
            {"code":"bogus","author":"noelyss","permlink":"plaza","idempotency_key":"abcd1234efgh"}
            """);
        await PrivateApi.Listen(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task AnonymousStatusSendsNoReader()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/listen-status", """
            {"author":"noelyss","permlink":"plaza"}
            """);
        await PrivateApi.ListenStatus(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_calls);
        Assert.Equal("listen-status", call.Endpoint);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("noelyss", Q(call, "author"));
        Assert.Equal("plaza", Q(call, "permlink"));
        Assert.DoesNotContain(call.Query, kv => kv.Key == "us");
    }

    [Fact]
    public async Task StatusWithACodeNamesTheReaderFromIt()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/listen-status", """
            {"code":"as:alice","us":"mallory","author":"noelyss","permlink":"plaza","updated":"2026-10-13T01:04:12"}
            """);
        await PrivateApi.ListenStatus(ctx);

        var call = Assert.Single(_calls);
        Assert.Equal("alice", Q(call, "us"));
        Assert.Equal("2026-10-13T01:04:12", Q(call, "updated"));
    }

    [Fact]
    public async Task StatusWithAnInvalidCodeIsRefusedNotAnonymous()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/listen-status", """
            {"code":"bogus","author":"noelyss","permlink":"plaza"}
            """);
        await PrivateApi.ListenStatus(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task StatusNeedsThePost()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/listen-status", """
            {"author":"noelyss"}
            """);
        await PrivateApi.ListenStatus(ctx);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Empty(_calls);
    }
}

[CollectionDefinition("listen", DisableParallelization = true)]
public class ListenCollection { }
