using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionFinishedGoodCostCorrectionResult(
    ProductionCostRestatementWriteResult FinishedGoodCost,
    decimal CorrectedFinishedGoodValue,
    decimal RemainingPoolValue);

/// <summary>
/// Restates one FG outbound after the exact WAC Inventory receipt value delta
/// has been appended in the same root. The caller still owns Inventory cost
/// state, later-event recosting and final cross-ledger reconciliation.
/// </summary>
internal static class ProductionFinishedGoodCostCorrectionWriter
{
    public static async Task<ProductionFinishedGoodCostCorrectionResult> AppendWithinRootAsync(
        StockPostingContext context, int receiptBatchId,
        ProductionEffectiveCost expectedFinishedGood,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedFinishedGood);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage)
            throw Invalid("This bounded FG correction requires WAC valuation.");
        var db = context.Db;
        if (db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<StockValuationFact>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Stale("FG correction has pending pool or Inventory valuation changes.");

        var projection = await ProductionFinishedGoodSourceCostAuthority.ReadWithinRootAsync(
            context, receiptBatchId, cancellationToken);
        var fact = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .SingleAsync(x => x.Id == projection.FinishedGoodFactId, cancellationToken);
        var current = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode,
            [projection.FinishedGoodMovementId], cancellationToken))
            [projection.FinishedGoodMovementId];
        var alreadyCorrectedHere = current.RestatementFactId is not null
            && current.TotalCost == projection.CorrectedFinishedGoodValue;
        if (current.MovementId != projection.FinishedGoodMovementId
            || !alreadyCorrectedHere && (current != expectedFinishedGood
                || current.TotalCost != fact.TotalValue)
            || alreadyCorrectedHere && (expectedFinishedGood.MovementId != current.MovementId
                || expectedFinishedGood.TotalCost != fact.TotalValue
                || expectedFinishedGood.UnitCost != StockLedgerPrecision.Money(
                    fact.TotalValue / fact.BaseQty)
                || expectedFinishedGood.RestatementFactId is not null))
            throw Stale("FG effective cost changed after planning.");

        var receipt = await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.Id == projection.OriginalInventoryValuationFactId,
                cancellationToken);
        var correction = StockLedgerPrecision.Money(
            projection.CorrectedFinishedGoodValue - receipt.CostAmount);
        var deltas = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.OriginalValuationFactId == receipt.Id
                && x.InventoryHistoryId == null).ToArrayAsync(cancellationToken);
        if (correction == 0m && deltas.Length != 0
            || correction != 0m && (deltas.Length != 1
                || deltas[0].StockPostingId != context.Posting.Id
                || deltas[0].LedgerEpochId != context.Epoch.Id
                || deltas[0].CompanyCode != context.CompanyCode
                || deltas[0].BranchCode != context.BranchCode
                || deltas[0].Direction != Math.Sign(correction)
                || deltas[0].CostAmount != Math.Abs(correction)
                || deltas[0].BaseCostAmount != deltas[0].CostAmount
                || deltas[0].BaseQty != 0m
                || deltas[0].CostMethod != StockCostMethods.MovingAverage
                || deltas[0].ValuationStatus != StockValuationStatuses.Valued
                || deltas[0].ReversesValuationFactId is not null
                || deltas[0].ValuationVersion != receipt.ValuationVersion + 1
                || deltas[0].ItemCode != receipt.ItemCode
                || deltas[0].EffectiveAt != receipt.EffectiveAt
                || deltas[0].BusinessDate != receipt.BusinessDate
                || deltas[0].PeriodKey != receipt.PeriodKey
                || deltas[0].SourceLineId != receipt.SourceLineId
                || deltas[0].SourceDocumentType != receipt.SourceDocumentType
                || deltas[0].SourceDocumentId != receipt.SourceDocumentId
                || deltas[0].SourceDocumentNo != receipt.SourceDocumentNo
                || deltas[0].SourceDocumentLine != receipt.SourceDocumentLine
                || deltas[0].BaseCurrency != receipt.BaseCurrency))
            throw Invalid("FG WAC receipt correction is absent or differs from projected value.");

        var movement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleAsync(x => x.Uid == projection.FinishedGoodMovementId,
                cancellationToken);
        var lot = await db.ProductionBalLots
            .SingleAsync(x => x.Uid == movement.ProductionBalLotId,
                cancellationToken);
        var pool = await db.ProductionPoolValuationRows
            .SingleAsync(x => x.ProductionBalLotId == lot.Uid,
                cancellationToken);
        if (lot.BaseQty < 0m || lot.TotalCost < 0m
            || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty
            || pool.TrackedValue != lot.TotalCost
            || lot.AverageUnitCost != (lot.BaseQty > 0m
                ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m)
            || projection.ProjectedRemainingPoolValue < 0m)
            throw Stale("FG source pool value changed after planning.");
        if (alreadyCorrectedHere)
            return new(new(null, null), projection.CorrectedFinishedGoodValue,
                projection.ProjectedRemainingPoolValue);

        var unit = StockLedgerPrecision.Money(
            projection.CorrectedFinishedGoodValue / movement.BaseQty);
        var result = await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, expectedFinishedGood,
            projection.CorrectedFinishedGoodValue, unit, cancellationToken);
        if (lot.TotalCost != projection.ProjectedRemainingPoolValue)
        {
            lot.TotalCost = projection.ProjectedRemainingPoolValue;
            lot.AverageUnitCost = lot.BaseQty > 0m
                ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m;
            pool.TrackedValue = lot.TotalCost;
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(result, projection.CorrectedFinishedGoodValue,
            projection.ProjectedRemainingPoolValue);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
