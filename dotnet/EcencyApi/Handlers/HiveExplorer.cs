using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/hive-explorer.ts — a helper module (no wired
/// routes) used by the wallet handlers.
///
/// Fetch chain data directly from Hive RPC nodes (HiveClients.Default handles
/// failover across the list) instead of routing through an external REST
/// gateway, which is a single point of failure for the portfolio endpoints.
/// </summary>
public static class HiveExplorer
{
    public sealed record GlobalProps(double HivePerMVests, double HbdApr, double HpApr);

    public static async Task<GlobalProps> FetchGlobalProps()
    {
        try
        {
            var globalDynamic = await HiveClients.Default.GetDynamicGlobalProperties();

            if (!JsJson.IsTruthy(globalDynamic))
            {
                throw new Exception("Invalid global props data");
            }

            var totalFunds = GetVestAmount(globalDynamic!["total_vesting_fund_hive"]);
            var totalShares = GetVestAmount(globalDynamic["total_vesting_shares"]);
            var virtualSupply = GetVestAmount(globalDynamic["virtual_supply"]);

            var hivePerMVests = (totalFunds / totalShares) * 1e6;

            var hbdInterestRateRaw = NumericFieldOrZero(globalDynamic, "hbd_interest_rate");
            var hbdApr = double.IsFinite(hbdInterestRateRaw) ? hbdInterestRateRaw / 100 : 0d;

            var vestingRewardPercentRaw = NumericFieldOrZero(globalDynamic, "vesting_reward_percent");
            var vestingRewardPercent = vestingRewardPercentRaw / 10000;

            var headBlockNumber = NumericFieldOrZero(globalDynamic, "head_block_number");

            // The blockchain exposes the current inflation rate (in basis points) as part
            // of the global props. Prefer that exact value when present so the APR stays
            // perfectly aligned with what on-chain tooling reports.
            var currentInflationRateRaw = NumericFieldOrZero(globalDynamic, "current_inflation_rate");

            const double inflationBase = 9.5;
            const double inflationDecreasePerStep = 0.01;
            const double blocksPerStep = 250000;
            const double inflationFloor = 0.95;
            // Historically Hive did not start lowering the inflation rate until several
            // million blocks into the chain. Mirror that behaviour so the fallback math
            // matches the node implementation when `current_inflation_rate` is missing.
            const double inflationStartBlock = 7000000;
            var inflationReductionSteps = Math.Max(headBlockNumber - inflationStartBlock, 0d) / blocksPerStep;
            var derivedInflationRate = Math.Max(
                inflationFloor,
                inflationBase - Math.Floor(inflationReductionSteps) * inflationDecreasePerStep);

            var inflationRatePercent = currentInflationRateRaw > 0
                ? currentInflationRateRaw / 100
                : derivedInflationRate;

            var hpAprCandidate =
                totalFunds > 0 && virtualSupply > 0 && vestingRewardPercent > 0
                    ? (virtualSupply * (inflationRatePercent / 100) * vestingRewardPercent * 100) / totalFunds
                    : 0d;
            var hpApr = double.IsFinite(hpAprCandidate) ? hpAprCandidate : 0d;

            return new GlobalProps(hivePerMVests, hbdApr, hpApr);
        }
        catch
        {
            throw new Exception("Failed to get globalProps");
        }
    }

    /// <summary>getAccount — fetch raw account data without post processings.</summary>
    public static async Task<JsonNode> GetAccount(string? username)
    {
        try
        {
            var data = await HiveClients.Default.GetAccounts(new[] { username });

            if (data is { Count: > 0 })
            {
                return data[0]!;
            }

            // In JS a non-array result TypeErrors on data.length instead; either way the
            // catch below rewrites the error to the fixed message.
            throw new Exception($"Account not found, {(data is null ? "null" : JsJson.Stringify(data))}");
        }
        catch
        {
            throw new Exception("Failed to get account data");
        }
    }

    // util.ts parseToken — shared with the wallet handlers.
    // condenser_api (what dhive calls) returns string assets like "123.456 HIVE".
    // database_api / NAI responses encode the same value as { amount, precision }.
    // Accept both forms so a balance never silently parses to 0 if the upstream
    // asset representation changes.
    public static double ParseToken(JsonNode? val)
    {
        if (val is JsonObject obj)
        {
            // Number(val.amount) — absent key is undefined -> NaN
            var amount = obj.TryGetPropertyValue("amount", out var amountNode)
                ? JsNumber(amountNode)
                : double.NaN;
            var precisionRaw = obj.TryGetPropertyValue("precision", out var precisionNode)
                ? JsNumber(precisionNode)
                : double.NaN;
            // Number(val.precision) || 0
            var precision = double.IsNaN(precisionRaw) || precisionRaw == 0d ? 0d : precisionRaw;
            return double.IsFinite(amount) ? amount / Math.Pow(10, precision) : 0d;
        }

        if (val is JsonArray)
        {
            // JS: typeof [] === 'object' -> object branch; missing .amount -> NaN -> 0
            return 0d;
        }

        if (val is not JsonValue jv || !JsVal.TryGetStringLenient(jv, out var s))
        {
            return 0d;
        }

        // checks if first part of string is float
        if (!TokenRegex.IsMatch(s))
        {
            return 0d;
        }

        return ParseFloatJs(s.Split(' ')[0]);
    }

    // JS: /^\-?[0-9]+(e[0-9]+)?(\.[0-9]+)? .*$/ — '.' excludes JS line terminators
    // (\n \r U+2028 U+2029) and '$' (non-multiline) anchors to the very end of input.
    private static readonly Regex TokenRegex =
        new("^-?[0-9]+(e[0-9]+)?(\\.[0-9]+)? [^\\n\\r\\u2028\\u2029]*\\z", RegexOptions.Compiled);

    // parseFloat: longest StrDecimalLiteral prefix ("1e3.5" parses as "1e3" -> 1000).
    private static readonly Regex ParseFloatPrefix =
        new("^[+-]?([0-9]+(\\.[0-9]*)?|\\.[0-9]+)([eE][+-]?[0-9]+)?", RegexOptions.Compiled);

    private static double ParseFloatJs(string s)
    {
        var m = ParseFloatPrefix.Match(s);
        if (!m.Success)
        {
            return double.NaN;
        }
        return double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // hive-explorer.ts _getVestAmount: string -> parseToken; otherwise
    // value.amount / Math.pow(10, value.precision). null/undefined throw (the JS
    // TypeError), which the outer catch rewrites to "Failed to get globalProps".
    private static double GetVestAmount(JsonNode? value)
    {
        if (value is JsonValue jv && JsVal.TryGetStringLenient(jv, out _))
        {
            return ParseToken(value);
        }

        if (value is JsonObject obj)
        {
            var amount = obj.TryGetPropertyValue("amount", out var amountNode)
                ? JsNumber(amountNode)
                : double.NaN;
            var precision = obj.TryGetPropertyValue("precision", out var precisionNode)
                ? JsNumber(precisionNode)
                : double.NaN;
            return amount / Math.Pow(10, precision);
        }

        if (value is null)
        {
            throw new Exception("Cannot read properties of null (reading 'amount')");
        }

        // numbers/booleans/arrays: .amount / .precision are undefined -> NaN / NaN
        return double.NaN;
    }

    // typeof v === 'number' ? v : Number(v) || 0
    private static double NumericFieldOrZero(JsonNode globalDynamic, string key)
    {
        var node = globalDynamic[key];
        if (node is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number)
        {
            return JsNumber(node);
        }
        // absent (Number(undefined) -> NaN) and JSON null (Number(null) -> 0) both
        // collapse to the || 0 fallback
        var n = node is null ? double.NaN : JsNumber(node);
        return double.IsNaN(n) || n == 0d ? 0d : n;
    }

    // Number(x) coercion for a JSON value. Callers map absent keys to NaN
    // themselves, since Number(undefined) is NaN while Number(null) is 0.
    private static double JsNumber(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return 0d; // Number(null)
            case JsonObject:
                return double.NaN; // Number({}) -> NaN
            case JsonArray arr:
                // Number([]) -> 0; Number([x]) coerces via [x].toString()
                if (arr.Count == 0)
                {
                    return 0d;
                }
                if (arr.Count == 1)
                {
                    var el = arr[0];
                    if (el is null)
                    {
                        return 0d; // [null] -> "" -> 0
                    }
                    if (el is JsonArray nested)
                    {
                        return JsNumber(nested);
                    }
                    if (el is JsonValue ev)
                    {
                        if (ev.TryGetValue<bool>(out _))
                        {
                            return double.NaN; // "true"/"false" don't parse
                        }
                        if (JsVal.TryGetStringLenient(ev, out var es))
                        {
                            return JsNumberString(es);
                        }
                        return JsNumber(el); // number round-trips through toString
                    }
                    return double.NaN; // [{}] -> "[object Object]"
                }
                return double.NaN; // "a,b" never parses as a number
            case JsonValue value:
                if (value.TryGetValue<bool>(out var b))
                {
                    return b ? 1d : 0d;
                }
                if (JsVal.TryGetStringLenient(value, out var str))
                {
                    return JsNumberString(str);
                }
                if (value.TryGetValue<double>(out var d))
                {
                    return d;
                }
                if (value.TryGetValue<long>(out var l))
                {
                    return l;
                }
                if (value.TryGetValue<int>(out var i))
                {
                    return i;
                }
                if (value.TryGetValue<decimal>(out var dec))
                {
                    return (double)dec;
                }
                return double.NaN;
            default:
                return double.NaN;
        }
    }

    // Number("...") string coercion (ES StringNumericLiteral).
    private static double JsNumberString(string s)
    {
        var t = TrimJsWhiteSpace(s);
        if (t.Length == 0)
        {
            return 0d; // Number("") / whitespace-only -> 0
        }

        // hex / binary / octal integer literals (no sign allowed)
        if (t.Length > 2 && t[0] == '0' && t[1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            var radix = t[1] is 'x' or 'X' ? 16 : t[1] is 'b' or 'B' ? 2 : 8;
            var acc = 0d;
            for (var i = 2; i < t.Length; i++)
            {
                var digit = DigitValue(t[i]);
                if (digit < 0 || digit >= radix)
                {
                    return double.NaN;
                }
                acc = acc * radix + digit;
            }
            return acc;
        }

        if (t == "Infinity" || t == "+Infinity")
        {
            return double.PositiveInfinity;
        }
        if (t == "-Infinity")
        {
            return double.NegativeInfinity;
        }

        return double.TryParse(
            t,
            NumberStyles.Float & ~(NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite),
            CultureInfo.InvariantCulture,
            out var v)
            ? v
            : double.NaN;
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static string TrimJsWhiteSpace(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsJsWhiteSpace(s[start]))
        {
            start++;
        }
        while (end > start && IsJsWhiteSpace(s[end - 1]))
        {
            end--;
        }
        return s.Substring(start, end - start);
    }

    // WhiteSpace + LineTerminator per ECMA-262 (differs from char.IsWhiteSpace:
    // U+0085 is not JS whitespace while U+FEFF is).
    private static bool IsJsWhiteSpace(char c) =>
        c is '\t' or '\n' or '\v' or '\f' or '\r' or ' '
            or '\u00A0' or '\u1680' or '\u2028' or '\u2029'
            or '\u202F' or '\u205F' or '\u3000' or '\uFEFF'
        || (c >= '\u2000' && c <= '\u200A');
}
