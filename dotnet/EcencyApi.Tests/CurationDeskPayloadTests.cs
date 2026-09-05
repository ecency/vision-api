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
        CurationDeskWrites.RecommendMeta, CurationDeskWrites.RecommendationDismiss,
    };

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
        return "{" + forged + "\"limit\":5}";
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
        Assert.Equal("excluded", unknown["view"]!.GetValue<string>());
        Assert.False(unknown["hide_reviewed"]!.GetValue<bool>());
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
}
