using EcencyApi;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Microsoft.Extensions.FileProviders;

// Docker HEALTHCHECK entrypoint: poll our own /healthcheck.json, exit 0/1.
// Mirrors the Node healthCheck.js behavior without needing curl in the image.
if (args.Contains("--healthcheck"))
{
    try
    {
        var hcPort = int.TryParse(Environment.GetEnvironmentVariable("API_PORT"), out var hp) ? hp : 4000;
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var resp = await hc.GetAsync($"http://localhost:{hcPort}/healthcheck.json");
        return (int)resp.StatusCode == 200 ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

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

// Quiet by default: ASP.NET's per-request Information logging (~4 lines per
// request) fills unbounded docker json-file logs on the origin hosts. Warnings
// and errors only, unless the standard Logging__LogLevel__Default env var is
// set (then configuration rules apply as usual).
if (Environment.GetEnvironmentVariable("Logging__LogLevel__Default") is null)
{
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

var app = builder.Build();

if (!ApiClient.HasIdentity())
{
    // Not fatal: most routes still work. Reader-bound backend calls (Listen) are
    // refused upstream until PRIVATE_API_AUTH carries the identity header.
    Console.Error.WriteLine(
        $"PRIVATE_API_AUTH has no {ApiClient.IdentityHeader} header: backend calls that need this service's identity will be refused");
}

// Global error handling: a 503 for a session code that could not be checked,
// and the Node app's plain 500 "Server Error" for anything else a handler
// throws (see ServerErrors).
app.Use(ServerErrors.Handle);

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

return 0;
