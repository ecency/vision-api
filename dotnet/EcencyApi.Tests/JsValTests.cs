using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The JS coercion primitives underpin the wallet numeric parity. These pin the
/// behaviors that differ from naive .NET parsing (Number("") == 0 but
/// parseFloat("") is NaN; Number("12abc") is NaN but parseFloat("12abc") is 12).
/// </summary>
public class JsValTests
{
    [Theory]
    [InlineData("", double.NaN)]
    [InlineData("   ", double.NaN)]
    [InlineData("12", 12)]
    [InlineData("12.5", 12.5)]
    [InlineData("12abc", 12)]
    [InlineData("  -3.14xyz", -3.14)]
    [InlineData("abc", double.NaN)]
    [InlineData(".5", 0.5)]
    [InlineData("1e3", 1000)]
    [InlineData("1.2e2foo", 120)]
    [InlineData("+7", 7)]
    [InlineData("Infinity", double.PositiveInfinity)]
    public void ParseFloat_MatchesJs(string input, double expected)
    {
        var actual = JsVal.ParseFloat(input);
        if (double.IsNaN(expected)) Assert.True(double.IsNaN(actual), $"expected NaN for '{input}', got {actual}");
        else Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("", 0)]            // Number("") === 0  (the classic footgun)
    [InlineData("   ", 0)]         // Number("  ") === 0
    [InlineData("12", 12)]
    [InlineData("12.5", 12.5)]
    [InlineData("12abc", double.NaN)]  // Number requires the WHOLE string
    [InlineData("  42  ", 42)]         // surrounding whitespace trimmed
    [InlineData("0x1F", 31)]           // hex literal
    [InlineData("abc", double.NaN)]
    [InlineData("1e3", 1000)]
    public void NumberCoerce_MatchesJs(string input, double expected)
    {
        var actual = JsVal.NumberCoerce(input);
        if (double.IsNaN(expected)) Assert.True(double.IsNaN(actual), $"expected NaN for '{input}', got {actual}");
        else Assert.Equal(expected, actual);
    }

    [Fact]
    public void LoneSurrogates_ExtractAndSerializeLikeJs()
    {
        // JSON.parse accepts lone surrogate escapes (a JS string is arbitrary
        // UTF-16); System.Text.Json throws InvalidOperationException when
        // materializing such strings, which turned payloads the Node service
        // handled fine into 500s. The lenient path must extract them, and
        // Stringify must re-emit the escape like well-formed JSON.stringify.
        var node = JsonNode.Parse("{\"app\":\"x\\ud83dy\"}")!;

        var s = JsVal.AsString(node["app"]);
        Assert.NotNull(s);
        Assert.Equal(3, s!.Length);
        Assert.Equal('\ud83d', s[1]);

        Assert.Equal("{\"app\":\"x\\ud83dy\"}", JsJson.Stringify(node));

        // ToJsString (template-literal coercion) must survive it too
        Assert.Equal("x\ud83dy", JsVal.ToJsString(node["app"]));
    }

    [Fact]
    public void ToJsString_CoercesLikeJs()
    {
        Assert.Equal("null", JsVal.ToJsString(null));
        Assert.Equal("[object Object]", JsVal.ToJsString(new JsonObject { ["a"] = 1 }));
        Assert.Equal("1,2,3", JsVal.ToJsString(new JsonArray(1, 2, 3)));
        Assert.Equal("hi", JsVal.ToJsString(JsonValue.Create("hi")));
        Assert.Equal("true", JsVal.ToJsString(JsonValue.Create(true)));
        Assert.Equal("42", JsVal.ToJsString(JsonValue.Create(42)));
        // Array.prototype.toString: null/undefined elements become empty
        Assert.Equal("1,,3", JsVal.ToJsString(new JsonArray(1, null, 3)));
    }

    [Fact]
    public void ParseMaybeNumber_MatchesTsHelper()
    {
        // number passthrough
        Assert.Equal(3.5, WalletApi.ParseMaybeNumber(JsonValue.Create(3.5)));
        // whole numeric string
        Assert.Equal(42, WalletApi.ParseMaybeNumber(JsonValue.Create("42")));
        // embedded number via regex fallback
        Assert.Equal(-7.25, WalletApi.ParseMaybeNumber(JsonValue.Create("balance: -7.25 HIVE")));
        // empty string -> null (trimmed empty short-circuits before Number())
        Assert.Null(WalletApi.ParseMaybeNumber(JsonValue.Create("")));
        // non-numeric -> null
        Assert.Null(WalletApi.ParseMaybeNumber(JsonValue.Create("nope")));
        // null node -> null
        Assert.Null(WalletApi.ParseMaybeNumber(null));
    }

    [Fact]
    public void ConvertBaseUnitsToAmount_HandlesBigIntegerStrings()
    {
        // 1 ETH in wei
        Assert.Equal(1.0, WalletApi.ConvertBaseUnitsToAmount("1000000000000000000", 18));
        // fractional trailing zeros stripped
        Assert.Equal(1.5, WalletApi.ConvertBaseUnitsToAmount("1500000000", 9));
        // sub-unit value
        Assert.Equal(0.000001, WalletApi.ConvertBaseUnitsToAmount("1000", 9));
        // negative
        Assert.Equal(-2.0, WalletApi.ConvertBaseUnitsToAmount("-2000000", 6));
        // empty -> 0
        Assert.Equal(0, WalletApi.ConvertBaseUnitsToAmount("", 8));
    }

    [Fact]
    public void ParseBoolean_MatchesTsHelper()
    {
        Assert.True(WalletApi.ParseBoolean(JsonValue.Create(true)));
        Assert.True(WalletApi.ParseBoolean(JsonValue.Create(1)));
        Assert.True(WalletApi.ParseBoolean(JsonValue.Create("yes")));
        Assert.False(WalletApi.ParseBoolean(JsonValue.Create("off")));
        Assert.False(WalletApi.ParseBoolean(JsonValue.Create(0)));
        Assert.Null(WalletApi.ParseBoolean(JsonValue.Create("maybe")));
        Assert.Null(WalletApi.ParseBoolean(null));
        Assert.Null(WalletApi.ParseBoolean(JsonValue.Create(2))); // only 0/1 map
    }
}
