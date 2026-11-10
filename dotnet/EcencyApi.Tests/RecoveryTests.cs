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

    internal static JsonObject Entry(string key, string lastValid) => new()
    {
        ["id"] = 1,
        ["account"] = Username,
        ["previous_owner_authority"] = new JsonObject
        {
            ["weight_threshold"] = 1,
            ["account_auths"] = new JsonArray(),
            ["key_auths"] = new JsonArray(new JsonArray(key, 1)),
        },
        ["last_valid_time"] = lastValid,
    };

    internal static JsonArray RecoverOp(string timestamp, int index = 7) =>
        new(index, new JsonObject
        {
            ["trx_id"] = "abc",
            ["block"] = 1,
            ["timestamp"] = timestamp,
            ["op"] = new JsonArray("recover_account", new JsonObject { ["account_to_recover"] = Username }),
        });

    private static string Fmt(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss");

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
        Assert.Null(RecoveryRules.ProvenKeyValidUntil(history, OldOwnerPub, Head, tr));
    }

    [Fact]
    public void KeyNewerThanTheLastRecoveryIsAccepted()
    {
        var tr = Head.AddDays(-2);
        var lvt = tr.AddSeconds(3);
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(lvt)));
        Assert.Equal(lvt, RecoveryRules.ProvenKeyValidUntil(history, OldOwnerPub, Head, tr));
    }

    [Fact]
    public void NoRecoveryMeansNoLineageConstraint()
    {
        var lvt = Head.AddDays(-5);
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(lvt)));
        Assert.Equal(lvt, RecoveryRules.ProvenKeyValidUntil(history, OldOwnerPub, Head, null));
    }

    [Fact]
    public void WindowEdgeIsExclusive()
    {
        var atEdge = new JsonArray(Entry(OldOwnerPub, Fmt(Head.AddDays(-30))));
        Assert.Null(RecoveryRules.ProvenKeyValidUntil(atEdge, OldOwnerPub, Head, null));

        var inside = Head.AddDays(-30).AddSeconds(1);
        var justInside = new JsonArray(Entry(OldOwnerPub, Fmt(inside)));
        Assert.Equal(inside, RecoveryRules.ProvenKeyValidUntil(justInside, OldOwnerPub, Head, null));
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
        Assert.Equal(newer, RecoveryRules.ProvenKeyValidUntil(history, OldOwnerPub, Head, null));
    }

    [Fact]
    public void UnknownKeyIsRejected()
    {
        var history = new JsonArray(Entry(NewOwnerPub, Fmt(Head.AddDays(-1))));
        Assert.Null(RecoveryRules.ProvenKeyValidUntil(history, OldOwnerPub, Head, null));
    }

    [Fact]
    public void RecentOwnerChangeRespectsWindowAndLineage()
    {
        Assert.False(RecoveryRules.HasRecentOwnerChange(new JsonArray(), Head, null));
        Assert.False(RecoveryRules.HasRecentOwnerChange(
            new JsonArray(Entry(OldOwnerPub, Fmt(Head.AddDays(-31)))), Head, null));
        var tr = Head.AddDays(-1);
        Assert.False(RecoveryRules.HasRecentOwnerChange(
            new JsonArray(Entry(OldOwnerPub, Fmt(tr))), Head, tr));
        Assert.True(RecoveryRules.HasRecentOwnerChange(
            new JsonArray(Entry(OldOwnerPub, Fmt(tr.AddSeconds(3)))), Head, tr));
    }

    [Fact]
    public void LatestRecoveryTimeIsTheMaximum()
    {
        var history = new JsonArray(
            RecoverOp("2026-10-01T00:00:00", 3),
            RecoverOp("2026-11-01T08:30:15", 9),
            RecoverOp("2026-10-20T00:00:00", 5));
        Assert.Equal(new DateTime(2026, 11, 1, 8, 30, 15, DateTimeKind.Utc),
            RecoveryRules.LatestRecoverAccountTime(history));
        Assert.Null(RecoveryRules.LatestRecoverAccountTime(new JsonArray()));
    }

    [Fact]
    public void RealChainKeyReplacedOneBlockBeforeTheRecoveryIsRejected()
    {
        var tr = Head.AddDays(-2);
        var history = new JsonArray(Entry(OldOwnerPub, Fmt(tr.AddSeconds(-3))));
        Assert.Null(RecoveryRules.ProvenKeyValidUntil(history, OldOwnerPub, Head, tr));
    }

    public static IEnumerable<object[]> UnreadableRecoverHistory() => new[]
    {
        // another op in an answer filtered to recover_account: the filter was not applied
        new object[] { new JsonArray(new JsonArray(4, new JsonObject
            { ["timestamp"] = "2026-11-09T00:00:00", ["op"] = new JsonArray("transfer", new JsonObject()) })) },
        new object[] { new JsonArray(new JsonArray(4, new JsonObject
            { ["timestamp"] = "yesterday", ["op"] = new JsonArray("recover_account", new JsonObject()) })) },
        new object[] { new JsonArray(new JsonArray(4, new JsonObject
            { ["timestamp"] = "2026-11-09T00:00:00", ["op"] = "recover_account" })) },
        new object[] { new JsonArray(new JsonArray(4)) },
        new object[] { new JsonArray(JsonValue.Create(4)) },
        new object[] { new JsonObject() },
    };

    [Theory]
    [MemberData(nameof(UnreadableRecoverHistory))]
    public void UnreadableRecoverHistoryIsAReadFailure(JsonNode history)
    {
        Assert.ThrowsAny<Exception>(() => RecoveryRules.LatestRecoverAccountTime(history));
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

    private static JsonArray HackedAccountOps() => new(
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
            RecoveryRules.OwnerSince(HackedAccountOps(), OldOwnerPub, validUntil));
        Assert.Equal(new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            RecoveryRules.OwnerSince(HackedAccountOps(), K0, new DateTime(2026, 2, 14, 18, 30, 0, DateTimeKind.Utc)));
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
            RecoveryRules.OwnerSince(ops, OldOwnerPub, validUntil));
        Assert.Null(RecoveryRules.OwnerSince(HackedAccountOps(), OldOwnerPub,
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
        Assert.Equal(new DateTime(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc), RecoveryRules.OwnerSince(ops, OldOwnerPub, until));
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), RecoveryRules.OwnerSince(ops, K0, until));
        Assert.Null(RecoveryRules.OwnerSince(ops, Taken, until));
        Assert.Null(RecoveryRules.OwnerSince(new JsonArray(), K0, until));
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
        Assert.ThrowsAny<Exception>(() => RecoveryRules.OwnerSince(ops, OldOwnerPub, Head));
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

    // chain state
    private string _recoveryAccount = "ecency";
    private bool _accountExists = true;
    private JsonArray _ownerHistory = new(RecoveryRulesTests.Entry(Old, "2026-11-08T10:00:00"));
    private JsonArray _accountHistory = new();
    private JsonNode _ownerOps = new JsonArray(RecoveryRulesTests.Op(77, "2026-02-14T18:30:00", "account_update",
        new JsonObject { ["account"] = RecoveryRulesTests.Username, ["owner"] = RecoveryRulesTests.Authority(Old) }));
    private JsonObject _owner = RecoveryRulesTests.Authority(OtherPub);
    private readonly List<double> _historyFilters = new();
    private const double RecoverFilter = 1L << 25;
    private const double OwnerOpsFilter = (1L << 9) | (1L << 10) | (1L << 23) | (1L << 25) | (1L << 43);
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
            if (_rpcDown) throw new Exception("all nodes failed");
            JsonNode? result = method switch
            {
                "get_accounts" => _accountExists
                    ? new JsonArray(new JsonObject
                    {
                        ["name"] = RecoveryRulesTests.Username,
                        ["recovery_account"] = _recoveryAccount,
                        ["owner"] = _owner.DeepClone(),
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
            RecoverFilter => _accountHistory.DeepClone(),
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
        _accountHistory = new JsonArray(RecoveryRulesTests.RecoverOp("2026-11-08T10:00:00"));
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
        Assert.Contains("get_account_history", _rpc);
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
        _accountHistory = new JsonArray(RecoveryRulesTests.RecoverOp("2026-11-08T10:00:00"));
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
        Assert.Equal(new[] { RecoverFilter, OwnerOpsFilter }, _historyFilters.OrderBy(f => f).ToArray());
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

    [Fact]
    public async Task CompleteForwards()
    {
        RowIs("requested", newOwnerPub: New);
        _owner = RecoveryRulesTests.Authority(New);
        ((JsonArray)_owner["account_auths"]!).Add(new JsonArray("someapp", 1));
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
    public async Task StartWithAnUnreadableRecoveryHistoryIsUnavailable()
    {
        _accountHistory = new JsonArray(new JsonArray(4, new JsonObject
            { ["timestamp"] = "2026-11-09T00:00:00", ["op"] = new JsonArray("transfer", new JsonObject()) }));
        var ctx = StartPost("""{"username":"recoverytest","captcha_token":"t"}""");
        await PrivateApi.RecoveryStart(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Empty(_privateApi);
    }

    [Fact]
    public async Task ProveWithAnUnreadableRecoveryHistoryIsUnavailable()
    {
        RowIs("started");
        _accountHistory = new JsonArray(new JsonArray(4, new JsonObject
            { ["timestamp"] = "bad", ["op"] = new JsonArray("recover_account", new JsonObject()) }));
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Single(_privateApi);
    }

    [Fact]
    public async Task ProveWithUnreadableOwnerOpsIsUnavailable()
    {
        RowIs("started");
        _ownerOps = new JsonArray(RecoveryRulesTests.Op(1, "2026-01-01T00:00:00", "transfer", new JsonObject()));
        var ctx = T.Post("/private-api/recovery/prove", ProveBody());
        await PrivateApi.RecoveryProve(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Single(_privateApi);
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
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(0, 1)]
    public async Task CompleteNeedsWeightAndThresholdOfOne(int weight, int threshold)
    {
        RowIs("requested", newOwnerPub: New);
        _owner = RecoveryRulesTests.Authority(New, weight, threshold);
        var ctx = T.Post("/private-api/recovery/complete", $$"""{"request_id":"{{RequestId}}"}""");
        await PrivateApi.RecoveryComplete(ctx);
        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal("""{"error":"not_recovered"}""", Body(ctx));
        Assert.Single(_privateApi);
    }
}
