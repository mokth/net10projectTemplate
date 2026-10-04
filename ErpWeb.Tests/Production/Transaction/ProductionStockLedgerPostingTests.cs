using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionStockLedgerPostingTests
{
    [Fact]
    public void Registry_signs_match_balance_signed_qty_helper()
    {
        var registry = new StockMovementRegistry();
        Assert.Equal(1, registry.GetRequired("ISSUE").Direction);
        Assert.Equal(-1, registry.GetRequired("CONSUME").Direction);
        Assert.Equal(
            ProductionBalLotSignedQty.SignedBaseQty(ProductionBalLotMovementTypes.Consume, 4m),
            registry.GetRequired("CONSUME").Direction * 4m);
        Assert.Equal(
            ProductionBalLotSignedQty.SignedBaseQty(ProductionBalLotMovementTypes.Produce, 3m),
            registry.GetRequired("PRODUCE").Direction * 3m);
    }

    [Fact]
    public void Compatibility_protocol_is_v2() =>
        Assert.Equal(2, StockLedgerCompatibility.ProtocolVersion);

    [Fact]
    public void Allocator_assigns_fifo_across_two_receipts()
    {
        var plan = new ProductionContributionAllocator().BuildPlan(
        [
            new ProductionContribution(1, 10, new DateTime(2026, 10, 1), 1, 1, 6m),
            new ProductionContribution(1, 11, new DateTime(2026, 10, 2), 2, 1, 6m)
        ],
        [
            new ProductionContributionDemand("LINE-A", 8m)
        ]);

        Assert.Equal(2, plan.Count);
        Assert.Equal(10, plan[0].ReceiptMovementId);
        Assert.Equal(6m, plan[0].BaseQty);
        Assert.Equal(11, plan[1].ReceiptMovementId);
        Assert.Equal(2m, plan[1].BaseQty);
    }
}
