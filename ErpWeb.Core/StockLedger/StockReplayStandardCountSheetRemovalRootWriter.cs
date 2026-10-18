using System.Collections.Immutable;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayStandardCountSheetRemovalResult(
    IReadOnlyList<int> ReversalHistoryIds,
    IReadOnlyList<long> RemovalGenerationIds);

/// <summary>
/// Removes a Standard count sheet with no later count or value-only events.
/// Later ordinary single-pile movements keep their original Standard value.
/// All original ADJ legs are reversed in one new batch while every counted
/// line gets a removal leaf.
/// </summary>
public static class StockReplayStandardCountSheetRemovalRootWriter
{
    public static async Task<StockReplayStandardCountSheetRemovalResult> AppendWithinRootAsync(
        StockPostingContext context,
        StockReplayStandardCountSheetRemovalPlan plan,
        IRunningNumberService numbers, string reason, DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 250)
            throw Invalid("Standard count rollback needs a valid reason.");
        await StockReplayStandardCountSheetRemovalPlanner.ValidateWithinRootAsync(
            context, plan, cancellationToken);
        if (context.CostMethod != StockCostMethods.Standard)
            throw Invalid("Standard count rollback needs the event-date cost policy.");
        var sheet = plan.Sheet;
        var db = context.Db;
        var lineIds = sheet.Lines.Select(l => l.CountLineId).ToArray();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, sheet.CompanyCode, sheet.BranchCode, lineIds,
            cancellationToken);
        var events = plan.Items.SelectMany(i => i.RemovedEvents)
            .ToDictionary(e => e.EventId);
        if (active.Count != lineIds.Length || events.Count != lineIds.Length
            || sheet.Lines.Any(l =>
                !active.TryGetValue(l.CountLineId, out var g)
                || !events.TryGetValue(-g.Id, out var e)
                || g.IsRemoval || g.AnchorPostingId != sheet.AnchorPostingId
                || g.WritePostingId != sheet.AnchorPostingId
                || g.SignedAdjustmentQty != e.OldSignedQuantity
                || (g.AdjustmentHistoryId is not null)
                    != (e.OldSignedQuantity != 0m)
                || e.Key != l.Physical.RemovedActiveEvent.Key
                || e.AbsoluteCountQty != l.PhysicalQty))
            throw Invalid("The sheet no longer has its original active Standard generations.");
        var originalIds = active.Values.Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).ToArray();
        if (originalIds.Length == 0)
            throw Invalid("An all-zero count uses the zero-adjustment removal writer.");
        var originalRows = await db.IvTrxHistories.AsNoTracking()
            .Where(h => originalIds.Contains(h.Id))
            .ToArrayAsync(cancellationToken);
        if (originalRows.Length != originalIds.Length)
            throw Invalid("The count adjustment history is incomplete.");
        var originals = originalRows.ToDictionary(h => h.Id);
        var activeFacts = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, sheet.CompanyCode, sheet.BranchCode, originalRows,
            cancellationToken);
        foreach (var line in sheet.Lines)
        {
            var generation = active[line.CountLineId];
            if (generation.AdjustmentHistoryId is not int historyId)
                continue;
            var history = originals[historyId];
            var facts = activeFacts[historyId];
            var movement = events[-generation.Id];
            var inbound = movement.OldSignedQuantity > 0m;
            if (history.StockPostingId != sheet.AnchorPostingId
                || history.LedgerVersion != 2
                || history.LedgerEpochId != context.Epoch.Id
                || history.TrxType != IvTrxTypes.StockAdjustment
                || history.BatchStatus != IvBatchStatuses.Posted
                || history.ValuationStatus != StockValuationStatuses.Valued
                || history.EntryRole != "ORIGINAL"
                || history.ReversesHistoryId is not null
                || history.BatchNo != generation.AdjustmentBatchNo
                || history.ICode != plan.Items.Single(i =>
                    i.CountLineIds.Contains(line.CountLineId)).ItemCode
                || (inbound
                    ? history.ToBalLocId != line.BalLocId
                        || history.FromBalLocId is not null
                        || history.ToStdQty != movement.OldSignedQuantity
                    : history.FromBalLocId != line.BalLocId
                        || history.ToBalLocId is not null
                        || history.FrStdQty != -movement.OldSignedQuantity)
                || facts.Count != 1
                || facts[0].StockPostingId != sheet.AnchorPostingId
                || facts[0].InventoryHistoryId != historyId
                || facts[0].ItemCode != history.ICode
                || facts[0].CostMethod != context.CostMethod
                || facts[0].SignedQty != movement.OldSignedQuantity
                || facts[0].SignedValue != movement.OldSignedValue)
                throw Invalid($"Count line {line.CountLineId} lacks exact Standard adjustment authority.");
        }
        var header = await db.IvStockCountHdrs.SingleAsync(h =>
            h.Id == sheet.CountHeaderId
            && h.CompanyCode == sheet.CompanyCode
            && h.BranchCode == sheet.BranchCode, cancellationToken);
        var originalBatchNo = active.Values.Where(g =>
                g.AdjustmentBatchNo.HasValue)
            .Select(g => g.AdjustmentBatchNo!.Value).Distinct().ToArray();
        if (header.Status != IvStockCountStatuses.Posted
            || !header.RowVersion.SequenceEqual(sheet.HeaderRowVersion)
            || originalBatchNo.Length != 1
            || !await StockCountFirstBatchPointer.MatchesAsync(db,
                sheet.CompanyCode, sheet.BranchCode, lineIds,
                header.PostedBatchNo, cancellationToken))
            throw Stale("The posted count batch or header changed before reversal.");
        var originalBatch = await db.IvTrxBatches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && b.BatchNo == originalBatchNo[0], cancellationToken);
        IvTrxBatchDetail[] details = originalBatch is null ? []
            : await db.IvTrxBatchDetails.AsNoTracking()
                .Where(d => d.BatchId == originalBatch.Id)
                .ToArrayAsync(cancellationToken);
        var batchHistoryIds = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == sheet.CompanyCode
                && h.BranchCode == sheet.BranchCode
                && h.BatchNo == originalBatchNo[0])
            .Select(h => h.Id).ToArrayAsync(cancellationToken);
        if (originalBatch is null
            || originalBatch.TrxType != IvTrxTypes.StockAdjustment
            || originalBatch.BatchStatus != IvBatchStatuses.Posted
            || originalBatch.TrxDtTime != header.CountDate.Date
            || originalBatch.RefNo != header.CountNo
            || originalBatch.PostedCount != 1
            || details.Length != originalRows.Length
            || batchHistoryIds.Length != originalRows.Length
            || !batchHistoryIds.Order().SequenceEqual(originalIds.Order())
            || details.Select(d => d.TrxLineNo).Distinct().Count()
                != details.Length)
            throw Invalid("The count's original ADJ batch is incomplete.");
        var byBatchLine = details.ToDictionary(d => d.TrxLineNo);
        if (originalRows.Any(h => !byBatchLine.TryGetValue(h.TrxLineNo,
                    out var d)
                || d.CompanyCode != sheet.CompanyCode
                || d.BranchCode != sheet.BranchCode
                || d.BatchNo != originalBatchNo[0]
                || d.DocumentRevision != h.DocumentRevision
                || d.TrxType != h.TrxType || d.ICode != h.ICode
                || d.FromBalLocId != h.FromBalLocId
                || d.ToBalLocId != h.ToBalLocId
                || d.FromLotId != h.FromLotId
                || d.ToLotId != h.ToLotId
                || d.FrWarehouse != h.FrWarehouse
                || d.ToWarehouse != h.ToWarehouse
                || d.FrLocation != h.FrLocation
                || d.ToLocation != h.ToLocation
                || d.FrLotNo != h.FrLotNo
                || d.ToLotNo != h.ToLotNo
                || d.FrStdQty != h.FrStdQty || d.ToStdQty != h.ToStdQty
                || d.FrStdUom != h.FrStdUom || d.ToStdUom != h.ToStdUom
                || d.FrPurQty != h.FrPurQty || d.ToPurQty != h.ToPurQty
                || d.FrPurUom != h.FrPurUom || d.ToPurUom != h.ToPurUom
                || d.IStatus != h.IStatus
                || d.StockCountCostBasisId != h.StockCountCostBasisId
                || d.CostEvidenceType != h.CostEvidenceType
                || d.CostOverrideReason != h.CostOverrideReason
                || d.CostApprovedBy != h.CostApprovedBy
                || d.CostApprovedAtUtc != h.CostApprovedAtUtc
                || d.UnitPrice != h.UnitPrice))
            throw Invalid("The count's original ADJ details changed.");

        var piles = await db.IvBalLocs
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && sheet.Lines.Select(l => l.BalLocId).Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != sheet.Lines.Count
            || sheet.Lines.Any(l => !piles.TryGetValue(l.BalLocId, out var pile)
                || pile.StdQty != l.Physical.CurrentQuantity
                || !pile.RowVersion.SequenceEqual(
                    l.Physical.BalanceRowVersion)))
            throw Stale("A counted pile changed before reversal.");
        db.ChangeTracker.DetectChanges();
        if (Dirty<IvStockCountHdr>(e => e.Id == header.Id)
            || Dirty<IvStockCountLine>(e => lineIds.Contains(e.Id))
            || Dirty<IvTrxBatch>(e => e.Id == originalBatch.Id)
            || Dirty<IvTrxBatchDetail>(e => e.BatchId == originalBatch.Id)
            || Dirty<IvTrxHistory>(_ => true)
            || Dirty<StockValuationFact>(_ => true)
            || Dirty<StockPosting>(e => e.Id != context.Posting.Id)
            || Dirty<IvBalLoc>(e => piles.ContainsKey(e.Id))
            || Dirty<StockCostState>(e => e.CompanyCode == sheet.CompanyCode
                && e.BranchCode == sheet.BranchCode
                && plan.Items.Any(i => i.ItemCode == e.ItemCode)))
            throw Invalid("The count or its Standard authority has pending edits.");

        var reversalBatchNo = await numbers.GetNextAsync(db,
            sheet.CompanyCode, RunningNumberKeys.IvBatch, cancellationToken);
        if (await db.IvTrxBatches.AsNoTracking().AnyAsync(b =>
                b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && b.BatchNo == reversalBatchNo, cancellationToken))
            throw Invalid("The count reversal batch number is already used.");
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        var reversalBatch = new IvTrxBatch
        {
            CompanyCode = sheet.CompanyCode,
            BranchCode = sheet.BranchCode,
            BatchNo = reversalBatchNo,
            TrxDtTime = header.CountDate.Date,
            TrxType = IvTrxTypes.StockAdjustment,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = header.CountNo,
            PostedDate = now, PostedBy = user,
            PostedCount = 1,
            CreatedDate = now, CreatedBy = user
        };
        db.IvTrxBatches.Add(reversalBatch);
        await db.SaveChangesAsync(cancellationToken);
        var reversals = new IvInventoryHistoryWriter().AppendCountReversal(
            context, originalRows, context.Posting.DocumentRevision,
            header.CountDate, reversalBatchNo, 1).ToArray();
        foreach (var reversal in reversals)
        {
            reversal.ValuationStatus = StockValuationStatuses.Unvalued;
            db.IvTrxBatchDetails.Add(new IvTrxBatchDetail
            {
                BatchId = reversalBatch.Id,
                CompanyCode = sheet.CompanyCode,
                BranchCode = sheet.BranchCode,
                BatchNo = reversalBatchNo,
                DocumentRevision = context.Posting.DocumentRevision,
                TrxLineNo = reversal.TrxLineNo,
                TrxType = IvTrxTypes.StockAdjustment,
                ICode = reversal.ICode,
                IDesc = reversal.IDesc,
                FromBalLocId = reversal.FromBalLocId,
                ToBalLocId = reversal.ToBalLocId,
                FromLotId = reversal.FromLotId,
                ToLotId = reversal.ToLotId,
                FrWarehouse = reversal.FrWarehouse,
                ToWarehouse = reversal.ToWarehouse,
                FrLocation = reversal.FrLocation,
                ToLocation = reversal.ToLocation,
                FrLotNo = reversal.FrLotNo,
                ToLotNo = reversal.ToLotNo,
                FrStdQty = reversal.FrStdQty,
                ToStdQty = reversal.ToStdQty,
                FrStdUom = reversal.FrStdUom,
                ToStdUom = reversal.ToStdUom,
                FrPurQty = reversal.FrPurQty,
                ToPurQty = reversal.ToPurQty,
                FrPurUom = reversal.FrPurUom,
                ToPurUom = reversal.ToPurUom,
                IStatus = reversal.IStatus,
                StockCountCostBasisId = reversal.StockCountCostBasisId,
                CostEvidenceType = reversal.CostEvidenceType,
                CostOverrideReason = reversal.CostOverrideReason,
                CostApprovedBy = reversal.CostApprovedBy,
                CostApprovedAtUtc = reversal.CostApprovedAtUtc,
                UnitPrice = reversal.UnitPrice
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        var reversalByOriginal = reversals.ToDictionary(r =>
            r.ReversesHistoryId!.Value);
        foreach (var original in originalRows)
        {
            var reversal = reversalByOriginal[original.Id];
            await StockReplayValuationReversalWriter.AppendAsync(context,
                original.Id,
                [new StockReplayValuationReversalLine(
                    activeFacts[original.Id][0].Id,
                    reversal.PostingLineNo!.Value, 0, reversal.Id)],
                cancellationToken);
            reversal.ValuationStatus = StockValuationStatuses.Reversed;
        }
        var nowUtc = DateTime.UtcNow;
        var removedGenerations = new List<StockCountReplayGeneration>(lineIds.Length);
        foreach (var line in sheet.Lines)
        {
            var pile = piles[line.BalLocId];
            pile.StdQty = line.Physical.Projection.FinalQuantity;
            pile.ModifiedDate = now;
            var timeline = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
                db, sheet.CompanyCode, sheet.BranchCode, pile.Id,
                new StockReplayEventKey(new DateTime(1753, 1, 1),
                    0, 0, "", 0), cancellationToken);
            var latest = timeline.Where(e => e.EventId
                    != line.Physical.RemovedActiveEvent.EventId)
                .Select(e => (DateTime?)e.Key.EffectiveAt).Max();
            pile.TransDate = latest ?? (pile.StdQty > 0m ? pile.TransDate : null);
            var prior = active[line.CountLineId];
            var reversal = prior.AdjustmentHistoryId is int historyId
                ? reversalByOriginal[historyId] : null;
            removedGenerations.Add(new StockCountReplayGeneration
            {
                CompanyCode = sheet.CompanyCode,
                BranchCode = sheet.BranchCode,
                StockCountLineId = line.CountLineId,
                AnchorPostingId = prior.AnchorPostingId,
                AnchorPostingSequence = prior.AnchorPostingSequence,
                WritePostingId = context.Posting.Id,
                PreviousGenerationId = prior.Id,
                EffectiveAt = prior.EffectiveAt,
                SignedAdjustmentQty = 0m,
                ReversalBatchNo = reversal?.BatchNo,
                ReversalHistoryId = reversal?.Id,
                IsRemoval = true,
                CreatedAtUtc = nowUtc
            });
        }
        db.StockCountReplayGenerations.AddRange(removedGenerations);
        header.Status = IvStockCountStatuses.RolledBack;
        header.RolledBackBy = user;
        header.RolledBackOn = now;
        header.RollbackReason = reason.Trim();
        header.ModifiedBy = user;
        header.ModifiedDate = now;
        foreach (var item in plan.Items)
        {
            var changed = sheet.Lines.Where(l =>
                    item.CountLineIds.Contains(l.CountLineId))
                .ToImmutableDictionary(l => l.BalLocId,
                    l => l.Physical.Projection.FinalQuantity);
            var permit = new StockReplayStandardWritePermit(context,
                item.ItemCode, item.PlanHash,
                item.Tail.CurrentQuantity, item.Tail.CurrentValue,
                item.FinalQuantity, item.FinalValue, changed);
            await StockReplayStandardCostStateWriter.ApplyAsync(context, permit,
                cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(reversals.Select(r => r.Id).ToArray(),
            removedGenerations.Select(g => g.Id).ToArray());

        bool Dirty<T>(Func<T, bool> matches) where T : class =>
            db.ChangeTracker.Entries<T>().Any(e =>
                matches(e.Entity) && e.State != EntityState.Unchanged);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
