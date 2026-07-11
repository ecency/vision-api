using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Result of an upstream call. Mirrors the slice of AxiosResponse the Node
/// handlers actually use: status, parsed JSON body (axios responseType "json"
/// falls back to the raw string when the body isn't valid JSON), and headers.
/// HTTP error statuses never throw (axios validateStatus: () => true).
/// </summary>
public sealed class UpstreamResponse
{
    public required int Status { get; init; }

    /// <summary>Parsed JSON body, or null when the body wasn't valid JSON (see RawText).</summary>
    public JsonNode? Json { get; init; }

    /// <summary>Set when the body wasn't parseable JSON (axios keeps the raw string).</summary>
    public string? RawText { get; init; }

    public required HttpResponseHeaders2 Headers { get; init; }

    public bool BodyIsJson => RawText == null;
}

/// <summary>Case-insensitive header lookup (axios lower-cases header names).</summary>
public sealed class HttpResponseHeaders2
{
    private readonly Dictionary<string, string> _headers;

    public HttpResponseHeaders2(HttpResponseMessage resp)
    {
        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers)
        {
            _headers[h.Key] = string.Join(", ", h.Value);
        }
        foreach (var h in resp.Content.Headers)
        {
            _headers[h.Key] = string.Join(", ", h.Value);
        }
    }

    public string? Get(string name) => _headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Thrown for transport-level failures; maps to pipe()'s 504/500 split.</summary>
public sealed class UpstreamTimeoutException : Exception
{
    public UpstreamTimeoutException(string url, Exception inner)
        : base($"Upstream timeout: {url}", inner) { }
}

/// <summary>
/// Port of src/server/util.ts baseApiRequest/pipe.
/// </summary>
public static class Upstream
{
    public const int DefaultTimeoutMs = 8000;

    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxConnectionsPerServer = 512,
    })
    {
        // Per-request timeouts via CTS; never use the client-wide timeout.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Mirror of baseApiRequest(url, method, headers, payload, params, timeout).
    /// Always sends a JSON body ({} minimum) like axios does with data: {...payload} —
    /// including on GET — so upstreams observe identical requests.
    /// Query params are appended axios-style. Any HTTP status is returned, transport
    /// errors throw (timeout -> UpstreamTimeoutException -> 504 in Pipe).
    /// </summary>
    public static async Task<UpstreamResponse> BaseApiRequest(
        string url,
        HttpMethod method,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        JsonNode? payload = null,
        IEnumerable<KeyValuePair<string, string?>>? query = null,
        int timeoutMs = DefaultTimeoutMs)
    {
        var finalUrl = AppendQuery(url, query);

        using var req = new HttpRequestMessage(method, finalUrl);

        var bodyJson = payload?.ToJsonString(RawJsonOptions) ?? "{}";
        req.Content = new StringContent(bodyJson, Encoding.UTF8);
        // axios sends bare "application/json" (no charset); keep upstream
        // requests byte-identical to the Node service.
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (headers != null)
        {
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    req.Content.Headers.ContentType =
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(value);
                }
                else
                {
                    req.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new UpstreamTimeoutException(url, e);
        }

        using (resp)
        {
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var status = (int)resp.StatusCode;
            var respHeaders = new HttpResponseHeaders2(resp);

            // axios responseType "json": try to parse; fall back to raw text.
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Length == 0)
            {
                // axios turns an empty body into an empty string
                return new UpstreamResponse { Status = status, RawText = "", Headers = respHeaders };
            }

            try
            {
                var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                });
                return new UpstreamResponse { Status = status, Json = node, Headers = respHeaders };
            }
            catch (JsonException)
            {
                return new UpstreamResponse { Status = status, RawText = text, Headers = respHeaders };
            }
        }
    }

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string AppendQuery(string url, IEnumerable<KeyValuePair<string, string?>>? query)
    {
        if (query == null)
        {
            return url;
        }

        var parts = new List<string>();
        foreach (var (key, value) in query)
        {
            if (value == null)
            {
                continue; // axios skips null/undefined params
            }
            parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        if (parts.Count == 0)
        {
            return url;
        }

        var sep = url.Contains('?') ? '&' : '?';
        return url + sep + string.Join("&", parts);
    }

    /// <summary>
    /// Port of pipe(): forward the upstream response to the client with Express
    /// res.send() semantics — objects/arrays/bools serialize as JSON
    /// (application/json), a bare JSON number is sent as its string form
    /// (matching the explicit number->toString in pipe), strings are sent raw
    /// (text/html like Express), transport errors become 504 "Upstream Timeout"
    /// or 500 "Server Error".
    /// </summary>
    public static async Task Pipe(Task<UpstreamResponse> upstreamTask, HttpContext ctx)
    {
        UpstreamResponse r;
        try
        {
            r = await upstreamTask;
        }
        catch (UpstreamTimeoutException e)
        {
            Console.Error.WriteLine($"pipe(): upstream timeout: {e.Message}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 504;
                await ctx.Response.WriteAsync("Upstream Timeout");
            }
            return;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"pipe(): error while processing API call: {e.Message}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("Server Error");
            }
            return;
        }

        if (ctx.Response.HasStarted)
        {
            Console.Error.WriteLine("pipe(): headers already sent, skipping response");
            return;
        }

        await SendLikeExpress(ctx, r.Status, r.Json, r.RawText);
    }

    /// <summary>Express res.status(status).send(data) for an axios-parsed body.</summary>
    public static async Task SendLikeExpress(HttpContext ctx, int status, JsonNode? json, string? rawText)
    {
        ctx.Response.StatusCode = status;

        if (rawText != null)
        {
            // Non-JSON upstream body: axios kept the raw string; res.send(string) -> text/html
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(rawText);
            return;
        }

        if (json == null || (json is JsonValue nv && nv.TryGetValue<JsonElement>(out var ne)
                             && ne.ValueKind == JsonValueKind.Null))
        {
            // Upstream body "null" parses to JS null; Express res.send(null)
            // sends an EMPTY body with no Content-Type (verified against the
            // Node service). Do the same.
            return;
        }

        if (json is JsonValue value)
        {
            var el = value.GetValue<JsonElement>();
            switch (el.ValueKind)
            {
                case JsonValueKind.Number:
                    // pipe(): typeof data === 'number' -> res.send(data.toString())
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync(JsJson.Stringify(json));
                    return;
                case JsonValueKind.String:
                    // res.send("str") sends the raw string as text/html
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync(value.GetValue<string>());
                    return;
            }
        }

        // Objects, arrays, booleans -> res.json()
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(json!.ToJsonString(RawJsonOptions));
    }
}
