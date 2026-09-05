using System.Text;
using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using Xunit;
using static EcencyApi.Tests.CurationDeskTestSupport;

namespace EcencyApi.Tests;

/// <summary>
/// A public desk body is memoized and shared-cached for its s-maxage, so a key
/// that names a curator or carries a hashed address must never get in. The
/// backend is specified to omit them; this is the fence on this side, and it
/// has to hold for every public route and at any depth of the tree.
/// </summary>
[Collection("curation-desk")]
public class CurationDeskPublicPayloadTests
{
    private const string Leaky =
        "{\"team_cursor\":{\"post_id\":1,\"created\":\"t\",\"set_by\":\"alice\",\"set_at\":\"t\"},"
        + "\"active_curators\":[{\"username\":\"alice\"}],\"trail_alerts\":[],"
        + "\"items\":[{\"post_id\":1,\"excluded_reason\":\"abuser\",\"marks\":[{\"curator\":\"alice\",\"note\":\"secret\"}],"
        + "\"recommenders\":[{\"username\":\"bob\",\"ip_hash\":\"ab\",\"key_id\":3}]}],\"generated_at\":\"t\"}";

    private static void AssertClean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    Assert.DoesNotContain(kv.Key, CurationDeskPublicPayload.PrivateKeys);
                    AssertClean(kv.Value);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) AssertClean(item);
                break;
        }
    }

    [Fact]
    public void TheFenceNamesEveryRosterOnlyKey()
    {
        Assert.Equal(
            new[] { "active_curators", "excluded_reason", "ip_hash", "key_id", "note", "set_at", "set_by", "trail_alerts" },
            CurationDeskPublicPayload.PrivateKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void StripRemovesPrivateKeysAtEveryDepthAndKeepsTheRest()
    {
        var node = JsonNode.Parse(Leaky);
        Assert.True(CurationDeskPublicPayload.Strip(node));
        AssertClean(node);

        var obj = (JsonObject)node!;
        Assert.Equal(1, obj["team_cursor"]!["post_id"]!.GetValue<int>());
        Assert.Equal("t", obj["team_cursor"]!["created"]!.GetValue<string>());
        Assert.Equal("t", obj["generated_at"]!.GetValue<string>());
        Assert.Equal("alice", obj["items"]![0]!["marks"]![0]!["curator"]!.GetValue<string>());
        Assert.Equal("bob", obj["items"]![0]!["recommenders"]![0]!["username"]!.GetValue<string>());
    }

    [Fact]
    public void ACleanBodyIsServedAsTheBytesItArrivedIn()
    {
        var r = JsonResponse(200, "{\"items\":[{\"post_id\":1,\"author\":\"bob\",\"trailed_by\":{\"curator\":\"alice\"}}],\"feed_version\":\"v\"}");
        Assert.False(CurationDeskPublicPayload.Strip(r.Json));
        Assert.Same(r.Bytes, CurationDeskPublicPayload.ToPublicBytes(r));
    }

    [Fact]
    public void ALeakyBodyIsReserializedWithoutTheKeys()
    {
        var r = JsonResponse(200, Leaky);
        var bytes = CurationDeskPublicPayload.ToPublicBytes(r);
        Assert.NotSame(r.Bytes, bytes);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var key in CurationDeskPublicPayload.PrivateKeys)
        {
            Assert.DoesNotContain("\"" + key + "\"", text);
        }
        Assert.Contains("\"generated_at\":\"t\"", text);
    }

    [Fact]
    public async Task EveryPublicRouteServesAndMemoizesTheStrippedBody()
    {
        var upstream = Install();
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, Leaky));

        foreach (var (name, handler, request, _) in PublicReads())
        {
            var ctx = request();
            await handler(ctx);
            Assert.Equal(200, ctx.Response.StatusCode);
            var served = JsonNode.Parse(Body(ctx));
            AssertClean(served);
            Assert.Equal("t", served!["generated_at"]!.GetValue<string>());

            // The memo holds the same clean bytes, so a hit cannot leak either.
            var again = request();
            await handler(again);
            AssertClean(JsonNode.Parse(Body(again)));
        }

        Assert.Equal(5, upstream.Calls.Count);
    }
}
