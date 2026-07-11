using System.Text.Json;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// Byte-for-byte verification of the crypto port against golden vectors
/// generated from the exact dhive/js-base64 versions the Node service uses
/// (dotnet/tools/gen-vectors.js).
/// </summary>
public class HiveCryptoTests
{
    private static readonly JsonObject Vectors = LoadVectors();

    private static JsonObject LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "crypto-vectors.json");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    [Fact]
    public void FromLogin_MatchesDhive()
    {
        foreach (var v in Vectors["fromLogin"]!.AsArray())
        {
            var username = v!["username"]!.GetValue<string>();
            var password = v["password"]!.GetValue<string>();
            var role = v["role"]!.GetValue<string>();

            var key = HiveCrypto.FromLogin(username, password, role);

            Assert.Equal(v["wif"]!.GetValue<string>(), HiveCrypto.ToWif(key));
            Assert.Equal(v["publicKey"]!.GetValue<string>(),
                HiveCrypto.PublicKeyToString(key.CreatePubKey()));
        }
    }

    [Fact]
    public void Sign_MatchesDhiveByteForByte()
    {
        foreach (var v in Vectors["sign"]!.AsArray())
        {
            var key = HiveCrypto.FromLogin(
                v!["username"]!.GetValue<string>(),
                v["password"]!.GetValue<string>(),
                v["role"]!.GetValue<string>());

            var digest = HiveCrypto.Sha256Utf8(v["message"]!.GetValue<string>());
            Assert.Equal(v["digestHex"]!.GetValue<string>(), Convert.ToHexStringLower(digest));

            Assert.Equal(v["signature"]!.GetValue<string>(), HiveCrypto.Sign(key, digest));
        }
    }

    [Fact]
    public void Recover_MatchesDhive()
    {
        foreach (var v in Vectors["recover"]!.AsArray())
        {
            var digest = Convert.FromHexString(v!["digestHex"]!.GetValue<string>());
            var recovered = HiveCrypto.RecoverPublicKey(v["signature"]!.GetValue<string>(), digest);

            Assert.Equal(v["recoveredPublicKey"]!.GetValue<string>(), recovered);
        }
    }

    [Fact]
    public void B64uEncode_MatchesJsBase64()
    {
        foreach (var v in Vectors["b64u"]!.AsArray())
        {
            Assert.Equal(v!["encoded"]!.GetValue<string>(),
                B64u.Encode(v["input"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void HsTokenCreate_FullFlow_MatchesNode()
    {
        foreach (var v in Vectors["hsTokenCreate"]!.AsArray())
        {
            var username = v!["username"]!.GetValue<string>();
            var password = v["password"]!.GetValue<string>();
            var app = v["app"]!.GetValue<string>();
            var timestamp = v["timestamp"]!.GetValue<long>();

            // Reproduce hsTokenCreate exactly: build the message object with JS
            // property order, hash the JSON.stringify form, sign, then append
            // signatures and b64u-encode.
            var messageObj = new JsonObject
            {
                ["signed_message"] = new JsonObject { ["type"] = "code", ["app"] = app },
                ["authors"] = new JsonArray(username),
                ["timestamp"] = timestamp,
            };

            var hash = HiveCrypto.Sha256Utf8(JsJson.Stringify(messageObj));
            var key = HiveCrypto.FromLogin(username, password, "posting");
            var signature = HiveCrypto.Sign(key, hash);
            messageObj["signatures"] = new JsonArray(signature);

            var signedJson = JsJson.Stringify(messageObj);
            Assert.Equal(v["signedJson"]!.GetValue<string>(), signedJson);
            Assert.Equal(v["code"]!.GetValue<string>(), B64u.Encode(signedJson));
        }
    }

    [Fact]
    public void ValidateCodeReserialization_MatchesNode()
    {
        foreach (var v in Vectors["validateCodeRaw"]!.AsArray())
        {
            var token = (JsonObject)JsonNode.Parse(v!["tokenJson"]!.GetValue<string>())!;

            // The exact re-serialization validateCode performs:
            // JSON.stringify({signed_message, authors, timestamp}) with the
            // parsed nodes (nested key order preserved from the token).
            var raw = new JsonObject
            {
                ["signed_message"] = token["signed_message"]!.DeepClone(),
                ["authors"] = token["authors"]!.DeepClone(),
                ["timestamp"] = token["timestamp"]!.DeepClone(),
            };

            var rawMessage = JsJson.Stringify(raw);
            Assert.Equal(v["rawMessage"]!.GetValue<string>(), rawMessage);
            Assert.Equal(v["digestHex"]!.GetValue<string>(),
                Convert.ToHexStringLower(HiveCrypto.Sha256Utf8(rawMessage)));
        }
    }

    [Fact]
    public void NumberFormatting_MatchesV8()
    {
        foreach (var v in Vectors["numberFormat"]!.AsArray())
        {
            var value = v!["value"]!.GetValue<double>();
            var expected = v["text"]!.GetValue<string>();
            // JSON.stringify path (fixture "value" is V8-serialized, so parsing it
            // and re-serializing must reproduce "text" byte-for-byte)
            Assert.Equal(expected, JsJson.Stringify(JsonValue.Create(value)));
        }
    }

    [Theory]
    [InlineData("{\"a\":1,\"b\":\"x\"}")]
    [InlineData("{\"b\":2,\"a\":1}")] // property order preserved, not sorted
    [InlineData("[1,2.5,\"s\",true,null,{}]")]
    [InlineData("{\"n\":1751900000.123}")]
    [InlineData("{\"u\":\"caf\\u00e9 漢字\"}")]
    public void JsJson_RoundTripsParsedJson(string json)
    {
        var node = JsonNode.Parse(json);
        var expected = json.Replace("\\u00e9", "é"); // JSON.stringify emits raw unicode
        Assert.Equal(expected, JsJson.Stringify(node));
    }
}
