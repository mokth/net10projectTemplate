using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoTransferRemovalWriteResult(
    int ReversalHistoryId, IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Reverses one posted, existing-pile FIFO transfer under its prepared root.
/// Its financial pool is unchanged, including when the transfer's active
/// allocation was corrected by an earlier replay root.
/// </summary>
public static class StockReplayFifoTransferRemovalRootWriter
{
    public static async Task<StockReplayFifoTransferRemovalWriteResult> RemoveAsync(
        StockPostingContext context,
        StockReplayFifoTransferRemovalPlan plan, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        await StockReplayFifoTransferRemovalPlanner.ValidateWithinRootAsync(
            context, plan, cancellationToken);
        var originalId = checked((int)plan.Source.RemovedActiveEvent.EventId);
        if (batchNo <= 0 || context.Posting.SourceDocumentNo
                != batchNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(plan.Source)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(
                plan.Destination)
            || StockReplayFifoMiRootWriter.ChangedIssues(
                plan.Projection).Count != 0)
            throw Invalid("FIFO transfer removal has changed financial descendants.");
        var first = plan.Source.RemovedActiveEvent.Key.EffectiveAt.Date;
        var last = plan.Source.LaterEvents.Select(e => e.Key.EffectiveAt.Date)
            .Concat(plan.Destination.LaterEvents.Select(e => e.Key.EffectiveAt.Date))
            .Append(first).Max();
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, first, last, cancellationToken);
        var db = context.Db;
        var batches = await db.IvTrxBatches
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.BatchNo == batchNo).ToArrayAsync(cancellationToken);
        if (batches.Length != 1)
            throw Invalid("The removed transfer batch is missing or ambiguous.");
        var batch = batches[0];
        var details = await db.IvTrxBatchDetails
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        var histories = await db.IvTrxHistories
            .Where(h => h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && h.BatchNo == batchNo
                && h.DocumentRevision == context.Posting.DocumentRevision)
            .ToArrayAsync(cancellationToken);
        if (details.Length != 1 || histories.Length != 1
            || histories[0].Id != originalId)
            throw Invalid("The transfer detail or original history changed.");
        var detail = details[0];
        var original = histories[0];
        var owner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == original.StockPostingId,
                cancellationToken);
        if (db.Entry(batch).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || db.Entry(original).State != EntityState.Unchanged
            || batch.TrxType != IvTrxTypes.StockTransfer
            || batch.BatchStatus != IvBatchStatuses.Posted
            || batch.DeletedAtUtc is not null
            || batch.ForceCloseDate is not null
            || batch.TrxDtTime != plan.Source.RemovedActiveEvent.Key.EffectiveAt
            || context.Posting.SourceDocumentId
                != batch.Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            || detail.DocumentRevision != context.Posting.DocumentRevision
            || detail.TrxLineNo != original.TrxLineNo
            || detail.ICode != plan.ItemCode
            || detail.FromBalLocId != plan.Source.BalLocId
            || detail.ToBalLocId != plan.Destination.BalLocId
            || detail.FrStdQty != -plan.Source.RemovedActiveEvent.SignedDelta
            || detail.ToStdQty != plan.Destination.RemovedActiveEvent.SignedDelta
            || detail.FrStdUom != original.FrStdUom
            || detail.ToStdUom != original.ToStdUom
            || detail.FrWarehouse != original.FrWarehouse
            || detail.FrLocation != original.FrLocation
            || detail.ToWarehouse != original.ToWarehouse
            || detail.ToLocation != original.ToLocation
            || original.EntryRole != "ORIGINAL"
            || original.ValuationStatus != StockValuationStatuses.Valued
            || original.StockPostingId != owner?.Id
            || owner.SealedAtUtc is null
            || context.Posting.ReversesPostingId != owner.Id)
            throw Invalid("The rollback root does not own the posted transfer.");
        var balances = await db.IvBalLocs
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && (b.Id == plan.Source.BalLocId
                    || b.Id == plan.Destination.BalLocId))
            .ToArrayAsync(cancellationToken);
        var source = balances.SingleOrDefault(b => b.Id == plan.Source.BalLocId);
        var destination = balances.SingleOrDefault(b =>
            b.Id == plan.Destination.BalLocId);
        if (balances.Length != 2 || source is null || destination is null
            || db.Entry(source).State != EntityState.Unchanged
            || db.Entry(destination).State != EntityState.Unchanged
            || source.StdQty != plan.Source.CurrentQuantity
            || destination.StdQty != plan.Destination.CurrentQuantity
            || !source.RowVersion.SequenceEqual(plan.Source.BalanceRowVersion)
            || !destination.RowVersion.SequenceEqual(
                plan.Destination.BalanceRowVersion))
            throw Invalid("The transfer piles changed after removal planning.");
        var beforeAll = new StockReplayEventKey(
            new DateTime(1753, 1, 1), 0, 0L, "", 0);
        var sourceTimeline = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, context.CompanyCode, context.BranchCode,
            source.Id, beforeAll, cancellationToken);
        var destinationTimeline = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, context.CompanyCode, context.BranchCode,
            destination.Id, beforeAll, cancellationToken);
        if (sourceTimeline.Count(e => e.EventId == originalId) != 1
            || destinationTimeline.Count(e => e.EventId == originalId) != 1)
            throw Invalid("The transfer no longer has two active physical legs.");
        var previous = plan.Snapshot.ActiveConsumptions
            .Where(c => c.IssueKey == plan.Source.RemovedActiveEvent.Key)
            .OrderBy(c => c.SplitOrdinal).ToArray();
        var active = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, [original],
            cancellationToken))[originalId]
            .OrderBy(f => f.SplitOrdinal).ToArray();
        var outbound = active.Where(f => f.MovementCode == "TRANSFER_OUT"
            && f.Direction == -1).ToArray();
        var inbound = active.Where(f => f.MovementCode == "TRANSFER_IN"
            && f.Direction == 1).ToArray();
        if (previous.Length == 0 || inbound.Length != 1
            || outbound.Length != previous.Length
            || active.Length != outbound.Length + 1
            || previous.Any(c => c.DepletesPool)
            || previous.Sum(c => c.Quantity)
                != -plan.Source.RemovedActiveEvent.SignedDelta
            || outbound.Sum(f => f.BaseQty) != inbound[0].BaseQty
            || outbound.Sum(f => f.CostAmount) != inbound[0].CostAmount
            || active.Any(f => f.InventoryHistoryId != originalId
                || f.CostMethod != StockCostMethods.Fifo
                || f.ValuationSource != StockValuationSources.Fifo
                || f.EffectiveAt != original.TrxDtTime)
            || outbound.Any(f => !previous.Any(c =>
                c.IssueFactId == f.Id && c.SplitOrdinal == f.SplitOrdinal
                && c.Quantity == f.BaseQty && c.Value == f.CostAmount)))
            throw Invalid("The transfer has no complete active FIFO generation.");
        var ids = outbound.Select(f => f.Id).ToArray();
        var persisted = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => ids.Contains(c.IssueValuationFactId))
            .ToArrayAsync(cancellationToken);
        if (persisted.Length != previous.Length
            || persisted.Any(c => !previous.Any(p =>
                p.ConsumptionId == c.Id
                && p.IssueFactId == c.IssueValuationFactId
                && p.LayerId == c.FifoLayerId
                && p.Quantity == c.ConsumedQty
                && p.Value == c.ConsumedValue
                && p.SplitOrdinal == c.SplitOrdinal)))
            throw Invalid("The transfer active consumption changed after planning.");
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        source.StdQty = plan.Source.Projection.FinalQuantity;
        destination.StdQty = plan.Destination.Projection.FinalQuantity;
        source.TransDate = sourceTimeline.Where(e => e.EventId != originalId)
            .Select(e => (DateTime?)e.Key.EffectiveAt).Max()
            ?? (source.StdQty > 0m ? source.TransDate : null);
        destination.TransDate = destinationTimeline
            .Where(e => e.EventId != originalId)
            .Select(e => (DateTime?)e.Key.EffectiveAt).Max()
            ?? (destination.StdQty > 0m ? destination.TransDate : null);
        source.ModifiedDate = now;
        destination.ModifiedDate = now;
        var reversal = new IvInventoryHistoryWriter().AppendReversal(
            context, [original], context.Posting.DocumentRevision).Single();
        detail.ToBalLocId = null;
        detail.FromLotId = null;
        detail.ToLotId = null;
        detail.DocumentRevision = checked(detail.DocumentRevision + 1);
        batch.BatchStatus = IvBatchStatuses.New;
        batch.RollbackDate = now;
        batch.RollbackBy = user;
        batch.RollbackCount += 1;
        batch.RollbackOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        var reversals = await StockReplayValuationReversalWriter.AppendAsync(
            context, originalId, active.Select((f, i) =>
                new StockReplayValuationReversalLine(
                    f.Id, 1, i, reversal.Id)).ToArray(),
            cancellationToken);
        var reverseById = reversals.ToDictionary(f =>
            f.ReversesValuationFactId!.Value);
        foreach (var old in previous)
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = reverseById[old.IssueFactId],
                FifoLayerId = old.LayerId,
                ConsumedQty = old.Quantity,
                ConsumedValue = old.Value,
                SplitOrdinal = old.SplitOrdinal,
                ReversesConsumptionId = old.ConsumptionId,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            });
        reversal.ValuationStatus = StockValuationStatuses.Reversed;
        return new(reversal.Id, []);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
