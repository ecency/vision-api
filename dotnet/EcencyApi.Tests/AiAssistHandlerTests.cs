using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// AI assist bills whoever <c>us</c> names. ePoints returns the cached result for a
/// repeated <c>idempotency_key</c> instead of charging again, so the proxy has to
/// forward the key the client sent. Older clients omit it; those requests still go
/// through.
/// </summary>
[Collection("ai-assist")]
public class AiAssistHandlerTests : IDisposable
{
    private readonly CurationDeskTestSupport.Recorder _upstream = new();

    public AiAssistHandlerTests()
    {
        // `code` "as:alice" validates as alice; anything else is invalid.
        // Same extraction as production ValidateCode: a lone-surrogate escape is a
        // real string, and GetValue<string>() throws on it.
        PrivateApi.AiAssistValidateCode = body =>
        {
            string? code = null;
            if (body["code"] is JsonValue codeValue && JsVal.TryGetStringLenient(codeValue, out var codeStr))
                code = codeStr;
            return Task.FromResult(code != null && code.StartsWith("as:", StringComparison.Ordinal) ? code[3..] : null);
        };
        PrivateApi.AiAssistUpstream = (endpoint, method, payload, _) =>
            _upstream.Handle(endpoint, method, Array.Empty<KeyValuePair<string, string>>(), payload);
    }

    public void Dispose()
    {
        PrivateApi.AiAssistValidateCode = PrivateApi.ValidateCode;
        PrivateApi.AiAssistUpstream = (endpoint, method, payload, timeoutMs) =>
            EcencyApi.Infrastructure.ApiClient.ApiRequest(endpoint, method, null, payload, null, timeoutMs);
    }

    [Fact]
    public async Task ForwardsTheIdempotencyKeyOnTheUpstreamBody()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/ai-assist", """
            {"code":"as:alice","action":"summarize","text":"hello world","idempotency_key":"abcd1234efgh"}
            """);
        await PrivateApi.AiAssist(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("ai-assist", call.Endpoint);
        Assert.Equal(HttpMethod.Post, call.Method);
        var payload = Assert.IsType<JsonObject>(call.Payload);
        Assert.Equal("alice", payload["us"]?.GetValue<string>());
        Assert.Equal("summarize", payload["action"]?.GetValue<string>());
        Assert.Equal("hello world", payload["text"]?.GetValue<string>());
        Assert.Equal("abcd1234efgh", payload["idempotency_key"]?.GetValue<string>());
    }

    [Fact]
    public async Task ForwardsWhenTheClientOmitsTheIdempotencyKey()
    {
        var ctx = CurationDeskTestSupport.Post("/private-api/ai-assist", """
            {"code":"as:alice","action":"summarize","text":"hello world"}
            """);
        await PrivateApi.AiAssist(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_upstream.Calls);
        var payload = Assert.IsType<JsonObject>(call.Payload);
        Assert.Equal("alice", payload["us"]?.GetValue<string>());
        Assert.Equal("summarize", payload["action"]?.GetValue<string>());
        Assert.Equal("hello world", payload["text"]?.GetValue<string>());
        Assert.False(payload.ContainsKey("idempotency_key"));
    }
}

[CollectionDefinition("ai-assist", DisableParallelization = true)]
public class AiAssistCollection { }
