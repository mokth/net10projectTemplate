using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Reverses one posted exact-pile Inventory issue under a prepared FIFO root.
/// The caller must reverse valuation, correct descendants and reconcile state
/// before sealing the enclosing transaction.
/// </summary>
public static class StockReplayFifoIssueRemovalPhysicalWriter
{
    public static async Task<int> AppendAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        var physical = plan.PhysicalPlan;
        var removed = physical.RemovedActiveEvent;
        if (batchNo <= 0 || removed.SignedDelta >= 0m
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
            || permit.ChangedPileId != physical.BalLocId
            || permit.ChangedPileFinalQuantity != physical.Projection.FinalQuantity
            || context.Posting.SourceModule != "INVENTORY"
            || !StockReplayFifoInventoryIssueKinds.IsSupported(
                context.Posting.SourceDocumentType)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "REVERSAL"
            || context.Posting.EffectiveAt != removed.Key.EffectiveAt)
            throw Invalid("FIFO issue removal needs its prepared MI rollback root.");

        var db = context.Db;
        var batches = await db.IvTrxBatches
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode && b.BatchNo == batchNo)
            .ToArrayAsync(cancellationToken);
        if (batches.Length != 1)
            throw Invalid("The removed MI batch is missing or ambiguous.");
        var batch = batches[0];
        if (batch.TrxType != context.Posting.SourceDocumentType
            || batch.BatchStatus != IvBatchStatuses.Posted
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null
            || batch.TrxDtTime != removed.Key.EffectiveAt
            || context.Posting.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            throw Invalid("The removed MI is no longer an eligible posted batch.");
        var details = await db.IvTrxBatchDetails
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1)
            throw Invalid("FIFO replay currently supports one MI detail per rollback root.");
        var detail = details[0];
        var histories = await db.IvTrxHistories
            .Where(h => h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode && h.BatchNo == batchNo
                && h.DocumentRevision == context.Posting.DocumentRevision)
            .ToArrayAsync(cancellationToken);
        if (histories.Length != 1 || histories[0].Id != permit.RemovedHistoryId)
            throw Invalid("The MI batch has changed or already reversed history.");
        var history = histories[0];
        if (history.EntryRole != "ORIGINAL" || history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.TrxType != context.Posting.SourceDocumentType
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.TrxDtTime != removed.Key.EffectiveAt
            || history.ICode != plan.ItemCode
            || history.FromBalLocId != physical.BalLocId
            || history.FrStdQty != -removed.SignedDelta
            || history.ToBalLocId is > 0 || history.ToStdQty is > 0m
            || detail.DocumentRevision != context.Posting.DocumentRevision
            || detail.TrxLineNo != history.TrxLineNo
            || detail.TrxType != history.TrxType
            || detail.ICode != plan.ItemCode
            || detail.FromBalLocId != physical.BalLocId
            || detail.FrStdQty != history.FrStdQty
            || detail.FrWarehouse != history.FrWarehouse
            || detail.FrLocation != history.FrLocation
            || detail.FrStdUom != history.FrStdUom
            || detail.FromLotId != history.FromLotId
            || !string.Equals(detail.FrLotNo ?? string.Empty,
                history.FrLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || detail.IStatus != history.IStatus)
            throw Invalid("The posted MI detail differs from its prepared issue.");
        var owner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(
            p => p.Id == history.StockPostingId, cancellationToken);
        if (owner is null || owner.SealedAtUtc is null
            || owner.PostingSequence != removed.Key.OriginalPostingSequence
            || context.Posting.ReversesPostingId != owner.Id)
            throw Invalid("The rollback root does not reverse the issue owner.");
        var masters = await db.IvStockMasters.AsNoTracking()
            .Where(m => m.CompanyCode == context.CompanyCode
                && m.ICode == plan.ItemCode).ToArrayAsync(cancellationToken);
        if (masters.Length != 1)
            throw Invalid("FIFO replay MI rollback has no exact stock item.");
        var balances = await db.IvBalLocs
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.Id == physical.BalLocId).ToArrayAsync(cancellationToken);
        if (balances.Length != 1)
            throw Invalid("The removed MI source pile is missing.");
        var balance = balances[0];
        if (!await StockReplayInventoryIssuePileAuthority.MatchesAsync(
                db, masters[0], balance, detail, cancellationToken,
                requireActive: false)
            || balance.LotId != history.FromLotId
            || !string.Equals(balance.LotNo ?? string.Empty,
                history.FrLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The removed MI source pile changed after preparation.");

        // Resolve the latest remaining canonical event before appending an
        // unsealed reversal history. Opening stock retains its current date.
        var timeline = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, context.CompanyCode, context.BranchCode, physical.BalLocId,
            new StockReplayEventKey(new DateTime(1753, 1, 1), 0, 0L, "", 0),
            cancellationToken);
        if (timeline.Count(e => e.EventId == removed.EventId) != 1)
            throw Invalid("The removed issue is no longer active in its pile.");
        var latest = timeline.Where(e => e.EventId != removed.EventId)
            .Select(e => (DateTime?)e.Key.EffectiveAt).Max();
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = latest ?? (balance.StdQty > 0m
            ? balance.TransDate : null);
        balance.ModifiedDate = now;
        var reversal = new IvInventoryHistoryWriter().AppendReversal(
            context, [history], context.Posting.DocumentRevision).Single();
        detail.FromLotId = null;
        detail.DocumentRevision = checked(detail.DocumentRevision + 1);
        batch.BatchStatus = IvBatchStatuses.New;
        batch.RollbackDate = now;
        batch.RollbackBy = user;
        batch.RollbackCount += 1;
        batch.RollbackOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return reversal.Id;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
