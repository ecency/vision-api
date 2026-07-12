using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/private-api.ts lines 186-711 (chain balance +
/// JSON-RPC proxy) plus the esplora/blockstream section of src/server/helper.ts
/// (ChainBalanceResponse, blockstream token cache, fetchEsploraBalance,
/// parseRateLimitHeader, toBigInt).
/// </summary>
public sealed class ChainBalanceResponse
{
    public required string Chain;
    public string? Balance;
    public required string Unit;
    public JsonNode? Raw;
    public string? NodeId;
    public required string Provider;
    public double? RateLimitRemaining;

    /// <summary>
    /// Serializes with the TS object-literal key order; nodeId/rateLimitRemaining
    /// are omitted when null the way JSON.stringify omits undefined keys.
    /// Raw is deep-cloned because a cached/deduped response can be serialized
    /// more than once and a JsonNode may only have a single parent.
    /// </summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["chain"] = Chain,
            ["balance"] = Balance,
            ["unit"] = Unit,
            ["raw"] = Raw?.DeepClone(),
        };
        if (NodeId != null)
        {
            obj["nodeId"] = NodeId;
        }
        obj["provider"] = Provider;
        if (RateLimitRemaining != null)
        {
            obj["rateLimitRemaining"] = JsonValue.Create(RateLimitRemaining.Value);
        }
        return obj;
    }
}

public static partial class PrivateApi
{
    private const int ProviderTimeoutMs = 10_000;

    // Short-lived per-address balance cache (via the shared cache) plus an
    // in-flight dedup map so concurrent requests for one address make one upstream call.
    private const double BalanceCacheTtlSeconds = 15;

    private const string BlockstreamTokenCacheKey = "blockstream:access-token";
    private const string BlockstreamTokenUrl =
        "https://login.blockstream.com/realms/blockstream-public/protocol/openid-connect/token";

    private const string EsploraUserAgent = "EcencyBalanceBot/1.0 (+https://ecency.com)";

    private static readonly Regex ChainParamRegex = new("^[a-z0-9-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Address validators double as a hard allowlist: every accepted address is a
    // safe URL path segment (no "/", ".", "%"), which blocks path-traversal into
    // other provider endpoints via the balance route.
    private static readonly Regex EvmAddressRegex = new("^0x[a-fA-F0-9]{40}$");
    private static readonly Regex BtcAddressRegex = new("^(bc1[a-z0-9]{6,87}|[13][a-km-zA-HJ-NP-Z1-9]{25,62})$");
    private static readonly Regex SolAddressRegex = new("^[1-9A-HJ-NP-Za-km-z]{32,44}$");

    private static readonly Regex HexStringRegex = new("^[a-fA-F0-9]+$");
    private static readonly Regex IntegerStringRegex = new("^-?[0-9]+$");

    private static readonly ConcurrentDictionary<string, Task<ChainBalanceResponse>> InFlightBalance = new();

    private static string BalanceCacheKey(string chain, string address) => $"chain-balance:{chain}:{address}";

    // A request the client got wrong (bad address, unsupported chain). It is
    // rejected up front with its HTTP status and never reaches a provider, so it
    // must not be reported as a 502 upstream failure.
    private sealed class TerminalProviderException : Exception
    {
        public int Status { get; }

        public TerminalProviderException(string message, int status = 400) : base(message)
        {
            Status = status;
        }
    }

    // Stand-in for an axios HTTP-status rejection (default validateStatus):
    // carries the upstream response so handlers can pass status/body through
    // exactly like the `axios.isAxiosError(err) && err.response` branches.
    private sealed class ChainHttpError : Exception
    {
        public UpstreamResponse Response { get; }

        public int Status => Response.Status;

        public ChainHttpError(UpstreamResponse response)
            : base($"Request failed with status code {response.Status}")
        {
            Response = response;
        }
    }

    // TS attaches `jsonRpcBody` to a plain Error: a JSON-RPC error envelope that
    // should fail over to the next provider but still reach the client verbatim
    // if every provider returns one.
    private sealed class ChainRpcEnvelopeError : Exception
    {
        public JsonNode? Body { get; }

        public ChainRpcEnvelopeError(string message, JsonNode? body) : base(message)
        {
            Body = body;
        }
    }

    // Try each provider in the pool in order until one succeeds; fail over on any
    // provider-side error to maximize balance availability.
    private static async Task<T> TryProviders<TProvider, T>(
        string chain,
        IReadOnlyList<TProvider> providers,
        string operation,
        Func<TProvider, Task<T>> fn) where TProvider : IChainProvider
    {
        Exception lastError = new Exception($"No {operation} providers configured for {chain}");

        foreach (var provider in providers)
        {
            try
            {
                return await fn(provider);
            }
            catch (TerminalProviderException)
            {
                throw;
            }
            catch (Exception err)
            {
                lastError = err;
                var detail = DescribeProviderError(err);
                Console.WriteLine($"{chain} {operation} failed on {provider.Id} ({detail}), trying next provider");
            }
        }

        throw lastError;
    }

    // axios-error detail string: `status=${err.response?.status || "none"} code=${err.code || "none"}`
    private static string DescribeProviderError(Exception err) => err switch
    {
        ChainHttpError httpErr =>
            $"status={httpErr.Status} code={(httpErr.Status >= 500 ? "ERR_BAD_RESPONSE" : "ERR_BAD_REQUEST")}",
        UpstreamTimeoutException => "status=none code=ECONNABORTED",
        HttpRequestException transportErr => $"status=none code={SocketErrorCode(transportErr)}",
        _ => err.Message,
    };

    private static string SocketErrorCode(HttpRequestException err) =>
        (err.InnerException as System.Net.Sockets.SocketException)?.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.ConnectionRefused => "ECONNREFUSED",
            System.Net.Sockets.SocketError.HostNotFound => "ENOTFOUND",
            System.Net.Sockets.SocketError.TimedOut => "ETIMEDOUT",
            System.Net.Sockets.SocketError.ConnectionReset => "ECONNRESET",
            _ => "none",
        };

    // axios timeout errors carry "timeout of {n}ms exceeded"; every other error
    // surfaces its own message (err instanceof Error ? err.message : ...).
    private static string ChainErrorMessage(Exception err) =>
        err is UpstreamTimeoutException ? $"timeout of {ProviderTimeoutMs}ms exceeded" : err.Message;

    private static void LogUpstreamError(string context, Exception err)
    {
        if (err is ChainHttpError or UpstreamTimeoutException or HttpRequestException)
        {
            var status = err is ChainHttpError httpErr ? httpErr.Status.ToString(CultureInfo.InvariantCulture) : "none";
            var code = err switch
            {
                ChainHttpError h => h.Status >= 500 ? "ERR_BAD_RESPONSE" : "ERR_BAD_REQUEST",
                UpstreamTimeoutException => "ECONNABORTED",
                HttpRequestException transportErr => SocketErrorCode(transportErr),
                _ => "none",
            };
            Console.Error.WriteLine($"{context} {{ status: {status}, code: '{code}', message: '{ChainErrorMessage(err)}' }}");
            return;
        }

        Console.Error.WriteLine($"{context} {err}");
    }

    // { ...JSON_RPC_HEADERS, ...provider.headers }
    private static Dictionary<string, string> RpcHeaders(RpcProvider provider)
    {
        var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
        if (provider.Headers != null)
        {
            foreach (var (name, value) in provider.Headers)
            {
                headers[name] = value;
            }
        }
        return headers;
    }

    // data.error?.message || fallback (with JS truthiness + String() coercion)
    private static string RpcErrorMessage(JsonNode? errorNode, string fallback)
    {
        var messageNode = errorNode is JsonObject obj ? obj["message"] : null;
        return JsJson.IsTruthy(messageNode) ? ChainJsString(messageNode) : fallback;
    }

    // Shared JSON-RPC POST used by every EVM balance and proxy call.
    private static async Task<JsonNode?> JsonRpcPost(
        RpcProvider provider,
        string method,
        JsonArray parameters,
        string id,
        string errorLabel)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        var resp = await Upstream.BaseApiRequest(provider.Url, HttpMethod.Post,
            RpcHeaders(provider), payload, timeoutMs: ProviderTimeoutMs);

        // axios default validateStatus: non-2xx rejects
        if (resp.Status < 200 || resp.Status >= 300)
        {
            throw new ChainHttpError(resp);
        }

        JsonNode? data = resp.BodyIsJson ? resp.Json : JsonValue.Create(resp.RawText);

        var errorNode = data is JsonObject dataObj ? dataObj["error"] : null;
        if (JsJson.IsTruthy(errorNode))
        {
            throw new Exception(RpcErrorMessage(errorNode, errorLabel));
        }

        return data;
    }

    private static string ParseHexBalance(JsonNode? value)
    {
        if (value is not JsonValue jsonValue || !JsVal.TryGetStringLenient(jsonValue, out var text))
        {
            throw new Exception("Invalid hexadecimal balance response");
        }

        var normalized = text.StartsWith("0x", StringComparison.Ordinal) || text.StartsWith("0X", StringComparison.Ordinal)
            ? text.Substring(2)
            : text;

        if (normalized == "")
        {
            return "0";
        }

        if (!HexStringRegex.IsMatch(normalized))
        {
            throw new Exception("Invalid hexadecimal balance response");
        }

        // The leading "0" keeps BigInteger's hex parser from reading a high first
        // nibble as a sign bit (BigInt("0x...") is always unsigned).
        return BigInteger.Parse("0" + normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString();
    }

    private static int FindJsonObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = start; index < text.Length; index += 1)
        {
            var ch = text[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                depth += 1;
            }
            else if (ch == '}')
            {
                depth -= 1;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static string? ExtractTopLevelNumericProperty(string text, string property)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        var keyLiteral = "\"" + property + "\"";

        for (var index = 0; index < text.Length; index += 1)
        {
            var ch = text[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (ch == '"')
            {
                if (depth == 0 && index + keyLiteral.Length <= text.Length
                    && string.CompareOrdinal(text, index, keyLiteral, 0, keyLiteral.Length) == 0)
                {
                    var cursor = index + keyLiteral.Length;
                    while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor += 1;
                    if (cursor >= text.Length || text[cursor] != ':')
                    {
                        inString = true;
                        continue;
                    }
                    cursor += 1;
                    while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor += 1;

                    // /^\d+/ — leading run of ASCII digits; returns unconditionally
                    // once the top-level key was located, exactly like the TS code.
                    var end = cursor;
                    while (end < text.Length && text[end] >= '0' && text[end] <= '9') end += 1;
                    return end > cursor ? text.Substring(cursor, end - cursor) : null;
                }

                inString = true;
            }
            else if (ch == '{' || ch == '[')
            {
                depth += 1;
            }
            else if (ch == '}' || ch == ']')
            {
                depth -= 1;
            }
        }

        return null;
    }

    private static string ExtractSolanaLamports(string rawText, JsonNode? parsed)
    {
        // typeof parsed?.result?.value !== "number"
        var resultNode = parsed is JsonObject parsedObj ? parsedObj["result"] : null;
        var valueNode = resultNode is JsonObject resultObj ? resultObj["value"] : null;

        if (valueNode is not JsonValue jsonValue
            || !jsonValue.TryGetValue<JsonElement>(out var element)
            || element.ValueKind != JsonValueKind.Number)
        {
            throw new Exception("Invalid Solana balance response");
        }

        var resultMatch = Regex.Match(rawText, "\"result\"\\s*:");
        if (resultMatch.Success)
        {
            var resultStart = resultMatch.Index + resultMatch.Length;
            var objectStart = rawText.IndexOf('{', resultStart);
            if (objectStart != -1)
            {
                var objectEnd = FindJsonObjectEnd(rawText, objectStart);
                if (objectEnd != -1)
                {
                    var resultObject = rawText.Substring(objectStart + 1, objectEnd - objectStart - 1);
                    var rawLamports = ExtractTopLevelNumericProperty(resultObject, "value");
                    if (!string.IsNullOrEmpty(rawLamports))
                    {
                        return rawLamports;
                    }
                }
            }
        }

        return ChainFormatNumber(Math.Truncate(element.GetDouble()));
    }

    private static async Task<ChainBalanceResponse> FetchEvmBalance(string chain, RpcProvider provider, string address)
    {
        var data = await JsonRpcPost(provider, "eth_getBalance", new JsonArray(address, "latest"),
            "balance", "EVM balance request failed");
        var balance = ParseHexBalance(data is JsonObject obj ? obj["result"] : null);

        return new ChainBalanceResponse
        {
            Chain = chain,
            Balance = balance,
            Unit = "wei",
            Raw = data,
            NodeId = provider.Id,
            Provider = provider.Id,
        };
    }

    private static async Task<ChainBalanceResponse> FetchSolanaBalance(RpcProvider provider, string address)
    {
        // TS keeps the raw response text (transformResponse: raw => raw): lamports
        // is a u64 and JSON.parse would cap a balance above 2^53 as a lossy double,
        // so it reads the integer from the text. Upstream already parsed the body,
        // but System.Text.Json keeps raw numeric literals, so ToJsonString() is a
        // faithful reconstruction of the raw body for the extraction below.
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "balance",
            ["method"] = "getBalance",
            ["params"] = new JsonArray(address, new JsonObject { ["commitment"] = "finalized" }),
        };

        var resp = await Upstream.BaseApiRequest(provider.Url, HttpMethod.Post,
            RpcHeaders(provider), payload, timeoutMs: ProviderTimeoutMs);

        // axios default validateStatus: non-2xx rejects
        if (resp.Status < 200 || resp.Status >= 300)
        {
            throw new ChainHttpError(resp);
        }

        if (!resp.BodyIsJson)
        {
            // JSON.parse(rawText) would throw
            throw new Exception("Invalid Solana balance response");
        }

        var parsed = resp.Json;
        var rawText = parsed?.ToJsonString() ?? "null";

        var errorNode = parsed is JsonObject parsedObj ? parsedObj["error"] : null;
        if (JsJson.IsTruthy(errorNode))
        {
            throw new Exception(RpcErrorMessage(errorNode, "Solana balance request failed"));
        }

        var balance = ExtractSolanaLamports(rawText, parsed);

        return new ChainBalanceResponse
        {
            Chain = "sol",
            Balance = balance,
            Unit = "lamports",
            Raw = parsed,
            NodeId = provider.Id,
            Provider = provider.Id,
        };
    }

    // ------------------------------------------------------------------
    // helper.ts: blockstream token + esplora balance
    // ------------------------------------------------------------------

    private static BigInteger ToBigInt(JsonNode? value)
    {
        // toBigInt: bigint pass-through (impossible from JSON), finite number ->
        // truncate, integer-string -> parse, everything else -> 0.
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<JsonElement>(out var element))
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                var d = element.GetDouble();
                return double.IsFinite(d) ? new BigInteger(Math.Truncate(d)) : BigInteger.Zero;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString()!.Trim();
                if (IntegerStringRegex.IsMatch(text))
                {
                    try
                    {
                        return BigInteger.Parse(text, CultureInfo.InvariantCulture);
                    }
                    catch (FormatException)
                    {
                        return BigInteger.Zero;
                    }
                }
            }
        }

        return BigInteger.Zero;
    }

    private static double? ParseRateLimitHeader(string? headerValue)
    {
        // The TS version also recurses into arrays of header values; axios on Node
        // only ever yields arrays for set-cookie — duplicates of these headers are
        // joined with ", " by Node's http module, and HttpResponseHeaders2 joins
        // identically, so this string path is the complete observable behavior
        // (a joined "5, 10" parses to NaN -> undefined in both stacks).
        if (headerValue == null)
        {
            return null;
        }

        var text = headerValue.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var num = ChainJsNumber(text);
        return double.IsFinite(num) ? num : null;
    }

    private static async Task<string> GetBlockstreamAccessToken()
    {
        var cached = MemCache.Get<string>(BlockstreamTokenCacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        var clientId = Environment.GetEnvironmentVariable("BLOCKSTREAM_CLIENT_ID")?.Trim();
        var clientSecret = Environment.GetEnvironmentVariable("BLOCKSTREAM_CLIENT_SECRET")?.Trim();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            throw new Exception("Blockstream credentials are not configured");
        }

        var bodyText = string.Join("&", new[]
        {
            ("client_id", clientId),
            ("client_secret", clientSecret),
            ("grant_type", "client_credentials"),
            ("scope", "openid"),
        }.Select(pair => FormUrlEncodeComponent(pair.Item1) + "=" + FormUrlEncodeComponent(pair.Item2)));

        var resp = await PostFormUrlEncoded(BlockstreamTokenUrl, bodyText);

        // axios default validateStatus: non-2xx rejects
        if (resp.Status < 200 || resp.Status >= 300)
        {
            throw new ChainHttpError(resp);
        }

        // const { access_token, expires_in } = resp.data ?? {};
        var data = resp.BodyIsJson ? resp.Json : JsonValue.Create(resp.RawText);
        var obj = data as JsonObject;

        var accessToken = obj?["access_token"] is JsonValue tokenValue && JsVal.TryGetStringLenient(tokenValue, out var tokenText)
            ? tokenText
            : null;

        if (string.IsNullOrEmpty(accessToken))
        {
            throw new Exception("Blockstream token response missing access_token");
        }

        double? expiresNumeric = null;
        if (obj?["expires_in"] is JsonValue expiresValue && expiresValue.TryGetValue<JsonElement>(out var expiresEl))
        {
            if (expiresEl.ValueKind == JsonValueKind.Number)
            {
                expiresNumeric = expiresEl.GetDouble();
            }
            else if (expiresEl.ValueKind == JsonValueKind.String)
            {
                expiresNumeric = ChainJsNumber(expiresEl.GetString()!);
            }
        }

        var ttl = 240d; // default 4 minutes
        if (expiresNumeric is double expires && double.IsFinite(expires))
        {
            var adjusted = Math.Max(30, Math.Floor(expires - 30));
            if (double.IsFinite(adjusted) && adjusted > 0)
            {
                ttl = adjusted;
            }
        }

        MemCache.Set(BlockstreamTokenCacheKey, accessToken, ttl);
        return accessToken;
    }

    // Documented exception to "always use Upstream.BaseApiRequest": the token
    // endpoint takes a URLSearchParams (application/x-www-form-urlencoded) body,
    // which BaseApiRequest cannot produce (it always JSON-encodes payloads).
    // Mirrors the axios call: 10000ms timeout, urlencoded body, JSON response
    // parsing with raw-text fallback.
    private static async Task<UpstreamResponse> PostFormUrlEncoded(string url, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body),
        };
        req.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");

        using var cts = new CancellationTokenSource(10000);
        HttpResponseMessage resp;
        try
        {
            resp = await Upstream.Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new UpstreamTimeoutException(url, e);
        }

        using (resp)
        {
            var status = (int)resp.StatusCode;
            var headers = new HttpResponseHeaders2(resp);
            var text = await resp.Content.ReadAsStringAsync();

            if (text.Length == 0)
            {
                return new UpstreamResponse { Status = status, RawText = "", Headers = headers };
            }

            try
            {
                var node = JsonNode.Parse(text);
                return new UpstreamResponse { Status = status, Json = node, Headers = headers };
            }
            catch (JsonException)
            {
                return new UpstreamResponse { Status = status, RawText = text, Headers = headers };
            }
        }
    }

    // application/x-www-form-urlencoded serializer with URLSearchParams parity:
    // space -> "+", unreserved set is [A-Za-z0-9*-._], everything else %XX (UTF-8).
    private static string FormUrlEncodeComponent(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b == (byte)' ')
            {
                sb.Append('+');
            }
            else if (b == (byte)'*' || b == (byte)'-' || b == (byte)'.' || b == (byte)'_'
                     || (b >= (byte)'0' && b <= (byte)'9')
                     || (b >= (byte)'A' && b <= (byte)'Z')
                     || (b >= (byte)'a' && b <= (byte)'z'))
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    private static async Task<Dictionary<string, string>> BuildEsploraHeaders(EsploraProvider provider)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = EsploraUserAgent,
        };

        if (provider.BearerAuth)
        {
            headers["Authorization"] = $"Bearer {await GetBlockstreamAccessToken()}";
        }

        return headers;
    }

    private static async Task<ChainBalanceResponse> FetchEsploraBalance(EsploraProvider provider, string address)
    {
        async Task<ChainBalanceResponse> Attempt(bool allowAuthRetry)
        {
            var resp = await Upstream.BaseApiRequest(
                $"{provider.Url}/address/{ChainProviders.EncodeUriComponent(address)}",
                HttpMethod.Get,
                await BuildEsploraHeaders(provider),
                timeoutMs: 10000);

            // axios validateStatus: (status) => status >= 200 && status < 500 — 5xx rejects
            if (resp.Status < 200 || resp.Status >= 500)
            {
                throw new ChainHttpError(resp);
            }

            if (resp.Status == 401 && provider.BearerAuth && allowAuthRetry)
            {
                MemCache.Del(BlockstreamTokenCacheKey);
                return await Attempt(false);
            }

            if (resp.Status != 200)
            {
                throw new Exception($"Esplora API error from {provider.Id} ({resp.Status})");
            }

            // const data = resp.data ?? {};
            JsonNode data = (resp.BodyIsJson ? resp.Json : JsonValue.Create(resp.RawText)) ?? new JsonObject();

            var chainStats = (data as JsonObject)?["chain_stats"] as JsonObject;
            var mempoolStats = (data as JsonObject)?["mempool_stats"] as JsonObject;

            var confirmedFunded = ToBigInt(chainStats?["funded_txo_sum"]);
            var confirmedSpent = ToBigInt(chainStats?["spent_txo_sum"]);
            var mempoolFunded = ToBigInt(mempoolStats?["funded_txo_sum"]);
            var mempoolSpent = ToBigInt(mempoolStats?["spent_txo_sum"]);

            var confirmedBalance = confirmedFunded - confirmedSpent;
            var mempoolBalance = mempoolFunded - mempoolSpent;
            var total = confirmedBalance + mempoolBalance;

            var rateLimitRemaining =
                ParseRateLimitHeader(resp.Headers.Get("x-ratelimit-remaining"))
                ?? ParseRateLimitHeader(resp.Headers.Get("ratelimit-remaining"));

            return new ChainBalanceResponse
            {
                Chain = "btc",
                Balance = total.ToString(),
                Unit = "satoshi",
                Raw = data,
                NodeId = provider.Id,
                Provider = provider.Id,
                RateLimitRemaining = rateLimitRemaining,
            };
        }

        return await Attempt(true);
    }

    // ------------------------------------------------------------------
    // chain handler registry + fetchChainBalance + route handlers
    // ------------------------------------------------------------------

    private sealed record ChainHandlerDef(
        Func<string, bool>? ValidateAddress,
        Func<string, Task<ChainBalanceResponse>> FetchBalance);

    private static readonly IReadOnlyDictionary<string, ChainHandlerDef> ChainHandlers =
        new Dictionary<string, ChainHandlerDef>
        {
            ["btc"] = new(
                address => BtcAddressRegex.IsMatch(address),
                address => TryProviders("btc", ChainProviders.BtcEsploraPool, "balance",
                    provider => FetchEsploraBalance(provider, address))),
            ["eth"] = new(
                address => EvmAddressRegex.IsMatch(address),
                address => TryProviders("eth", ChainProviders.EthRpcPool, "balance",
                    provider => FetchEvmBalance("eth", provider, address))),
            ["bnb"] = new(
                address => EvmAddressRegex.IsMatch(address),
                address => TryProviders("bnb", ChainProviders.BnbRpcPool, "balance",
                    provider => FetchEvmBalance("bnb", provider, address))),
            ["sol"] = new(
                address => SolAddressRegex.IsMatch(address),
                address => TryProviders("sol", ChainProviders.SolRpcPool, "balance",
                    provider => FetchSolanaBalance(provider, address))),
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RpcProvider>> RpcPoolByChain =
        new Dictionary<string, IReadOnlyList<RpcProvider>>
        {
            ["sol"] = ChainProviders.SolRpcPool,
            ["eth"] = ChainProviders.EthRpcPool,
            ["bnb"] = ChainProviders.BnbRpcPool,
        };

    /// <summary>
    /// Fetches balance for a given chain and address directly (without HTTP overhead).
    /// This is the core logic extracted from the balance endpoint for reuse.
    /// </summary>
    public static async Task<ChainBalanceResponse> FetchChainBalance(string chain, string address)
    {
        if (string.IsNullOrEmpty(chain) || string.IsNullOrEmpty(address))
        {
            throw new Exception("Missing chain or address");
        }

        if (!ChainParamRegex.IsMatch(chain))
        {
            throw new Exception("Invalid chain parameter");
        }

        var normalizedChain = chain.ToLowerInvariant();

        if (!ChainHandlers.TryGetValue(normalizedChain, out var handler))
        {
            throw new TerminalProviderException("Unsupported chain");
        }

        if (handler.ValidateAddress != null && !handler.ValidateAddress(address))
        {
            throw new TerminalProviderException("Invalid address format");
        }

        var cacheKey = BalanceCacheKey(normalizedChain, address);

        var cached = MemCache.Get<ChainBalanceResponse>(cacheKey);
        if (cached != null)
        {
            return cached;
        }

        if (InFlightBalance.TryGetValue(cacheKey, out var existing))
        {
            return await existing;
        }

        var pending = new TaskCompletionSource<ChainBalanceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = InFlightBalance.GetOrAdd(cacheKey, pending.Task);
        if (!ReferenceEquals(winner, pending.Task))
        {
            return await winner;
        }

        try
        {
            var response = await handler.FetchBalance(address);
            MemCache.Set(cacheKey, response, BalanceCacheTtlSeconds);
            pending.TrySetResult(response);
            return response;
        }
        catch (Exception e)
        {
            pending.TrySetException(e);
            _ = pending.Task.Exception; // observe: a deduped waiter may not exist
            throw;
        }
        finally
        {
            InFlightBalance.TryRemove(new KeyValuePair<string, Task<ChainBalanceResponse>>(cacheKey, pending.Task));
        }
    }

    public static async Task Balance(HttpContext ctx)
    {
        var chain = ctx.Request.RouteValues["chain"]?.ToString();
        var address = ctx.Request.RouteValues["address"]?.ToString();

        if (string.IsNullOrEmpty(chain) || string.IsNullOrEmpty(address))
        {
            await ctx.SendText(400, "Missing chain or address");
            return;
        }

        // Older clients retry with ?provider=... (e.g. chainz); the parameter is
        // accepted and ignored — provider selection is handled by the pool.

        try
        {
            var balanceResponse = await FetchChainBalance(chain, address);

            ctx.Response.Headers["x-provider"] = balanceResponse.Provider;
            if (balanceResponse.RateLimitRemaining != null)
            {
                ctx.Response.Headers["x-provider-ratelimit-remaining"] =
                    ChainFormatNumber(balanceResponse.RateLimitRemaining.Value);
            }

            await ctx.SendJson(200, balanceResponse.ToJson());
        }
        catch (Exception err)
        {
            if (err is TerminalProviderException terminal)
            {
                await ctx.SendJson(terminal.Status, new JsonObject { ["error"] = terminal.Message });
                return;
            }

            LogUpstreamError("balance(): error while fetching chain balance", err);

            if (err is ChainHttpError httpErr)
            {
                // axios always materializes response.data (an empty body is ""), so
                // the TS `data === undefined -> res.sendStatus(status)` branch is
                // unreachable; objects/arrays pass through, everything else is
                // { error: String(data) }.
                var data = httpErr.Response.BodyIsJson
                    ? httpErr.Response.Json
                    : JsonValue.Create(httpErr.Response.RawText);
                if (data is JsonObject or JsonArray)
                {
                    await ctx.SendJson(httpErr.Status, data);
                }
                else
                {
                    await ctx.SendJson(httpErr.Status, new JsonObject { ["error"] = ChainJsString(data) });
                }
                return;
            }

            // Final catch-all
            await ctx.SendJson(502, new JsonObject { ["error"] = ChainErrorMessage(err) });
        }
    }

    /// <summary>
    /// Allowed JSON-RPC methods per chain for the RPC proxy.
    /// Only read-only / transaction-building methods are allowed; signing and
    /// broadcasting happen client-side (MetaMask submits the transaction directly).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedRpcMethods =
        new Dictionary<string, HashSet<string>>
        {
            ["sol"] = new()
            {
                "getLatestBlockhash",
                "getBalance",
                "getBlockHeight",
                "getFeeForMessage",
                "getMinimumBalanceForRentExemption",
                "getRecentPrioritizationFees",
            },
            ["eth"] = new()
            {
                "eth_chainId",
                "eth_gasPrice",
                "eth_estimateGas",
                "eth_getBalance",
                "eth_blockNumber",
                "eth_getTransactionCount",
            },
            ["bnb"] = new()
            {
                "eth_chainId",
                "eth_gasPrice",
                "eth_estimateGas",
                "eth_getBalance",
                "eth_blockNumber",
                "eth_getTransactionCount",
            },
        };

    /// <summary>
    /// Generic JSON-RPC proxy for supported chains.
    /// Forwards allowed read-only RPC calls to the configured public RPC providers.
    ///
    /// POST /private-api/rpc/:chain
    /// Body: standard JSON-RPC payload { jsonrpc, id, method, params }
    /// </summary>
    public static async Task ChainRpc(HttpContext ctx)
    {
        var chain = ctx.Request.RouteValues["chain"]?.ToString();

        if (string.IsNullOrEmpty(chain) || !ChainParamRegex.IsMatch(chain))
        {
            await ctx.SendJson(400, new JsonObject { ["error"] = "Invalid chain parameter" });
            return;
        }

        var normalizedChain = chain.ToLowerInvariant();

        if (!AllowedRpcMethods.TryGetValue(normalizedChain, out var allowedMethods))
        {
            await ctx.SendJson(400, new JsonObject { ["error"] = $"RPC proxy not available for chain: {chain}" });
            return;
        }

        var body = await ctx.ReadBody();
        var methodNode = body.Field("method");
        if (!JsJson.IsTruthy(methodNode))
        {
            await ctx.SendJson(400, new JsonObject { ["error"] = "Invalid JSON-RPC payload" });
            return;
        }

        var methodName = body.Str("method");
        if (methodName == null || !allowedMethods.Contains(methodName))
        {
            await ctx.SendJson(403, new JsonObject { ["error"] = $"Method not allowed: {ChainJsString(methodNode)}" });
            return;
        }

        if (!RpcPoolByChain.TryGetValue(normalizedChain, out var pool))
        {
            await ctx.SendJson(400, new JsonObject { ["error"] = "Unsupported chain" });
            return;
        }

        // express.json parse + axios JSON.stringify round-trip: reproduces V8
        // number normalization (e.g. integers beyond 2^53 become lossy doubles)
        // so the forwarded payload is byte-equal to what the Node service sends.
        var forwardBody = JsonNode.Parse(JsJson.Stringify(body));

        try
        {
            var result = await TryProviders(normalizedChain, pool, "rpc", async provider =>
            {
                var resp = await Upstream.BaseApiRequest(provider.Url, HttpMethod.Post,
                    RpcHeaders(provider), forwardBody, timeoutMs: ProviderTimeoutMs);

                // axios default validateStatus: non-2xx rejects
                if (resp.Status < 200 || resp.Status >= 300)
                {
                    throw new ChainHttpError(resp);
                }

                JsonNode? data = resp.BodyIsJson ? resp.Json : JsonValue.Create(resp.RawText);

                // A JSON-RPC error body arrives with HTTP 200; treat it as a provider
                // failure so tryProviders advances to the next pool member (e.g. a
                // rate-limited node returning {error:{code:-32005}}), but keep the
                // envelope so a genuine application error can still reach the client
                // if every provider returns one.
                var errorNode = data is JsonObject dataObj ? dataObj["error"] : null;
                if (JsJson.IsTruthy(errorNode))
                {
                    throw new ChainRpcEnvelopeError(RpcErrorMessage(errorNode, "RPC error"), data);
                }

                return (ProviderId: provider.Id, Data: data);
            });

            ctx.Response.Headers["x-provider"] = result.ProviderId;
            await ctx.SendJson(200, result.Data);
        }
        catch (Exception err)
        {
            // Every provider returned a JSON-RPC error — pass the last envelope through verbatim.
            if (err is ChainRpcEnvelopeError envelope)
            {
                await ctx.SendJson(200, envelope.Body);
                return;
            }

            LogUpstreamError($"chainRpc({normalizedChain}): error", err);

            if (err is ChainHttpError httpErr)
            {
                // res.status(status).json(data !== undefined ? data : {...}) — axios
                // always materializes data (empty body -> ""), so pass it through.
                var data = httpErr.Response.BodyIsJson
                    ? httpErr.Response.Json
                    : JsonValue.Create(httpErr.Response.RawText);
                await ctx.SendJson(httpErr.Status, data);
                return;
            }

            await ctx.SendJson(502, new JsonObject { ["error"] = ChainErrorMessage(err) });
        }
    }

    // ------------------------------------------------------------------
    // small JS-semantics helpers
    // ------------------------------------------------------------------

    // String(number) / JSON number formatting parity (JsJson formats doubles the
    // way V8 does: shortest round-trip, no ".0" on integral values).
    private static string ChainFormatNumber(double value) => JsJson.Stringify(JsonValue.Create(value));

    // JS String() coercion for values that land in template literals / Error messages.
    private static string ChainJsString(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "[object Object]",
        JsonArray arr => string.Join(",", arr.Select(ChainJsString)),
        JsonValue v when JsVal.TryGetStringLenient(v, out var s) => s,
        _ => JsJson.Stringify(node),
    };

    // Number(string) semantics: "" -> 0 (callers pre-check), hex/octal/binary
    // prefixes, decimal/exponent forms, anything else -> NaN.
    private static double ChainJsNumber(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            return 0;
        }

        if (t.Length > 2 && t[0] == '0')
        {
            var radix = t[1] switch
            {
                'x' or 'X' => 16,
                'o' or 'O' => 8,
                'b' or 'B' => 2,
                _ => 0,
            };
            if (radix != 0)
            {
                return ParseRadixInteger(t.Substring(2), radix);
            }
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    private static double ParseRadixInteger(string digits, int radix)
    {
        if (digits.Length == 0)
        {
            return double.NaN;
        }

        var acc = BigInteger.Zero;
        foreach (var c in digits)
        {
            var v = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (v < 0 || v >= radix)
            {
                return double.NaN;
            }
            acc = acc * radix + v;
        }
        return (double)acc;
    }
}
