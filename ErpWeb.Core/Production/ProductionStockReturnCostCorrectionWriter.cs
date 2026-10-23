using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionStockReturnCostCorrectionResult(
    ProductionCostRestatementWriteResult ReturnCost,
    decimal CorrectedReturnValue,
    decimal RemainingPoolValue);

/// <summary>
/// Propagates one already corrected ISSUE and WAC return receipt into an
/// isolated MATERIAL_IN pool. The caller owns Inventory cost-state and later
/// Inventory/Sales recosting in the same replay transaction.
/// </summary>
internal static class ProductionStockReturnCostCorrectionWriter
{
    public static async Task<ProductionStockReturnCostCorrectionResult> AppendWithinRootAsync(
        StockPostingContext context, int returnBatchId,
        ProductionEffectiveCost expectedReturn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedReturn);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage)
            throw Invalid("This bounded return correction requires WAC valuation.");
        var db = context.Db;
        if (db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<StockValuationFact>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Stale("Return cost correction has pending pool or Inventory valuation changes.");

        var projection = await ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(
            context, returnBatchId, cancellationToken);
        var fact = await db.ProductionStockReturnFacts.AsNoTracking()
            .SingleAsync(x => x.Id == projection.ReturnFactId, cancellationToken);
        var current = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode,
            [projection.ReturnMovementId], cancellationToken))[projection.ReturnMovementId];
        var alreadyCorrectedHere = current.RestatementFactId is not null
            && current.TotalCost == projection.CorrectedReturnValue;
        if (current.MovementId != projection.ReturnMovementId
            || !alreadyCorrectedHere && (current != expectedReturn
                || current.TotalCost != fact.TotalValue)
            || alreadyCorrectedHere && (expectedReturn.MovementId != current.MovementId
                || expectedReturn.TotalCost != fact.TotalValue
                || expectedReturn.UnitCost != StockLedgerPrecision.Money(
                    fact.TotalValue / fact.BaseQty)
                || expectedReturn.RestatementFactId is not null))
            throw Stale("Return effective cost changed after planning.");

        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleAsync(x => x.Id == projection.ReturnHistoryId, cancellationToken);
        var receiptFacts = (await StockReplayValuationGenerationResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode,
            [history], cancellationToken))[history.Id];
        var receipt = receiptFacts.SingleOrDefault();
        if (receipt is null || receipt.StockPostingId != fact.StockPostingId
            || receipt.Direction != 1 || receipt.CostMethod != StockCostMethods.MovingAverage
            || receipt.BaseQty != fact.BaseQty || receipt.CostAmount != fact.TotalValue
            || receipt.BaseCostAmount != receipt.CostAmount
            || receipt.ValuationStatus != StockValuationStatuses.Valued
            || receipt.ReversesValuationFactId is not null)
            throw Invalid("Return Inventory receipt has no exact active WAC valuation.");
        var deltas = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.OriginalValuationFactId == receipt.Id
                && x.InventoryHistoryId == null).ToArrayAsync(cancellationToken);
        var correction = StockLedgerPrecision.Money(
            projection.CorrectedReturnValue - receipt.CostAmount);
        if (correction == 0m && deltas.Length != 0
            || correction != 0m && (deltas.Length != 1
                || deltas[0].StockPostingId != context.Posting.Id
                || deltas[0].LedgerEpochId != context.Epoch.Id
                || deltas[0].CompanyCode != context.CompanyCode
                || deltas[0].BranchCode != context.BranchCode
                || deltas[0].OriginalValuationFactId != receipt.Id
                || deltas[0].ReversesValuationFactId is not null
                || deltas[0].Direction != Math.Sign(correction)
                || deltas[0].CostAmount != Math.Abs(correction)
                || deltas[0].BaseCostAmount != deltas[0].CostAmount
                || deltas[0].UnitCost != StockLedgerPrecision.Money(
                    Math.Abs(correction) / receipt.BaseQty)
                || deltas[0].BaseQty != 0m
                || deltas[0].CostMethod != StockCostMethods.MovingAverage
                || deltas[0].ValuationStatus != StockValuationStatuses.Valued
                || deltas[0].ValuationSource != StockValuationSources.ProductionActual
                || deltas[0].ValuationVersion != receipt.ValuationVersion + 1
                || deltas[0].ItemCode != receipt.ItemCode
                || deltas[0].WarehouseCode != receipt.WarehouseCode
                || deltas[0].LocationCode != receipt.LocationCode
                || deltas[0].LotId != receipt.LotId
                || deltas[0].LotNo != receipt.LotNo
                || deltas[0].ItemStatus != receipt.ItemStatus
                || deltas[0].BaseUom != receipt.BaseUom
                || deltas[0].MovementCode != receipt.MovementCode
                || deltas[0].FromBalLocId != receipt.FromBalLocId
                || deltas[0].ToBalLocId != receipt.ToBalLocId
                || deltas[0].EffectiveAt != receipt.EffectiveAt
                || deltas[0].BusinessDate != receipt.BusinessDate
                || deltas[0].PeriodKey != receipt.PeriodKey
                || deltas[0].SourceLineId != receipt.SourceLineId
                || deltas[0].SourceDocumentType != receipt.SourceDocumentType
                || deltas[0].SourceDocumentId != receipt.SourceDocumentId
                || deltas[0].SourceDocumentNo != receipt.SourceDocumentNo
                || deltas[0].SourceDocumentLine != receipt.SourceDocumentLine
                || deltas[0].BaseCurrency != receipt.BaseCurrency
                || deltas[0].TransactionCurrency is not null
                || deltas[0].TransactionCostAmount is not null
                || deltas[0].ExchangeRate is not null))
            throw Invalid("Return WAC receipt correction is absent or differs from the projected value.");

        var returnMovement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleAsync(x => x.Uid == projection.ReturnMovementId,
                cancellationToken);
        var lot = await db.ProductionBalLots
            .SingleAsync(x => x.Uid == returnMovement.ProductionBalLotId,
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
            || projection.ProjectedRemainingValue < 0m)
            throw Stale("Return source pool value changed after planning.");

        if (alreadyCorrectedHere)
            return new(new(null, null), projection.CorrectedReturnValue,
                projection.ProjectedRemainingValue);

        var unit = StockLedgerPrecision.Money(
            projection.CorrectedReturnValue / returnMovement.BaseQty);
        var result = await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, expectedReturn, projection.CorrectedReturnValue, unit,
            cancellationToken);
        if (lot.TotalCost != projection.ProjectedRemainingValue)
        {
            lot.TotalCost = projection.ProjectedRemainingValue;
            lot.AverageUnitCost = lot.BaseQty > 0m
                ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m;
            pool.TrackedValue = lot.TotalCost;
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(result, projection.CorrectedReturnValue,
            projection.ProjectedRemainingValue);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
