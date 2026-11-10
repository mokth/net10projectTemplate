using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private async Task<IvInventoryPostingBatchResult> PostTwoPileWacMrAsync(
        AppDbContext read, IvTrxBatch batch, IvTrxBatchDetail[] details,
        IReadOnlyList<StockReplayFifoMrReceiptAuthority> authorities, string draftFingerprint,
        string company, string branch, int batchNo, CancellationToken ct)
    {
        var plan = await StockReplayWacMultiPileReceiptPlanner.BuildInsertionAsync(
            read, company, branch, authorities, ct);
        if (plan.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                read, plan.Tail.LaterEvents, batch.TrxDtTime, ct) is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
        await using var write = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await write.Database.BeginTransactionAsync(ct);
        await new BranchStockTransactionLock().AcquireAsync(write, company, branch, ct);
        var locked = await _posting.LockBatchForUpdateAsync(write, company, branch, batchNo, ct);
        if (locked is null || locked.Id != batch.Id || locked.TrxType != batch.TrxType
            || locked.BatchStatus != IvBatchStatuses.New || locked.TrxDtTime != batch.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt changed. Retry posting.");
        var currentDetails = await write.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsRepeatedItemWacMrShape(locked, currentDetails)
            || !currentDetails.Select(d => d.Id).SequenceEqual(details.Select(d => d.Id))
            || MultiWacMrDraftFingerprint(locked, currentDetails) != draftFingerprint
            || !await HasOnlyWacPoolsAsync(write, company, branch, currentDetails, ct)
            || await HasReversedOutgoingAfterAsync(write, company, branch, [plan.Item], batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt or its dependencies changed. Retry posting.");
        if (await RejectReversedProductionReceiptAsync(write, company, branch, batchNo,
                IvTrxTypes.MiscellaneousReceipt, rollback: false, ct) is { } rejected)
            return rejected;
        foreach (var authority in authorities)
        {
            var current = await StockReplayFifoMrReceiptAuthorityReader.ReadLineAsync(write, company, branch,
                batchNo, authority.BalLocId, authority.DetailId, ct);
            if (current != authority)
                return IvInventoryPostingBatchResult.Fail(batchNo, "An approved receipt line changed. Retry posting.");
        }
        if (plan.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                write, plan.Tail.LaterEvents, batch.TrxDtTime, ct) is string changed)
            return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        var ledger = await BeginStandaloneLedgerAsync(write, company, branch, batchNo, reversal: false, ct);
        await using var scope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null) return ledger.Fail;
        var context = ledger.Context ?? throw MultiWacError("The receipt replay needs an active stock ledger.");
        var lines = authorities.Select(a => new MultiWacMrInsertLine(a, null)).ToArray();
        var required = await WriteMultiWacMrAsync(context, lines, ct, plan);
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, ct);
        scope.MarkCompleted();
        await tx.CommitAsync(ct);
        return IvInventoryPostingBatchResult.Ok(batchNo, locked.PostingOperationId ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult> RollBackTwoPileWacMrAsync(
        AppDbContext read, IvTrxBatch batch, IvTrxBatchDetail[] details, IvTrxHistory[] histories,
        StockPosting owner, string company, string branch, string userId, int batchNo, CancellationToken ct)
    {
        var plan = await StockReplayWacMultiPileReceiptPlanner.BuildRemovalAsync(
            read, company, branch, histories, owner, ct);
        if (plan.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                read, plan.Tail.LaterEvents, batch.TrxDtTime, ct) is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
        var fingerprint = MultiWacMrDraftFingerprint(batch, details);
        await using var write = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await write.Database.BeginTransactionAsync(ct);
        await new BranchStockTransactionLock().AcquireAsync(write, company, branch, ct);
        var locked = await _posting.LockBatchForUpdateAsync(write, company, branch, batchNo, ct);
        if (locked is null || locked.Id != batch.Id || locked.TrxType != batch.TrxType
            || locked.BatchStatus != IvBatchStatuses.Posted || locked.TrxDtTime != batch.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt changed. Retry rollback.");
        var currentDetails = await write.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        var currentHistories = (await _posting.LoadHistoryForBatchAsync(write, company, branch, batchNo, ct))
            .OrderBy(h => h.Id).ToArray();
        var currentOwner = await write.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == owner.Id, ct);
        if (!IsRepeatedItemWacMrShape(locked, currentDetails)
            || MultiWacMrDraftFingerprint(locked, currentDetails) != fingerprint
            || !currentDetails.Select(d => d.Id).SequenceEqual(details.Select(d => d.Id))
            || !currentHistories.Select(h => h.Id).SequenceEqual(histories.Select(h => h.Id))
            || currentDetails.Any(d => currentHistories.Count(h => h.TrxLineNo == d.TrxLineNo
                && MultiWacMrHistoryMatches(d, h)) != 1)
            || currentHistories.Any(h => h.StockPostingId != owner.Id || h.LedgerEpochId != owner.LedgerEpochId
                || h.DocumentRevision != owner.DocumentRevision || h.TrxDtTime != owner.EffectiveAt
                || h.EntryRole != "ORIGINAL" || h.LedgerVersion != 2
                || h.TrxType != IvTrxTypes.MiscellaneousReceipt || h.ValuationStatus != StockValuationStatuses.Valued)
            || currentOwner is null || currentOwner.CompanyCode != owner.CompanyCode
            || currentOwner.BranchCode != owner.BranchCode || currentOwner.LedgerEpochId != owner.LedgerEpochId
            || currentOwner.PostingSequence != owner.PostingSequence || currentOwner.CommandType != owner.CommandType
            || currentOwner.SourceModule != owner.SourceModule || currentOwner.SourceDocumentType != owner.SourceDocumentType
            || currentOwner.SourceDocumentId != owner.SourceDocumentId || currentOwner.SourceDocumentNo != owner.SourceDocumentNo
            || currentOwner.DocumentRevision != owner.DocumentRevision || currentOwner.PostingRole != owner.PostingRole
            || currentOwner.EffectiveAt != owner.EffectiveAt || currentOwner.SealedAtUtc != owner.SealedAtUtc
            || currentOwner.RequestFingerprint != owner.RequestFingerprint || currentOwner.SourceSnapshotHash != owner.SourceSnapshotHash
            || !await HasOnlyWacPoolsAsync(write, company, branch, currentDetails, ct)
            || await HasReversedOutgoingAfterAsync(write, company, branch, [plan.Anchor.ItemCode], batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt or its dependencies changed. Retry rollback.");
        if (await RejectReversedProductionReceiptAsync(write, company, branch, batchNo,
                IvTrxTypes.MiscellaneousReceipt, rollback: true, ct) is { } rejected)
            return rejected;
        var qaRestore = await _qaPhysical.RestoreStockOutAsync(write, company, branch, batchNo, ct);
        if (qaRestore is not null) return IvInventoryPostingBatchResult.Fail(batchNo, qaRestore);
        if (plan.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                write, plan.Tail.LaterEvents, batch.TrxDtTime, ct) is string changed)
            return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        var ledger = await BeginStandaloneLedgerAsync(write, company, branch, batchNo, reversal: true, ct);
        await using var scope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null) return ledger.Fail;
        var context = ledger.Context ?? throw MultiWacError("The receipt rollback needs an active stock ledger.");
        if (context.CostMethod != StockCostMethods.MovingAverage || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || context.Posting.SourceDocumentId != owner.SourceDocumentId
            || context.Posting.SourceDocumentNo != owner.SourceDocumentNo
            || context.Posting.PostingRole != "REVERSAL" || context.Posting.ReversesPostingId != owner.Id
            || context.Posting.EffectiveAt != owner.EffectiveAt)
            throw MultiWacError("The rollback root does not reverse its receipt owner.");
        var (refreshed, permit) = await StockReplayWacMultiPileReceiptPlanner.PrepareRemovalAsync(
            context, plan, currentHistories, owner, ct);
        var result = await RollBackInventoryMRCoreAsync(write, company, branch, userId, batchNo,
            IvTrxTypes.MiscellaneousReceipt, ct, context);
        if (!result.Succeeded) return result;
        await write.SaveChangesAsync(ct);
        var reversals = await write.IvTrxHistories.Where(h => h.StockPostingId == context.Posting.Id
            && h.EntryRole == "REVERSAL").ToArrayAsync(ct);
        if (reversals.Length != currentHistories.Length)
            throw MultiWacError("The receipt has incomplete reversal histories.");
        var activeFacts = await StockReplayValuationGenerationResolver.ResolveAsync(write, company, branch,
            currentHistories, ct);
        foreach (var pilePlan in refreshed.Piles)
        {
            var pile = await write.IvBalLocs.AsNoTracking().SingleAsync(p => p.Id == pilePlan.BalLocId
                && p.CompanyCode == company && p.BranchCode == branch, ct);
            if (pile.StdQty != pilePlan.Projection.FinalQuantity)
                throw MultiWacError("The receipt rollback does not reconcile with its destination pile.");
        }
        for (var i = 0; i < currentHistories.Length; i++)
        {
            var original = currentHistories[i];
            var reversal = reversals.SingleOrDefault(h => h.ReversesHistoryId == original.Id);
            var facts = activeFacts[original.Id];
            var source = refreshed.SourceEvents[original.Id];
            if (reversal is null || reversal.FromBalLocId != original.ToBalLocId
                || reversal.FrStdQty != original.ToStdQty || reversal.FromLotId != original.ToLotId
                || facts.Count != 1 || facts[0].CostMethod != StockCostMethods.MovingAverage
                || facts[0].SignedQty != source.OldSignedQuantity || facts[0].SignedValue != source.OldSignedValue
                || facts[0].LotId != original.ToLotId
                || !string.Equals(facts[0].LotNo ?? "", original.ToLotNo ?? "", StringComparison.OrdinalIgnoreCase))
                throw MultiWacError("A receipt line reversal does not reconcile with its active value.");
            await StockReplayValuationReversalWriter.AppendAsync(context, original.Id,
                [new StockReplayValuationReversalLine(facts[0].Id, i + 1, 0, reversal.Id)], ct);
            reversal.ValuationStatus = StockValuationStatuses.Reversed;
        }
        var tail = await StockReplayWacTailRepricingWriter.AppendPreparedMultiPileReceiptRemovalAsync(
            context, permit, refreshed, currentHistories.Length + 1, ct);
        await StockReplayWacCostStateWriter.ApplyAsync(context, permit, ct);
        await _coordinator!.CompleteReplayInTransactionAsync(context, tail.RepricedHistoryIds, ct);
        scope.MarkCompleted();
        await tx.CommitAsync(ct);
        return result;
    }
}
