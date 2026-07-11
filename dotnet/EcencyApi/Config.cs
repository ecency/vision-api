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

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}
