using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionTerminalAdjustmentCostProjectionTests
{
    [Fact]
    public void Applies_each_outbound_to_the_preceding_pool_and_conserves_rounded_value()
    {
        ProductionTerminalAdjustmentCostStep[] ordered =
        [
            new(101, 2m), new(102, 3m), new(103, 1m)
        ];

        var original = ProductionTerminalAdjustmentCostProjection.Project(
            7m, 29m, ordered);
        Assert.Equal(8.285714m, original.OutboundValues[101]);
        Assert.Equal(12.428572m, original.OutboundValues[102]);
        Assert.Equal(4.142857m, original.OutboundValues[103]);
        Assert.Equal(1m, original.RemainingBaseQty);
        Assert.Equal(4.142857m, original.RemainingValue);
        Assert.Equal(29m, original.OutboundValues.Values.Sum()
            + original.RemainingValue);

        var corrected = ProductionTerminalAdjustmentCostProjection.Project(
            7m, 35m, ordered);
        Assert.Equal([10m, 15m, 5m], ordered.Select(s =>
            corrected.OutboundValues[s.MovementId]));
        Assert.Equal(1m, corrected.RemainingBaseQty);
        Assert.Equal(5m, corrected.RemainingValue);
    }

    [Fact]
    public void Gives_all_remaining_value_to_a_final_full_quantity_outbound()
    {
        var result = ProductionTerminalAdjustmentCostProjection.Project(3m, 10m,
            [new(101, 1m), new(102, 2m)]);

        Assert.Equal(3.333333m, result.OutboundValues[101]);
        Assert.Equal(6.666667m, result.OutboundValues[102]);
        Assert.Equal(0m, result.RemainingBaseQty);
        Assert.Equal(0m, result.RemainingValue);
    }

    [Fact]
    public void Rejects_overconsumption_and_duplicate_movement_identity()
    {
        var over = Assert.Throws<StockLedgerException>(() =>
            ProductionTerminalAdjustmentCostProjection.Project(3m, 10m,
                [new(101, 2m), new(102, 2m)]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, over.Error.Code);

        var duplicate = Assert.Throws<StockLedgerException>(() =>
            ProductionTerminalAdjustmentCostProjection.Project(3m, 10m,
                [new(101, 1m), new(101, 1m)]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            duplicate.Error.Code);
    }
}
