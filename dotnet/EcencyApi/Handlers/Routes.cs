using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// 1:1 port of the route table in src/server/index.tsx. Handler modules are
/// static partial classes; every route maps to the same path and method as the
/// Express original.
/// </summary>
public static class Routes
{
    public static void Map(WebApplication app)
    {
        // NOTE: handler route groups are wired here as the port lands; the
        // health check and fallback below match fallback.tsx today.

        // Health check for docker swarm
        app.MapGet("/healthcheck.json", async ctx =>
        {
            await ctx.SendJson(200, new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = 200,
                ["body"] = new System.Text.Json.Nodes.JsonObject { ["status"] = "ok" },
            });
        });

        // For all other GET paths — the razzle template page
        app.MapFallback(async ctx =>
        {
            if (!HttpMethods.IsGet(ctx.Request.Method))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            await ctx.SendText(200, FallbackHtml);
        });
    }

    /// <summary>Rendered output of template.tsx (static markup, no client JS).</summary>
    private const string FallbackHtml = """
        <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <link rel="icon" href="/favicon.png" />
                <meta name="theme-color" content="#000000" />
                <title>Ecency Api</title>
            </head>
            <body>
                <div id="root">Hello there! This is Vision API.</div>
            </body>
        </html>
        """;
}
