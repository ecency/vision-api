using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/announcements.ts.
///
/// id - incremental
/// title - title of announcement
/// description - description of announcement
/// button_text - text of actionable button
/// button_link - link that actionable button opens
/// path - which path it should show, supports regex on location
/// auth - should there be authorized/logged in user to show announcement
/// ops - hive uri format operation for mobile app signing
/// proposal_ids - hive proposal ids for an inline support prompt; web builds the
///                update_proposal_votes operation from these, mobile uses ops
/// </summary>
public static class Announcements
{
    /// <summary>Fresh copy of the announcements array (wire shape identical to the TS export).</summary>
    public static JsonArray Json() => new()
    {
        new JsonObject
        {
            ["id"] = 110,
            ["title"] = "Support Ecency! ❤️",
            ["description"] = "New proposal to support Ecency and its future development. Every vote and support counts!",
            ["button_text"] = "Support now",
            ["button_link"] = "/proposals/379",
            ["path"] = "/@.+/(blog|posts|wallet|points|engine|permissions|spk)",
            ["auth"] = true,
            ["proposal_ids"] = new JsonArray { 379 },
            ["ops"] = "hive://sign/op/WyJ1cGRhdGVfcHJvcG9zYWxfdm90ZXMiLHsidm90ZXIiOiAiX19zaWduZXIiLCJwcm9wb3NhbF9pZHMiOiBbMzc5XSwiYXBwcm92ZSI6dHJ1ZSwiZXh0ZW5zaW9ucyI6IFtdfV0.",
        },
    };
}

public static partial class PrivateApi
{
    // GET ^/private-api/announcements$
    public static async Task GetAnnouncement(HttpContext ctx)
    {
        await ctx.SendJson(200, Announcements.Json());
    }
}
