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
        if (value.TryGetValue<string>(out var s))
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
    /// Format a number the way V8's JSON.stringify would after JSON.parse:
    /// numbers become doubles, printed in shortest round-trip form; integral
    /// values print without a fractional part; exponent (rare) uses "e+"/"e-".
    /// </summary>
    private static string FormatNumber(double d)
    {

        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return "null"; // JSON.stringify(NaN/Infinity) -> null
        }

        // .NET Core 3.0+ default double formatting is shortest round-trippable,
        // same algorithm family as V8. Normalize exponent casing/format.
        var text = d.ToString("R", CultureInfo.InvariantCulture);

        if (text.Contains('E'))
        {
            // .NET: "1E+21" -> JS: "1e+21"
            text = text.Replace("E", "e");
        }

        return text;
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
                if (v.TryGetValue<string>(out var s)) return s.Length > 0;
                if (v.TryGetValue<bool>(out var b)) return b;
                var el = v.GetValue<JsonElement>();
                if (el.ValueKind == JsonValueKind.Number)
                {
                    var d = el.GetDouble();
                    return d != 0 && !double.IsNaN(d);
                }
                return el.ValueKind != JsonValueKind.Null;
            default:
                return false;
        }
    }
}
