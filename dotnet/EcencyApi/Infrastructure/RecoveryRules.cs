using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Pure rules for self-serve account recovery, kept free of network access so they
/// can be tested directly. A proven owner key is usable when it appears in the
/// account's owner history, its replacement is still inside the chain's 30-day
/// recovery window, it was replaced after the account's most recent recovery, and it
/// meets that old authority's threshold on its own.
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

    public static bool IsRequestId(string? id) => id is { Length: 36 } && RequestIdPattern().IsMatch(id);

    public static bool IsCode(string? code) => code is { Length: 6 } && CodePattern().IsMatch(code);

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
    /// True when the authority is the shape a finished recovery leaves: exactly one key, the
    /// given one, whose weight alone meets the threshold, and no account auths.
    /// </summary>
    public static bool IsRecoveredOwner(JsonNode? authority, string key)
    {
        if (JsVal.Prop(authority, "account_auths") is not JsonArray { Count: 0 }
            || JsVal.AsNumber(JsVal.Prop(authority, "weight_threshold")) is not { } threshold || threshold < 1
            || JsVal.Prop(authority, "key_auths") is not JsonArray { Count: 1 } auths)
        {
            return false;
        }
        return auths[0] is JsonArray { Count: 2 } pair
            && pair[0] is JsonValue kv && JsVal.TryGetStringLenient(kv, out var k)
            && string.Equals(k, key, StringComparison.Ordinal)
            && JsVal.AsNumber(pair[1]) is { } weight && weight >= threshold;
    }

    /// <summary>
    /// The account's latest recovery from its last_account_recovery field, or null when it was
    /// never recovered (the chain's 1970 placeholder). Throws when the field cannot be read.
    /// </summary>
    public static DateTime? LastAccountRecovery(JsonNode? account)
    {
        var t = ParseChainTime(JsVal.Prop(account, "last_account_recovery"))
            ?? throw new FormatException("unreadable last_account_recovery");
        return t <= DateTime.UnixEpoch ? null : t;
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

    /// <summary>account_create, account_update, create_claimed_account, recover_account, account_update2.</summary>
    public const long OwnerOpsFilter = (1L << 9) | (1L << 10) | (1L << 23) | (1L << 25) | (1L << 43);

    /// <summary>
    /// When the proven key most recently entered the account's owner authority, from an
    /// answer filtered to <see cref="OwnerOpsFilter"/>: the owner-setting ops of this account
    /// before validUntil are walked in order, and the key's span starts where it is present
    /// and the previous owner did not hold it. A later update that keeps the key does not move
    /// it. Null when the key is not an owner key at validUntil, or when the answer does not
    /// reach back to where the span started (the earliest op seen already holds the key and
    /// is not the account's creation). Throws when the answer cannot be read.
    /// </summary>
    public static DateTime? OwnerSince(JsonNode? history, string username, string provenKeyPub, DateTime validUntil)
    {
        var changes = new List<(DateTime Time, bool Creation, bool HoldsKey)>();
        foreach (var item in HistoryItems(history))
        {
            var (time, name, payload) = ReadHistoryItem(item);
            var (subject, field, required) = name switch
            {
                "account_create" or "create_claimed_account" => ("new_account_name", "owner", true),
                "account_update" or "account_update2" => ("account", "owner", false),
                "recover_account" => ("account_to_recover", "new_owner_authority", true),
                _ => throw new FormatException("unexpected operation in filtered history"),
            };
            // An account's history also holds the accounts it created.
            if (StringField(payload, subject) != username)
            {
                continue;
            }
            var authority = JsVal.Prop(payload, field);
            if (authority == null && !required)
            {
                continue;
            }
            if (authority is not JsonObject || JsVal.Prop(authority, "key_auths") is not JsonArray)
            {
                throw new FormatException("unreadable owner authority");
            }
            if (time < validUntil)
            {
                changes.Add((time, required && field == "owner", AuthorityKeys(authority).Contains(provenKeyPub)));
            }
        }

        // null start inside an open span means it began before the answer reaches back.
        var open = false;
        DateTime? start = null;
        bool? previousHeldKey = null;
        foreach (var change in changes.OrderBy(c => c.Time))
        {
            if (!change.HoldsKey)
            {
                open = false;
                start = null;
            }
            else if (!open)
            {
                open = true;
                start = change.Creation || previousHeldKey == false ? change.Time : null;
            }
            previousHeldKey = change.HoldsKey;
        }
        return open ? start : null;
    }

    private static string StringField(JsonObject payload, string name) =>
        JsVal.Prop(payload, name) is JsonValue v && JsVal.TryGetStringLenient(v, out var s)
            ? s
            : throw new FormatException("unreadable account name in history");

    /// <summary>An owner authority as the recovery rules need it.</summary>
    public sealed record OwnerAuthority(double Threshold, IReadOnlyList<(string Key, double Weight)> Keys)
    {
        /// <summary>The key's weight, or null when the authority does not list it.</summary>
        public double? WeightOf(string key)
        {
            foreach (var (k, w) in Keys)
            {
                if (string.Equals(k, key, StringComparison.Ordinal))
                {
                    return w;
                }
            }
            return null;
        }

        /// <summary>True when the key alone meets the threshold.</summary>
        public bool IsSatisfiedBy(string key) => WeightOf(key) is { } w && w >= Threshold;

        public bool HasSufficientKey => Keys.Any(k => IsSatisfiedBy(k.Key));
    }

    /// <summary>One owner_history entry: the authority that was replaced and until when it was valid.</summary>
    public sealed record OwnerHistoryEntry(DateTime LastValid, OwnerAuthority Previous);

    /// <summary>
    /// A get_owner_history answer. Throws on anything unreadable: a skipped entry could turn a
    /// read problem into a refusal.
    /// </summary>
    public static List<OwnerHistoryEntry> ReadOwnerHistory(JsonNode? history)
    {
        var list = new List<OwnerHistoryEntry>();
        if (history is not JsonArray entries)
        {
            throw new FormatException("owner history is not a list");
        }
        foreach (var entry in entries)
        {
            var lastValid = ParseChainTime(JsVal.Prop(entry, "last_valid_time"))
                ?? throw new FormatException("unreadable owner history time");
            list.Add(new OwnerHistoryEntry(lastValid, ReadAuthority(JsVal.Prop(entry, "previous_owner_authority"))));
        }
        return list;
    }

    private static OwnerAuthority ReadAuthority(JsonNode? authority)
    {
        if (JsVal.Prop(authority, "weight_threshold") is not JsonValue tv || !IsWeight(JsVal.AsNumber(tv))
            || JsVal.Prop(authority, "key_auths") is not JsonArray auths)
        {
            throw new FormatException("unreadable owner authority");
        }
        var keys = new List<(string, double)>();
        foreach (var auth in auths)
        {
            if (auth is not JsonArray { Count: 2 } pair || pair[0] is not JsonValue kv
                || !JsVal.TryGetStringLenient(kv, out var key) || !IsWeight(JsVal.AsNumber(pair[1])))
            {
                throw new FormatException("unreadable owner key");
            }
            keys.Add((key, JsVal.AsNumber(pair[1])!.Value));
        }
        return new OwnerAuthority(JsVal.AsNumber(tv)!.Value, keys);
    }

    private static bool IsWeight(double? n) => n is { } d && d >= 0 && double.IsInteger(d);

    /// <summary>
    /// Owner-history entries still usable for recovery: inside the window measured from
    /// head block time, and strictly newer than the latest recovery (the key a recovery
    /// replaced carries exactly the recovery's time and must not count).
    /// </summary>
    private static IEnumerable<OwnerHistoryEntry> UsableEntries(
        IEnumerable<OwnerHistoryEntry> ownerHistory, DateTime headTime, DateTime? lastRecovery) =>
        ownerHistory.Where(e => e.LastValid + RecoveryWindow > headTime
            && (lastRecovery == null || e.LastValid > lastRecovery.Value));

    public enum Verdict
    {
        Ok,
        NoRecentOwnerChange,
        KeyNotRecent,
        KeyNotSufficient,
    }

    /// <summary>
    /// Whether any key could still recover the account: a usable entry must exist, and one of
    /// them must hold a key that meets its threshold alone.
    /// </summary>
    public static Verdict CheckOwnerHistory(IEnumerable<OwnerHistoryEntry> ownerHistory, DateTime headTime,
        DateTime? lastRecovery)
    {
        var usable = UsableEntries(ownerHistory, headTime, lastRecovery).ToList();
        if (usable.Count == 0)
        {
            return Verdict.NoRecentOwnerChange;
        }
        return usable.Any(e => e.Previous.HasSufficientKey) ? Verdict.Ok : Verdict.KeyNotSufficient;
    }

    /// <summary>
    /// The proven key's verdict and, when usable, the latest last_valid_time among usable
    /// entries in which the key alone meets the threshold.
    /// </summary>
    public static (Verdict Verdict, DateTime? ValidUntil) CheckProvenKey(IEnumerable<OwnerHistoryEntry> ownerHistory,
        string provenKeyPub, DateTime headTime, DateTime? lastRecovery)
    {
        DateTime? best = null;
        var insufficient = false;
        foreach (var entry in UsableEntries(ownerHistory, headTime, lastRecovery))
        {
            if (entry.Previous.WeightOf(provenKeyPub) == null)
            {
                continue;
            }
            if (!entry.Previous.IsSatisfiedBy(provenKeyPub))
            {
                insufficient = true;
            }
            else if (best == null || entry.LastValid > best)
            {
                best = entry.LastValid;
            }
        }
        if (best != null)
        {
            return (Verdict.Ok, best);
        }
        return (insufficient ? Verdict.KeyNotSufficient : Verdict.KeyNotRecent, null);
    }
}
