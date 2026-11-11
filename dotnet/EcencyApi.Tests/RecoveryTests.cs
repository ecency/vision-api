using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Xunit;
using T = EcencyApi.Tests.CurationDeskTestSupport;

namespace EcencyApi.Tests;

[CollectionDefinition("recovery", DisableParallelization = true)]
public class RecoveryCollection { }

/// <summary>Pure rules for self-serve account recovery: key lineage, window, proof, input formats.</summary>
public class RecoveryRulesTests
{
    // Golden vector produced by @hiveio/dhive: PrivateKey.fromLogin("recoverytest", ..., "owner"),
    // signature = oldOwner.sign(sha256(challenge)).toString().
    internal const string Username = "recoverytest";
    internal const string OldOwnerPub = "STM6TR1xz8uFu24zpZYdYCkKYnhBq7L8aELAe8uL9gny1D1NiYWRs";
    internal const string NewOwnerPub = "STM5Ki4Y2rPorZ4GKJEaxWZFEKPSZ79eHGih5y7UVPkcgqa8YUfDN";
    internal const string Nonce = "0123456789abcdef0123456789abcdef";
    internal const string Signature =
        "20412e9e9230c61f9389e7ed345174e78e46666c2d3fc134a102443159cc74a9f160f9674d0d12de1ba8bfcb380934a199a16055a7612367c2b7d656e0cb301940";

    private static readonly DateTime Head = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    internal static JsonObject Entry(string key, string lastValid, int weight = 1, int threshold = 1) =>
        MultiKeyEntry(lastValid, threshold, (key, weight));

    internal static JsonObject MultiKeyEntry(string lastValid, int threshold, params (string Key, int Weight)[] keys)
    {
        var auths = new JsonArray();
        foreach (var (key, weight) in keys)
        {
            auths.Add(new JsonArray(key, weight));
        }
        return new JsonObject
        {
            ["id"] = 1,
            ["account"] = Username,
            ["previous_owner_authority"] = new JsonObject
            {
                ["weight_threshold"] = threshold,
                ["account_auths"] = new JsonArray(),
                ["key_auths"] = auths,
            },
            ["last_valid_time"] = lastValid,
        };
    }

    private static string Fmt(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss");

    private static DateTime? ValidUntil(JsonArray history, string key, DateTime head, DateTime? lastRecovery) =>
        RecoveryRules.CheckProvenKey(RecoveryRules.ReadOwnerHistory(history), key, head, lastRecovery).ValidUntil;

    private static RecoveryRules.Verdict ProvenVerdict(JsonArray history, string key) =>
        RecoveryRules.CheckProvenKey(RecoveryRules.ReadOwnerHistory(history), key, Head, null).Verdict;

    private static bool HasRecentOwnerChange(JsonArray history, DateTime head, DateTime? lastRecovery) =>
        RecoveryRules.CheckOwnerHistory(RecoveryRules.ReadOwnerHistory(history), head, lastRecovery)
        != RecoveryRules.Verdict.NoRecentOwnerChange;

    [Fact]
    public void ChallengeIsTheAgreedString()
    {
        Assert.Equal($"ecency-recovery:{Username}:{NewOwnerPub}:{Nonce}",
            RecoveryRules.Challenge(Username, NewOwnerPub, Nonce));
    }

    [Fact]
    public void GoldenProofVerifies()
    {
        Assert.True(RecoveryRules.ProofMatches(Username, NewOwnerPub, Nonce, Signature, OldOwnerPub));
    }

    [Fact]
    public void ProofForAnotherKeyIsRejected()
    {
        Assert.False(RecoveryRules.ProofMatches(Username, NewOwnerPub, Nonce, Signature, NewOwnerPub));
    }

    [Theory]
    [InlineData("recoverytest", "ffffffffffffffffffffffffffffffff")]
    [InlineData("otheraccount", Nonce)]
    public void ProofIsBoundToUsernameAndNonce(string username, string nonce)
    {
        Assert.False(RecoveryRules.ProofMatches(username, NewOwnerPub, nonce, Signature, OldOwnerPub));
    }

    [Fact]
    public void ProofIsBoundToTheNewKey()
    {
        Assert.False(RecoveryRules.ProofMatches(Username, OldOwnerPub, Nonce, Signature, OldOwnerPub));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("20412e9e")]
    public void MalformedSignatureIsRejected(string sig)
    {
        Assert.False(RecoveryRules.ProofMatches(Username, NewOwnerPub, Nonce, sig, OldOwnerPub));
    }

    [Fact]
    public void ProofSignedByHiveCryptoVerifies()
    {
        var key = HiveCrypto.FromLogin("someone", "pw", "owner");
        var pub = HiveCrypto.PublicKeyToString(key.CreatePubKey());
        var sig = HiveCrypto.Sign(key, HiveCrypto.Sha256Utf8(RecoveryRules.Challenge("someone", NewOwnerPub, Nonce)));
        Assert.True(RecoveryRules.ProofMatches("someone", NewOwnerPub, Nonce, sig, pub));
    }

    [Theory]
    [InlineData(OldOwnerPub, true)]
    [InlineData(NewOwnerPub, true)]
    [InlineData("STM5Ki4Y2rPorZ4GKJEaxWZFEKPSZ79eHGih5y7UVPkcgqa8YUfDM", false)] // checksum
    [InlineData("TST5Ki4Y2rPorZ4GKJEaxWZFEKPSZ79eHGih5y7UVPkcgqa8YUfDN", false)] // prefix
    [InlineData("STM", false)]
    [InlineData("STM0OIl", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PublicKeyFormat(string? key, bool ok)
    {
        Assert.Equal(ok, HiveCrypto.IsPublicKey(key));
    }

    [Theory]
    [InlineData("3f2b1c9e-8d7a-4b6c-9e5f-1a2b3c4d5e6f", true)]
    [InlineData("3F2B1C9E-8D7A-4B6C-9E5F-1A2B3C4D5E6F", false)]
    [InlineData("3f2b1c9e8d7a4b6c9e5f1a2b3c4d5e6f", false)]
    [InlineData("../admin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RequestIdFormat(string? id, bool ok)
    {
        Assert.Equal(ok, RecoveryRules.IsRequestId(id));
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("000000", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12345a", false)]
    [InlineData("١٢٣٤٥٦", false)]
    [InlineData(null, false)]
    public void CodeFormat(string? code, bool ok)
    {
        Assert.Equal(ok, RecoveryRules.IsCode(code));
    }

    [Fact]
    public void KeyReplacedByARecoveryIsRejected()
    {
        // The key a recovery replaced carries last_valid_time == the recovery time.
        var tr = Head.AddDays(-2);
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(tr)));
        Assert.Null(ValidUntil(history, OldOwnerPub, Head, tr));
    }

    [Fact]
    public void KeyNewerThanTheLastRecoveryIsAccepted()
    {
        var tr = Head.AddDays(-2);
        var lvt = tr.AddSeconds(3);
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(lvt)));
        Assert.Equal(lvt, ValidUntil(history, OldOwnerPub, Head, tr));
    }

    [Fact]
    public void NoRecoveryMeansNoLineageConstraint()
    {
        var lvt = Head.AddDays(-5);
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(lvt)));
        Assert.Equal(lvt, ValidUntil(history, OldOwnerPub, Head, null));
    }

    [Fact]
    public void WindowEdgeIsExclusive()
    {
        var atEdge = new JsonArray(Entry(OldOwnerPub, Fmt(Head.AddDays(-30))));
        Assert.Null(ValidUntil(atEdge, OldOwnerPub, Head, null));

        var inside = Head.AddDays(-30).AddSeconds(1);
        var justInside = new JsonArray(Entry(OldOwnerPub, Fmt(inside)));
        Assert.Equal(inside, ValidUntil(justInside, OldOwnerPub, Head, null));
    }

    [Fact]
    public void SeveralMatchesPickTheLatest()
    {
        var older = Head.AddDays(-10);
        var newer = Head.AddDays(-1);
        var history = new JsonArray(
            Entry(OldOwnerPub, Fmt(older)),
            Entry(NewOwnerPub, Fmt(Head.AddHours(-1))),
            Entry(OldOwnerPub, Fmt(newer)));
        Assert.Equal(newer, ValidUntil(history, OldOwnerPub, Head, null));
    }

    [Fact]
    public void UnknownKeyIsRejected()
    {
        var history = new JsonArray(Entry(NewOwnerPub, Fmt(Head.AddDays(-1))));
        Assert.Null(ValidUntil(history, OldOwnerPub, Head, null));
    }

    [Fact]
    public void RecentOwnerChangeRespectsWindowAndLineage()
    {
        Assert.False(HasRecentOwnerChange(new JsonArray(), Head, null));
        Assert.False(HasRecentOwnerChange(
            new JsonArray(Entry(OldOwnerPub, Fmt(Head.AddDays(-31)))), Head, null));
        var tr = Head.AddDays(-1);
        Assert.False(HasRecentOwnerChange(
            new JsonArray(Entry(OldOwnerPub, Fmt(tr))), Head, tr));
        Assert.True(HasRecentOwnerChange(
            new JsonArray(Entry(OldOwnerPub, Fmt(tr.AddSeconds(3)))), Head, tr));
    }

    [Fact]
    public void LastRecoveryComesFromTheAccount()
    {
        Assert.Equal(new DateTime(2026, 11, 10, 14, 25, 30, DateTimeKind.Utc),
            RecoveryRules.LastAccountRecovery(new JsonObject { ["last_account_recovery"] = "2026-11-10T14:25:30" }));
        Assert.Null(RecoveryRules.LastAccountRecovery(new JsonObject { ["last_account_recovery"] = "1970-01-01T00:00:00" }));
    }

    public static IEnumerable<object[]> UnreadableLastRecovery() => new[]
    {
        new object[] { new JsonObject() },
        new object[] { new JsonObject { ["last_account_recovery"] = "" } },
        new object[] { new JsonObject { ["last_account_recovery"] = "yesterday" } },
        new object[] { new JsonObject { ["last_account_recovery"] = 1731248730 } },
        new object[] { new JsonObject { ["last_account_recovery"] = null } },
    };

    [Theory]
    [MemberData(nameof(UnreadableLastRecovery))]
    public void UnreadableLastRecoveryIsAReadFailure(JsonObject account)
    {
        Assert.ThrowsAny<Exception>(() => RecoveryRules.LastAccountRecovery(account));
    }

    // ---- key weight ----

    [Fact]
    public void PartialWeightKeyIsNotSufficient()
    {
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(Head.AddDays(-1)), weight: 1, threshold: 2));
        Assert.Equal(RecoveryRules.Verdict.KeyNotSufficient, ProvenVerdict(history, OldOwnerPub));
        Assert.Null(ValidUntil(history, OldOwnerPub, Head, null));
    }

    [Fact]
    public void TwoOfTwoKeysAreEachNotSufficient()
    {
        var history = new JsonArray(MultiKeyEntry(Fmt(Head.AddDays(-1)), 2, (OldOwnerPub, 1), (NewOwnerPub, 1)));
        Assert.Equal(RecoveryRules.Verdict.KeyNotSufficient, ProvenVerdict(history, OldOwnerPub));
        Assert.Equal(RecoveryRules.Verdict.KeyNotSufficient, ProvenVerdict(history, NewOwnerPub));
        Assert.Equal(RecoveryRules.Verdict.KeyNotSufficient,
            RecoveryRules.CheckOwnerHistory(RecoveryRules.ReadOwnerHistory(history), Head, null));
    }

    [Fact]
    public void WeightEqualToThresholdIsSufficient()
    {
        var lvt = Head.AddDays(-1);
        var history = new JsonArray(MultiKeyEntry(Fmt(lvt), 3, (OldOwnerPub, 3), (NewOwnerPub, 1)));
        Assert.Equal(RecoveryRules.Verdict.Ok, ProvenVerdict(history, OldOwnerPub));
        Assert.Equal(lvt, ValidUntil(history, OldOwnerPub, Head, null));
        Assert.Equal(RecoveryRules.Verdict.KeyNotSufficient, ProvenVerdict(history, NewOwnerPub));
        Assert.Equal(RecoveryRules.Verdict.Ok,
            RecoveryRules.CheckOwnerHistory(RecoveryRules.ReadOwnerHistory(history), Head, null));
    }

    [Fact]
    public void ASufficientEntryWinsOverANewerInsufficientOne()
    {
        var older = Head.AddDays(-5);
        var history = new JsonArray(
            Entry(OldOwnerPub, Fmt(older)),
            Entry(OldOwnerPub, Fmt(Head.AddDays(-1)), weight: 1, threshold: 2));
        Assert.Equal(older, ValidUntil(history, OldOwnerPub, Head, null));
    }

    [Fact]
    public void AKeyOnlyInAnOldEntryIsNotRecentEvenIfSufficient()
    {
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(Head.AddDays(-40))));
        Assert.Equal(RecoveryRules.Verdict.KeyNotRecent, ProvenVerdict(history, OldOwnerPub));
    }

    public static IEnumerable<object[]> UnreadableOwnerHistory()
    {
        JsonObject Bad(Action<JsonObject> change)
        {
            var e = Entry(OldOwnerPub, "2026-11-08T10:00:00");
            change(e);
            return e;
        }
        JsonObject Auth(JsonObject e) => (JsonObject)e["previous_owner_authority"]!;
        return new[]
        {
            new object[] { new JsonObject() },
            new object[] { new JsonArray(JsonValue.Create(4)) },
            new object[] { new JsonArray(Bad(e => e["last_valid_time"] = "not a time")) },
            new object[] { new JsonArray(Bad(e => e.Remove("last_valid_time"))) },
            new object[] { new JsonArray(Bad(e => e.Remove("previous_owner_authority"))) },
            new object[] { new JsonArray(Bad(e => Auth(e)["weight_threshold"] = "1")) },
            new object[] { new JsonArray(Bad(e => Auth(e)["weight_threshold"] = -1)) },
            new object[] { new JsonArray(Bad(e => Auth(e)["key_auths"] = "x")) },
            new object[] { new JsonArray(Bad(e => Auth(e)["key_auths"] = new JsonArray(new JsonArray(OldOwnerPub)))) },
            new object[] { new JsonArray(Bad(e => Auth(e)["key_auths"] = new JsonArray(new JsonArray(OldOwnerPub, "1")))) },
            new object[] { new JsonArray(Bad(e => Auth(e)["key_auths"] = new JsonArray(new JsonArray(7, 1)))) },
            new object[] { new JsonArray(Bad(e => Auth(e)["key_auths"] = new JsonArray(new JsonArray(OldOwnerPub, -1)))) },
            new object[] { new JsonArray(Bad(e => Auth(e)["key_auths"] = new JsonArray(new JsonArray(OldOwnerPub, 1.5)))) },
        };
    }

    [Theory]
    [MemberData(nameof(UnreadableOwnerHistory))]
    public void UnreadableOwnerHistoryIsAReadFailure(JsonNode history)
    {
        Assert.ThrowsAny<Exception>(() => RecoveryRules.ReadOwnerHistory(history));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(3, 3, true)]
    [InlineData(65535, 65535, true)]
    [InlineData(1, 2, false)]
    [InlineData(0, 1, false)]
    public void RecoveredOwnerNeedsTheNewKeyToMeetItsThreshold(int weight, int threshold, bool ok)
    {
        Assert.Equal(ok, RecoveryRules.IsRecoveredOwner(Authority(NewOwnerPub, weight, threshold), NewOwnerPub));
    }

    [Fact]
    public void RecoveredOwnerHoldsOnlyTheNewKeyAndNoAccounts()
    {
        Assert.False(RecoveryRules.IsRecoveredOwner(Authority(OldOwnerPub), NewOwnerPub));
        var extraKey = Authority(NewOwnerPub);
        ((JsonArray)extraKey["key_auths"]!).Add(new JsonArray(OldOwnerPub, 1));
        Assert.False(RecoveryRules.IsRecoveredOwner(extraKey, NewOwnerPub));
        var withAccount = Authority(NewOwnerPub, 3, 3);
        ((JsonArray)withAccount["account_auths"]!).Add(new JsonArray("someapp", 1));
        Assert.False(RecoveryRules.IsRecoveredOwner(withAccount, NewOwnerPub));
        var noAccountList = Authority(NewOwnerPub);
        noAccountList.Remove("account_auths");
        Assert.False(RecoveryRules.IsRecoveredOwner(noAccountList, NewOwnerPub));
    }

    [Fact]
    public void ReadFailureLogNamesTheMethodAndCauseButNoMessage()
    {
        var (key, line) = PrivateApi.RecoveryReadFailureLog("get_account_history",
            new InvalidOperationException($"no match for {Username}\nwarn: forged line"));
        Assert.Equal("warn: recovery chain read failed: get_account_history (InvalidOperationException, other)", line);
        Assert.DoesNotContain(Username, line);
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain(Username, key);
        Assert.Equal(key, PrivateApi.RecoveryReadFailureLog("get_account_history",
            new InvalidOperationException("other text")).Key);
    }

    public static IEnumerable<object[]> FailureCategories() => new[]
    {
        new object[] { new TaskCanceledException("x"), "timeout" },
        new object[] { new TimeoutException("x"), "timeout" },
        new object[] { new HttpRequestException("x"), "http_error" },
        new object[] { new HiveRpcClient.RpcException("x"), "rpc_error" },
        new object[] { new FormatException("x"), "parse_error" },
        new object[] { new System.Text.Json.JsonException("x"), "parse_error" },
        new object[] { new Exception("x"), "other" },
    };

    [Theory]
    [MemberData(nameof(FailureCategories))]
    public void ReadFailureLogCategory(Exception e, string category)
    {
        var (_, line) = PrivateApi.RecoveryReadFailureLog("get_accounts", e);
        Assert.EndsWith($"({e.GetType().Name}, {category})", line);
    }

    [Fact]
    public void RealChainKeyReplacedOneBlockBeforeTheRecoveryIsRejected()
    {
        var tr = Head.AddDays(-2);
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(tr.AddSeconds(-3))));
        Assert.Null(ValidUntil(history, OldOwnerPub, Head, tr));
    }

    internal static JsonObject Authority(string key, int weight = 1, int threshold = 1) => new()
    {
        ["weight_threshold"] = threshold,
        ["account_auths"] = new JsonArray(),
        ["key_auths"] = new JsonArray(new JsonArray(key, weight)),
    };

    internal static JsonArray Op(int index, string timestamp, string name, JsonObject payload) =>
        new(index, new JsonObject
        {
            ["trx_id"] = "t" + index,
            ["block"] = index,
            ["timestamp"] = timestamp,
            ["op"] = new JsonArray(name, payload),
        });

    // Shaped like a real hacked account: created with a claimed account, password changed
    // once by the owner, profile edits in between, then the owner key taken over.
    private const string K0 = "STM8ZSw7FmkxEFtkGpo7Gc9Jbs6bHjEu5XfGjNcKqZaHB3wnSXN6W";
    private const string Taken = "STM7xA2DJ2zG3uNoCzFfQ2BVwXqLHxvVW3rRKfS6kNY9iFMRtCsGq";

    internal static JsonArray HackedAccountOps() => new(
        Op(1, "2024-03-01T10:00:00", "create_claimed_account", new JsonObject
        {
            ["creator"] = "ecency", ["new_account_name"] = Username,
            ["owner"] = Authority(K0), ["active"] = Authority(K0), ["posting"] = Authority(K0),
            ["memo_key"] = K0, ["json_metadata"] = "",
        }),
        Op(40, "2025-06-01T09:00:00", "account_update2", new JsonObject
        {
            ["account"] = Username, ["json_metadata"] = "", ["posting_json_metadata"] = "{}",
        }),
        Op(77, "2026-02-14T18:30:00", "account_update", new JsonObject
        {
            ["account"] = Username, ["owner"] = Authority(OldOwnerPub), ["active"] = Authority(OldOwnerPub),
            ["posting"] = Authority(OldOwnerPub), ["memo_key"] = OldOwnerPub, ["json_metadata"] = "",
        }),
        Op(90, "2026-09-02T07:00:00", "account_update", new JsonObject
        {
            ["account"] = Username, ["memo_key"] = OldOwnerPub, ["json_metadata"] = "{}",
        }),
        Op(120, "2026-11-08T10:00:00", "account_update", new JsonObject
        {
            ["account"] = Username, ["owner"] = Authority(Taken), ["active"] = Authority(Taken),
            ["posting"] = Authority(Taken), ["memo_key"] = Taken, ["json_metadata"] = "",
        }));

    [Fact]
    public void OwnerSinceIsTheOpThatSetTheProvenKey()
    {
        var validUntil = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 2, 14, 18, 30, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(HackedAccountOps(), Username, OldOwnerPub, validUntil));
        Assert.Equal(new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(HackedAccountOps(), Username, K0, new DateTime(2026, 2, 14, 18, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void OwnerSinceIgnoresOpsAtOrAfterValidUntil()
    {
        // The key set again later (after it was replaced) must not move owner_since forward.
        var ops = HackedAccountOps();
        ops.Add(Op(130, "2026-11-09T10:00:00", "account_update", new JsonObject
        {
            ["account"] = Username, ["owner"] = Authority(OldOwnerPub), ["json_metadata"] = "",
        }));
        var validUntil = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 2, 14, 18, 30, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(ops, Username, OldOwnerPub, validUntil));
        Assert.Null(RecoveryRules.OwnerSince(HackedAccountOps(), Username, OldOwnerPub,
            new DateTime(2026, 2, 14, 18, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void OwnerSinceFromARecoveryAndAnAccountCreate()
    {
        var ops = new JsonArray(
            Op(1, "2023-01-01T00:00:00", "account_create", new JsonObject
            {
                ["creator"] = "ecency", ["new_account_name"] = Username, ["fee"] = "3.000 HIVE",
                ["owner"] = Authority(K0), ["active"] = Authority(K0), ["posting"] = Authority(K0),
                ["memo_key"] = K0, ["json_metadata"] = "",
            }),
            Op(9, "2025-05-05T05:05:05", "recover_account", new JsonObject
            {
                ["account_to_recover"] = Username,
                ["new_owner_authority"] = Authority(OldOwnerPub),
                ["recent_owner_authority"] = Authority(K0),
                ["extensions"] = new JsonArray(),
            }));
        var until = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc), RecoveryRules.OwnerSince(ops, Username, OldOwnerPub, until));
        // K0 was replaced by the recovery, so its span is closed by then.
        Assert.Null(RecoveryRules.OwnerSince(ops, Username, K0, until));
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(ops, Username, K0, new DateTime(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc)));
        Assert.Null(RecoveryRules.OwnerSince(ops, Username, Taken, until));
        Assert.Null(RecoveryRules.OwnerSince(new JsonArray(), Username, K0, until));
    }

    private static readonly DateTime Nov8 = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);

    private static JsonArray OwnerUpdate(int index, string time, string key, string account = Username) =>
        Op(index, time, "account_update", new JsonObject
        {
            ["account"] = account, ["owner"] = Authority(key), ["memo_key"] = key, ["json_metadata"] = "",
        });

    [Fact]
    public void OwnerSinceStaysAtTheFirstAddWhenALaterUpdateKeepsTheKey()
    {
        var ops = HackedAccountOps();
        ops.Insert(3, OwnerUpdate(85, "2026-08-01T00:00:00", OldOwnerPub));
        Assert.Equal(new DateTime(2026, 2, 14, 18, 30, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(ops, Username, OldOwnerPub, Nov8));
    }

    [Fact]
    public void OwnerSinceIsTheLatestReAdd()
    {
        var ops = HackedAccountOps();
        ops.Insert(3, OwnerUpdate(85, "2026-06-01T00:00:00", K0));
        ops.Insert(4, OwnerUpdate(86, "2026-07-01T00:00:00", OldOwnerPub));
        ops.Insert(5, OwnerUpdate(87, "2026-08-01T00:00:00", OldOwnerPub));
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(ops, Username, OldOwnerPub, Nov8));
    }

    [Fact]
    public void OwnerSinceIsUnknownWhenTheEarliestSeenOpAlreadyHoldsTheKey()
    {
        // The read did not reach back to where the key entered the owner authority.
        var ops = new JsonArray(
            OwnerUpdate(77, "2026-02-14T18:30:00", OldOwnerPub),
            OwnerUpdate(85, "2026-08-01T00:00:00", OldOwnerPub));
        Assert.Null(RecoveryRules.OwnerSince(ops, Username, OldOwnerPub, Nov8));
    }

    [Fact]
    public void OwnerSinceIsTheAccountCreationWhenItSetTheKey()
    {
        var ops = HackedAccountOps();
        Assert.Equal(new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(ops, Username, K0, new DateTime(2026, 2, 14, 18, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void OwnerSinceIgnoresOwnerUpdatesOfOtherAccounts()
    {
        var ops = HackedAccountOps();
        ops.Insert(3, OwnerUpdate(85, "2026-06-01T00:00:00", K0, account: "someoneelse"));
        Assert.Equal(new DateTime(2026, 2, 14, 18, 30, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(ops, Username, OldOwnerPub, Nov8));
    }

    [Fact]
    public void OwnerSinceFromADelegatedAccountCreation()
    {
        var ops = new JsonArray(
            Op(1, "2018-04-01T12:00:00", "account_create_with_delegation", new JsonObject
            {
                ["fee"] = "0.500 HIVE", ["delegation"] = "30000.000000 VESTS", ["creator"] = "ecency",
                ["new_account_name"] = Username,
                ["owner"] = Authority(K0), ["active"] = Authority(K0), ["posting"] = Authority(K0),
                ["memo_key"] = K0, ["json_metadata"] = "", ["extensions"] = new JsonArray(),
            }),
            Op(2, "2019-01-01T00:00:00", "account_create_with_delegation", new JsonObject
            {
                ["creator"] = Username, ["new_account_name"] = "childaccount", ["owner"] = Authority(OldOwnerPub),
            }));
        Assert.Equal(new DateTime(2018, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(ops, Username, K0, Nov8));
        Assert.Null(RecoveryRules.OwnerSince(ops, Username, OldOwnerPub, Nov8));
    }

    [Fact]
    public void OwnerOpsFilterSelectsExactlyTheOwnerSettingOps()
    {
        // Hive operation ids: account_create 9, account_update 10, create_claimed_account 23,
        // recover_account 25, account_create_with_delegation 41, account_update2 43. All are
        // below 64, so the high filter word stays 0.
        var bits = Enumerable.Range(0, 64).Where(i => (RecoveryRules.OwnerOpsFilter >> i & 1) == 1).ToArray();
        Assert.Equal(new[] { 9, 10, 23, 25, 41, 43 }, bits);
    }

    [Fact]
    public void OwnerSinceIgnoresAccountsTheUserCreated()
    {
        var ops = new JsonArray(
            Op(1, "2023-01-01T00:00:00", "account_create", new JsonObject
            {
                ["creator"] = "ecency", ["new_account_name"] = Username, ["fee"] = "3.000 HIVE",
                ["owner"] = Authority(K0), ["active"] = Authority(K0), ["posting"] = Authority(K0),
                ["memo_key"] = K0, ["json_metadata"] = "",
            }),
            Op(5, "2025-01-01T00:00:00", "create_claimed_account", new JsonObject
            {
                ["creator"] = Username, ["new_account_name"] = "childaccount",
                ["owner"] = Authority(K0), ["active"] = Authority(K0), ["posting"] = Authority(K0),
                ["memo_key"] = K0, ["json_metadata"] = "",
            }));
        var until = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), RecoveryRules.OwnerSince(ops, Username, K0, until));
        Assert.Null(RecoveryRules.OwnerSince(new JsonArray(ops[1]!.DeepClone()), Username, K0, until));
    }

    public static IEnumerable<object[]> UnreadableOwnerOps() => new[]
    {
        new object[] { new JsonArray(Op(1, "2026-01-01T00:00:00", "transfer", new JsonObject())) },
        new object[] { new JsonArray(Op(1, "not a time", "account_update", new JsonObject { ["account"] = Username })) },
        new object[] { new JsonArray(Op(1, "2026-01-01T00:00:00", "account_create", new JsonObject { ["memo_key"] = K0 })) },
        new object[] { new JsonArray(Op(1, "2026-01-01T00:00:00", "recover_account", new JsonObject { ["account_to_recover"] = Username })) },
        new object[] { new JsonArray(Op(1, "2026-01-01T00:00:00", "account_update", new JsonObject { ["owner"] = "x" })) },
        new object[] { new JsonArray(new JsonArray(1, new JsonObject { ["timestamp"] = "2026-01-01T00:00:00", ["op"] = 3 })) },
        new object[] { new JsonObject() },
    };

    [Theory]
    [MemberData(nameof(UnreadableOwnerOps))]
    public void UnreadableOwnerOpsAreAReadFailure(JsonNode ops)
    {
        Assert.ThrowsAny<Exception>(() => RecoveryRules.OwnerSince(ops, Username, OldOwnerPub, Head));
    }

    [Fact]
    public void TurnstileActionIsBound()
    {
        var ok = JsonNode.Parse("""{"success":true,"action":"recover"}""");
        var other = JsonNode.Parse("""{"success":true,"action":"signup"}""");
        var failed = JsonNode.Parse("""{"success":false,"action":"recover"}""");
        Assert.True(PrivateApi.TurnstileAccepted(ok, "recover"));
        Assert.False(PrivateApi.TurnstileAccepted(other, "recover"));
        Assert.False(PrivateApi.TurnstileAccepted(failed, "recover"));
        // Callers without an action keep the original success-only check.
        Assert.True(PrivateApi.TurnstileAccepted(other, null));
        Assert.False(PrivateApi.TurnstileAccepted(null, null));
    }
}

/// <summary>Recovery endpoints with chain, captcha and the private API replaced by fakes.</summary>
[Collection("recovery")]
public class RecoveryHandlerTests : IDisposable
{
    private const string RequestId = "3f2b1c9e-8d7a-4b6c-9e5f-1a2b3c4d5e6f";
    private const string Old = RecoveryRulesTests.OldOwnerPub;
    private const string New = RecoveryRulesTests.NewOwnerPub;
    private const string OtherPub = "STM8ZSw7FmkxEFtkGpo7Gc9Jbs6bHjEu5XfGjNcKqZaHB3wnSXN6W";

    private readonly List<(string Endpoint, HttpMethod Method, JsonNode? Payload)> _privateApi = new();
    private readonly List<string> _rpc = new();
    private Func<string, HttpMethod, UpstreamResponse> _answer = (_, _) => T.JsonResponse(200, "{}");
    private bool _captchaRejected;
    private bool _rpcDown;
    private string? _failingMethod;

    // chain state
    private string _recoveryAccount = "ecency";
    private bool _accountExists = true;
    private JsonArray _ownerHistory = new(RecoveryRulesTests.Entry(Old, "2026-11-08T10:00:00"));
    private JsonNode? _lastRecovery = "1970-01-01T00:00:00";
    private JsonNode _ownerOps = RecoveryRulesTests.HackedAccountOps();
    private JsonObject _owner = RecoveryRulesTests.Authority(OtherPub);
    private readonly List<double> _historyFilters = new();
    private const double OwnerOpsFilter = (1L << 9) | (1L << 10) | (1L << 23) | (1L << 25) | (1L << 41) | (1L << 43);
    private const string HeadTime = "2026-11-10T12:00:00";

    public RecoveryHandlerTests()
    {
        PrivateApi.RecoveryCaptchaRejected = (_, _) => Task.FromResult(_captchaRejected);
        PrivateApi.RecoveryUpstream = (endpoint, method, payload) =>
        {
            _privateApi.Add((endpoint, method, payload?.DeepClone()));
            return Task.FromResult(_answer(endpoint, method));
        };
        PrivateApi.RecoveryRpc = (method, @params) =>
        {
            _rpc.Add(method);
            if (_rpcDown || method == _failingMethod) throw new Exception("all nodes failed");
            JsonNode? result = method switch
            {
                "get_accounts" => _accountExists
                    ? new JsonArray(new JsonObject
                    {
                        ["name"] = RecoveryRulesTests.Username,
                        ["recovery_account"] = _recoveryAccount,
                        ["owner"] = _owner.DeepClone(),
                        ["last_account_recovery"] = _lastRecovery?.DeepClone(),
                    })
                    : new JsonArray(),
                "get_owner_history" => _ownerHistory.DeepClone(),
                "get_account_history" => HistoryFor(@params),
                "get_dynamic_global_properties" => new JsonObject { ["time"] = HeadTime },
                _ => throw new InvalidOperationException(method),
            };
            return Task.FromResult(result);
        };
    }

    public void Dispose() => PrivateApi.ResetRecoverySeams();

    private JsonNode HistoryFor(JsonArray @params)
    {
        var filter = JsVal.AsNumber(@params[3]) ?? -1;
        _historyFilters.Add(filter);
        Assert.Equal(RecoveryRulesTests.Username, @params[0]!.GetValue<string>());
        Assert.Equal(-1, JsVal.AsNumber(@params[1]));
        Assert.Equal(1000, JsVal.AsNumber(@params[2]));
        return filter switch
        {
            OwnerOpsFilter => _ownerOps.DeepClone(),
            _ => throw new InvalidOperationException("unexpected filter " + filter),
        };
    }

    private static Microsoft.AspNetCore.Http.HttpContext StartPost(string json, string ip = "203.0.113.9")
    {
        var ctx = T.Post("/private-api/recovery/start", json);
        if (ip.Length > 0) ctx.Request.Headers["x-real-ip"] = ip;
        return ctx;
    }

    private static string Body(Microsoft.AspNetCore.Http.HttpContext ctx) => T.Body(ctx);

    private static string ProveBody(string? sig = null, string newPub = New, string proven = Old,
        string username = RecoveryRulesTests.Username, string id = RequestId) =>
        new JsonObject
        {
            ["request_id"] = id,
            ["username"] = username,
            ["new_owner_pub"] = newPub,
            ["proven_key_pub"] = proven,
            ["signature"] = sig ?? RecoveryRulesTests.Signature,
        }.ToJsonString();

    private void RowIs(string status, string username = RecoveryRulesTests.Username, string? newOwnerPub = null)
    {
        var inner = _answer;
        _answer = (endpoint, method) =>
        {
            if (method == HttpMethod.Get && endpoint == $"recovery-request/{RequestId}")
            {
                var row = new JsonObject
                {
                    ["username"] = username,
                    ["nonce"] = RecoveryRulesTests.Nonce,
                    ["status"] = status,
                };
                if (newOwnerPub != null) row["new_owner_pub"] = newOwnerPub;
                return T.JsonResponse(200, row.ToJsonString());
            }
            return inner(endpoint, method);
        };
    }

    // ---- start ----

    [Theory]
    [InlineData("""{"captcha_token":"t"}""")]
    [InlineData("""{"username":"A","captcha_token":"t"}""")]
    [InlineData("""{"username":"bad name!","captcha_token":"t"}""")]
    [InlineData("""{"username":12,"captcha_token":"t"}""")]
    public async Task StartRejectsABadUsernameBeforeAnyCall(string json)
    {
        var ctx = StartPost(json);
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_username"}""", Body(ctx));
        Assert.Empty(_rpc);
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task StartRefusesAFailedCaptchaBeforeTheChain()
    {
        _captchaRejected = true;
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(403, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"captcha"}""", Body(ctx));
        Assert.Empty(_rpc);
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task StartUnknownAccount()
    {
        _accountExists = false;
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_found"}""", Body(ctx));
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task StartOtherRecoveryAccount()
    {
        _recoveryAccount = "someoneelse";
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_ecency_recovery"}""", Body(ctx));
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task StartWithoutARecentOwnerChange()
    {
        _ownerHistory = new JsonArray();
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"no_recent_owner_change"}""", Body(ctx));
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task StartWhenOnlyChangeWasARecovery()
    {
        _lastRecovery = "2026-11-08T10:00:00";
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"no_recent_owner_change"}""", Body(ctx));
    }

    [Fact]
    public async Task StartChainDownIsUnavailable()
    {
        _rpcDown = true;
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task StartForwardsUsernameAndIpAndPipes()
    {
        _answer = (_, _) => T.JsonResponse(200, $$"""{"request_id":"{{RequestId}}","nonce":"{{RecoveryRulesTests.Nonce}}"}""");
        var ctx = StartPost("""{"username":"  RecoveryTest ","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_privateApi);
        Assert.Equal("recovery-request/start", call.Endpoint);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("""{"username":"recoverytest","ip":"203.0.113.9"}""", call.Payload!.ToJsonString());
        Assert.Contains("request_id", Body(ctx));
        Assert.DoesNotContain("get_account_history", _rpc);
    }

    [Fact]
    public async Task StartPipesPrivateApiErrors()
    {
        _answer = (_, _) => T.JsonResponse(429, """{"error":"rate_limited"}""");
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(429, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"rate_limited"}""", Body(ctx));
    }

    [Fact]
    public async Task StartPassesInProgressWithItsRetryTimeThrough()
    {
        const string answer = """{"error":"in_progress","retry_after":"2026-11-11T10:15:00Z"}""";
        _answer = (_, _) => T.JsonResponse(409, answer);
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal(answer, Body(ctx));
    }

    // ---- prove ----

    [Theory]
    [InlineData("not-a-uuid", New, Old, "0")]
    [InlineData(RequestId, "STMbogus", Old, "0")]
    [InlineData(RequestId, New, "STMbogus", "0")]
    public async Task ProveValidatesInputBeforeAnyCall(string id, string newPub, string proven, string _)
    {
        var ctx = T.Post("/private-api/recovery/prove", ProveBody(newPub: newPub, proven: proven, id: id));
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_request"}""", Body(ctx));
        Assert.Empty(_privateApi);
        Assert.Empty(_rpc);
    }

    [Fact]
    public async Task ProveRejectsNewKeyEqualToProvenKey()
    {
        var ctx = T.Post("/private-api/recovery/prove", ProveBody(newPub: Old));
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_request"}""", Body(ctx));
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task ProveUnknownRequest()
    {
        _answer = (_, _) => T.JsonResponse(404, """{"error":"not_found"}""");
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_found"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Theory]
    [InlineData("proven", RecoveryRulesTests.Username)]
    [InlineData("started", "someoneelse")]
    public async Task ProveNeedsAStartedRowForTheSameUser(string status, string rowUser)
    {
        RowIs(status, rowUser);
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"bad_state"}""", Body(ctx));
        Assert.Single(_privateApi);
        Assert.DoesNotContain(RecoveryRulesTests.Nonce, Body(ctx));
    }

    [Fact]
    public async Task ProveWithAWrongSignature()
    {
        RowIs("started");
        var key = HiveCrypto.FromLogin("x", "y", "owner");
        var sig = HiveCrypto.Sign(key, HiveCrypto.Sha256Utf8("something else"));
        var ctx = T.Post("/private-api/recovery/prove", ProveBody(sig: sig));
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_proof"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task ProveWithAKeyFromBeforeTheLastRecovery()
    {
        RowIs("started");
        _lastRecovery = "2026-11-08T10:00:00";
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"key_not_recent"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task ProveRejectsNewKeyThatIsACurrentOwnerKey()
    {
        RowIs("started");
        _owner = RecoveryRulesTests.Authority(New);
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_request"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task ProveOtherRecoveryAccount()
    {
        RowIs("started");
        _recoveryAccount = "someoneelse";
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_ecency_recovery"}""", Body(ctx));
    }

    [Fact]
    public async Task ProveForwardsTheValidityAndNeverTheNonce()
    {
        RowIs("started");
        var inner = _answer;
        _answer = (endpoint, method) => method == HttpMethod.Post
            ? T.JsonResponse(200, """{"emails":["o****9@gmail.com"]}""")
            : inner(endpoint, method);
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("""{"emails":["o****9@gmail.com"]}""", Body(ctx));
        Assert.Equal(2, _privateApi.Count);
        var call = _privateApi[1];
        Assert.Equal($"recovery-request/{RequestId}/prove", call.Endpoint);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(
            $$"""{"new_owner_pub":"{{New}}","proven_key_pub":"{{Old}}","proven_key_valid_until":"2026-11-08T10:00:00","signature":"{{RecoveryRulesTests.Signature}}","owner_since":"2026-02-14T18:30:00"}""",
            call.Payload!.ToJsonString());
        Assert.Equal(new[] { OwnerOpsFilter }, _historyFilters.ToArray());
    }

    // ---- verify-code ----

    [Theory]
    [InlineData("""{"request_id":"nope","code":"123456"}""")]
    [InlineData("""{"request_id":"3f2b1c9e-8d7a-4b6c-9e5f-1a2b3c4d5e6f","code":"12345"}""")]
    [InlineData("""{"request_id":"3f2b1c9e-8d7a-4b6c-9e5f-1a2b3c4d5e6f","code":123456}""")]
    public async Task VerifyValidatesInput(string json)
    {
        var ctx = T.Post("/private-api/recovery/verify-code", json);
        await PrivateApi.RecoveryVerifyCode(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_request"}""", Body(ctx));
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task VerifyForwardsTheCode()
    {
        _answer = (_, _) => T.JsonResponse(400, """{"error":"invalid_code","attempts_left":3}""");
        var ctx = T.Post("/private-api/recovery/verify-code", $$"""{"request_id":"{{RequestId}}","code":"123456"}""");
        await PrivateApi.RecoveryVerifyCode(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_code","attempts_left":3}""", Body(ctx));
        var call = Assert.Single(_privateApi);
        Assert.Equal($"recovery-request/{RequestId}/verify", call.Endpoint);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("""{"code":"123456"}""", call.Payload!.ToJsonString());
    }

    // ---- status ----

    [Fact]
    public async Task StatusValidatesInput()
    {
        var ctx = T.Get("/private-api/recovery/status", "request_id=../x");
        await PrivateApi.RecoveryStatus(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task StatusForwardsTheSafeRoute()
    {
        _answer = (_, _) => T.JsonResponse(200, """{"status":"requested","tx_id":"ab","error":null}""");
        var ctx = T.Get("/private-api/recovery/status", $"request_id={RequestId}");
        await PrivateApi.RecoveryStatus(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        var call = Assert.Single(_privateApi);
        Assert.Equal($"recovery-request/{RequestId}/status", call.Endpoint);
        Assert.Equal(HttpMethod.Get, call.Method);
    }

    // ---- complete ----

    [Fact]
    public async Task CompleteNeedsARequestedRow()
    {
        RowIs("approved", newOwnerPub: New);
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"bad_state"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task CompleteNeedsTheOwnerToBeTheNewKeyOnly()
    {
        RowIs("requested", newOwnerPub: New);
        _owner = RecoveryRulesTests.Authority(New);
        ((JsonArray)_owner["key_auths"]!).Add(new JsonArray(OtherPub, 1));
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_recovered"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public async Task CompleteForwards(int weight, int threshold)
    {
        RowIs("requested", newOwnerPub: New);
        _owner = RecoveryRulesTests.Authority(New, weight, threshold);
        var inner = _answer;
        _answer = (endpoint, method) => method == HttpMethod.Post
            ? T.JsonResponse(200, """{"status":"completed"}""")
            : inner(endpoint, method);
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("""{"status":"completed"}""", Body(ctx));
        Assert.Equal($"recovery-request/{RequestId}/complete", _privateApi[1].Endpoint);
    }

    // ---- round 2 ----

    [Fact]
    public async Task StartWithoutAClientIpIsRefused()
    {
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""", ip: "");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_request"}""", Body(ctx));
        Assert.Empty(_rpc);
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task ProveOnAnExpiredRowIsNotFound()
    {
        RowIs("expired");
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_found"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task CompleteOnAnExpiredRowIsNotFound()
    {
        RowIs("expired", newOwnerPub: New);
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_found"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task CompleteOnAnUnknownRowIsNotFound()
    {
        _answer = (_, _) => T.JsonResponse(404, """{"error":"not_found"}""");
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_found"}""", Body(ctx));
    }

    [Fact]
    public async Task StartWithAnUnreadableLastRecoveryIsUnavailable()
    {
        _lastRecovery = "not a time";
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task ProveWithAnUnreadableLastRecoveryIsUnavailable()
    {
        RowIs("started");
        _lastRecovery = null;
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Single(_privateApi);
    }

    private async Task<JsonObject> ProveExpectingOwnerSince(string? expected)
    {
        RowIs("started");
        var inner = _answer;
        _answer = (endpoint, method) => method == HttpMethod.Post
            ? T.JsonResponse(200, """{"emails":["o****9@gmail.com"]}""")
            : inner(endpoint, method);
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        var payload = Assert.IsType<JsonObject>(_privateApi[1].Payload);
        Assert.True(payload.ContainsKey("owner_since"));
        Assert.Equal(expected, payload["owner_since"]?.GetValue<string>());
        Assert.Equal("2026-11-08T10:00:00", payload["proven_key_valid_until"]!.GetValue<string>());
        return payload;
    }

    [Fact]
    public async Task ProveSendsANullOwnerSinceWhenOwnerOpsAreUnreadable()
    {
        _ownerOps = new JsonArray(RecoveryRulesTests.Op(1, "2026-01-01T00:00:00", "transfer", new JsonObject()));
        await ProveExpectingOwnerSince(null);
    }

    [Fact]
    public async Task ProveSendsANullOwnerSinceWhenTheHistoryReadFails()
    {
        // A long history can end the filtered scan with an error instead of an answer.
        _failingMethod = "get_account_history";
        await ProveExpectingOwnerSince(null);
    }

    [Fact]
    public async Task ProveIgnoresAnAccountTheUserCreatedForOwnerSince()
    {
        _ownerOps = new JsonArray(RecoveryRulesTests.Op(5, "2026-03-01T00:00:00", "create_claimed_account",
            new JsonObject
            {
                ["creator"] = RecoveryRulesTests.Username, ["new_account_name"] = "childaccount",
                ["owner"] = RecoveryRulesTests.Authority(Old),
            }));
        await ProveExpectingOwnerSince(null);
    }

    [Fact]
    public async Task ProveForwardsANullOwnerSinceWhenNoOpSetTheKey()
    {
        RowIs("started");
        _ownerOps = new JsonArray();
        var inner = _answer;
        _answer = (endpoint, method) => method == HttpMethod.Post
            ? T.JsonResponse(200, """{"emails":[]}""")
            : inner(endpoint, method);
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        var payload = Assert.IsType<JsonObject>(_privateApi[1].Payload);
        Assert.True(payload.ContainsKey("owner_since"));
        Assert.Null(payload["owner_since"]);
        Assert.Equal(RecoveryRulesTests.Signature, payload["signature"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(0, 1)]
    public async Task CompleteNeedsTheNewKeyToMeetTheThreshold(int weight, int threshold)
    {
        RowIs("requested", newOwnerPub: New);
        _owner = RecoveryRulesTests.Authority(New, weight, threshold);
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_recovered"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    // ---- round 3 ----

    [Fact]
    public async Task StartWhenNoKeyCanSatisfyTheOldOwnerAlone()
    {
        _ownerHistory = new JsonArray(RecoveryRulesTests.MultiKeyEntry("2026-11-08T10:00:00", 2, (Old, 1), (OtherPub, 1)));
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"key_not_sufficient"}""", Body(ctx));
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task ProveWithAPartialWeightKey()
    {
        RowIs("started");
        _ownerHistory = new JsonArray(
            RecoveryRulesTests.MultiKeyEntry("2026-11-08T10:00:00", 2, (Old, 1), (OtherPub, 2)));
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"key_not_sufficient"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task ProveWithAKeyWeightEqualToTheThreshold()
    {
        _ownerHistory = new JsonArray(RecoveryRulesTests.Entry(Old, "2026-11-08T10:00:00", weight: 3, threshold: 3));
        await ProveExpectingOwnerSince("2026-02-14T18:30:00");
    }

    [Fact]
    public async Task StartWithAnUnreadableOwnerHistoryEntryIsUnavailable()
    {
        _ownerHistory = new JsonArray(
            RecoveryRulesTests.Entry(Old, "2026-11-08T10:00:00"),
            RecoveryRulesTests.Entry(OtherPub, "garbled"));
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Equal("5", ctx.Response.Headers.RetryAfter.ToString());
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task ProveWithAnUnreadableOwnerHistoryEntryIsUnavailable()
    {
        RowIs("started");
        var bad = RecoveryRulesTests.Entry(Old, "2026-11-08T10:00:00");
        bad["previous_owner_authority"]!["weight_threshold"] = "one";
        _ownerHistory = new JsonArray(bad);
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Single(_privateApi);
    }

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(128)]
    [InlineData(132)]
    public async Task ProveRefusesASignatureOfTheWrongLengthBeforeAnyCall(int length)
    {
        RowIs("started");
        var ctx = T.Post("/private-api/recovery/prove", ProveBody(sig: new string('a', length)));
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_proof"}""", Body(ctx));
        Assert.Empty(_privateApi);
        Assert.Empty(_rpc);
    }

    [Fact]
    public async Task ProveRefusesOversizedFieldsBeforeAnyCall()
    {
        var huge = "STM" + new string('1', 1_000_000);
        foreach (var body in new[] { ProveBody(newPub: huge), ProveBody(proven: huge), ProveBody(id: RequestId + huge) })
        {
            var ctx = T.Post("/private-api/recovery/prove", body);
            await PrivateApi.RecoveryProve(ctx);
            Assert.Equal(400, ctx.Response.StatusCode);
            Assert.Equal("""{"error":"invalid_request"}""", Body(ctx));
        }
        Assert.Empty(_privateApi);
        Assert.Empty(_rpc);
    }

    [Fact]
    public async Task CompleteRefusesAnOwnerWithAnAccountAuth()
    {
        RowIs("requested", newOwnerPub: New);
        _owner = RecoveryRulesTests.Authority(New);
        ((JsonArray)_owner["account_auths"]!).Add(new JsonArray("someapp", 1));
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_recovered"}""", Body(ctx));
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task CompleteChainDownIsUnavailable()
    {
        RowIs("requested", newOwnerPub: New);
        _rpcDown = true;
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Single(_privateApi);
    }
}
