using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The desk handler tests replace static seams (the configured token, the
/// upstream call, code validation) and share the static memo, so they must not
/// run alongside each other. One collection serializes them.
/// </summary>
[CollectionDefinition("curation-desk", DisableParallelization = true)]
public class CurationDeskCollection { }

/// <summary>
/// Handler-level scaffolding: a request context whose OnStarting callbacks can be
/// run (DefaultHttpContext drops them, and CacheWhenOk relies on them), a
/// recording upstream, and a reset of every seam between tests.
/// </summary>
internal static class CurationDeskTestSupport
{
    public const string Token = "test-desk-token";

    /// <summary>HttpResponseFeature that keeps OnStarting callbacks so a test can fire them.</summary>
    public sealed class StartingAwareResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = new();

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

        public async Task RunStarting()
        {
            foreach (var (callback, state) in _starting)
            {
                await callback(state);
            }
        }
    }

    public sealed record Call(string Endpoint, HttpMethod Method, List<KeyValuePair<string, string>> Headers, JsonNode? Payload)
    {
        public string? Header(string name) =>
            Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Records every upstream call and answers from a script.</summary>
    public sealed class Recorder
    {
        public readonly List<Call> Calls = new();
        public Func<Call, Task<UpstreamResponse>> Answer = _ => Task.FromResult(JsonResponse(200, "{}"));

        public Task<UpstreamResponse> Handle(string endpoint, HttpMethod method,
            IEnumerable<KeyValuePair<string, string>> headers, JsonNode? payload)
        {
            var call = new Call(endpoint, method, headers.ToList(), payload?.DeepClone());
            lock (Calls) Calls.Add(call);
            return Answer(call);
        }
    }

    /// <summary>
    /// Fresh seams: token configured, validation accepts any non-empty code as
    /// the account named in it (`code` "as:alice" -> "alice"), empty memo.
    /// </summary>
    public static Recorder Install(string? token = Token)
    {
        PrivateApi.DeskToken = token;
        PrivateApi.DeskAuthMemoSeconds = 90;
        PrivateApi.DeskValidateCode = body =>
        {
            var code = body["code"]?.GetValue<string>();
            return Task.FromResult(code != null && code.StartsWith("as:", StringComparison.Ordinal) ? code[3..] : null);
        };
        CurationDeskMemo.ResetForTests();
        var recorder = new Recorder();
        PrivateApi.DeskUpstream = recorder.Handle;
        return recorder;
    }

    public static UpstreamResponse JsonResponse(int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return new UpstreamResponse
        {
            Status = status,
            Json = JsonNode.Parse(json),
            Headers = new HttpResponseHeaders2(new HttpResponseMessage()),
            Bytes = bytes,
        };
    }

    public static UpstreamResponse TextResponse(int status, string text)
    {
        return new UpstreamResponse
        {
            Status = status,
            RawText = text,
            Headers = new HttpResponseHeaders2(new HttpResponseMessage()),
            Bytes = Encoding.UTF8.GetBytes(text),
        };
    }

    public static DefaultHttpContext Get(string path, string query = "", (string Name, string Value)[]? routeValues = null)
    {
        var ctx = NewContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        if (query.Length > 0)
        {
            ctx.Request.QueryString = new QueryString(query.StartsWith('?') ? query : "?" + query);
        }
        if (routeValues != null)
        {
            foreach (var (name, value) in routeValues)
            {
                ctx.Request.RouteValues[name] = value;
            }
        }
        return ctx;
    }

    public static DefaultHttpContext Post(string path, string body)
    {
        var ctx = NewContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = path;
        ctx.Request.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        return ctx;
    }

    private static DefaultHttpContext NewContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpResponseFeature>(new StartingAwareResponseFeature());
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    /// <summary>Fire the OnStarting callbacks, as the server would before the first byte.</summary>
    public static Task Start(HttpContext ctx) =>
        ((StartingAwareResponseFeature)ctx.Features.Get<IHttpResponseFeature>()!).RunStarting();

    public static string Body(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    public static string? CacheControl(HttpContext ctx) =>
        ctx.Response.Headers.TryGetValue("Cache-Control", out var v) ? v.ToString() : null;

    /// <summary>Every public read, as (handler, request factory, policy).</summary>
    public static IEnumerable<(string Name, Func<HttpContext, Task> Handler, Func<DefaultHttpContext> Request, string Policy)> PublicReads()
    {
        yield return ("feed", PrivateApi.CurationDeskFeed,
            () => Get("/private-api/curation-desk/feed", "limit=10"), CachePolicy.CurationDeskFeed);
        yield return ("status", PrivateApi.CurationDeskStatus,
            () => Get("/private-api/curation-desk/status"), CachePolicy.CurationDeskStatus);
        yield return ("roster", PrivateApi.CurationDeskRoster,
            () => Get("/private-api/curation-desk/roster"), CachePolicy.CurationDeskRoster);
        yield return ("recommendations", PrivateApi.CurationDeskRecommendations,
            () => Get("/private-api/curation-desk/recommendations", "sort=unique"), CachePolicy.CurationDeskRecommendations);
        yield return ("post", PrivateApi.CurationDeskPost,
            () => Get("/private-api/curation-desk/post/good-karma/hello-world", "",
                new[] { ("author", "good-karma"), ("permlink", "hello-world") }), CachePolicy.CurationDeskPost);
    }

    /// <summary>Every signed write, as (handler, a body that passes validation).</summary>
    public static IEnumerable<(string Name, Func<HttpContext, Task> Handler, string Body)> SignedWrites()
    {
        const string code = "\"code\":\"as:alice\"";
        yield return ("roster-feed", PrivateApi.CurationDeskRosterFeed, "{" + code + ",\"limit\":10}");
        yield return ("tick", PrivateApi.CurationDeskTick, "{" + code + ",\"since\":\"2026-09-05T00:00:00Z\",\"need\":[1],\"visible\":[1,2]}");
        yield return ("mark", PrivateApi.CurationDeskMark, "{" + code + ",\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"reviewed\"}");
        yield return ("mark-clear", PrivateApi.CurationDeskMarkClear, "{" + code + ",\"author\":\"bob\",\"permlink\":\"p\"}");
        yield return ("marks", PrivateApi.CurationDeskMarks, "{" + code + ",\"state\":\"flagged\"}");
        yield return ("cursor", PrivateApi.CurationDeskCursor, "{" + code + ",\"post_id\":42,\"action\":\"advance\"}");
        yield return ("recommend-meta", PrivateApi.CurationDeskRecommendMeta, "{" + code + ",\"author\":\"bob\",\"permlink\":\"p\",\"ua_class\":\"web\"}");
        yield return ("recommendation-dismiss", PrivateApi.CurationDeskRecommendationDismiss, "{" + code + ",\"author\":\"bob\",\"permlink\":\"p\",\"action\":\"dismiss\"}");
    }
}
