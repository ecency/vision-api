using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The desk writes forward a body built here, never the client's. What matters
/// is who the backend believes is acting (the validated username, always) and
/// that a value the backend would only reject is refused before it travels.
/// </summary>
public class CurationDeskPayloadTests
{
    private static readonly CurationDeskWrites.Route[] AllRoutes =
    {
        CurationDeskWrites.RosterFeed, CurationDeskWrites.Tick, CurationDeskWrites.Mark,
        CurationDeskWrites.MarkClear, CurationDeskWrites.Marks, CurationDeskWrites.Cursor,
        CurationDeskWrites.RecommendMeta, CurationDeskWrites.RecommendationDismiss, CurationDeskWrites.Ingest,
    };

    private const string IngestBody =
        "\"v\":1,\"type\":\"post\",\"id\":\"post:bob/p\",\"ts\":\"2026-09-05T10:00:00Z\",\"attempts\":3,"
        + "\"payload\":{\"author\":\"bob\",\"permlink\":\"p\",\"rep\":71,\"flags\":{\"spaminator\":true}}";

    private static JsonObject Body(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonObject Ok(CurationDeskWrites.Route route, string json)
    {
        var (payload, error) = CurationDeskWrites.Build(route, "alice", Body(json));
        Assert.Null(error);
        Assert.NotNull(payload);
        return payload!;
    }

    private static string Rejected(CurationDeskWrites.Route route, string json)
    {
        var (payload, error) = CurationDeskWrites.Build(route, "alice", Body(json));
        Assert.Null(payload);
        Assert.NotNull(error);
        return error!;
    }

    /// <summary>A body that passes each route's validation, with a forged identity attached.</summary>
    private static string ValidBodyFor(CurationDeskWrites.Route route)
    {
        const string forged = "\"username\":\"victim\",\"code\":\"as:victim\",";
        if (ReferenceEquals(route, CurationDeskWrites.Mark))
            return "{" + forged + "\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"flagged\",\"reason\":\"farming\"}";
        if (ReferenceEquals(route, CurationDeskWrites.MarkClear) || ReferenceEquals(route, CurationDeskWrites.RecommendMeta))
            return "{" + forged + "\"author\":\"bob\",\"permlink\":\"p\"}";
        if (ReferenceEquals(route, CurationDeskWrites.Cursor))
            return "{" + forged + "\"post_id\":7,\"action\":\"advance\"}";
        if (ReferenceEquals(route, CurationDeskWrites.RecommendationDismiss))
            return "{" + forged + "\"author\":\"bob\",\"permlink\":\"p\",\"action\":\"restore\"}";
        if (ReferenceEquals(route, CurationDeskWrites.Ingest))
            return "{" + forged + IngestBody + "}";
        return "{" + forged + "\"limit\":5}";
    }

    // ---- ingest envelope -----------------------------------------------------

    [Fact]
    public void TheIngestEnvelopeIsForwardedWithoutTheSendersRetryCounter()
    {
        var payload = Ok(CurationDeskWrites.Ingest, "{" + IngestBody + "}");
        Assert.Equal("alice", payload["username"]!.GetValue<string>());
        Assert.Equal(1, payload["v"]!.GetValue<int>());
        Assert.Equal("post", payload["type"]!.GetValue<string>());
        Assert.Equal("post:bob/p", payload["id"]!.GetValue<string>());
        Assert.Equal("2026-09-05T10:00:00Z", payload["ts"]!.GetValue<string>());
        // The nested object travels as it is: the backend range-checks its fields.
        Assert.Equal("bob", payload["payload"]!["author"]!.GetValue<string>());
        Assert.True(payload["payload"]!["flags"]!["spaminator"]!.GetValue<bool>());
        Assert.False(payload.ContainsKey("attempts"));
        Assert.False(payload.ContainsKey("code"));
    }

    [Theory]
    [InlineData("{\"v\":2,\"type\":\"post\",\"id\":\"post:bob/p\",\"payload\":{}}", "unsupported envelope version")]
    [InlineData("{\"v\":\"1\",\"type\":\"post\",\"id\":\"post:bob/p\",\"payload\":{}}", "unsupported envelope version")]
    [InlineData("{\"type\":\"post\",\"id\":\"post:bob/p\",\"payload\":{}}", "unsupported envelope version")]
    [InlineData("{\"v\":1,\"type\":\"like\",\"id\":\"post:bob/p\",\"payload\":{}}", "invalid type")]
    [InlineData("{\"v\":1,\"id\":\"post:bob/p\",\"payload\":{}}", "invalid type")]
    [InlineData("{\"v\":1,\"type\":\"post\",\"payload\":{}}", "id required")]
    [InlineData("{\"v\":1,\"type\":\"post\",\"id\":\"\",\"payload\":{}}", "id required")]
    [InlineData("{\"v\":1,\"type\":\"post\",\"id\":7,\"payload\":{}}", "id required")]
    [InlineData("{\"v\":1,\"type\":\"post\",\"id\":\"post:bob/p\",\"ts\":5,\"payload\":{}}", "invalid ts")]
    [InlineData("{\"v\":1,\"type\":\"post\",\"id\":\"post:bob/p\"}", "payload required")]
    [InlineData("{\"v\":1,\"type\":\"post\",\"id\":\"post:bob/p\",\"payload\":\"x\"}", "payload required")]
    [InlineData("{\"v\":1,\"type\":\"post\",\"id\":\"post:bob/p\",\"payload\":[1]}", "payload required")]
    public void AnEnvelopeTheBackendWouldRefuseIsRefusedHere(string body, string error)
    {
        Assert.Equal(error, Rejected(CurationDeskWrites.Ingest, body));
    }

    [Fact]
    public void TheEventIdLengthBoundIsTheBackendsColumn()
    {
        string With(int length) =>
            "{\"v\":1,\"type\":\"vote\",\"id\":\"" + new string('a', length) + "\",\"payload\":{}}";
        Ok(CurationDeskWrites.Ingest, With(CurationDeskWrites.MaxIngestIdLength));
        Assert.Equal("id required", Rejected(CurationDeskWrites.Ingest, With(CurationDeskWrites.MaxIngestIdLength + 1)));
    }

    [Fact]
    public void TheValidatedUsernameIsTheOnlyIdentityForwarded()
    {
        foreach (var route in AllRoutes)
        {
            var payload = Ok(route, ValidBodyFor(route));
            Assert.Equal("alice", payload["username"]!.GetValue<string>());
            Assert.False(payload.ContainsKey("code"), route.UpstreamPath);

            // And no whitelist can ever be widened to include them.
            Assert.DoesNotContain("username", route.Keys);
            Assert.DoesNotContain("code", route.Keys);
        }
    }

    [Fact]
    public void OnlyWhitelistedKeysTravel()
    {
        var payload = Ok(CurationDeskWrites.Mark,
            "{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"noted\",\"note\":\"hi\",\"admin\":true,\"weight\":10000}");
        Assert.Equal(new[] { "username", "author", "permlink", "state", "note" }, payload.Select(kv => kv.Key).ToArray());

        var tick = Ok(CurationDeskWrites.Tick, "{\"since\":\"t\",\"need\":[1],\"visible\":[2],\"curator\":\"x\"}");
        Assert.Equal(new[] { "username", "since", "need", "visible" }, tick.Select(kv => kv.Key).ToArray());
    }

    [Theory]
    [InlineData("{\"permlink\":\"p\",\"state\":\"reviewed\"}", "author required")]
    [InlineData("{\"author\":\"\",\"permlink\":\"p\",\"state\":\"reviewed\"}", "author required")]
    [InlineData("{\"author\":\"bob\",\"state\":\"reviewed\"}", "permlink required")]
    [InlineData("{\"author\":\"bob\",\"permlink\":\"\",\"state\":\"reviewed\"}", "permlink required")]
    [InlineData("{\"author\":7,\"permlink\":\"p\",\"state\":\"reviewed\"}", "author required")]
    [InlineData("{\"author\":null,\"permlink\":\"p\",\"state\":\"reviewed\"}", "author required")]
    public void MarkRequiresANonEmptyAuthorAndPermlink(string body, string error)
    {
        Assert.Equal(error, Rejected(CurationDeskWrites.Mark, body));
    }

    [Fact]
    public void EveryPostAddressedRouteRequiresAuthorAndPermlink()
    {
        Assert.Equal("author required", Rejected(CurationDeskWrites.MarkClear, "{\"permlink\":\"p\"}"));
        Assert.Equal("permlink required", Rejected(CurationDeskWrites.MarkClear, "{\"author\":\"bob\",\"permlink\":\"\"}"));
        Assert.Equal("author required", Rejected(CurationDeskWrites.RecommendMeta, "{\"permlink\":\"p\"}"));
        Assert.Equal("permlink required",
            Rejected(CurationDeskWrites.RecommendationDismiss, "{\"author\":\"bob\",\"action\":\"dismiss\"}"));
    }

    [Theory]
    [InlineData("reviewed")]
    [InlineData("snoozed")]
    [InlineData("flagged")]
    [InlineData("noted")]
    public void MarkAcceptsEachKnownState(string state)
    {
        var payload = Ok(CurationDeskWrites.Mark, $"{{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"{state}\"}}");
        Assert.Equal(state, payload["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"author\":\"bob\",\"permlink\":\"p\"}")]
    [InlineData("{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"deleted\"}")]
    [InlineData("{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"Reviewed\"}")]
    [InlineData("{\"author\":\"bob\",\"permlink\":\"p\",\"state\":1}")]
    public void MarkRefusesAnUnknownState(string body)
    {
        Assert.Equal("invalid state", Rejected(CurationDeskWrites.Mark, body));
    }

    [Fact]
    public void MarksListStateIsOptionalButMustBeKnownWhenGiven()
    {
        Assert.Equal(new[] { "username" }, Ok(CurationDeskWrites.Marks, "{}").Select(kv => kv.Key).ToArray());
        Assert.Equal("snoozed", Ok(CurationDeskWrites.Marks, "{\"state\":\"snoozed\",\"limit\":10}")["state"]!.GetValue<string>());
        Assert.Equal("invalid state", Rejected(CurationDeskWrites.Marks, "{\"state\":\"all\"}"));
    }

    [Theory]
    [InlineData("advance")]
    [InlineData("rewind")]
    public void CursorAcceptsEachKnownAction(string action)
    {
        var payload = Ok(CurationDeskWrites.Cursor, $"{{\"post_id\":42,\"action\":\"{action}\",\"reason\":\"oops\"}}");
        Assert.Equal(action, payload["action"]!.GetValue<string>());
        Assert.Equal(42, payload["post_id"]!.GetValue<int>());
        Assert.Equal("oops", payload["reason"]!.GetValue<string>());
    }

    [Fact]
    public void CursorRefusesAnUnknownActionOrAMissingPostId()
    {
        Assert.Equal("invalid action", Rejected(CurationDeskWrites.Cursor, "{\"post_id\":42,\"action\":\"jump\"}"));
        Assert.Equal("invalid action", Rejected(CurationDeskWrites.Cursor, "{\"post_id\":42}"));
        Assert.Equal("post_id required", Rejected(CurationDeskWrites.Cursor, "{\"action\":\"advance\"}"));
        Assert.Equal("post_id required", Rejected(CurationDeskWrites.Cursor, "{\"post_id\":null,\"action\":\"advance\"}"));
        Assert.Equal("post_id required", Rejected(CurationDeskWrites.Cursor, "{\"post_id\":{\"id\":1},\"action\":\"advance\"}"));
    }

    [Fact]
    public void DismissAcceptsDismissAndRestoreOnly()
    {
        Ok(CurationDeskWrites.RecommendationDismiss, "{\"author\":\"bob\",\"permlink\":\"p\",\"action\":\"dismiss\"}");
        Ok(CurationDeskWrites.RecommendationDismiss, "{\"author\":\"bob\",\"permlink\":\"p\",\"action\":\"restore\"}");
        Assert.Equal("invalid action",
            Rejected(CurationDeskWrites.RecommendationDismiss, "{\"author\":\"bob\",\"permlink\":\"p\",\"action\":\"delete\"}"));
        Assert.Equal("invalid action",
            Rejected(CurationDeskWrites.RecommendationDismiss, "{\"author\":\"bob\",\"permlink\":\"p\"}"));
    }

    [Fact]
    public void RecommendMetaTrxIdIsOptionalAndStrictWhenPresent()
    {
        var without = Ok(CurationDeskWrites.RecommendMeta, "{\"author\":\"bob\",\"permlink\":\"p\"}");
        Assert.False(without.ContainsKey("trx_id"));

        var trx = new string('a', 40);
        var with = Ok(CurationDeskWrites.RecommendMeta, $"{{\"author\":\"bob\",\"permlink\":\"p\",\"trx_id\":\"{trx}\"}}");
        Assert.Equal(trx, with["trx_id"]!.GetValue<string>());

        foreach (var bad in new[] { "\"abc\"", "\"" + new string('A', 40) + "\"", "\"" + new string('a', 39) + "\"", "null", "42" })
        {
            Assert.Equal("invalid trx_id",
                Rejected(CurationDeskWrites.RecommendMeta, $"{{\"author\":\"bob\",\"permlink\":\"p\",\"trx_id\":{bad}}}"));
        }
    }

    [Fact]
    public void RecommendMetaForwardsOnlyAKnownUaClass()
    {
        Assert.Equal("mobile",
            Ok(CurationDeskWrites.RecommendMeta, "{\"author\":\"bob\",\"permlink\":\"p\",\"ua_class\":\"mobile\"}")["ua_class"]!.GetValue<string>());
        Assert.False(
            Ok(CurationDeskWrites.RecommendMeta, "{\"author\":\"bob\",\"permlink\":\"p\",\"ua_class\":\"bot\"}").ContainsKey("ua_class"));
        Assert.True(CurationDeskWrites.RecommendMeta.ForwardClientAddress);
        Assert.All(AllRoutes.Where(r => !ReferenceEquals(r, CurationDeskWrites.RecommendMeta)),
            r => Assert.False(r.ForwardClientAddress, r.UpstreamPath));
    }

    [Fact]
    public void RosterFeedKeepsSeedOnlyForTheRandomOrderAndClampsLimit()
    {
        var random = Ok(CurationDeskWrites.RosterFeed, "{\"sort\":\"random\",\"seed\":\"abcd1234\",\"limit\":500}");
        Assert.Equal("abcd1234", random["seed"]!.GetValue<string>());
        Assert.Equal(50, random["limit"]!.GetValue<int>());

        var queue = Ok(CurationDeskWrites.RosterFeed, "{\"sort\":\"queue\",\"seed\":\"abcd1234\",\"limit\":0}");
        Assert.False(queue.ContainsKey("seed"));
        Assert.Equal(1, queue["limit"]!.GetValue<int>());

        var unknown = Ok(CurationDeskWrites.RosterFeed, "{\"sort\":\"payout\",\"seed\":\"abcd1234\",\"view\":\"excluded\",\"hide_reviewed\":false}");
        Assert.False(unknown.ContainsKey("sort"));
        Assert.False(unknown.ContainsKey("seed"));
        // The roster is the only feed that lists excluded rows, so its view
        // allowlist is the public one plus that.
        Assert.Equal("excluded", unknown["view"]!.GetValue<string>());
        Assert.False(unknown["hide_reviewed"]!.GetValue<bool>());
    }

    [Fact]
    public void RosterFeedTakesOnlyASeedTheBackendCanHashWith()
    {
        foreach (var seed in new[] { "\"abc\"", "\"" + new string('a', 17) + "\"", "\"ABCD1234\"", "\"abcd 1234\"", "\"abcd1234\\n\"", "42", "null" })
        {
            var payload = Ok(CurationDeskWrites.RosterFeed, "{\"sort\":\"random\",\"seed\":" + seed + "}");
            Assert.False(payload.ContainsKey("seed"), seed);
        }
        Assert.Equal(new string('a', 16),
            Ok(CurationDeskWrites.RosterFeed, "{\"sort\":\"random\",\"seed\":\"" + new string('a', 16) + "\"}")["seed"]!.GetValue<string>());
    }

    [Fact]
    public void RosterFeedFiltersFollowThePublicFeedsValueRules()
    {
        // Allowlists: a value the public feed drops is dropped here too, so the
        // two feeds answer the same question for the same request.
        var enums = Ok(CurationDeskWrites.RosterFeed,
            "{\"view\":\"secret\",\"app\":\"hive\",\"window\":\"week\",\"community\":\"photography\",\"cursor\":\"a b\"}");
        Assert.Equal(new[] { "username" }, enums.Select(kv => kv.Key).ToArray());

        var kept = Ok(CurationDeskWrites.RosterFeed,
            "{\"view\":\"queue\",\"app\":\"ecency\",\"window\":\"full\",\"community\":\"hive-125125\",\"cursor\":\"s:abc.1:25\"}");
        Assert.Equal("queue", kept["view"]!.GetValue<string>());
        Assert.Equal("ecency", kept["app"]!.GetValue<string>());
        Assert.Equal("full", kept["window"]!.GetValue<string>());
        Assert.Equal("hive-125125", kept["community"]!.GetValue<string>());
        Assert.Equal("s:abc.1:25", kept["cursor"]!.GetValue<string>());

        // Trailing newlines and non-ASCII digits are not the value either.
        var newline = Ok(CurationDeskWrites.RosterFeed, "{\"community\":\"hive-125125\\n\",\"cursor\":\"abc\\n\",\"view\":\"queue\\n\"}");
        Assert.Equal(new[] { "username" }, newline.Select(kv => kv.Key).ToArray());
        Assert.False(Ok(CurationDeskWrites.RosterFeed, "{\"community\":\"hive-\\u0661\\u0662\\u0663\\u0664\\u0665\"}").ContainsKey("community"));

        // Ranges clamp instead of travelling as sent, from a number or its
        // string spelling; something that is neither is dropped.
        var ranges = Ok(CurationDeskWrites.RosterFeed,
            "{\"rep_min\":150,\"rep_max\":-1,\"min_words\":999999,\"max_words\":\"300\",\"limit\":\"99999999999\"}");
        Assert.Equal(100, ranges["rep_min"]!.GetValue<int>());
        Assert.Equal(0, ranges["rep_max"]!.GetValue<int>());
        Assert.Equal(50000, ranges["min_words"]!.GetValue<int>());
        Assert.Equal(300, ranges["max_words"]!.GetValue<int>());
        Assert.Equal(50, ranges["limit"]!.GetValue<int>());

        var unusable = Ok(CurationDeskWrites.RosterFeed, "{\"limit\":\"lots\",\"rep_min\":true,\"max_words\":null}");
        Assert.Equal(new[] { "username" }, unusable.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public void RosterFeedNumbersAreWholeNumbersOrNothing()
    {
        // These names count rows, reputations and words. A fraction is none of
        // them: truncating 1.9 to 1 would forward a filter nobody asked for, so
        // it is dropped and the backend applies its default, exactly as the
        // query string does with `limit=1.9`.
        var fractions = Ok(CurationDeskWrites.RosterFeed,
            "{\"limit\":1.9,\"rep_min\":10.5,\"rep_max\":99.9,\"min_words\":0.5,\"max_words\":300.25}");
        Assert.Equal(new[] { "username" }, fractions.Select(kv => kv.Key).ToArray());

        // A whole number is kept, as a number or as its plain spelling.
        Assert.Equal(12, Ok(CurationDeskWrites.RosterFeed, "{\"limit\":12}")["limit"]!.GetValue<int>());
        Assert.Equal(12, Ok(CurationDeskWrites.RosterFeed, "{\"limit\":\"12\"}")["limit"]!.GetValue<int>());
        Assert.Equal(40, Ok(CurationDeskWrites.RosterFeed, "{\"rep_min\":40}")["rep_min"]!.GetValue<int>());

        // JSON keeps no spelling of a number, so 1e6 is the number 1000000 and
        // clamps to the bound the same way that value does in a query string.
        Assert.Equal(50, Ok(CurationDeskWrites.RosterFeed, "{\"limit\":1e6}")["limit"]!.GetValue<int>());
        Assert.Equal(50, Ok(CurationDeskWrites.RosterFeed, "{\"limit\":1000000}")["limit"]!.GetValue<int>());
        Assert.Equal(1, Ok(CurationDeskWrites.RosterFeed, "{\"limit\":-5}")["limit"]!.GetValue<int>());
        Assert.Equal(new[] { "username" }, Ok(CurationDeskWrites.RosterFeed, "{\"limit\":-1.5}").Select(kv => kv.Key).ToArray());

        // A string is read by the query string's rule, so only a plain signed
        // integer is a number there.
        foreach (var spelling in new[] { "\"1e6\"", "\"1.9\"", "\"12.0\"", "\" 12\"", "\"0x0c\"" })
        {
            Assert.False(
                Ok(CurationDeskWrites.RosterFeed, "{\"limit\":" + spelling + "}").ContainsKey("limit"), spelling);
        }
    }

    [Fact]
    public void TheTickNamesAtMost100IdsPerList()
    {
        var need = string.Join(",", Enumerable.Range(1, 150));
        var visible = string.Join(",", Enumerable.Range(1000, 101));
        var payload = Ok(CurationDeskWrites.Tick,
            "{\"since\":\"t\",\"need\":[" + need + "],\"visible\":[" + visible + "]}");

        Assert.Equal(CurationDeskWrites.MaxTickIds, ((JsonArray)payload["need"]!).Count);
        Assert.Equal(CurationDeskWrites.MaxTickIds, ((JsonArray)payload["visible"]!).Count);
        Assert.Equal(1, payload["need"]![0]!.GetValue<int>());
        Assert.Equal(100, payload["need"]![99]!.GetValue<int>());
        Assert.Equal(1000, payload["visible"]![0]!.GetValue<int>());

        // A list that already fits travels unchanged.
        var short_ = Ok(CurationDeskWrites.Tick, "{\"since\":\"t\",\"need\":[1,2,3],\"visible\":[]}");
        Assert.Equal(3, ((JsonArray)short_["need"]!).Count);
        Assert.Empty((JsonArray)short_["visible"]!);
    }

    // ---- route 5 path --------------------------------------------------------

    [Fact]
    public void RealAuthorsAndPermlinksMapToTheUpstreamPathUnchanged()
    {
        Assert.Equal("curation/desk/post/good-karma/my-post-title-2026",
            PrivateApi.CurationDeskPostPath("good-karma", "my-post-title-2026"));
        Assert.Equal("curation/desk/post/user.name/re-a-b-c-20260905t101010z",
            PrivateApi.CurationDeskPostPath("user.name", "re-a-b-c-20260905t101010z"));
    }

    [Theory]
    // Dot segments would resolve upward once the string becomes a Uri.
    [InlineData("..", "p")]
    [InlineData("good-karma", "..")]
    [InlineData(".", "p")]
    // Route values arrive percent-decoded, so a slash is a slash; and the
    // still-encoded spelling is not a name character either.
    [InlineData("a/b", "p")]
    [InlineData("good-karma", "p/q")]
    [InlineData("a%2Fb", "p")]
    [InlineData("good-karma", "p%2Fq")]
    // A question mark or hash would truncate the path.
    [InlineData("a?x=1", "p")]
    [InlineData("good-karma", "p?x=1")]
    [InlineData("good-karma", "p#f")]
    // Outside the Hive name grammar.
    [InlineData("ab", "p")]
    [InlineData("Good-Karma", "p")]
    [InlineData("good_karma", "p")]
    [InlineData("good-karma", "")]
    [InlineData("good-karma", "P")]
    [InlineData("good-karma", "a_b")]
    [InlineData("", "p")]
    [InlineData("undefined-undefined", "p")]
    // The right alphabet and length, but not a name: labels must be three or
    // more characters, start with a letter and end with a letter or digit.
    [InlineData("-ab", "p")]
    [InlineData("abc-", "p")]
    [InlineData("a..b", "p")]
    [InlineData("ab.cdef", "p")]
    [InlineData("...", "p")]
    [InlineData(".abc", "p")]
    [InlineData("abc.", "p")]
    [InlineData("1abc", "p")]
    // `$` would match before a trailing newline; these anchor with \A and \z.
    [InlineData("good-karma\n", "p")]
    [InlineData("good-karma", "p\n")]
    public void AnythingOutsideTheNameGrammarIsRejected(string author, string permlink)
    {
        Assert.Null(PrivateApi.CurationDeskPostPath(author, permlink));
    }

    [Fact]
    public void ThePermlinkLengthBoundIsEnforced()
    {
        Assert.NotNull(PrivateApi.CurationDeskPostPath("good-karma", new string('a', 255)));
        Assert.Null(PrivateApi.CurationDeskPostPath("good-karma", new string('a', 256)));
        Assert.NotNull(PrivateApi.CurationDeskPostPath(new string('a', 16), "p"));
        Assert.Null(PrivateApi.CurationDeskPostPath(new string('a', 17), "p"));
    }

    // ---- route 14 path -------------------------------------------------------

    [Fact]
    public void ARecommenderNameMapsToTheUpstreamPathUnchanged()
    {
        Assert.Equal("curation/desk/recommenders/good-karma",
            PrivateApi.CurationDeskRecommenderPath("good-karma"));
        Assert.Equal("curation/desk/recommenders/user.name",
            PrivateApi.CurationDeskRecommenderPath("user.name"));
        Assert.Equal("curation/desk/recommenders/a-b",
            PrivateApi.CurationDeskRecommenderPath("a-b"));
        Assert.Equal("curation/desk/recommenders/abc1.d-2e.fgh",
            PrivateApi.CurationDeskRecommenderPath("abc1.d-2e.fgh"));
        Assert.Equal("curation/desk/recommenders/" + new string('a', 16),
            PrivateApi.CurationDeskRecommenderPath(new string('a', 16)));
    }

    [Theory]
    // Dot segments would resolve upward once the string becomes a Uri.
    [InlineData("..")]
    [InlineData(".")]
    // Route values arrive percent-decoded, so a slash is a slash; and the
    // still-encoded spelling is not a name character either.
    [InlineData("a/b")]
    [InlineData("a%2Fb")]
    // A question mark or hash would truncate the path.
    [InlineData("good-karma?x=1")]
    [InlineData("good-karma#f")]
    // Outside the Hive name grammar, on either side of the length bound.
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Good-Karma")]
    [InlineData("good_karma")]
    // The right alphabet and length, but not a name: labels must be three or
    // more characters, start with a letter and end with a letter or digit.
    [InlineData("-ab")]
    [InlineData("abc-")]
    [InlineData("a..b")]
    [InlineData("ab.cdef")]
    [InlineData("...")]
    [InlineData(".abc")]
    [InlineData("abc.")]
    [InlineData("1abc")]
    [InlineData("abc.-def")]
    [InlineData("abc.def-")]
    // `$` would match before a trailing newline; this anchors with \A and \z.
    [InlineData("good-karma\n")]
    public void ANameOutsideTheGrammarHasNoRecommenderPath(string username)
    {
        Assert.Null(PrivateApi.CurationDeskRecommenderPath(username));
    }

    [Fact]
    public void TheRecommenderNameLengthBoundIsEnforced()
    {
        Assert.NotNull(PrivateApi.CurationDeskRecommenderPath(new string('a', 3)));
        Assert.Null(PrivateApi.CurationDeskRecommenderPath(new string('a', 17)));
    }
}
