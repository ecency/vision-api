using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of wallet-api.ts lines 1-799: token-action builders, chain config,
/// currency normalization, and the market-data price extraction/conversion
/// helpers. All arithmetic mirrors JS double semantics; JSON is JsonNode.
/// </summary>
public static partial class WalletApi
{
    // ---- token action builders (each returns [{id}, ...]) ----------------

    private static JsonArray Actions(params string[] ids)
    {
        var arr = new JsonArray();
        foreach (var id in ids) arr.Add(new JsonObject { ["id"] = id });
        return arr;
    }

    private static readonly string[] EcencyActionIds = { "ecency_point_transfer", "promote", "boost" };
    internal static JsonArray EcencyActions() => Actions(EcencyActionIds);

    internal static JsonArray BuildHiveActions(double savings)
    {
        var ids = new List<string> { "transfer", "transfer_to_savings", "transfer_to_vesting" };
        if (savings > 0) ids.Add("transfer_from_savings");
        ids.Add("swap_token");
        ids.Add("recurrent_transfer");
        return Actions(ids.ToArray());
    }

    internal static JsonArray HpActions() => Actions("delegate_vesting_shares", "withdraw_vesting");

    internal static JsonArray BuildHbdActions(double savings)
    {
        var ids = new List<string> { "transfer", "transfer_to_savings", "convert" };
        if (savings > 0) ids.Add("transfer_from_savings");
        ids.Add("swap_token");
        ids.Add("recurrent_transfer");
        return Actions(ids.ToArray());
    }

    internal static JsonArray SpkActions() => Actions("spkcc_spk_send");

    internal static JsonArray LarynxActions() => Actions(
        "transfer_larynx_spk", "spkcc_send", "spkcc_power_grant", "spkcc_power_up", "spkcc_power_down");

    internal static JsonArray ChainActions() => Actions("receive");

    internal const double PointsUsdRate = 0.002;

    // ---- chain config ----------------------------------------------------

    internal sealed record ChainConfig(string Name, string Symbol, int Decimals, string[]? Aliases, string? IconUrl);

    internal static readonly Dictionary<string, ChainConfig> ChainConfigs = new()
    {
        ["btc"] = new("Bitcoin", "BTC", 8, new[] { "bitcoin" }, Constants.AssetIconUrls["BTC"]),
        ["eth"] = new("Ethereum", "ETH", 18, null, Constants.AssetIconUrls["ETH"]),
        ["bnb"] = new("BNB Chain", "BNB", 18, null, Constants.AssetIconUrls["BNB"]),
        ["sol"] = new("Solana", "SOL", 9, null, Constants.AssetIconUrls["SOL"]),
    };

    private static readonly Dictionary<string, (string Key, ChainConfig Config)> ChainSymbolLookup = BuildLookup();

    private static Dictionary<string, (string, ChainConfig)> BuildLookup()
    {
        var lookup = new Dictionary<string, (string, ChainConfig)>();
        foreach (var (key, config) in ChainConfigs)
        {
            var baseSymbol = config.Symbol.ToLowerInvariant();
            if (!lookup.ContainsKey(baseSymbol)) lookup[baseSymbol] = (key, config);
            lookup[key.ToLowerInvariant()] = (key, config);
            if (config.Aliases != null)
            {
                foreach (var alias in config.Aliases)
                {
                    var normalized = alias.ToLowerInvariant();
                    if (!lookup.ContainsKey(normalized)) lookup[normalized] = (key, config);
                }
            }
        }
        return lookup;
    }

    /// <summary>firstDefined(...values): first value that is not undefined/null.</summary>
    internal static JsonNode? FirstDefined(params JsonNode?[] values)
    {
        foreach (var v in values)
        {
            if (v != null) return v;
        }
        return null;
    }

    internal static (string Key, ChainConfig Config)? ResolveChainConfig(JsonNode? value, JsonNode? fallback = null)
    {
        foreach (var candidate in new[] { value, fallback })
        {
            if (candidate == null) continue;
            var normalized = JsVal.ToJsString(candidate).Trim().ToLowerInvariant();
            if (normalized.Length == 0) continue;
            if (ChainSymbolLookup.TryGetValue(normalized, out var match)) return match;
        }
        return null;
    }

    // ---- numeric parsing -------------------------------------------------

    private static readonly Regex NumberInStringRegex = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>parseMaybeNumber(unknown): number|null with JS Number() + regex fallback.</summary>
    internal static double? ParseMaybeNumber(JsonNode? value)
    {
        var num = JsVal.AsNumber(value);
        if (num.HasValue) return num.Value; // finite number
        if (JsVal.IsNumber(value)) return null; // non-finite number -> null

        var s = JsVal.AsString(value);
        if (s == null) return null;
        var trimmed = s.Trim();
        if (trimmed.Length == 0) return null;

        var direct = JsVal.NumberCoerce(trimmed);
        if (!double.IsNaN(direct)) return direct;

        var m = NumberInStringRegex.Match(trimmed);
        if (m.Success)
        {
            var parsed = JsVal.NumberCoerce(m.Value);
            return double.IsNaN(parsed) ? null : parsed;
        }
        return null;
    }

    internal static double? PickFirstNumericValue(params JsonNode?[] candidates)
    {
        foreach (var c in candidates)
        {
            var parsed = ParseMaybeNumber(c);
            if (parsed.HasValue) return parsed.Value;
        }
        return null;
    }

    private static readonly Regex IntegerRegex = new(@"^-?\d+$", RegexOptions.Compiled);

    internal static double ConvertBaseUnitsToAmount(string? value, int decimals)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        var normalized = value.Trim();

        if (IntegerRegex.IsMatch(normalized))
        {
            var negative = normalized.StartsWith("-");
            var digits = negative ? normalized[1..] : normalized;
            var padded = digits.PadLeft(decimals + 1, '0');
            var integerPart = padded[..(padded.Length - decimals)];
            if (integerPart.Length == 0) integerPart = "0";
            var fractionalPart = padded[(padded.Length - decimals)..].TrimEnd('0');
            var combined = (negative ? "-" : "") + integerPart;
            var formatted = fractionalPart.Length > 0 ? $"{combined}.{fractionalPart}" : combined;
            var parsed = JsVal.NumberCoerce(formatted);
            return double.IsNaN(parsed) ? 0 : parsed;
        }

        var numeric = JsVal.NumberCoerce(normalized);
        return double.IsNaN(numeric) ? 0 : numeric;
    }

    private static readonly Dictionary<string, int> UnitDecimals = new()
    {
        ["wei"] = 18, ["lamports"] = 9, ["sun"] = 6, ["nanotons"] = 9, ["octas"] = 8, ["satoshi"] = 8,
    };

    internal static double ConvertChainBalanceToAmount(ChainBalanceResponse? balanceResponse, int decimals)
    {
        if (balanceResponse == null) return 0;

        var unit = balanceResponse.Unit;
        var normalizedUnit = unit.Trim().ToLowerInvariant();
        var resolvedDecimals = UnitDecimals.TryGetValue(normalizedUnit, out var ud) ? ud : decimals;

        var balance = balanceResponse.Balance;
        if (balance == null) return 0;

        var trimmed = balance.Trim();
        if (trimmed.Length == 0) return 0;

        if (normalizedUnit == "btc")
        {
            var parsed = JsVal.NumberCoerce(trimmed);
            return double.IsNaN(parsed) ? 0 : parsed;
        }

        return ConvertBaseUnitsToAmount(trimmed, resolvedDecimals);
    }

    internal static string NormalizeCurrency(JsonNode? currency) => NormalizeCurrency(JsVal.AsString(currency));

    internal static string NormalizeCurrency(string? currency)
    {
        if (currency == null) return "usd";
        var t = currency.Trim().ToLowerInvariant();
        return t.Length > 0 ? t : "usd";
    }

    // ---- price extraction ------------------------------------------------

    internal static double GetUsdToCurrencyRate(JsonNode? marketData, string currency)
    {
        var currencyKey = currency.ToLowerInvariant();
        if (currencyKey == "usd") return 1.0;

        var containers = CollectContainers(marketData, 3);
        var referenceTokens = new[] { "hive", "btc", "eth", "hbd" };

        foreach (var reference in referenceTokens)
        {
            var tokenKey = reference.ToLowerInvariant();
            var candidates = new List<JsonNode?>();

            foreach (var container in containers)
            {
                if (container == null || !JsVal.IsObjectOrArray(container)) continue;

                if (container is JsonArray arr)
                {
                    foreach (var entry in arr)
                    {
                        if (entry == null || !JsVal.IsObjectOrArray(entry)) continue;
                        var symbolKeys = new[] { "symbol", "token", "name", "id", "ticker" };
                        var symbolVal = symbolKeys
                            .Select(k => JsVal.AsString(JsVal.Prop(entry, k)))
                            .FirstOrDefault(v => v != null && v.Trim().ToLowerInvariant() == tokenKey);
                        if (symbolVal != null) candidates.Add(entry);
                    }
                }
                else
                {
                    var directMatch = JsVal.Prop(container, tokenKey) ?? JsVal.Prop(container, reference.ToUpperInvariant());
                    if (directMatch != null) candidates.Add(directMatch);
                }
            }

            double? priceInUsd = null;
            double? priceInCurrency = null;

            foreach (var candidate in candidates)
            {
                if (priceInUsd is null or 0)
                    priceInUsd = ExtractPriceFromValue(candidate, "usd");
                if (priceInCurrency is null or 0)
                    priceInCurrency = ExtractPriceFromValue(candidate, currencyKey);
                if (priceInUsd is > 0 && priceInCurrency is > 0) break;
            }

            if (priceInUsd is > 0 && priceInCurrency is > 0)
                return priceInCurrency.Value / priceInUsd.Value;
        }

        return 0;
    }

    /// <summary>parseBoolean(unknown): true/false/undefined(null).</summary>
    internal static bool? ParseBoolean(JsonNode? value)
    {
        if (value == null) return null;
        var b = JsVal.AsBool(value);
        if (b.HasValue) return b;

        var num = JsVal.AsNumber(value);
        if (num.HasValue)
        {
            if (num.Value == 1) return true;
            if (num.Value == 0) return false;
        }

        var s = JsVal.AsString(value);
        if (s != null)
        {
            var normalized = s.Trim().ToLowerInvariant();
            if (normalized.Length == 0) return null;
            if (normalized is "true" or "1" or "yes" or "on") return true;
            if (normalized is "false" or "0" or "no" or "off") return false;
        }
        return null;
    }

    internal static List<string> CreateCurrencyKeyVariants(string currencyKey, string? baseKey = null)
    {
        var normalizedCurrency = currencyKey.ToLowerInvariant();
        var upperCurrency = normalizedCurrency.ToUpperInvariant();
        var capitalizedCurrency = normalizedCurrency.Length > 0
            ? char.ToUpperInvariant(normalizedCurrency[0]) + normalizedCurrency[1..]
            : normalizedCurrency;

        var currencyVariants = new[] { normalizedCurrency, upperCurrency, capitalizedCurrency };

        if (string.IsNullOrEmpty(baseKey))
            return currencyVariants.Distinct().ToList();

        var normalizedBase = baseKey.ToLowerInvariant();
        var upperBase = normalizedBase.ToUpperInvariant();
        var capitalizedBase = normalizedBase.Length > 0
            ? char.ToUpperInvariant(normalizedBase[0]) + normalizedBase[1..]
            : normalizedBase;

        var baseVariants = new[] { normalizedBase, upperBase, capitalizedBase };
        var separators = new[] { "", "_", "-", ".", "/" };
        var results = new List<string>();
        var seen = new HashSet<string>();
        void Add(string s) { if (seen.Add(s)) results.Add(s); }

        foreach (var cv in currencyVariants)
            foreach (var bv in baseVariants)
                foreach (var sep in separators)
                {
                    Add($"{cv}{sep}{bv}");
                    Add($"{bv}{sep}{cv}");
                }

        return results;
    }

    internal static double? ExtractPriceFromValue(JsonNode? value, string currencyKey, HashSet<JsonNode>? visited = null)
    {
        var num = JsVal.AsNumber(value);
        if (num.HasValue) return num.Value;
        if (JsVal.IsNumber(value)) return null; // non-finite number

        var str = JsVal.AsString(value);
        if (str != null)
        {
            var parsed = ParseMaybeNumber(value);
            return parsed;
        }

        if (value == null || !JsVal.IsObjectOrArray(value)) return null;

        visited ??= new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        if (!visited.Add(value)) return null;

        // token.quotes.{currency}.price
        var quotes = JsVal.Prop(value, "quotes");
        if (quotes is JsonObject)
        {
            var currencyQuote = JsVal.Prop(quotes, currencyKey);
            if (currencyQuote is JsonObject)
            {
                var price = JsVal.AsNumber(JsVal.Prop(currencyQuote, "price"));
                if (price.HasValue) return price.Value;
            }
        }

        if (value is JsonArray arr)
        {
            foreach (var entry in arr)
            {
                var result = ExtractPriceFromValue(entry, currencyKey, visited);
                if (result.HasValue) return result;
            }
            return null;
        }

        foreach (var key in CreateCurrencyKeyVariants(currencyKey))
        {
            if (JsVal.HasProp(value, key))
            {
                var result = ExtractPriceFromValue(JsVal.Prop(value, key), currencyKey, visited);
                if (result.HasValue) return result;
            }
        }

        var baseKeys = new[] { "price", "rate", "value", "amount", "last", "lastPrice", "lastValue" };
        foreach (var baseKey in baseKeys)
        {
            foreach (var key in CreateCurrencyKeyVariants(currencyKey, baseKey))
            {
                if (JsVal.HasProp(value, key))
                {
                    var result = ExtractPriceFromValue(JsVal.Prop(value, key), currencyKey, visited);
                    if (result.HasValue) return result;
                }
            }
        }

        foreach (var baseKey in baseKeys)
        {
            if (JsVal.HasProp(value, baseKey))
            {
                var result = ExtractPriceFromValue(JsVal.Prop(value, baseKey), currencyKey, visited);
                if (result.HasValue) return result;
            }
        }

        var nestedKeys = new[]
        {
            "data", "result", "results", "quote", "quotes", "current", "current_price",
            "currentPrice", "market", "markets", "metrics", "stats", "values", "priceData", "price_data",
        };
        foreach (var nestedKey in nestedKeys)
        {
            if (JsVal.HasProp(value, nestedKey))
            {
                var result = ExtractPriceFromValue(JsVal.Prop(value, nestedKey), currencyKey, visited);
                if (result.HasValue) return result;
            }
        }

        return null;
    }

    internal static List<JsonNode?> CollectContainers(JsonNode? root, int depth, HashSet<JsonNode>? visited = null)
    {
        if (root == null || !JsVal.IsObjectOrArray(root)) return new List<JsonNode?>();
        visited ??= new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        if (!visited.Add(root) || depth < 0) return new List<JsonNode?>();

        var results = new List<JsonNode?> { root };
        if (depth == 0) return results;

        var nestedKeys = new[]
        {
            "data", "result", "results", "payload", "response", "market", "markets", "prices",
            "priceData", "price_data", "tokens", "tokenPrices", "token_prices", "tokenprices",
            "items", "entries", "list", "assets", "values",
        };

        foreach (var key in nestedKeys)
        {
            var nested = JsVal.Prop(root, key);
            if (nested != null && JsVal.IsObjectOrArray(nested))
                results.AddRange(CollectContainers(nested, depth - 1, visited));
        }

        if (root is JsonArray arr)
        {
            foreach (var entry in arr)
            {
                if (entry != null && JsVal.IsObjectOrArray(entry))
                    results.AddRange(CollectContainers(entry, depth - 1, visited));
            }
        }

        return results;
    }

    internal static double GetTokenPrice(JsonNode? marketData, string token, string currency)
    {
        if (marketData == null || string.IsNullOrEmpty(token)) return 0;

        var currencyKey = currency.ToLowerInvariant();
        var tokenKey = token.ToLowerInvariant();
        var upperToken = token.ToUpperInvariant();

        if (tokenKey == currencyKey) return 1.0;

        var containers = CollectContainers(marketData, 3);
        var candidates = new List<JsonNode?>();
        void AddCandidate(JsonNode? v) { if (v != null) candidates.Add(v); }

        var candidateListKeys = new[]
        {
            "tokens", "tokenPrices", "token_prices", "tokenprices", "prices",
            "markets", "market", "items", "entries", "list", "assets",
        };
        var symbolKeys = new[] { "symbol", "token", "name", "id", "ticker" };

        foreach (var container in containers)
        {
            if (container == null) continue;

            if (container is JsonArray arr)
            {
                foreach (var entry in arr)
                {
                    if (entry == null || !JsVal.IsObjectOrArray(entry)) continue;
                    var symbolCandidate = FirstDefined(symbolKeys.Select(k => JsVal.Prop(entry, k)).ToArray());
                    var sc = JsVal.AsString(symbolCandidate);
                    if (sc != null && sc.Trim().ToLowerInvariant() == tokenKey) AddCandidate(entry);
                }
                continue;
            }

            AddCandidate(JsVal.Prop(container, tokenKey));
            AddCandidate(JsVal.Prop(container, upperToken));
            if (token != tokenKey && token != upperToken) AddCandidate(JsVal.Prop(container, token));

            foreach (var listKey in candidateListKeys)
            {
                var nestedList = JsVal.Prop(container, listKey);
                if (nestedList == null) continue;

                if (nestedList is JsonArray nl)
                {
                    foreach (var entry in nl)
                    {
                        if (entry == null || !JsVal.IsObjectOrArray(entry)) continue;
                        var symbolCandidate = FirstDefined(symbolKeys.Select(k => JsVal.Prop(entry, k)).ToArray());
                        var sc = JsVal.AsString(symbolCandidate);
                        if (sc != null && sc.Trim().ToLowerInvariant() == tokenKey) AddCandidate(entry);
                    }
                }
                else if (JsVal.IsObjectOrArray(nestedList))
                {
                    AddCandidate(JsVal.Prop(nestedList, tokenKey));
                    AddCandidate(JsVal.Prop(nestedList, upperToken));
                    if (token != tokenKey && token != upperToken) AddCandidate(JsVal.Prop(nestedList, token));
                }
            }
        }

        foreach (var candidate in candidates)
        {
            var price = ExtractPriceFromValue(candidate, currencyKey);
            if (price is > 0) return price.Value;
        }

        if (currencyKey != "usd")
        {
            var priceInUsd = GetTokenPrice(marketData, token, "usd");
            if (priceInUsd > 0)
            {
                var usdToCurrencyRate = GetUsdToCurrencyRate(marketData, currencyKey);
                if (usdToCurrencyRate > 0) return priceInUsd * usdToCurrencyRate;
            }
        }

        return 0;
    }

    internal static double ConvertUsdRateToCurrency(JsonNode? marketData, string currency, double usdRate)
    {
        var normalizedCurrency = NormalizeCurrency(currency);

        if (!double.IsFinite(usdRate) || usdRate <= 0) return 0;
        if (normalizedCurrency == "usd") return usdRate;

        var referenceTokens = new[] { "hive", "hbd", "btc", "eth" };
        foreach (var reference in referenceTokens)
        {
            var priceInUsd = GetTokenPrice(marketData, reference, "usd");
            var priceInTarget = GetTokenPrice(marketData, reference, normalizedCurrency);
            if (priceInUsd > 0 && priceInTarget > 0)
            {
                var converted = usdRate * (priceInTarget / priceInUsd);
                if (double.IsFinite(converted) && converted > 0) return converted;
            }
        }

        var direct = GetTokenPrice(marketData, "usd", normalizedCurrency);
        if (direct > 0 && double.IsFinite(direct))
        {
            var converted = usdRate * direct;
            if (double.IsFinite(converted) && converted > 0) return converted;
        }

        var inverse = GetTokenPrice(marketData, normalizedCurrency, "usd");
        if (inverse > 0 && double.IsFinite(inverse))
        {
            var converted = usdRate / inverse;
            if (double.IsFinite(converted) && converted > 0) return converted;
        }

        return usdRate;
    }
}
