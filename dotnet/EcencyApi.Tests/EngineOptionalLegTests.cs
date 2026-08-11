using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The engine layer is assembled from one required leg (balances) and three
/// enrichment legs (token metadata, market metrics, unclaimed rewards). Losing
/// enrichment must cost the decoration, not the balances: a metrics outage used
/// to surface identically to a total Hive-Engine outage — no tokens at all.
/// </summary>
public class EngineOptionalLegTests
{
    [Fact]
    public async Task Optional_PassesThroughASuccessfulLeg()
    {
        var expected = new JsonArray { new JsonObject { ["symbol"] = "LEO" } };

        var result = await WalletApi.Optional(Task.FromResult(expected), "engine tokens");

        Assert.Single(result);
        Assert.Equal("LEO", result[0]!["symbol"]!.GetValue<string>());
    }

    [Fact]
    public async Task Optional_DegradesAFailedLegToEmptyInsteadOfThrowing()
    {
        var failed = Task.FromException<JsonArray>(new Exception("upstream down"));

        var result = await WalletApi.Optional(failed, "engine metrics");

        Assert.Empty(result);
    }

    // Enrichment legs must not be able to take the layer down between them: even
    // with both token metadata and metrics failing, the balances still render
    // (unpriced) rather than the wallet showing no engine tokens at all.
    [Fact]
    public async Task Optional_LetsBalancesSurviveLosingEveryEnrichmentLeg()
    {
        var tokens = WalletApi.Optional(
            Task.FromException<JsonArray>(new Exception("tokens down")), "engine tokens");
        var metrics = WalletApi.Optional(
            Task.FromException<JsonArray>(new Exception("metrics down")), "engine metrics");

        Assert.Empty(await tokens);
        Assert.Empty(await metrics);

        // ConvertEngineToken null-tolerates both, so a balance row still converts.
        var converted = EcencyApi.Models.HiveEngine.ConvertEngineToken(
            new JsonObject { ["symbol"] = "LEO", ["balance"] = "1.5" }, null, null, null);
        Assert.Equal("LEO", JsVal.AsString(JsVal.Prop(converted, "symbol")));
    }
}
