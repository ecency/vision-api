using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Pure rules for self-serve account recovery, kept free of network access so they
/// can be tested directly. A proven owner key is usable when it appears in the
/// account's owner history, its replacement is still inside the chain's 30-day
/// recovery window, and it was replaced after the account's most recent recovery.
/// </summary>
public static partial class RecoveryRules
{
    /// <summary>The chain's owner-authority recovery window.</summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromDays(30);

    /// <summary>The chain's timestamp format (UTC, no zone suffix).</summary>
    public const string ChainTimeFormat = "yyyy-MM-ddTHH:mm:ss";

    [GeneratedRegex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"\A[0-9]{6}\z")]
    private static partial Regex CodePattern();

    public static bool IsRequestId(string? id) => id != null && RequestIdPattern().IsMatch(id);

    public static bool IsCode(string? code) => code != null && CodePattern().IsMatch(code);

    /// <summary>The message the proven key signs; binds the account, the new key and the request.</summary>
    public static string Challenge(string username, string newOwnerPub, string nonce) =>
        $"ecency-recovery:{username}:{newOwnerPub}:{nonce}";

    /// <summary>True when the signature over the challenge recovers to the proven key.</summary>
    public static bool ProofMatches(string username, string newOwnerPub, string nonce, string signature,
        string provenKeyPub)
    {
        var digest = HiveCrypto.Sha256Utf8(Challenge(username, newOwnerPub, nonce));
        var recovered = HiveCrypto.RecoverPublicKey(signature, digest);
        return recovered != null && string.Equals(recovered, provenKeyPub, StringComparison.Ordinal);
    }

    public static DateTime? ParseChainTime(JsonNode? node)
    {
        if (node is not JsonValue v || !JsVal.TryGetStringLenient(v, out var s))
        {
            return null;
        }
        if (s.EndsWith('Z'))
        {
            s = s[..^1];
        }
        return DateTime.TryParseExact(s, ChainTimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
    }

    public static string FormatChainTime(DateTime t) =>
        t.ToString(ChainTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>Keys listed in an authority object's key_auths.</summary>
    public static List<string> AuthorityKeys(JsonNode? authority)
    {
        var keys = new List<string>();
        if (JsVal.Prop(authority, "key_auths") is not JsonArray auths)
        {
            return keys;
        }
        foreach (var auth in auths)
        {
            if (auth is JsonArray pair && pair.Count > 0 && pair[0] is JsonValue kv
                && JsVal.TryGetStringLenient(kv, out var key))
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    /// <summary>
    /// True when the authority's threshold is 1 and it holds exactly one key, the given one,
    /// with weight 1. Account auths are not looked at.
    /// </summary>
    public static bool IsSingleKeyAuthority(JsonNode? authority, string key)
    {
        if (JsVal.AsNumber(JsVal.Prop(authority, "weight_threshold")) != 1
            || JsVal.Prop(authority, "key_auths") is not JsonArray auths || auths.Count != 1)
        {
            return false;
        }
        return auths[0] is JsonArray pair && pair.Count == 2
            && pair[0] is JsonValue kv && JsVal.TryGetStringLenient(kv, out var k)
            && string.Equals(k, key, StringComparison.Ordinal)
            && pair[1] is JsonValue wv && JsVal.AsNumber(wv) == 1;
    }

    /// <summary>
    /// One item of an account-history answer: [index, {timestamp, op: [name, payload]}].
    /// Throws on anything else. The answers read here decide whether a key may be used, so an
    /// item that cannot be read must fail the request rather than be skipped.
    /// </summary>
    private static (DateTime Time, string Name, JsonObject Payload) ReadHistoryItem(JsonNode? item)
    {
        var entry = item is JsonArray pair && pair.Count > 1 ? pair[1] : null;
        if (JsVal.Prop(entry, "op") is not JsonArray op || op.Count != 2
            || op[0] is not JsonValue nv || !JsVal.TryGetStringLenient(nv, out var name)
            || op[1] is not JsonObject payload)
        {
            throw new FormatException("unreadable account history item");
        }
        var time = ParseChainTime(JsVal.Prop(entry, "timestamp"))
            ?? throw new FormatException("unreadable account history timestamp");
        const string suffix = "_operation";
        if (name.EndsWith(suffix, StringComparison.Ordinal))
        {
            name = name[..^suffix.Length];
        }
        return (time, name, payload);
    }

    private static JsonArray HistoryItems(JsonNode? history) =>
        history as JsonArray ?? throw new FormatException("account history is not a list");

    /// <summary>
    /// Timestamp of the latest recover_account in an answer filtered to that operation, or
    /// null when there is none. Throws when the answer cannot be read.
    /// </summary>
    public static DateTime? LatestRecoverAccountTime(JsonNode? history)
    {
        DateTime? latest = null;
        foreach (var item in HistoryItems(history))
        {
            var (time, name, _) = ReadHistoryItem(item);
            if (name != "recover_account")
            {
                throw new FormatException("unexpected operation in filtered history");
            }
            if (latest == null || time > latest)
            {
                latest = time;
            }
        }
        return latest;
    }

    /// <summary>account_create, account_update, create_claimed_account, recover_account, account_update2.</summary>
    public const long OwnerOpsFilter = (1L << 9) | (1L << 10) | (1L << 23) | (1L << 25) | (1L << 43);

    /// <summary>
    /// When the proven key became an owner key: the time of the latest operation before
    /// validUntil whose new owner authority holds the key, from an answer filtered to
    /// <see cref="OwnerOpsFilter"/>. Null when no such operation is in the answer. Throws when
    /// the answer cannot be read.
    /// </summary>
    public static DateTime? OwnerSince(JsonNode? history, string provenKeyPub, DateTime validUntil)
    {
        DateTime? latest = null;
        foreach (var item in HistoryItems(history))
        {
            var (time, name, payload) = ReadHistoryItem(item);
            var (field, required) = name switch
            {
                "account_create" or "create_claimed_account" => ("owner", true),
                "account_update" or "account_update2" => ("owner", false),
                "recover_account" => ("new_owner_authority", true),
                _ => throw new FormatException("unexpected operation in filtered history"),
            };
            var authority = JsVal.Prop(payload, field);
            if (authority == null && !required)
            {
                continue;
            }
            if (authority is not JsonObject || JsVal.Prop(authority, "key_auths") is not JsonArray)
            {
                throw new FormatException("unreadable owner authority");
            }
            if (time < validUntil && AuthorityKeys(authority).Contains(provenKeyPub)
                && (latest == null || time > latest))
            {
                latest = time;
            }
        }
        return latest;
    }

    /// <summary>
    /// Owner-history entries still usable for recovery: inside the window measured from
    /// head block time, and strictly newer than the latest recovery (the key a recovery
    /// replaced carries exactly the recovery's time and must not count).
    /// </summary>
    private static IEnumerable<(JsonNode Entry, DateTime LastValid)> UsableEntries(
        JsonNode? ownerHistory, DateTime headTime, DateTime? lastRecovery)
    {
        if (ownerHistory is not JsonArray entries)
        {
            yield break;
        }
        foreach (var entry in entries)
        {
            if (entry == null)
            {
                continue;
            }
            var lastValid = ParseChainTime(JsVal.Prop(entry, "last_valid_time"));
            if (lastValid == null
                || lastValid.Value + RecoveryWindow <= headTime
                || (lastRecovery != null && lastValid.Value <= lastRecovery.Value))
            {
                continue;
            }
            yield return (entry, lastValid.Value);
        }
    }

    public static bool HasRecentOwnerChange(JsonNode? ownerHistory, DateTime headTime, DateTime? lastRecovery) =>
        UsableEntries(ownerHistory, headTime, lastRecovery).Any();

    /// <summary>
    /// The latest last_valid_time among usable entries whose previous owner authority holds
    /// the proven key, or null when the key cannot be used.
    /// </summary>
    public static DateTime? ProvenKeyValidUntil(JsonNode? ownerHistory, string provenKeyPub, DateTime headTime,
        DateTime? lastRecovery)
    {
        DateTime? best = null;
        foreach (var (entry, lastValid) in UsableEntries(ownerHistory, headTime, lastRecovery))
        {
            if (AuthorityKeys(JsVal.Prop(entry, "previous_owner_authority")).Contains(provenKeyPub)
                && (best == null || lastValid > best))
            {
                best = lastValid;
            }
        }
        return best;
    }
}
