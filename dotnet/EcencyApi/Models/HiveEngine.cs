using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Models;

/// <summary>
/// Port of src/models/hiveEngine.types.ts + converters.ts. The dynamic
/// Hive-Engine payloads stay as JsonNode trees; the converters replicate the
/// exact field mapping and parseFloat/Number coercions the TS applies.
/// </summary>
public static class HiveEngine
{
    // enum string values used when building JSON-RPC payloads
    public const string MethodFind = "find";
    public const string MethodFindOne = "findOne";
    public const string JsonRpc2 = "2.0";
    public const string ContractTokens = "tokens";
    public const string ContractMarket = "market";
    public const string TableBalances = "balances";
    public const string TableTokens = "tokens";
    public const string TableMetrics = "metrics";
    public const string IdOne = "1";

    /// <summary>convertEngineToken — merges balance/token/metrics/status into a HiveEngineToken shape.</summary>
    public static JsonObject? ConvertEngineToken(JsonNode? balanceObj, JsonNode? token, JsonNode? metrics, JsonNode? tokenStatus)
    {
        if (balanceObj == null || !JsVal.IsObjectOrArray(balanceObj))
        {
            // !balanceObj in JS is only true for null/undefined; a present object passes.
            if (balanceObj == null) return null;
        }

        JsonNode? tokenMetadata = null;
        var metadataStr = JsVal.AsString(JsVal.Prop(token, "metadata"));
        if (token != null && !string.IsNullOrEmpty(metadataStr))
        {
            try
            {
                tokenMetadata = JsonNode.Parse(metadataStr!);
            }
            catch
            {
                Console.WriteLine($"failed to parse token metadata {JsVal.AsString(JsVal.Prop(token, "symbol"))}");
                tokenMetadata = null;
            }
        }

        var stake = ParseFloatOrZero(JsVal.Prop(balanceObj, "stake"));
        var delegationsIn = ParseFloatOrZero(JsVal.Prop(balanceObj, "delegationsIn"));
        var delegationsOut = ParseFloatOrZero(JsVal.Prop(balanceObj, "delegationsOut"));
        var balance = ParseFloatOrZero(JsVal.Prop(balanceObj, "balance"));

        var tokenPrice = metrics != null ? ParseFloatRaw(JsVal.Prop(metrics, "lastPrice")) : 0;
        var percentChange = metrics != null ? ParseFloatRaw(JsVal.Prop(metrics, "priceChangePercent")) : 0;
        var volume24h = metrics != null ? ParseFloatRaw(JsVal.Prop(metrics, "volume")) : 0;

        var pendingRewards = tokenStatus != null ? (JsVal.AsNumber(JsVal.Prop(tokenStatus, "pendingRewards")) ?? 0) : 0;
        var unclaimedBalance = tokenStatus != null
            ? $"{JsVal.JsNumberToString(pendingRewards)} {JsVal.ToJsString(JsVal.Prop(tokenStatus, "symbol"))}"
            : "";

        return new JsonObject
        {
            ["symbol"] = JsVal.Prop(balanceObj, "symbol")?.DeepClone(),
            ["name"] = JsVal.AsString(JsVal.Prop(token, "name")) ?? "",
            ["icon"] = JsVal.AsString(JsVal.Prop(tokenMetadata, "icon")) ?? "",
            ["precision"] = JsVal.AsNumber(JsVal.Prop(token, "precision")) ?? 0,
            ["stakingEnabled"] = JsVal.AsBool(JsVal.Prop(token, "stakingEnabled")) ?? false,
            ["delegationEnabled"] = JsVal.AsBool(JsVal.Prop(token, "delegationEnabled")) ?? false,
            ["stakedBalance"] = stake,
            ["unclaimedBalance"] = unclaimedBalance,
            ["pendingRewards"] = pendingRewards,
            ["balance"] = balance,
            ["stake"] = stake,
            ["delegationsIn"] = delegationsIn,
            ["delegationsOut"] = delegationsOut,
            ["tokenPrice"] = tokenPrice,
            ["percentChange"] = percentChange,
            ["volume24h"] = volume24h,
        };
    }

    /// <summary>convertRewardsStatus — SCOT rewards row -> TokenStatus.</summary>
    public static JsonObject ConvertRewardsStatus(JsonNode? rawData)
    {
        var pendingToken = JsVal.AsNumber(JsVal.Prop(rawData, "pending_token"));
        var precision = JsVal.AsNumber(JsVal.Prop(rawData, "precision"));
        // pending_token / 10 ** precision — JS arithmetic (NaN propagates if missing)
        var pt = pendingToken ?? double.NaN;
        var pr = precision ?? double.NaN;
        var pendingRewards = pt / Math.Pow(10, pr);

        return new JsonObject
        {
            ["symbol"] = JsVal.Prop(rawData, "symbol")?.DeepClone(),
            ["pendingToken"] = NumOrRaw(pendingToken, JsVal.Prop(rawData, "pending_token")),
            ["precision"] = NumOrRaw(precision, JsVal.Prop(rawData, "precision")),
            ["pendingRewards"] = double.IsNaN(pendingRewards) ? null : pendingRewards,
        };
    }

    // parseFloat(x) || 0  (JS: NaN and 0 both fall to 0)
    private static double ParseFloatOrZero(JsonNode? n)
    {
        var f = ParseFloatRaw(n);
        return f == 0 || double.IsNaN(f) ? 0 : f;
    }

    // parseFloat(x): if the node is a string use JS parseFloat; if a number,
    // parseFloat(number) stringifies then parses (identity for finite numbers).
    private static double ParseFloatRaw(JsonNode? n)
    {
        var s = JsVal.AsString(n);
        if (s != null) return JsVal.ParseFloat(s);
        var num = JsVal.AsNumber(n);
        if (num.HasValue) return num.Value;
        // parseFloat(undefined/null/object) -> NaN
        return double.NaN;
    }

    private static JsonNode? NumOrRaw(double? parsed, JsonNode? raw) =>
        parsed.HasValue ? parsed.Value : raw?.DeepClone();
}
