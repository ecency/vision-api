using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// JavaScript value semantics over System.Text.Json.Nodes — the primitives the
/// wallet portfolio port leans on: typeof checks, property access that yields
/// null for missing keys, and the parseFloat / Number() coercions (which differ:
/// Number("") is 0, parseFloat("") is NaN; Number("12abc") is NaN, parseFloat
/// takes the leading number). Kept separate so the behavior is testable in isolation.
/// </summary>
public static class JsVal
{
    // ---- typeof ----------------------------------------------------------

    public static bool IsString(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<JsonElement>(out var e)
            ? e.ValueKind == JsonValueKind.String
            : n is JsonValue v2 && JsVal.TryGetStringLenient(v2, out _);

    public static bool IsNumber(JsonNode? n)
    {
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<JsonElement>(out var e)) return e.ValueKind == JsonValueKind.Number;
        return v.TryGetValue<double>(out _) || v.TryGetValue<long>(out _) || v.TryGetValue<int>(out _);
    }

    public static bool IsBool(JsonNode? n)
    {
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<JsonElement>(out var e))
            return e.ValueKind is JsonValueKind.True or JsonValueKind.False;
        return v.TryGetValue<bool>(out _);
    }

    /// <summary>typeof x === "object" (JS): true for arrays and objects, NOT null.</summary>
    public static bool IsObjectOrArray(JsonNode? n) => n is JsonObject or JsonArray;

    // ---- accessors -------------------------------------------------------

    /// <summary>
    /// String extraction tolerant of lone-surrogate \u escapes. JavaScript's
    /// JSON.parse accepts them (JS strings are arbitrary UTF-16), but
    /// System.Text.Json throws InvalidOperationException when materializing
    /// such strings — which turned payloads the Node service handled fine into
    /// 500s. Falls back to manually unescaping the raw JSON text with JS
    /// semantics (\uXXXX decodes to the code unit, paired or not).
    /// </summary>
    public static bool TryGetStringLenient(JsonValue v, out string value)
    {
        try
        {
            if (v.TryGetValue<string>(out value!))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // incomplete surrogate pair in the JSON text; fall through
        }

        if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = UnescapeJsonStringLenient(el.GetRawText());
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>Unescape a raw JSON string literal (including quotes) with JS semantics.</summary>
    internal static string UnescapeJsonStringLenient(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length - 1; i++)
        {
            var c = raw[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            i++;
            switch (raw[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    sb.Append((char)ushort.Parse(raw.AsSpan(i + 1, 4),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The JSON string value, or null when the node is not a string.</summary>
    public static string? AsString(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (JsVal.TryGetStringLenient(v, out var s)) return s;
            if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
        }
        return null;
    }

    /// <summary>The finite double value, or null when not a JS-finite number.</summary>
    public static double? AsNumber(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        double d;
        if (v.TryGetValue<JsonElement>(out var e))
        {
            if (e.ValueKind != JsonValueKind.Number) return null;
            d = e.GetDouble();
        }
        else if (v.TryGetValue<double>(out var dd)) d = dd;
        else if (v.TryGetValue<long>(out var l)) d = l;
        else if (v.TryGetValue<int>(out var i)) d = i;
        else return null;
        return double.IsFinite(d) ? d : null;
    }

    public static bool? AsBool(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<JsonElement>(out var e))
        {
            if (e.ValueKind == JsonValueKind.True) return true;
            if (e.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    /// <summary>obj[key] — null when n is not an object or the key is absent.</summary>
    public static JsonNode? Prop(JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>Whether n is an object that owns key (Object.prototype.hasOwnProperty).</summary>
    public static bool HasProp(JsonNode? n, string key) =>
        n is JsonObject o && o.ContainsKey(key);

    // ---- coercions -------------------------------------------------------

    /// <summary>JS parseFloat: leading optional sign, digits, decimal, exponent; NaN otherwise.</summary>
    public static double ParseFloat(string? s)
    {
        if (s == null) return double.NaN;
        var i = 0;
        var n = s.Length;
        while (i < n && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '\f' || s[i] == '\v')) i++;
        var start = i;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;

        // Infinity
        if (i < n && (s[i] == 'I') && s.AsSpan(i).StartsWith("Infinity"))
        {
            var sign = s[start] == '-' ? -1.0 : 1.0;
            return sign * double.PositiveInfinity;
        }

        var digitsStart = i;
        while (i < n && char.IsAsciiDigit(s[i])) i++;
        if (i < n && s[i] == '.')
        {
            i++;
            while (i < n && char.IsAsciiDigit(s[i])) i++;
        }
        if (i == digitsStart || (i == digitsStart + 1 && s[digitsStart] == '.'))
        {
            // no digits consumed
            return double.NaN;
        }
        // exponent
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            var save = i;
            i++;
            if (i < n && (s[i] == '+' || s[i] == '-')) i++;
            if (i < n && char.IsAsciiDigit(s[i]))
            {
                while (i < n && char.IsAsciiDigit(s[i])) i++;
            }
            else
            {
                i = save; // exponent not well-formed; stop before 'e'
            }
        }

        var slice = s.Substring(start, i - start);
        return double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : double.NaN;
    }

    /// <summary>JS Number(string): whole (trimmed) string must be numeric; "" -> 0; else NaN.</summary>
    public static double NumberCoerce(string? s)
    {
        if (s == null) return double.NaN;
        var t = s.Trim();
        if (t.Length == 0) return 0;
        if (t is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (t == "-Infinity") return double.NegativeInfinity;
        // hex / binary / octal literals
        if (t.Length > 2 && t[0] == '0')
        {
            var p = char.ToLowerInvariant(t[1]);
            try
            {
                if (p == 'x') return (double)Convert.ToInt64(t[2..], 16);
                if (p == 'o') return (double)Convert.ToInt64(t[2..], 8);
                if (p == 'b') return (double)Convert.ToInt64(t[2..], 2);
            }
            catch { return double.NaN; }
        }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : double.NaN;
    }

    /// <summary>JS String(x) coercion for the values wallet code interpolates.</summary>
    public static string ToJsString(JsonNode? n)
    {
        switch (n)
        {
            case null:
                return "null"; // String(null); undefined is handled by callers as "undefined"
            case JsonArray arr:
                return string.Join(",", arr.Select(e => e is null ? "" : ToJsString(e)));
            case JsonObject:
                return "[object Object]";
            case JsonValue v:
                if (JsVal.TryGetStringLenient(v, out var s)) return s;
                if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                var num = AsNumber(n);
                if (num.HasValue) return JsNumberToString(num.Value);
                if (v.TryGetValue<JsonElement>(out var e))
                {
                    if (e.ValueKind == JsonValueKind.Null) return "null";
                    if (e.ValueKind == JsonValueKind.Number) return JsNumberToString(e.GetDouble());
                }
                return v.ToString();
            default:
                return "";
        }
    }

    /// <summary>Number -> string per ECMA-262 Number::toString (same digits and
    /// fixed/scientific thresholds as V8; shared with JsJson.FormatNumber).</summary>
    public static string JsNumberToString(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        return JsJson.FormatNumber(d);
    }
}
