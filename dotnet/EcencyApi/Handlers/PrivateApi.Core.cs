using System.Collections.Concurrent;
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
                // names what is missing, never the token: a real one carries a signature
                LogOnce(
                    $"Invalid token structure: signed_message={signedMessageTypeofObject} author={author is not null} timestamp={timestampIsNumber} signature={signature is not null}");
                return null;
            }

            // A session is a token Ecency's own clients hold: one issued to the
            // Ecency app, as a code or as the posting token HiveSigner exchanges
            // it for. Nothing else signed by the same keys is one: another app's
            // token, a sign-in proof ("login"), a signed message. Decided before
            // any node lookup: the shape alone settles it.
            if (!IsEcencySession(signedMessage))
            {
                NoteRefusal(signedMessage as JsonObject);
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

            // A read that fails (node error, timeout, unusable answer) is not a
            // verdict on the code: it throws TokenCheckUnavailableException, which
            // the pipeline answers 503 (ServerErrors) instead of the 401 that tells
            // clients their session was refused. Only the chain's answer decides,
            // so a name that can never be an account is refused without a read.
            if (!IsAccountNameShape(author))
            {
                LogOnce("Token author is not an account name");
                return null;
            }
            var accounts = await ReadValidationAccounts(author);
            var account = accounts.Count > 0 ? accounts[0] : null;
            if (account is null)
            {
                Console.Error.WriteLine("Fetching account error");
                return null;
            }

            var postingPubKeys = PostingKeys(account);
            if (recoveredPubKey != null && postingPubKeys.Contains(recoveredPubKey))
            {
                return author;
            }

            // Use cached hivesigner account, refresh every 24h
            var hsRefreshFailed = false;
            if (_hivesignerAccountCache is null
                || currentTime - _hivesignerCacheTime > HiveSignerCacheTtlMs)
            {
                try
                {
                    var hsAccounts = await ReadValidationAccounts("hivesigner");
                    var hsAccount = hsAccounts.Count > 0 ? hsAccounts[0] : null;
                    // An unusable answer is not cached for a day. @hivesigner exists
                    // and signs with its posting key, so an answer without the
                    // account or with an empty key list is unusable too.
                    if (hsAccount is null)
                    {
                        throw Unavailable("hivesigner answer without the account");
                    }
                    if (PostingKeys(hsAccount).Count == 0)
                    {
                        throw Unavailable("hivesigner answer without posting keys");
                    }
                    // Only a usable answer is cached (TS cached whatever came back,
                    // undefined included).
                    _hivesignerAccountCache = hsAccount;
                    _hivesignerCacheTime = currentTime;
                }
                catch (TokenCheckUnavailableException)
                    when (_hivesignerAccountCache is not null
                          && currentTime - _hivesignerCacheTime <= HiveSignerStaleMaxMs)
                {
                    // A failed refresh keeps the keys already held (they change
                    // rarely) for a bounded time; the time is not stamped, so the
                    // next check retries. They may only accept: a mismatch below
                    // could be a key the failed read would have shown.
                    hsRefreshFailed = true;
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

            if (hsRefreshFailed)
            {
                throw Unavailable("hivesigner keys could not be refreshed");
            }

            Console.WriteLine(
                $"Posting key mismatch {recoveredPubKey} [{string.Join(", ", postingPubKeys)}] [{string.Join(", ", hsPostingKeys)}]");
            return null;
        }
        catch (Exception err) when (err is not TokenCheckUnavailableException)
        {
            Console.Error.WriteLine($"Token validation error {err}");
            return null;
        }
    }

    /// <summary>
    /// The accounts read for ValidateCode, for a name of account shape. Any
    /// failure, RPC-level errors included (a node can answer an internal error
    /// for a valid read), and an answer that is not an array, means the keys
    /// could not be read.
    /// </summary>
    private static async Task<JsonArray> ReadValidationAccounts(string name)
    {
        JsonArray? accounts;
        try
        {
            accounts = await ValidationAccounts(new[] { name });
        }
        catch (Exception err)
        {
            throw Unavailable("account read failed", err);
        }

        return accounts ?? throw Unavailable("account read answered no array");
    }

    /// <summary>
    /// A shape every existing Hive account name has: 3 to 16 characters of a-z,
    /// 0-9, '.' and '-'. Deliberately looser than HiveNames.IsAccountName (no
    /// segment rules), so no older account is ever refused by it.
    /// </summary>
    internal static bool IsAccountNameShape(string name) =>
        name.Length is >= 3 and <= 16
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    /// <summary>
    /// account.posting.key_auths.map(([key]) => key). Every account has a posting
    /// authority, so an answer without a key list, or with an entry that is not a
    /// key, is unusable, not a refusal. An empty list is a real account shape
    /// (posting held by other accounts only) and is returned as is.
    /// </summary>
    private static List<string?> PostingKeys(JsonNode account)
    {
        List<string?> keys;
        try
        {
            keys = MapKeyAuths(account["posting"]!["key_auths"]);
        }
        catch (Exception err)
        {
            throw Unavailable("account answer without posting keys", err);
        }

        if (keys.Any(string.IsNullOrEmpty))
        {
            throw Unavailable("account answer with a posting key that is not a key");
        }
        return keys;
    }

    private static TokenCheckUnavailableException Unavailable(string reason, Exception? inner = null)
    {
        var (key, line) = UnavailableLog(reason, inner);
        LogOnce(key, line);
        return new TokenCheckUnavailableException(reason, inner);
    }

    /// <summary>Forgets the cached @hivesigner account (tests).</summary>
    internal static void ResetHivesignerCache()
    {
        _hivesignerAccountCache = null;
        _hivesignerCacheTime = 0;
    }

    /// <summary>Makes the cached @hivesigner account `ageMs` old, keeping it (tests).</summary>
    internal static void AgeHivesignerCache(long ageMs) =>
        _hivesignerCacheTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ageMs;

    /// <summary>How old cached @hivesigner keys may be when a refresh fails.</summary>
    internal const long HiveSignerStaleMaxMs = 2 * HiveSignerCacheTtlMs;

    /// <summary>
    /// The log line for an unavailable check, and the key it is written once under:
    /// the cause's type and message only, never the code or the request, once per
    /// reason and cause type, since a node outage fails every request the same way.
    /// </summary>
    internal static (string Key, string Line) UnavailableLog(string reason, Exception? inner)
    {
        var type = inner?.GetType().Name ?? "-";
        var cause = inner is null ? "" : $" ({type}: {inner.Message})";
        return ($"Token check unavailable: {reason} [{type}]", $"Token check unavailable: {reason}{cause}");
    }

    /// <summary>The chain read behind token validation (tests answer it).</summary>
    internal static Func<IEnumerable<string?>, Task<JsonArray?>> ValidationAccounts =
        names => HiveClients.Default.GetAccounts(names);

    /// <summary>The HiveSigner app id Ecency's clients sign in as.</summary>
    internal const string EcencyApp = "ecency.app";

    /// <summary>
    /// True for a signed_message issued to the Ecency app as a "code" (what
    /// the apps sign with a key they hold) or a "posting" token (what
    /// HiveSigner gives for one): the two things Ecency's clients send as their
    /// session. Everything else is refused, including a missing or non-string
    /// type or app.
    /// </summary>
    internal static bool IsEcencySession(JsonNode? signedMessage) =>
        signedMessage is JsonObject obj
        && StringField(obj, "app") == EcencyApp
        && StringField(obj, "type") is "posting" or "code";

    // Token diagnostics on the request path, written once per distinct line and
    // at most MaxTokenLogLines lines per process: the service stays quiet on
    // request paths however many bad tokens arrive. The first sighting of a
    // first-party client refused by the session rule still reaches the logs,
    // unless junk tokens have used up the lines since the last restart.
    private const int MaxTokenLogLines = 32;
    private static int _tokenLogLines;
    private static readonly ConcurrentDictionary<string, byte> TokenLogSeen = new();

    private static void LogOnce(string line) => LogOnce(line, line);

    private static void LogOnce(string key, string line)
    {
        if (Volatile.Read(ref _tokenLogLines) >= MaxTokenLogLines || TokenLogSeen.ContainsKey(key)) return;
        if (!TokenLogSeen.TryAdd(key, 0)) return;
        if (Interlocked.Increment(ref _tokenLogLines) > MaxTokenLogLines) return;
        Console.WriteLine(line);
    }

    private static void NoteRefusal(JsonObject? obj) =>
        LogOnce($"Token refused as a session: app={LogLabel(obj, "app")} type={LogLabel(obj, "type")}");

    /// <summary>A signed_message label for a log line: short, printable, never the value of anything else.</summary>
    private static string LogLabel(JsonObject? obj, string name)
    {
        var text = obj is null ? null : StringField(obj, name);
        if (text is null) return "-";
        var clean = new string(text.Where(c => c is >= ' ' and <= '~').Take(40).ToArray());
        return clean.Length == 0 ? "?" : clean;
    }

    private static string? StringField(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node)
        && node is JsonValue value
        && JsVal.TryGetStringLenient(value, out var text)
            ? text
            : null;

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
    // public, cached list served straight from the backend. The response is the
    // same for every caller, so it carries a Cache-Control and clients can reuse
    // it across page views instead of refetching the roster on each one.
    public static async Task ProMembers(HttpContext ctx)
    {
        ctx.CacheWhenOk(CachePolicy.ProMembers);
        await Upstream.Pipe(ApiClient.ApiRequest("pro-members", HttpMethod.Get), ctx);
    }
}
