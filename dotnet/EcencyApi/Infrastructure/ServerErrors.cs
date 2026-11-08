namespace EcencyApi.Infrastructure;

/// <summary>
/// A session code could not be checked because the account keys it is checked
/// against could not be read (node error, timeout, unusable answer). Not a
/// verdict on the code: the request is answered 503, never 401, so a client
/// does not take a passing node problem for a refused session and sign out.
/// </summary>
public sealed class TokenCheckUnavailableException : Exception
{
    public TokenCheckUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>The request pipeline's error handling, wrapped around every route.</summary>
public static class ServerErrors
{
    /// <summary>Seconds a client is asked to wait before retrying a 503.</summary>
    public const int RetryAfterSeconds = 5;

    public static async Task Handle(HttpContext ctx, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (TokenCheckUnavailableException)
        {
            // Quiet on purpose: during a node outage this fires per request, and
            // ValidateCode already notes the cause once.
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                await ctx.SendText(503, "Service Unavailable");
            }
        }
        catch (Exception e)
        {
            // The Node app's error middleware sends a plain 500 "Server Error"
            // for anything thrown by handlers (including body-parser
            // SyntaxErrors), so mirror that exactly.
            Console.Error.WriteLine($"Unhandled server error: {e}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync("Server Error");
            }
        }
    }
}
