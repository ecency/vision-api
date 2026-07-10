namespace EcencyApi.Handlers;

// Port of src/server/chain-providers.ts.
//
// Static pools of public endpoints per supported chain, ordered by preference.
// Each pool can be overridden without a code change via a comma-separated env
// var (ETH_RPC_URLS, BNB_RPC_URLS, SOL_RPC_URLS, BTC_ESPLORA_URLS).

/// <summary>Structural stand-in for the TS generic constraint `P extends { id: string }`.</summary>
public interface IChainProvider
{
    string Id { get; }
}

/// <summary>
/// JSON-RPC provider. Extra request headers (e.g. an Authorization bearer for a
/// keyed endpoint) are kept out of the URL so credentials never land in access
/// logs or error traces.
/// </summary>
public sealed record RpcProvider(string Id, string Url, IReadOnlyDictionary<string, string>? Headers = null) : IChainProvider;

/// <summary>
/// Esplora (Bitcoin) provider. BearerAuth: authenticate with the Blockstream
/// enterprise OAuth token (BLOCKSTREAM_CLIENT_ID/SECRET).
/// </summary>
public sealed record EsploraProvider(string Id, string Url, bool BearerAuth = false) : IChainProvider;

public static class ChainProviders
{
    private static string HostnameId(string url)
    {
        try
        {
            return new Uri(url, UriKind.Absolute).Host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    private static IReadOnlyList<RpcProvider>? PoolFromEnv(string envName)
    {
        var raw = Environment.GetEnvironmentVariable(envName);

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var providers = raw
            .Split(',')
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Select(url => new RpcProvider(HostnameId(url), url))
            .ToList();

        return providers.Count > 0 ? providers : null;
    }

    public static readonly IReadOnlyList<RpcProvider> EthRpcPool = PoolFromEnv("ETH_RPC_URLS") ?? new List<RpcProvider>
    {
        new("publicnode", "https://ethereum-rpc.publicnode.com"),
        new("drpc", "https://eth.drpc.org"),
        new("mevblocker", "https://rpc.mevblocker.io"),
    };

    public static readonly IReadOnlyList<RpcProvider> BnbRpcPool = PoolFromEnv("BNB_RPC_URLS") ?? new List<RpcProvider>
    {
        new("publicnode", "https://bsc-rpc.publicnode.com"),
        new("bnbchain", "https://bsc-dataseed.bnbchain.org"),
        new("defibit", "https://bsc-dataseed1.defibit.io"),
    };

    private static readonly string HeliusKey =
        (Environment.GetEnvironmentVariable("HELIUS_API_KEY") ?? "").Trim();

    private static string HeliusRpcUrl(string apiKey) =>
        $"https://mainnet.helius-rpc.com/?api-key={EncodeUriComponent(apiKey)}";

    public static readonly IReadOnlyList<RpcProvider> SolRpcPool = PoolFromEnv("SOL_RPC_URLS") ?? BuildDefaultSolPool();

    private static IReadOnlyList<RpcProvider> BuildDefaultSolPool()
    {
        var pool = new List<RpcProvider>
        {
            new("publicnode", "https://solana-rpc.publicnode.com"),
            new("solana-foundation", "https://api.mainnet.solana.com"),
        };

        // Helius RPC authenticates with the api-key query parameter.
        if (HeliusKey.Length > 0)
        {
            pool.Add(new RpcProvider("helius", HeliusRpcUrl(HeliusKey)));
        }

        return pool;
    }

    private static readonly bool BlockstreamCredsConfigured =
        (Environment.GetEnvironmentVariable("BLOCKSTREAM_CLIENT_ID") ?? "").Trim().Length > 0
        && (Environment.GetEnvironmentVariable("BLOCKSTREAM_CLIENT_SECRET") ?? "").Trim().Length > 0;

    // enterprise.blockstream.info requires the Blockstream OAuth bearer token; infer
    // it so an env override can include the enterprise endpoint without silently
    // running unauthenticated.
    private static readonly HashSet<string> BearerAuthHosts = new() { "enterprise.blockstream.info" };

    private static EsploraProvider EsploraProviderFromUrl(string url) =>
        new(HostnameId(url), url, BearerAuthHosts.Contains(HostnameId(url)));

    public static readonly IReadOnlyList<EsploraProvider> BtcEsploraPool = BuildBtcEsploraPool();

    private static IReadOnlyList<EsploraProvider> BuildBtcEsploraPool()
    {
        var rawEnv = Environment.GetEnvironmentVariable("BTC_ESPLORA_URLS");
        if (!string.IsNullOrEmpty(rawEnv))
        {
            var fromEnv = rawEnv
                .Split(',')
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Select(EsploraProviderFromUrl)
                .ToList();
            if (fromEnv.Count > 0)
            {
                return fromEnv;
            }
        }

        var pool = new List<EsploraProvider> { new("mempool", "https://mempool.space/api") };

        if (BlockstreamCredsConfigured)
        {
            pool.Add(new EsploraProvider("blockstream", "https://enterprise.blockstream.info/api", true));
        }

        pool.Add(new EsploraProvider("blockstream-public", "https://blockstream.info/api"));

        return pool;
    }

    /// <summary>
    /// JS encodeURIComponent parity. Uri.EscapeDataString escapes the RFC 3986
    /// sub-delims ! * ' ( ) that encodeURIComponent leaves raw, so restore them.
    /// (EscapeDataString output only contains "%21" etc. as escapes of those very
    /// characters, so the replacements are unambiguous.)
    /// </summary>
    internal static string EncodeUriComponent(string value) =>
        Uri.EscapeDataString(value)
            .Replace("%21", "!")
            .Replace("%2A", "*")
            .Replace("%27", "'")
            .Replace("%28", "(")
            .Replace("%29", ")");
}
