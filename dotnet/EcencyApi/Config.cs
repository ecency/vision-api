namespace EcencyApi;

/// <summary>
/// Mirror of src/config.ts — same env vars, same defaults, so the two
/// implementations are drop-in interchangeable behind the same compose file.
/// </summary>
public static class Config
{
    public static string PrivateApiAddr { get; } =
        Env("PRIVATE_API_ADDR") ?? "https://domain.com/api";

    public static string PrivateApiAuth { get; } =
        Env("PRIVATE_API_AUTH") ?? "privateapiauth";

    public static string HsClientSecret { get; } =
        Env("HIVESIGNER_SECRET") ?? "hivesignerclientsecret";

    public static string SearchApiAddr { get; } =
        Env("SEARCH_API_ADDR") ?? "https://api.search.com";

    public static string SearchApiToken { get; } =
        Env("SEARCH_API_SECRET") ?? "searchApiSecret";

    // No default: when unset the Stripe routes fail closed (503) rather than
    // forward an empty secret (matches config.ts).
    public static string? StripeInternalSecret { get; } = Env("STRIPE_INTERNAL_SECRET");

    public static string? TurnstileSecret { get; } = Env("TURNSTILE_SECRET");

    public static string CaptchaMode { get; } =
        (Env("CAPTCHA_MODE") ?? "hard").Trim().ToLowerInvariant();

    // ---- SSR RPC cache (Handlers/SsrRpc.cs) ----
    // Shared secret the web tier sends on every call. Unset = the routes are
    // switched off and answer exactly like unknown routes.
    public static string? SsrInternalSecret { get; } = NonEmpty(Env("SSR_INTERNAL_SECRET"));

    // Total bytes of cached responses kept in memory (LRU beyond that).
    public static long SsrCacheBytes { get; } =
        long.TryParse(Env("SSR_CACHE_BYTES"), out var b) && b >= 0 ? b : 512L * 1024 * 1024;

    // Wall-clock budget for one lookup. The web tier gives up on the proxy a
    // little later and falls back to its own node pool, so this must stay
    // under that; a lookup that outlives it still completes and fills the cache.
    public static int SsrBudgetMs { get; } =
        int.TryParse(Env("SSR_RPC_BUDGET_MS"), out var ms) && ms > 0 ? ms : 1500;

    // Per-node timeout for the cache's own RPC client (one attempt per node).
    public static int SsrNodeTimeoutMs { get; } =
        int.TryParse(Env("SSR_RPC_NODE_TIMEOUT_MS"), out var nt) && nt > 0 ? nt : 1200;

    // Optional node pool for the cache's RPC client, comma-separated; defaults
    // to the shared pool. Lets a deployment put its own node first.
    public static string[]? SsrRpcNodes { get; } =
        NonEmpty(Env("SSR_RPC_NODES"))?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is { Length: > 0 } nodes ? nodes : null;

    // Upper bound on upstream fills in progress at once. A fill outlives the
    // request budget on purpose (it still lands in the cache), so without a
    // bound a slow pool plus many distinct keys would pile up detached calls.
    public static int SsrMaxConcurrentFills { get; } =
        int.TryParse(Env("SSR_RPC_MAX_FILLS"), out var f) && f > 0 ? f : 64;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}
