using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Request/response helpers that replicate the Express behaviors the handlers
/// rely on (express.json body parsing, res.send/res.json semantics).
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>Thrown when the request body is malformed JSON; the outer error
    /// middleware turns it into 500 "Server Error" exactly like the Node app's
    /// global error handler does with body-parser SyntaxErrors.</summary>
    public sealed class BodyParseException : Exception
    {
        public BodyParseException(Exception inner) : base("entity.parse.failed", inner) { }
    }

    /// <summary>
    /// express.json() semantics: only parses when the Content-Type is JSON-ish,
    /// an absent/empty body yields an empty object, malformed JSON throws.
    /// Handlers can therefore always treat the result as `req.body`.
    /// </summary>
    public static async Task<JsonObject> ReadBody(this HttpContext ctx)
    {
        var contentType = ctx.Request.ContentType ?? "";
        var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

        // express.urlencoded({limit:'50mb'}) is registered alongside express.json,
        // so form posts populate req.body with string values. Parse flat key=value
        // pairs (qs "extended" nesting like a[b]=c is not replicated; no client
        // sends nested forms).
        if (!isJson && contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var form = await ctx.Request.ReadFormAsync();
            var obj = new JsonObject();
            foreach (var kv in form)
            {
                obj[kv.Key] = kv.Value.Count > 0 ? kv.Value[^1] : "";
            }
            return obj;
        }

        if (!isJson || ctx.Request.ContentLength is 0)
        {
            return new JsonObject();
        }

        using var reader = new StreamReader(ctx.Request.Body);
        var text = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        try
        {
            var node = JsonNode.Parse(text);
            // express.json({strict: true}) (default) only accepts objects/arrays;
            // handlers all destructure objects, so coerce non-objects to {} the
            // same way a non-object body would fail destructuring gracefully.
            return node as JsonObject ?? new JsonObject();
        }
        catch (JsonException e)
        {
            throw new BodyParseException(e);
        }
    }

    /// <summary>res.status(code).send(text) — Express string send (text/html).</summary>
    public static async Task SendText(this HttpContext ctx, int status, string text)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(text);
    }

    /// <summary>res.status(code).send(obj) / res.json(obj). Serialized with
    /// JsJson (JSON.stringify parity; tolerates lone surrogates that
    /// System.Text.Json's writer throws on).</summary>
    public static async Task SendJson(this HttpContext ctx, int status, JsonNode? node)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(node is null ? "null" : JsJson.Stringify(node));
    }

    /// <summary>Convenience: string body field (undefined -> null).</summary>
    public static string? Str(this JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) && v is JsonValue val && JsVal.TryGetStringLenient(val, out var s)
            ? s
            : null;

    /// <summary>Raw node for a body field (undefined -> null).</summary>
    public static JsonNode? Field(this JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>
    /// Attach a <see cref="CachePolicy"/> to this response, applied only if it
    /// finishes as a 200.
    ///
    /// The check has to happen at header-flush time, not at call time: handlers
    /// call this before awaiting the upstream, and Pipe() can still replace the
    /// status with 504/500 afterwards. OnStarting runs once the status is final
    /// and before anything is written.
    /// </summary>
    public static void CacheWhenOk(this HttpContext ctx, string policy)
    {
        ctx.Response.OnStarting(() =>
        {
            var value = CachePolicy.ForStatus(ctx.Response.StatusCode, policy);
            if (value != null)
            {
                ctx.Response.Headers.CacheControl = value;
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// <see cref="CacheWhenOk(HttpContext, string)"/> for a body this service has
    /// been holding for <paramref name="ageSeconds"/> before sending it.
    ///
    /// The age goes out as an `Age` header so a shared cache that computes
    /// freshness from it reaches the same expiry as one that only reads
    /// `s-maxage`; without it a body handed out at the end of its in-process
    /// lifetime would start a full downstream window of its own. Shortening the
    /// window is the caller's job (<see cref="CachePolicy.Aged"/>,
    /// <see cref="CachePolicy.Stale"/>): what is left of it differs between a
    /// memo hit and a body served because the upstream call failed.
    /// </summary>
    public static void CacheWhenOk(this HttpContext ctx, string policy, int ageSeconds)
    {
        var age = Math.Max(0, ageSeconds).ToString(CultureInfo.InvariantCulture);
        ctx.Response.OnStarting(() =>
        {
            var value = CachePolicy.ForStatus(ctx.Response.StatusCode, policy);
            if (value != null)
            {
                ctx.Response.Headers.CacheControl = value;
                ctx.Response.Headers.Age = age;
            }
            return Task.CompletedTask;
        });
    }
}
