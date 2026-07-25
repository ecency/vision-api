using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The token action ids are a client-facing contract: web and mobile map them to
/// concrete wallet operations, and an id neither client recognizes either renders
/// nothing (web drops unknown ids) or renders an unlabeled control (mobile passes
/// them through). Pin the ids and their order.
/// </summary>
public class WalletActionsTests
{
    private static string[] Ids(JsonArray actions) =>
        actions.Select(a => a!["id"]!.GetValue<string>()).ToArray();

    [Fact]
    public void HpActions_AreTheChainOperationNamesInRenderOrder()
    {
        Assert.Equal(
            new[] { "delegate_vesting_shares", "withdraw_vesting", "set_withdraw_vesting_route" },
            Ids(WalletApi.HpActions()));
    }

    [Fact]
    public void HiveActions_OnlyOfferSavingsWithdrawalWhenSavingsExist()
    {
        Assert.DoesNotContain("transfer_from_savings", Ids(WalletApi.BuildHiveActions(0)));
        Assert.Contains("transfer_from_savings", Ids(WalletApi.BuildHiveActions(1.5)));
    }

    [Fact]
    public void HbdActions_OnlyOfferSavingsWithdrawalWhenSavingsExist()
    {
        Assert.DoesNotContain("transfer_from_savings", Ids(WalletApi.BuildHbdActions(0)));
        Assert.Contains("transfer_from_savings", Ids(WalletApi.BuildHbdActions(1.5)));
    }
}
