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

    /// <summary>res.status(code).send(obj) / res.json(obj).</summary>
    public static async Task SendJson(this HttpContext ctx, int status, JsonNode? node)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(node?.ToJsonString(JsonOpts) ?? "null");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Convenience: string body field (undefined -> null).</summary>
    public static string? Str(this JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s)
            ? s
            : null;

    /// <summary>Raw node for a body field (undefined -> null).</summary>
    public static JsonNode? Field(this JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) ? v : null;
}
