using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/private-api.ts lines 1-185 plus 890-900:
/// validateCode (HiveSigner code validation), requireAuthedUsername, and the
/// simple pipe handlers (receivedVesting, receivedRC, rewardedCommunities,
/// proMembers).
/// </summary>
public static partial class PrivateApi
{
    // Module-level hivesigner account cache (plain fields; refresh races are
    // benign, matching the Node module variables).
    private static JsonNode? _hivesignerAccountCache;
    private static long _hivesignerCacheTime;
    private const long HiveSignerCacheTtlMs = 24 * 60 * 60 * 1000; // 24 hours

    // String.prototype.trim character set (ECMAScript WhiteSpace + LineTerminator,
    // including U+FEFF which .NET's Trim() does not strip).
    private static readonly char[] JsWhitespace =
    {
        '\t', '\n', '\v', '\f', '\r', ' ', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006',
        '\u2007', '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F',
        '\u205F', '\u3000', '\uFEFF',
    };

    /// <summary>
    /// validateCode(req): resolves the validated username from the signed
    /// HiveSigner-style code in the request body, or null (TS returns false).
    /// </summary>
    public static async Task<string?> ValidateCode(JsonObject body)
    {
        var hasCode = body.TryGetPropertyValue("code", out var codeNode);
        string? code = null;
        if (codeNode is JsonValue codeValue && JsVal.TryGetStringLenient(codeValue, out var codeStr))
        {
            code = codeStr;
        }

        if (code == null)
        {
            // typeof code !== "string" — warn only when the key was present with
            // a non-null value (undefined and null stay silent).
            if (hasCode && codeNode != null)
            {
                Console.WriteLine("validateCode(): received non-string code payload");
            }

            return null;
        }

        var trimmedCode = code.Trim(JsWhitespace);

        if (trimmedCode.Length == 0)
        {
            return null;
        }

        // Normalize base64url → standard base64 (client encodes with b64uEnc which replaces +→-, /→_, =→.)
        var normalizedCode = trimmedCode.Replace('-', '+').Replace('_', '/').Replace('.', '=');

        try
        {
            var buffer = B64u.DecodeLenient(normalizedCode);

            if (buffer == null || buffer.Length == 0)
            {
                return null;
            }

            var decoded = JsonNode.Parse(Encoding.UTF8.GetString(buffer));

            // const {...} = decoded — destructuring null throws in JS, landing
            // in the catch below ("Token validation error").
            if (decoded is null)
            {
                throw new InvalidOperationException("Cannot destructure properties of null");
            }

            var decodedObj = decoded as JsonObject;

            JsonNode? signedMessage = null;
            var hasSignedMessage =
                decodedObj != null && decodedObj.TryGetPropertyValue("signed_message", out signedMessage);
            var authors = decodedObj?["authors"] as JsonArray;
            var timestampNode = decodedObj?["timestamp"];
            var signatures = decodedObj?["signatures"] as JsonArray;

            // typeof signed_message !== "object" — JSON null and arrays pass
            // (typeof null === "object"), missing/strings/numbers/bools fail.
            var signedMessageTypeofObject =
                hasSignedMessage && signedMessage is null or JsonObject or JsonArray;

            string? author = null;
            if (authors is { Count: > 0 } && authors[0] is JsonValue authorValue
                && JsVal.TryGetStringLenient(authorValue, out var authorStr))
            {
                author = authorStr;
            }

            var timestampIsNumber =
                timestampNode is JsonValue timestampValue
                && timestampValue.GetValueKind() == JsonValueKind.Number;

            string? signature = null;
            if (signatures is { Count: > 0 } && signatures[0] is JsonValue signatureValue
                && JsVal.TryGetStringLenient(signatureValue, out var signatureStr))
            {
                signature = signatureStr;
            }

            if (!signedMessageTypeofObject || author is null || !timestampIsNumber || signature is null)
            {
                Console.WriteLine($"Invalid token structure {JsJson.Stringify(decoded)}");
                return null;
            }

            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // rawMessage = JSON.stringify({ signed_message, authors, timestamp })
            // — property order matters because the digest is hashed from it;
            // signed_message keeps its original key order from the token.
            var messageObj = new JsonObject
            {
                ["signed_message"] = signedMessage?.DeepClone(),
                ["authors"] = authors!.DeepClone(),
                ["timestamp"] = timestampNode!.DeepClone(),
            };
            var rawMessage = JsJson.Stringify(messageObj);
            var digest = HiveCrypto.Sha256Utf8(rawMessage);
            var recoveredPubKey = HiveCrypto.RecoverPublicKey(signature, digest);

            var accounts = await HiveClients.Default.GetAccounts(new[] { author })
                ?? throw new InvalidOperationException("getAccounts result is not iterable");
            var account = accounts.Count > 0 ? accounts[0] : null;
            if (account is null)
            {
                Console.Error.WriteLine("Fetching account error");
                return null;
            }

            // account.posting.key_auths.map(([key]) => key) — unexpected shapes
            // throw and land in the catch below, like the TS TypeError would.
            var postingPubKeys = MapKeyAuths(account["posting"]!["key_auths"]);
            if (recoveredPubKey != null && postingPubKeys.Contains(recoveredPubKey))
            {
                return author;
            }

            // Use cached hivesigner account, refresh every 24h
            if (_hivesignerAccountCache is null
                || currentTime - _hivesignerCacheTime > HiveSignerCacheTtlMs)
            {
                try
                {
                    var hsAccounts = await HiveClients.Default.GetAccounts(new[] { "hivesigner" })
                        ?? throw new InvalidOperationException("getAccounts result is not iterable");
                    // TS caches whatever destructures out — undefined included —
                    // and stamps the time either way.
                    _hivesignerAccountCache = hsAccounts.Count > 0 ? hsAccounts[0] : null;
                    _hivesignerCacheTime = currentTime;
                }
                catch (Exception err)
                {
                    Console.Error.WriteLine($"Failed to fetch hivesigner account {err}");
                    return null;
                }
            }

            // hivesignerAccountCache?.posting?.key_auths?.map(([key]) => key) || []
            var hsCache = _hivesignerAccountCache;
            var hsKeyAuths = ((hsCache as JsonObject)?["posting"] as JsonObject)?["key_auths"];
            var hsPostingKeys = hsKeyAuths is null ? new List<string?>() : MapKeyAuths(hsKeyAuths);
            if (recoveredPubKey != null && hsPostingKeys.Contains(recoveredPubKey))
            {
                return author;
            }

            Console.WriteLine(
                $"Posting key mismatch {recoveredPubKey} [{string.Join(", ", postingPubKeys)}] [{string.Join(", ", hsPostingKeys)}]");
            return null;
        }
        catch (Exception err)
        {
            Console.Error.WriteLine($"Token validation error {err}");
            return null;
        }
    }

    /// <summary>key_auths.map(([key]) => key) — throws on non-array input like .map on a non-array.</summary>
    private static List<string?> MapKeyAuths(JsonNode? keyAuths)
    {
        if (keyAuths is not JsonArray arr)
        {
            throw new InvalidOperationException("key_auths is not an array");
        }

        var keys = new List<string?>();
        foreach (var item in arr)
        {
            if (item is null)
            {
                // destructuring [key] of null throws in JS
                throw new InvalidOperationException("key_auths entry is not iterable");
            }

            var first = item[0]; // throws for non-indexable nodes, like JS destructuring a non-iterable
            keys.Add(first is JsonValue v && JsVal.TryGetStringLenient(v, out var key) ? key : null);
        }

        return keys;
    }

    // Resolve the authenticated username from the signed token, or send 401 and return
    // null. Used by the spend endpoints, which must never trust a body-supplied user.
    public static async Task<string?> RequireAuthedUsername(HttpContext ctx, JsonObject body)
    {
        var username = await ValidateCode(body);
        if (string.IsNullOrEmpty(username))
        {
            await ctx.SendText(401, "Unauthorized");
            return null;
        }
        return username;
    }

    public static async Task ReceivedVesting(HttpContext ctx)
    {
        var username = ctx.Request.RouteValues["username"]?.ToString();
        await Upstream.Pipe(
            ApiClient.ApiRequest($"delegatee_vesting_shares/{username}", HttpMethod.Get), ctx);
    }

    public static async Task ReceivedRC(HttpContext ctx)
    {
        var username = ctx.Request.RouteValues["username"]?.ToString();
        await Upstream.Pipe(ApiClient.ApiRequest($"delegatee_rc/{username}", HttpMethod.Get), ctx);
    }

    public static async Task RewardedCommunities(HttpContext ctx)
    {
        await Upstream.Pipe(ApiClient.ApiRequest("rewarded-communities", HttpMethod.Get), ctx);
    }

    // Public list of Pro usernames for the Pro badge roster. No auth: this is a
    // public, cached list served straight from the backend.
    public static async Task ProMembers(HttpContext ctx)
    {
        await Upstream.Pipe(ApiClient.ApiRequest("pro-members", HttpMethod.Get), ctx);
    }
}
