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

        var result = await WalletApi.Optional(Task.FromResult(expected), "engine tokens", 2000);

        Assert.Single(result);
        Assert.Equal("LEO", result[0]!["symbol"]!.GetValue<string>());
    }

    [Fact]
    public async Task Optional_DegradesAFailedLegToEmptyInsteadOfThrowing()
    {
        var failed = Task.FromException<JsonArray>(new Exception("upstream down"));

        var result = await WalletApi.Optional(failed, "engine metrics", 2000);

        Assert.Empty(result);
    }

    // A stalling upstream, not a throwing one, is the outage that matters here:
    // the engine node pool is walked at 2s per attempt and far outlasts the leg
    // budget, so catching exceptions alone would leave the caller waiting until
    // its own timeout returned an empty layer — losing the balances entirely.
    [Fact]
    public async Task Optional_BoundsAStallingLeg()
    {
        var stalled = new TaskCompletionSource<JsonArray>();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await WalletApi.Optional(stalled.Task, "engine metrics", 150);
        started.Stop();

        Assert.Empty(result);
        Assert.True(started.ElapsedMilliseconds < 2000,
            $"should have given up near the timeout, took {started.ElapsedMilliseconds}ms");

        // Completing late must not fault anything the caller already moved past.
        stalled.SetException(new Exception("late failure"));
        await Task.Delay(50);
    }

    // Enrichment legs must not be able to take the layer down between them: even
    // with both token metadata and metrics failing, the balances still render
    // (unpriced) rather than the wallet showing no engine tokens at all.
    [Fact]
    public async Task Optional_LetsBalancesSurviveLosingEveryEnrichmentLeg()
    {
        var tokens = WalletApi.Optional(
            Task.FromException<JsonArray>(new Exception("tokens down")), "engine tokens", 2000);
        var metrics = WalletApi.Optional(
            Task.FromException<JsonArray>(new Exception("metrics down")), "engine metrics", 2000);

        Assert.Empty(await tokens);
        Assert.Empty(await metrics);

        // ConvertEngineToken null-tolerates both, so a balance row still converts.
        var converted = EcencyApi.Models.HiveEngine.ConvertEngineToken(
            new JsonObject { ["symbol"] = "LEO", ["balance"] = "1.5" }, null, null, null);
        Assert.Equal("LEO", JsVal.AsString(JsVal.Prop(converted, "symbol")));
    }
}
