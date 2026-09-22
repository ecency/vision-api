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
        CurationDeskWrites.RosterList, CurationDeskWrites.RosterSet, CurationDeskWrites.RosterRetire,
        CurationDeskWrites.ApplicationApply, CurationDeskWrites.ApplicationMine,
        CurationDeskWrites.ApplicationWithdraw, CurationDeskWrites.ApplicationList,
        CurationDeskWrites.ApplicationDecide, CurationDeskWrites.ApplicationVote,
        CurationDeskWrites.ApplicationWindow,
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
        if (ReferenceEquals(route, CurationDeskWrites.RosterSet))
            return "{" + forged + "\"curator\":\"bob\",\"role\":\"curator\"}";
        if (ReferenceEquals(route, CurationDeskWrites.RosterRetire))
            return "{" + forged + "\"curator\":\"bob\"}";
        if (ReferenceEquals(route, CurationDeskWrites.ApplicationApply))
            return "{" + forged + "\"motivation\":\"why\",\"availability\":\"evenings\",\"pick\":\"a post\"}";
        if (ReferenceEquals(route, CurationDeskWrites.ApplicationDecide))
            return "{" + forged + "\"applicant\":\"bob\",\"state\":\"declined\"}";
        if (ReferenceEquals(route, CurationDeskWrites.ApplicationVote))
            return "{" + forged + "\"applicant\":\"bob\",\"vote\":\"endorse\"}";
        if (ReferenceEquals(route, CurationDeskWrites.ApplicationWindow))
            return "{" + forged + "\"open\":true}";
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

    /// <summary>
    /// A mark carries the lane the desk was showing. It is rebuilt from the allow
    /// list and cleaned like a roster-feed body, so a key the feed does not know,
    /// a value it would refuse, and the paging fields never reach the backend.
    /// </summary>
    [Fact]
    public void MarkLaneIsRebuiltFromTheAllowListAndNormalized()
    {
        var payload = Ok(CurationDeskWrites.Mark,
            "{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"reviewed\",\"lane\":{" +
            "\"app\":\"peakd\",\"sort\":\"unique\",\"new_authors\":\"1\",\"rep_min\":250," +
            "\"window\":\"bogus\",\"community\":\"../etc\",\"cursor\":\"c1\",\"limit\":5,\"seed\":\"abcd1234\"," +
            "\"admin\":true,\"username\":\"mallory\"}}");
        var lane = Assert.IsType<JsonObject>(payload["lane"]);
        Assert.Equal(new[] { "app", "sort", "rep_min", "new_authors" }, lane.Select(kv => kv.Key).ToArray());
        Assert.Equal("peakd", lane["app"]!.GetValue<string>());
        Assert.Equal("unique", lane["sort"]!.GetValue<string>());
        Assert.Equal(100, lane["rep_min"]!.GetValue<int>());
        Assert.Equal(new[] { "username", "author", "permlink", "state", "lane" }, payload.Select(kv => kv.Key).ToArray());
    }

    /// <summary>
    /// The order travels, because it decides whether a position is a watermark: a mark
    /// on newest-first says nothing about the older posts. The seed never travels, and
    /// the backend reads the sort without its feed parser's seed rule.
    /// </summary>
    [Theory]
    [InlineData("random", true)]
    [InlineData("newest", true)]
    [InlineData("queue", true)]
    [InlineData("unique", true)]
    [InlineData("payout", false)]
    public void MarkLaneCarriesAKnownSortAndNeverTheSeed(string sort, bool travels)
    {
        var payload = Ok(CurationDeskWrites.Mark,
            $"{{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"reviewed\",\"lane\":{{\"sort\":\"{sort}\",\"seed\":\"abcd1234\",\"app\":\"peakd\"}}}}");
        var lane = Assert.IsType<JsonObject>(payload["lane"]);
        Assert.Equal(travels, lane.ContainsKey("sort"));
        Assert.False(lane.ContainsKey("seed"));
        Assert.Equal("peakd", lane["app"]!.GetValue<string>());
    }

    [Fact]
    public void MarkWithoutALaneStaysWithoutOne()
    {
        var payload = Ok(CurationDeskWrites.Mark, "{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"reviewed\"}");
        Assert.False(payload.ContainsKey("lane"));
        // and an empty object is a real answer: the whole queue
        var whole = Ok(CurationDeskWrites.Mark, "{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"reviewed\",\"lane\":{}}");
        Assert.Empty(Assert.IsType<JsonObject>(whole["lane"]));
    }

    [Theory]
    [InlineData("{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"reviewed\",\"lane\":\"peakd\"}")]
    [InlineData("{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"reviewed\",\"lane\":[\"peakd\"]}")]
    [InlineData("{\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"reviewed\",\"lane\":7}")]
    public void MarkLaneMustBeAnObject(string body)
    {
        Assert.Equal("lane must be an object", Rejected(CurationDeskWrites.Mark, body));
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
        // The 12 h band is the newest window; a gateway behind the desk would drop it silently.
        var shift = Ok(CurationDeskWrites.RosterFeed, "{\"window\":\"12h\"}");
        Assert.Equal("12h", shift["window"]!.GetValue<string>());

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

    // ---- roster writes -------------------------------------------------------

    [Fact]
    public void ARosterSetForwardsOnlyTheCuratorFieldsAndNeverTheCallersIdentity()
    {
        var payload = Ok(CurationDeskWrites.RosterSet,
            "{\"curator\":\"bob\",\"role\":\"mod\",\"rules\":{\"trail\":false,\"min_weight\":1090},"
            + "\"note\":\"mod only\",\"active\":true,\"added_by\":\"someone\",\"removed_at\":null}");
        Assert.Equal(new[] { "username", "curator", "role", "rules", "note" }, payload.Select(kv => kv.Key).ToArray());
        Assert.Equal("alice", payload["username"]!.GetValue<string>());
        Assert.Equal("bob", payload["curator"]!.GetValue<string>());
        // The rules object travels as sent: the backend stores it whole, and a key
        // dropped here would silently change what the admin asked for.
        var rules = (JsonObject)payload["rules"]!;
        Assert.False(rules["trail"]!.GetValue<bool>());
        Assert.Equal(1090, rules["min_weight"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{\"role\":\"curator\"}", "curator required")]
    [InlineData("{\"curator\":\"Bob\",\"role\":\"curator\"}", "curator required")]
    [InlineData("{\"curator\":\"bob\\n\",\"role\":\"curator\"}", "curator required")]
    [InlineData("{\"curator\":\"bob\"}", "invalid role")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"owner\"}", "invalid role")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":\"trail\"}", "rules must be an object")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":{\"weight\":1}}", "unknown rule: weight")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":{\"trail\":\"yes\"}}", "trail must be true or false")]
    public void AMalformedRosterSetIsRefusedRatherThanTrimmed(string json, string expected)
    {
        Assert.Equal(expected, Rejected(CurationDeskWrites.RosterSet, json));
    }

    [Theory]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":{\"min_weight\":10001}}")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":{\"min_weight\":-1}}")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":{\"min_weight\":true}}")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":{\"max_weight\":\"2000\"}}")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":{\"waves_only_below\":1.5}}")]
    public void AWeightRuleOutsideTheVoteRangeIsRefused(string json)
    {
        Assert.Contains("vote weight between 0 and 10000", Rejected(CurationDeskWrites.RosterSet, json));
    }

    [Fact]
    public void ACuratorNoteLongerThanTheColumnIsRefused()
    {
        var note = new string('x', 201);
        Assert.Equal("invalid note", Rejected(CurationDeskWrites.RosterSet,
            "{\"curator\":\"bob\",\"role\":\"curator\",\"note\":\"" + note + "\"}"));
        Assert.True(Ok(CurationDeskWrites.RosterSet,
            "{\"curator\":\"bob\",\"role\":\"curator\",\"note\":\"" + note[..200] + "\"}").ContainsKey("note"));
    }

    [Fact]
    public void ANoteIsMeasuredTheWayTheColumnMeasuresIt()
    {
        // varchar(200) counts characters and Python's len() counts code points, but
        // string.Length counts UTF-16 code units, so 200 emoji measure 400 and a note the
        // column would have accepted was refused here.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        Assert.Equal(400, emoji.Length);
        Assert.True(Ok(CurationDeskWrites.RosterSet,
            "{\"curator\":\"bob\",\"role\":\"curator\",\"note\":\"" + emoji + "\"}").ContainsKey("note"));
        Assert.Equal("invalid note", Rejected(CurationDeskWrites.RosterSet,
            "{\"curator\":\"bob\",\"role\":\"curator\",\"note\":\"" + emoji + "\U0001F600\"}"));
    }

    [Fact]
    public void APresentButNullRulesIsRefusedRatherThanForwarded()
    {
        // CopyIfPresent forwards a null through the allowlist, so "rules": null would
        // travel upstream while the fence claimed every rules value is an object.
        Assert.Equal("rules must be an object", Rejected(CurationDeskWrites.RosterSet,
            "{\"curator\":\"bob\",\"role\":\"curator\",\"rules\":null}"));
        // absent is still absent: that is how an admin clears every rule
        Assert.False(Ok(CurationDeskWrites.RosterSet,
            "{\"curator\":\"bob\",\"role\":\"curator\"}").ContainsKey("rules"));
    }

    [Fact]
    public void ARetireCarriesNothingButTheCurator()
    {
        var payload = Ok(CurationDeskWrites.RosterRetire, "{\"curator\":\"bob\",\"role\":\"admin\",\"force\":true}");
        Assert.Equal(new[] { "username", "curator" }, payload.Select(kv => kv.Key).ToArray());
        Assert.Equal("curator required", Rejected(CurationDeskWrites.RosterRetire, "{}"));
        Assert.Equal("curator required", Rejected(CurationDeskWrites.RosterRetire, "{\"curator\":\"..\"}"));
    }

    // ---- guest curator applications ------------------------------------------

    [Fact]
    public void AnApplicationForwardsTheThreeAnswersAndNothingElse()
    {
        var payload = Ok(CurationDeskWrites.ApplicationApply,
            "{\"motivation\":\"why\",\"availability\":\"evenings\",\"pick\":\"a post\","
            + "\"code\":\"as:alice\",\"username\":\"boss\"}");
        Assert.Equal(new[] { "username", "motivation", "availability", "pick" },
            payload.Select(kv => kv.Key).ToArray());
        // The caller is the validated account, never the one the body asked for.
        Assert.Equal("alice", payload["username"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("discord")]
    [InlineData("state")]
    [InlineData("role")]
    public void AnUnknownApplicationFieldIsRefusedRatherThanDropped(string field)
    {
        // The desk answers 400 for a field it does not know, so dropping one here would
        // turn that refusal into a silent half-application.
        Assert.Equal($"unknown field: {field}", Rejected(CurationDeskWrites.ApplicationApply,
            "{\"motivation\":\"why\",\"availability\":\"evenings\",\"pick\":\"a post\","
            + $"\"{field}\":\"x\"}}"));
    }

    [Fact]
    public void APresentButNullQueueLimitIsRefusedRatherThanForwarded()
    {
        Assert.Equal("limit must be a whole number from 1 to 200",
            Rejected(CurationDeskWrites.ApplicationList, "{\"limit\":null}"));
        // absent is still absent: that is how the backend's own default answers
        Assert.False(Ok(CurationDeskWrites.ApplicationList, "{}").ContainsKey("limit"));
    }

    [Theory]
    [InlineData("{\"availability\":\"evenings\",\"pick\":\"a post\"}", "motivation required")]
    [InlineData("{\"motivation\":\"   \",\"availability\":\"evenings\",\"pick\":\"a post\"}", "motivation required")]
    [InlineData("{\"motivation\":\"why\",\"availability\":null,\"pick\":\"a post\"}", "availability required")]
    [InlineData("{\"motivation\":\"why\",\"availability\":5,\"pick\":\"a post\"}", "availability required")]
    [InlineData("{\"motivation\":\"why\",\"availability\":\"evenings\"}", "pick required")]
    public void AnIncompleteApplicationIsRefused(string json, string expected)
    {
        Assert.Equal(expected, Rejected(CurationDeskWrites.ApplicationApply, json));
    }

    [Fact]
    public void AnAnswerIsMeasuredTheWayTheColumnMeasuresIt()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        Assert.Equal(400, emoji.Length);
        Assert.True(Ok(CurationDeskWrites.ApplicationApply,
            "{\"motivation\":\"why\",\"availability\":\"" + emoji + "\",\"pick\":\"a post\"}")
            .ContainsKey("availability"));
        Assert.Equal("availability is at most 200 characters", Rejected(CurationDeskWrites.ApplicationApply,
            "{\"motivation\":\"why\",\"availability\":\"" + emoji + "\U0001F600\",\"pick\":\"a post\"}"));
    }

    [Fact]
    public void TheApplicantsOwnRoutesCarryNothingTheCallerSent()
    {
        foreach (var route in new[] { CurationDeskWrites.ApplicationMine, CurationDeskWrites.ApplicationWithdraw })
        {
            var payload = Ok(route, "{\"username\":\"boss\",\"id\":7,\"state\":\"accepted\"}");
            Assert.Equal(new[] { "username" }, payload.Select(kv => kv.Key).ToArray());
            Assert.Equal("alice", payload["username"]!.GetValue<string>());
        }
    }

    [Fact]
    public void ADecisionNamesTheApplicantInItsOwnField()
    {
        var payload = Ok(CurationDeskWrites.ApplicationDecide,
            "{\"applicant\":\"bob\",\"state\":\"accepted\",\"role\":\"curator\",\"note\":\"guest curator\"}");
        Assert.Equal(new[] { "username", "applicant", "state", "role", "note" },
            payload.Select(kv => kv.Key).ToArray());
        Assert.Equal("alice", payload["username"]!.GetValue<string>());
        Assert.Equal("bob", payload["applicant"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"state\":\"accepted\"}", "applicant required")]
    [InlineData("{\"applicant\":\"Bob\",\"state\":\"accepted\"}", "applicant required")]
    [InlineData("{\"applicant\":\"bob\\n\",\"state\":\"accepted\"}", "applicant required")]
    [InlineData("{\"applicant\":\"bob\"}", "invalid state")]
    [InlineData("{\"applicant\":\"bob\",\"state\":\"open\"}", "invalid state")]
    [InlineData("{\"applicant\":\"bob\",\"state\":\"withdrawn\"}", "invalid state")]
    [InlineData("{\"applicant\":\"bob\",\"state\":\"accepted\",\"role\":\"admin\"}", "invalid role")]
    [InlineData("{\"applicant\":\"bob\",\"state\":\"accepted\",\"role\":null}", "invalid role")]
    public void AMalformedDecisionIsRefusedRatherThanTrimmed(string json, string expected)
    {
        Assert.Equal(expected, Rejected(CurationDeskWrites.ApplicationDecide, json));
    }

    [Fact]
    public void ANeverGrantedRoleCannotArriveThroughAnAcceptance()
    {
        // The seat an acceptance grants is a curator, or a mod when an admin says so.
        // Admin runs the desk, so it is not something a form hands out. `trial` is gone
        // for a different reason: a guest seat is TRAILED and bounded by its term, and an
        // untrailed month would be a month of work nothing follows.
        Assert.DoesNotContain("admin", CurationDeskWrites.ApplicationRoles);
        Assert.DoesNotContain("trial", CurationDeskWrites.ApplicationRoles);
    }

    // ---- electing a guest curator ------------------------------------------

    [Fact]
    public void AVoteNamesTheApplicantInItsOwnField()
    {
        var payload = Ok(CurationDeskWrites.ApplicationVote,
            "{\"applicant\":\"bob\",\"vote\":\"object\",\"note\":\"farms comments\"}");
        Assert.Equal(new[] { "username", "applicant", "vote", "note" },
            payload.Select(kv => kv.Key).ToArray());
        Assert.Equal("alice", payload["username"]!.GetValue<string>());
        Assert.Equal("bob", payload["applicant"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"vote\":\"endorse\"}", "applicant required")]
    [InlineData("{\"applicant\":\"Bob\",\"vote\":\"endorse\"}", "applicant required")]
    [InlineData("{\"applicant\":\"bob\"}", "invalid vote")]
    [InlineData("{\"applicant\":\"bob\",\"vote\":\"yes\"}", "invalid vote")]
    [InlineData("{\"applicant\":\"bob\",\"vote\":\"withdraw\"}", "invalid vote")]
    [InlineData("{\"applicant\":\"bob\",\"vote\":\"ENDORSE\"}", "invalid vote")]
    [InlineData("{\"applicant\":\"bob\",\"vote\":null}", "invalid vote")]
    [InlineData("{\"applicant\":\"bob\",\"vote\":true}", "invalid vote")]
    public void AMalformedVoteIsRefusedRatherThanTrimmed(string json, string expected)
    {
        Assert.Equal(expected, Rejected(CurationDeskWrites.ApplicationVote, json));
    }

    [Fact]
    public void AStopCanBeLiftedWithoutBecomingAnEndorsement()
    {
        // Without a third value the only way out of an objection is to endorse, which
        // makes somebody who merely stopped objecting add a vote toward the quorum.
        Assert.Contains("abstain", CurationDeskWrites.ApplicationVotes);
        Assert.Equal(new[] { "username", "applicant", "vote" },
            Ok(CurationDeskWrites.ApplicationVote, "{\"applicant\":\"bob\",\"vote\":\"abstain\"}")
                .Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public void AVoteNoteIsCappedInRunesNotUtf16Units()
    {
        // The column counts characters and so does Python's len(). An emoji is two UTF-16
        // units and one rune, so counting the wrong one refuses 250 valid characters.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 500));
        Assert.True(Ok(CurationDeskWrites.ApplicationVote,
            "{\"applicant\":\"bob\",\"vote\":\"endorse\",\"note\":\"" + emoji + "\"}")
            .ContainsKey("note"));
        Assert.Equal("invalid note", Rejected(CurationDeskWrites.ApplicationVote,
            "{\"applicant\":\"bob\",\"vote\":\"endorse\",\"note\":\"" + emoji + "\U0001F600\"}"));
    }

    [Theory]
    [InlineData("{\"open\":true,\"quorum\":0}", "quorum must be a whole number from 1 to 50")]
    [InlineData("{\"open\":true,\"quorum\":51}", "quorum must be a whole number from 1 to 50")]
    [InlineData("{\"open\":true,\"quorum\":true}", "quorum must be a whole number from 1 to 50")]
    [InlineData("{\"open\":true,\"quorum\":1.5}", "quorum must be a whole number from 1 to 50")]
    [InlineData("{\"open\":true,\"quorum\":\"3\"}", "quorum must be a whole number from 1 to 50")]
    // A present null would be copied through the allowlist and read as absent upstream,
    // quietly doing nothing while this fence claimed it had checked the number.
    [InlineData("{\"open\":true,\"quorum\":null}", "quorum must be a whole number from 1 to 50")]
    [InlineData("{\"open\":true,\"term_days\":0}", "term_days must be a whole number from 1 to 365")]
    [InlineData("{\"open\":true,\"term_days\":366}", "term_days must be a whole number from 1 to 365")]
    public void AMalformedElectionKnobIsRefused(string json, string expected)
    {
        Assert.Equal(expected, Rejected(CurationDeskWrites.ApplicationWindow, json));
    }

    [Fact]
    public void TheKnobsAreOptionalAndTravelWhenTheyAreSent()
    {
        Assert.Equal(new[] { "username", "open" },
            Ok(CurationDeskWrites.ApplicationWindow, "{\"open\":true}").Select(kv => kv.Key).ToArray());
        Assert.Equal(new[] { "username", "open", "quorum", "term_days" },
            Ok(CurationDeskWrites.ApplicationWindow, "{\"open\":true,\"quorum\":4,\"term_days\":14}")
                .Select(kv => kv.Key).ToArray());
    }

    [Theory]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"term_days\":-1}")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"term_days\":366}")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"term_days\":true}")]
    [InlineData("{\"curator\":\"bob\",\"role\":\"curator\",\"term_days\":null}")]
    public void AMalformedSeatTermIsRefused(string json)
    {
        Assert.Equal("term_days must be a whole number from 0 to 365",
            Rejected(CurationDeskWrites.RosterSet, json));
    }

    [Fact]
    public void AZeroTermIsHowASeatIsMadePermanent()
    {
        // 0 is not "no term given": absent means keep the term the seat has, and 0 is the
        // one way to say the seat should stop expiring. So it has to reach the backend.
        var payload = Ok(CurationDeskWrites.RosterSet,
            "{\"curator\":\"bob\",\"role\":\"curator\",\"term_days\":0}");
        Assert.Equal(new[] { "username", "curator", "role", "term_days" },
            payload.Select(kv => kv.Key).ToArray());
        Assert.Equal(0, payload["term_days"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{\"state\":\"nonsense\"}", "invalid state")]
    [InlineData("{\"limit\":0}", "limit must be a whole number from 1 to 200")]
    [InlineData("{\"limit\":201}", "limit must be a whole number from 1 to 200")]
    [InlineData("{\"limit\":\"50\"}", "limit must be a whole number from 1 to 200")]
    [InlineData("{\"limit\":1.5}", "limit must be a whole number from 1 to 200")]
    public void AMalformedQueueRequestIsRefused(string json, string expected)
    {
        Assert.Equal(expected, Rejected(CurationDeskWrites.ApplicationList, json));
    }

    [Fact]
    public void TheQueueTakesAStateAndALimitAndNothingElse()
    {
        var payload = Ok(CurationDeskWrites.ApplicationList,
            "{\"state\":\"open\",\"limit\":10,\"username\":\"boss\",\"cursor\":\"x\"}");
        Assert.Equal(new[] { "username", "state", "limit" }, payload.Select(kv => kv.Key).ToArray());
        // Neither is required: the backend's own defaults answer the common case.
        Assert.Equal(new[] { "username" },
            Ok(CurationDeskWrites.ApplicationList, "{}").Select(kv => kv.Key).ToArray());
    }

    [Theory]
    [InlineData("{}", "open must be true or false")]
    [InlineData("{\"message\":\"back soon\"}", "open must be true or false")]
    [InlineData("{\"open\":\"false\"}", "open must be true or false")]
    [InlineData("{\"open\":0}", "open must be true or false")]
    [InlineData("{\"open\":null}", "open must be true or false")]
    public void AWindowWithoutABooleanIsRefused(string json, string expected)
    {
        Assert.Equal(expected, Rejected(CurationDeskWrites.ApplicationWindow, json));
    }

    [Fact]
    public void AClosedWindowMessageIsCappedAtTheColumn()
    {
        var message = new string('x', 201);
        Assert.Equal("invalid message", Rejected(CurationDeskWrites.ApplicationWindow,
            "{\"open\":false,\"message\":\"" + message + "\"}"));
        var payload = Ok(CurationDeskWrites.ApplicationWindow,
            "{\"open\":false,\"message\":\"" + message[..200] + "\"}");
        Assert.Equal(new[] { "username", "open", "message" }, payload.Select(kv => kv.Key).ToArray());
        // A present null clears the message, and the fence lets that through.
        Assert.True(Ok(CurationDeskWrites.ApplicationWindow, "{\"open\":true,\"message\":null}")
            .ContainsKey("message"));
    }

    [Fact]
    public void TheRosterListCarriesNothingTheCallerSent()
    {
        var payload = Ok(CurationDeskWrites.RosterList, "{\"limit\":5,\"role\":\"admin\",\"include_retired\":true}");
        Assert.Equal(new[] { "username" }, payload.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public void TheRecommenderNameLengthBoundIsEnforced()
    {
        Assert.NotNull(PrivateApi.CurationDeskRecommenderPath(new string('a', 3)));
        Assert.Null(PrivateApi.CurationDeskRecommenderPath(new string('a', 17)));
    }
}
