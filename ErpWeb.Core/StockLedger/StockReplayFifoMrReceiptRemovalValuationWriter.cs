using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Reverses the exact original FIFO MR receipt fact inside its root.</summary>
public static class StockReplayFifoMrReceiptRemovalValuationWriter
{
    public static async Task<StockValuationFact> AppendAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit, int reversalHistoryId,
        int postingLineNo, CancellationToken cancellationToken = default,
        bool goodsReceipt = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        var removed = plan.PhysicalPlan.RemovedActiveEvent;
        var layer = plan.Projection.CandidateReceipt;
        if (postingLineNo <= 0 || reversalHistoryId <= 0
            || removed.SignedDelta <= 0m
            || layer is null || layer.ReceiptKey != removed.Key
            || permit.Operation != "REMOVE"
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != plan.ItemCode
            || permit.PlanHash != plan.PlanHash
            || permit.SnapshotHash != plan.Snapshot.SnapshotHash
            || permit.RemovedHistoryId != removed.EventId
            || context.Posting.PostingRole != "REVERSAL")
            throw Invalid("FIFO MR valuation reversal needs its prepared root.");
        var db = context.Db;
        var originalId = permit.RemovedHistoryId!.Value;
        var original = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == originalId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode,
                cancellationToken);
        var reversal = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == reversalHistoryId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode,
                cancellationToken);
        if (original is null || reversal is null
            || original.ICode != plan.ItemCode
            || original.TrxType != (goodsReceipt
                ? IvTrxTypes.GoodsReceive : IvTrxTypes.MiscellaneousReceipt)
            || original.EntryRole != "ORIGINAL"
            || original.ValuationStatus != StockValuationStatuses.Valued
            || original.ToBalLocId != plan.PhysicalPlan.BalLocId
            || original.ToStdQty != removed.SignedDelta
            || original.TrxDtTime != removed.Key.EffectiveAt
            || reversal.EntryRole != "REVERSAL"
            || reversal.ReversesHistoryId != originalId
            || reversal.StockPostingId != context.Posting.Id
            || reversal.FromBalLocId != plan.PhysicalPlan.BalLocId
            || reversal.FrStdQty != removed.SignedDelta
            || reversal.FromLotId != original.ToLotId)
            throw Invalid("The removed MR has no exact root reversal history.");
        var active = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode,
            [original], cancellationToken))[originalId];
        if (active.Count != 1 || active[0].Id != layer.OriginFactId
            || active[0].MovementCode != "RECEIPT_IN"
            || active[0].LotId != original.ToLotId
            || !string.Equals(active[0].LotNo ?? string.Empty,
                original.ToLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || active[0].Direction != 1
            || active[0].BaseQty != layer.OriginalQuantity
            || active[0].CostAmount != layer.OriginalValue
            || active[0].CostMethod != StockCostMethods.Fifo
            || active[0].ValuationSource != StockValuationSources.Fifo
            || active[0].BaseCostAmount != active[0].CostAmount
            || active[0].TransactionCurrency is not null
            || active[0].TransactionCostAmount is not null
            || active[0].ExchangeRate is not null
            || active[0].ValuationVersion != 1
            || active[0].OriginalValuationFactId is not null
            || active[0].EffectiveAt != removed.Key.EffectiveAt
            || active[0].StockPostingId != layer.OriginPostingId)
            throw Invalid("The removed MR has no unchanged original FIFO receipt fact.");
        var persisted = await db.StockFifoLayers.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Id == layer.LayerId,
                cancellationToken);
        if (persisted is null
            || persisted.CompanyCode != context.CompanyCode
            || persisted.BranchCode != context.BranchCode
            || persisted.ItemCode != plan.ItemCode
            || persisted.OriginValuationFactId != layer.OriginFactId
            || persisted.OriginStockPostingId != layer.OriginPostingId
            || persisted.OriginalQty != layer.OriginalQuantity
            || persisted.OriginalValue != layer.OriginalValue
            || persisted.RemainingQty != layer.RemainingQuantity
            || persisted.RemainingValue != layer.RemainingValue
            || Convert.ToHexString(persisted.RowVersion) != layer.RowVersionHex)
            throw Invalid("The removed MR FIFO layer changed after planning.");
        var facts = await StockReplayValuationReversalWriter.AppendAsync(
            context, originalId,
            [new StockReplayValuationReversalLine(layer.OriginFactId,
                postingLineNo, 0, reversalHistoryId)], cancellationToken);
        var trackedReversal = await db.IvTrxHistories.SingleAsync(
            h => h.Id == reversalHistoryId, cancellationToken);
        trackedReversal.ValuationStatus = StockValuationStatuses.Reversed;
        return facts.Single();
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
