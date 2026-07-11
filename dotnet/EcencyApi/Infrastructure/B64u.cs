using System.Text;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Port of the b64u helpers (auth-api.ts / common/util/b64.ts).
/// Encoding: standard base64 of the UTF-8 bytes, then + -> -, / -> _, = -> .
/// (the client-side b64uEnc uses the same mapping, so tokens round-trip).
/// </summary>
public static class B64u
{
    public static string Encode(string str)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(str));
        return b64.Replace('+', '-').Replace('/', '_').Replace('=', '.');
    }

    /// <summary>
    /// Normalize base64url back to standard base64 and decode to UTF-8. Mirrors
    /// the normalization used by validateCode/decodeToken: - -> +, _ -> /, . -> =.
    /// Returns null when the input is not valid base64 (Node's Buffer.from is
    /// lenient — it skips invalid chars — so we replicate that leniency).
    /// </summary>
    public static byte[]? DecodeLenient(string code)
    {
        var normalized = code.Replace('-', '+').Replace('_', '/').Replace('.', '=');

        // Node Buffer.from(str, "base64") tolerates missing padding and stray
        // characters. Strip anything outside the base64 alphabet, then fix padding.
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')
            {
                sb.Append(c);
            }
            else if (c == '=')
            {
                break; // padding starts — Node stops decoding at padding
            }
        }

        // Node decodes as many complete groups as possible; a single leftover
        // base64 char contributes nothing.
        var len = sb.Length - (sb.Length % 4 == 1 ? 1 : 0);
        sb.Length = len;
        var rem = len % 4;
        if (rem > 0) sb.Append('=', 4 - rem);

        try
        {
            return Convert.FromBase64String(sb.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
