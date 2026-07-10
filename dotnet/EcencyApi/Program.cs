using EcencyApi;
using EcencyApi.Handlers;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // express.json({limit: '50mb'})
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
    options.AddServerHeader = false; // .disable("x-powered-by") equivalent hygiene
});

builder.Services.AddCors();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();

var app = builder.Build();

// Global error handling — the Node app's error middleware sends a plain
// 500 "Server Error" for anything thrown by handlers (including body-parser
// SyntaxErrors), so mirror that exactly.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Unhandled server error: {e}");
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("Server Error");
        }
    }
});

// cors() — allow-all, like the Node service
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

// express.static(RAZZLE_PUBLIC_DIR) — serve the repo's public/ folder
var publicDir = Environment.GetEnvironmentVariable("PUBLIC_DIR")
                ?? Path.Combine(AppContext.BaseDirectory, "public");
if (Directory.Exists(publicDir))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(publicDir),
        ServeUnknownFileTypes = true,
    });
}

Routes.Map(app);

var port = int.TryParse(Environment.GetEnvironmentVariable("API_PORT"), out var p) ? p : 4000;

Console.WriteLine($"> Started on port {port}");
Console.WriteLine(
    $"> account-create captcha: {(Config.CaptchaMode == "off" ? "OFF (break-glass)" : "hard")}");

app.Run($"http://0.0.0.0:{port}");
