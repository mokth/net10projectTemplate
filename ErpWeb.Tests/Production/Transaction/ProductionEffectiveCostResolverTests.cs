using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Production.Transaction;

public sealed class ProductionEffectiveCostResolverTests
{
    [Fact]
    public void Selects_replacement_after_prior_restatement_is_reversed()
    {
        var initial = Restatement(1, 10m, 12m, null);
        var reversal = Restatement(2, 12m, 10m, 1);
        var replacement = Restatement(3, 10m, 15m, null);

        var resolved = ProductionEffectiveCostResolver.Resolve(
            [new ProductionEffectiveCost(5, 10m, 1m, null)],
            [initial, reversal, replacement]);

        Assert.Equal(15m, resolved[5].TotalCost);
        Assert.Equal(3L, resolved[5].RestatementFactId);
    }

    [Fact]
    public void Rejects_two_active_restatements_for_one_movement()
    {
        var error = Assert.Throws<StockLedgerException>(() =>
            ProductionEffectiveCostResolver.Resolve(
                [new ProductionEffectiveCost(5, 10m, 1m, null)],
                [Restatement(1, 10m, 12m, null), Restatement(2, 10m, 15m, null)]));

        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    private static ProductionCostRestatementFact Restatement(
        long id, decimal prior, decimal corrected, long? reverses) => new()
    {
        Id = id,
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        ReplayRequestId = Guid.NewGuid(),
        ReplayStockPostingId = 99,
        ProductionBalLotMovementId = 5,
        EffectiveAt = new DateTime(2026, 10, 1),
        PriorEffectiveTotalCost = prior,
        CorrectedEffectiveTotalCost = corrected,
        CorrectedUnitCost = corrected / 10m,
        DeltaCost = corrected - prior,
        ReversesRestatementFactId = reverses
    };
}
