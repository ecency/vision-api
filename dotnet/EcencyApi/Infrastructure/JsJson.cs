using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// JavaScript-compatible JSON serialization for <see cref="JsonNode"/>.
///
/// The Node service hashes JSON.stringify() output for HiveSigner code
/// signing/verification (auth-api.ts hsTokenCreate, private-api.ts
/// validateCode). Byte-for-byte parity with V8's JSON.stringify therefore
/// matters: property insertion order is preserved, no whitespace, JS string
/// escaping rules (short escapes for \b \t \n \f \r, \u00XX for other control
/// chars, lone surrogates escaped, everything else raw UTF-8), and JS number
/// formatting (shortest round-trip, integral doubles without a decimal point).
/// Verified against dhive-generated golden vectors in EcencyApi.Tests.
/// </summary>
public static class JsJson
{
    public static string Stringify(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteValue(sb, node);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var kv in obj)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(sb, arr[i]);
                }
                sb.Append(']');
                break;
            case JsonValue value:
                WritePrimitive(sb, value);
                break;
        }
    }

    private static void WritePrimitive(StringBuilder sb, JsonValue value)
    {
        if (JsVal.TryGetStringLenient(value, out var s))
        {
            WriteString(sb, s);
            return;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            sb.Append(b ? "true" : "false");
            return;
        }

        // Parsed values are backed by JsonElement; programmatically built ones
        // by CLR primitives. Handle both.
        if (value.TryGetValue<JsonElement>(out var el))
        {
            if (el.ValueKind == JsonValueKind.Number)
            {
                sb.Append(FormatNumber(el.GetDouble()));
                return;
            }
            sb.Append(el.GetRawText());
            return;
        }

        if (value.TryGetValue<long>(out var l))
        {
            sb.Append(l.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (value.TryGetValue<int>(out var n))
        {
            sb.Append(n.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (value.TryGetValue<double>(out var dbl))
        {
            sb.Append(FormatNumber(dbl));
            return;
        }

        if (value.TryGetValue<decimal>(out var dec))
        {
            sb.Append(FormatNumber((double)dec));
            return;
        }

        // Fallback (shouldn't happen for JSON trees)
        sb.Append(value.ToJsonString());
    }

    /// <summary>
    /// Format a number exactly the way V8 does (ECMA-262 Number::toString, which
    /// JSON.stringify uses): shortest round-trip digits, fixed-point notation for
    /// decimal exponents in (-6, 21], scientific ("d.ddde±X") outside that range.
    /// .NET's "R" is shortest-round-trip too but switches to scientific at
    /// different thresholds (e.g. 1e20 -> "1E+20" while JS prints
    /// "100000000000000000000"), so the digits are re-rendered per the JS rules.
    /// Verified against node-generated vectors in EcencyApi.Tests.
    /// </summary>
    internal static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return "null"; // JSON.stringify(NaN/Infinity) -> null
        }

        if (d == 0)
        {
            return "0"; // covers -0: JSON.stringify(-0) === "0"
        }

        var negative = d < 0;
        var r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);

        // Decompose the shortest-round-trip form into significant digits and the
        // decimal exponent n, where value = 0.digits * 10^n (ECMA notation).
        string digits;
        int n;
        var eIdx = r.IndexOf('E');
        if (eIdx >= 0)
        {
            var mant = r[..eIdx];
            var exp = int.Parse(r[(eIdx + 1)..], CultureInfo.InvariantCulture);
            var dot = mant.IndexOf('.');
            var mdigits = dot >= 0 ? mant.Remove(dot, 1) : mant;
            var intLen = dot >= 0 ? dot : mant.Length;
            digits = mdigits.TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            n = exp + intLen;
        }
        else
        {
            var dot = r.IndexOf('.');
            if (dot < 0)
            {
                digits = r.TrimEnd('0');
                if (digits.Length == 0) digits = "0";
                n = r.Length;
            }
            else
            {
                var mdigits = r.Remove(dot, 1);
                var firstSig = 0;
                while (firstSig < mdigits.Length && mdigits[firstSig] == '0') firstSig++;
                digits = mdigits[firstSig..].TrimEnd('0');
                if (digits.Length == 0) digits = "0";
                n = dot - firstSig;
            }
        }

        var k = digits.Length;
        string result;
        if (k <= n && n <= 21)
        {
            result = digits + new string('0', n - k);
        }
        else if (n is > 0 and <= 21)
        {
            result = digits[..n] + "." + digits[n..];
        }
        else if (n is > -6 and <= 0)
        {
            result = "0." + new string('0', -n) + digits;
        }
        else
        {
            var e = n - 1;
            var mantissa = k == 1 ? digits : digits[..1] + "." + digits[1..];
            result = mantissa + "e" + (e >= 0 ? "+" : "-") +
                     Math.Abs(e).ToString(CultureInfo.InvariantCulture);
        }

        return negative ? "-" + result : result;
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        // Well-formed JSON.stringify (ES2019): paired surrogates pass
                        // through raw, lone surrogates are escaped.
                        if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(c).Append(s[i + 1]);
                            i++;
                        }
                        else
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Truthiness of a JsonNode per JavaScript `if (x)` semantics.</summary>
    public static bool IsTruthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonObject:
            case JsonArray:
                return true;
            case JsonValue v:
                if (JsVal.TryGetStringLenient(v, out var s)) return s.Length > 0;
                if (v.TryGetValue<bool>(out var b)) return b;
                // Parsed values are element-backed; programmatically built ones
                // are CLR-backed and would throw on GetValue<JsonElement>().
                if (v.TryGetValue<JsonElement>(out var el))
                {
                    if (el.ValueKind == JsonValueKind.Number)
                    {
                        var d = el.GetDouble();
                        return d != 0 && !double.IsNaN(d);
                    }
                    return el.ValueKind != JsonValueKind.Null;
                }
                if (v.TryGetValue<long>(out var l)) return l != 0;
                if (v.TryGetValue<int>(out var i)) return i != 0;
                if (v.TryGetValue<double>(out var dbl)) return dbl != 0 && !double.IsNaN(dbl);
                if (v.TryGetValue<decimal>(out var dec)) return dec != 0;
                return true;
            default:
                return false;
        }
    }
}
