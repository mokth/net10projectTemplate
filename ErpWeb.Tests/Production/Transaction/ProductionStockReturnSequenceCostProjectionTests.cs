using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using Xunit;

namespace ErpWeb.Tests.Production.Transaction;

public sealed class ProductionStockReturnSequenceCostProjectionTests
{
    [Fact]
    public void Two_returns_reprice_in_order_from_corrected_issue()
    {
        var first = ProductionStockReturnSequenceCostProjection.Project(
            3m, 30m, 33m,
            [new(101, 1m, 10m, 10m), new(102, 1m, 10m, 10m)], 101);
        Assert.Equal(11m, first.CorrectedTargetValue);
        Assert.Equal(12m, first.ProjectedRemainingValue);
        Assert.Equal(1m, first.RemainingBaseQty);

        var second = ProductionStockReturnSequenceCostProjection.Project(
            3m, 30m, 33m,
            [new(101, 1m, 10m, 11m), new(102, 1m, 10m, 10m)], 102);
        Assert.Equal(11m, second.CorrectedTargetValue);
        Assert.Equal(11m, second.ProjectedRemainingValue);
        Assert.Equal(10m, second.OriginalRemainingValue);
        Assert.Equal(12m, second.EffectiveRemainingValue);
    }

    [Fact]
    public void Sequential_rounding_allocates_from_each_preceding_pool()
    {
        var projected = ProductionStockReturnSequenceCostProjection.Project(
            3m, 10m, 11m,
            [new(101, 1m, 3.333333m, 3.666667m),
             new(102, 1m, 3.333334m, 3.333334m)], 102);

        Assert.Equal(3.666667m, projected.CorrectedReturnValues[101]);
        Assert.Equal(3.666667m, projected.CorrectedTargetValue);
        Assert.Equal(3.666666m, projected.ProjectedRemainingValue);
    }

    [Fact]
    public void Rejects_out_of_order_and_incorrect_original_return_value()
    {
        var outOfOrder = Assert.Throws<StockLedgerException>(() =>
            ProductionStockReturnSequenceCostProjection.Project(3m, 30m, 33m,
                [new(101, 1m, 10m, 10m), new(102, 1m, 10m, 11m)], 101));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, outOfOrder.Error.Code);

        var wrongOriginal = Assert.Throws<StockLedgerException>(() =>
            ProductionStockReturnSequenceCostProjection.Project(3m, 30m, 33m,
                [new(101, 1m, 10m, 11m), new(102, 1m, 9m, 9m)], 102));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongOriginal.Error.Code);
    }

    [Fact]
    public void Rounded_unchanged_return_keeps_source_delta_in_remaining_pool()
    {
        var projected = ProductionStockReturnSequenceCostProjection.Project(
            100m, 200m, 200.25m,
            [new(101, 0.0001m, 0.0002m, 0.0002m)], 101);

        Assert.Equal(0.0002m, projected.CorrectedTargetValue);
        Assert.Equal(199.9998m, projected.OriginalRemainingValue);
        Assert.Equal(200.2498m, projected.ProjectedRemainingValue);
        Assert.Equal(200.2498m, projected.EffectiveRemainingValue);
    }
}
