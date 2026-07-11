using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of src/server/handlers/auth-api.ts, plus the getTokenUrl/decodeToken
/// helpers it uses from src/common/helper/hive-signer.ts.
/// </summary>
public static class AuthApi
{
    /// <summary>hive-signer.ts getTokenUrl — values interpolated raw, not URL-encoded.</summary>
    private static string GetTokenUrl(string code, string secret) =>
        $"https://hivesigner.com/api/oauth2/token?code={code}&client_secret={secret}";

    /// <summary>
    /// hive-signer.ts decodeToken: normalize base64url -> base64, decode with
    /// Node Buffer leniency, JSON.parse. Returns the parsed node (JSON null
    /// parses to a null reference, matching the JS falsy check) or null on
    /// any decode/parse failure.
    /// </summary>
    private static JsonNode? DecodeToken(string code)
    {
        var bytes = B64u.DecodeLenient(code);
        if (bytes == null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// `${field}` template-literal / string-concatenation coercion for body
    /// fields (absent -> "undefined", null -> "null", etc), as used for
    /// username/password in hsTokenCreate.
    /// </summary>
    private static string JsTemplate(JsonObject body, string key) =>
        body.TryGetPropertyValue(key, out var v) ? JsToString(v) : "undefined";

    private static string JsToString(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject:
                return "[object Object]";
            case JsonArray arr:
                // Array.prototype.toString: elements joined with ",", null -> ""
                return string.Join(",", arr.Select(e => e == null ? "" : JsToString(e)));
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) return s;
                if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                return JsJson.Stringify(v);
            default:
                return "undefined";
        }
    }

    public static async Task HsTokenRefresh(HttpContext ctx)
    {
        var body = await ctx.ReadBody();
        var code = body.Str("code");

        // if (!decodeToken(code)) — the JS falsy check also rejects codes that
        // decode to falsy JSON (null/0/""/false).
        if (code == null || !JsJson.IsTruthy(DecodeToken(code)))
        {
            await ctx.SendText(401, "Unauthorized");
            return;
        }

        await Upstream.Pipe(
            Upstream.BaseApiRequest(GetTokenUrl(code, Config.HsClientSecret), HttpMethod.Get), ctx);
    }

    public static async Task HsTokenCreate(HttpContext ctx)
    {
        var body = await ctx.ReadBody();

        var username = JsTemplate(body, "username");
        var password = JsTemplate(body, "password");

        // parseInt((new Date().getTime() / 1000) + '', 10) -> whole seconds
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000;

        // {"type":"code",app} — JSON.stringify drops the "app" key when the
        // body has no app (undefined); present-with-null is kept as null.
        var signedMessage = new JsonObject { ["type"] = "code" };
        if (body.TryGetPropertyValue("app", out var app))
        {
            signedMessage["app"] = app?.DeepClone();
        }

        var messageObj = new JsonObject
        {
            ["signed_message"] = signedMessage,
            ["authors"] = new JsonArray(JsonValue.Create(username)),
            ["timestamp"] = timestamp,
        };

        var hash = HiveCrypto.Sha256Utf8(JsJson.Stringify(messageObj));
        var privateKey = HiveCrypto.FromLogin(username, password, "posting");
        var signature = HiveCrypto.Sign(privateKey, hash);
        messageObj["signatures"] = new JsonArray(JsonValue.Create(signature));

        // res.send(string) — plain 200 text response
        await ctx.SendText(200, B64u.Encode(JsJson.Stringify(messageObj)));
    }
}
