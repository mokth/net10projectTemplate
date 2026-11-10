using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record SameItemWacRemovalPlan(
        StockReplayWacRemovalPlan Anchor,
        StockReplayWacRemovalPlan Combined,
        IReadOnlyDictionary<int, StockReplayWacEvent> SourceEvents);

    private static bool IsRepeatedItemWacMrShape(IvTrxBatch batch, IvTrxBatchDetail[] details) =>
        details.Length is >= 2 and <= 10
        && details.All(d => d.BatchId == batch.Id && d.CompanyCode == batch.CompanyCode
            && d.BranchCode == batch.BranchCode && d.BatchNo == batch.BatchNo
            && d.TrxType == IvTrxTypes.MiscellaneousReceipt && d.TrxLineNo > 0
            && !string.IsNullOrWhiteSpace(d.ICode))
        && details.Select(d => d.TrxLineNo).Distinct().Count() == details.Length
        && details.Select(d => d.DocumentRevision).Distinct().Count() == 1
        && details.Select(d => d.ICode!).Distinct(StringComparer.OrdinalIgnoreCase).Count() < details.Length;

    private static string SameItemMrDraftFingerprint(IvTrxBatch batch, IReadOnlyList<IvTrxBatchDetail> details)
    {
        var evidence = StockPostingFingerprint.Create(new
        {
            batch.Id, batch.CompanyCode, batch.BranchCode, batch.BatchNo, batch.TrxType,
            batch.BatchStatus, batch.TrxDtTime, batch.RefNo, batch.Remarks, batch.VendCode,
            batch.VendName, batch.LocationCode, batch.SourceFingerprint, batch.DeletedAtUtc,
            batch.ForceCloseDate, batch.ModifiedDate, batch.ModifiedBy
        }, details.OrderBy(d => d.TrxLineNo).Select(d => new
        {
            d.Id, d.BatchId, d.CompanyCode, d.BranchCode, d.BatchNo, d.DocumentRevision,
            d.TrxLineNo, d.TrxType, d.ICode, d.IDesc, d.ProdCode, d.ProdDesc,
            d.FromBalLocId, d.FromLotId, d.FrWarehouse, d.FrLocation, d.FrLotNo,
            d.FrStdQty, d.FrStdUom, d.FrPurQty, d.FrPurUom, d.ToBalLocId, d.ToLotId,
            d.ToWarehouse, d.ToLocation, d.ToLotNo, d.ToStdQty, d.ToStdUom, d.ToPurQty,
            d.ToPurUom, d.IStatus, d.IClassCode, d.ExpiryDate, d.Reason, d.DoNo, d.InvNo,
            d.SoNo, d.PoNo, d.PoRelNo, d.SoLineNo, d.PoLineNo, d.Remarks,
            d.PriceEvidence, d.CostEvidenceType, d.CostOverrideReason, d.CostApprovedBy,
            d.CostApprovedAtUtc, d.StockCountCostBasisId, d.UnitPrice, d.Cost, d.CostPrice,
            d.BaseUnitPrices, d.Currency, d.LocationCode
        }).ToArray());
        return $"{evidence.RequestFingerprint}:{evidence.SourceSnapshotHash}";
    }

    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedRepeatedItemWacMrAsync(
        string company, string branch, int batchNo, CancellationToken ct)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(ct);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == batchNo
            && b.TrxType == IvTrxTypes.MiscellaneousReceipt && b.BatchStatus == IvBatchStatuses.New, ct);
        if (batch is null) return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsRepeatedItemWacMrShape(batch, details)
            || !await HasOnlyWacPoolsAsync(read, company, branch, details, ct))
            return null;
        var draftFingerprint = SameItemMrDraftFingerprint(batch, details);
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company, branch, batch.TrxDtTime, ct) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        if (FuturePostDateError(batch) is string future)
            return IvInventoryPostingBatchResult.Fail(batchNo, future);
        var items = details.Select(d => d.ICode!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (await HasReversedOutgoingAfterAsync(read, company, branch, items, batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "This receipt has a later reversed issue that needs additional replay.");

        var authorities = new List<StockReplayFifoMrReceiptAuthority>(details.Length);
        foreach (var detail in details)
        {
            var pile = await StockReplayInventoryReceiptPileLocator.FindUniqueAsync(read, company, branch, detail, ct);
            if (pile is not > 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, $"Line {detail.TrxLineNo} needs an existing destination pile for replay.");
            authorities.Add(await StockReplayFifoMrReceiptAuthorityReader.ReadLineAsync(
                read, company, branch, batchNo, pile.Value, detail.Id, ct));
        }
        var plans = new Dictionary<string, StockReplayWacInsertionPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in authorities.GroupBy(a => a.ItemCode, StringComparer.OrdinalIgnoreCase))
        {
            var itemLines = group.ToArray();
            if (itemLines.Select(a => a.BalLocId).Distinct().Count() != 1)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    "Repeated lines for one item currently need the same destination pile for replay.");
            var quantity = IvQty.Round(itemLines.Sum(a => a.Quantity));
            var value = StockLedgerPrecision.Money(itemLines.Sum(a => a.Value));
            var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(read, company, branch,
                itemLines[0].BalLocId, batch.TrxDtTime, quantity, ct);
            var candidate = new StockReplayWacEvent(physical.Candidate.EventId, physical.Candidate.Key,
                0m, 0m, quantity, value);
            var financial = await StockReplayWacInsertionPlanner.BuildAsync(read, company, branch,
                group.Key, physical, candidate, ct);
            if (financial.Tail.LaterEvents.Count == 0 && physical.LaterEvents.Count != 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt has a later movement without valuation.");
            if (financial.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    read, financial.Tail.LaterEvents, batch.TrxDtTime, ct) is string unsupported)
                return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
            plans.Add(group.Key, financial);
        }

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
            || SameItemMrDraftFingerprint(locked, currentDetails) != draftFingerprint
            || !await HasOnlyWacPoolsAsync(write, company, branch, currentDetails, ct)
            || await HasReversedOutgoingAfterAsync(write, company, branch, items, batch.TrxDtTime, ct))
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
        foreach (var financial in plans.Values)
            if (financial.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    write, financial.Tail.LaterEvents, batch.TrxDtTime, ct) is string changed)
                return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        var ledger = await BeginStandaloneLedgerAsync(write, company, branch, batchNo, reversal: false, ct);
        await using var scope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null) return ledger.Fail;
        var context = ledger.Context ?? throw MultiWacError("The receipt replay needs an active stock ledger.");
        var lines = authorities.Select(a => new MultiWacMrInsertLine(a, plans[a.ItemCode])).ToArray();
        var required = await WriteMultiWacMrAsync(context, lines, ct);
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, ct);
        scope.MarkCompleted();
        await tx.CommitAsync(ct);
        return IvInventoryPostingBatchResult.Ok(batchNo, locked.PostingOperationId ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedRepeatedItemWacMrAsync(
        string company, string branch, string userId, int batchNo, CancellationToken ct)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(ct);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == batchNo
            && b.TrxType == IvTrxTypes.MiscellaneousReceipt && b.BatchStatus == IvBatchStatuses.Posted, ct);
        if (batch is null) return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsRepeatedItemWacMrShape(batch, details)
            || !await HasOnlyWacPoolsAsync(read, company, branch, details, ct))
            return null;
        var route = await StockReplayInventoryRouteSelector.ReadRollbackAsync(read, company, branch,
            batchNo, IvTrxTypes.MiscellaneousReceipt, ct);
        if (!route.RequiresReplay) return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company, branch, batch.TrxDtTime, ct) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        var items = details.Select(d => d.ICode!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (await HasReversedOutgoingAfterAsync(read, company, branch, items, batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "This receipt has a later reversed issue that needs additional replay before rollback.");
        var histories = (await _posting.LoadHistoryForBatchAsync(read, company, branch, batchNo, ct))
            .OrderBy(h => h.Id).ToArray();
        if (histories.Length != details.Length || histories.Select(h => h.StockPostingId).Distinct().Count() != 1
            || histories.Any(h => h.EntryRole != "ORIGINAL" || h.LedgerVersion != 2 || h.StockPostingId is not > 0
                || h.TrxType != IvTrxTypes.MiscellaneousReceipt || h.ValuationStatus != StockValuationStatuses.Valued)
            || details.Any(d => histories.Count(h => h.TrxLineNo == d.TrxLineNo && MultiWacMrHistoryMatches(d, h)) != 1))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt has incomplete or changed posted history.");
        if (histories.GroupBy(h => h.ICode, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Select(h => h.ToBalLocId).Distinct().Count() != 1))
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Repeated lines for one item currently need the same destination pile for rollback.");
        var owner = await read.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == histories[0].StockPostingId, ct);
        if (owner is null || owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
            || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || owner.CommandType != $"IV_POST_{IvTrxTypes.MiscellaneousReceipt}"
            || owner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.PostingRole != "PRIMARY" || owner.EffectiveAt != batch.TrxDtTime
            || histories.Any(h => h.LedgerEpochId != owner.LedgerEpochId || h.TrxDtTime != owner.EffectiveAt
                || h.DocumentRevision != owner.DocumentRevision))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt has no exact sealed owner.");

        var plans = new Dictionary<string, SameItemWacRemovalPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in histories.GroupBy(h => h.ICode!, StringComparer.OrdinalIgnoreCase))
        {
            var originals = group.OrderBy(h => h.Id).ToArray();
            var first = originals[0];
            var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"H{first.Id:D10}", 0);
            var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(read, company, branch,
                first.ToBalLocId!.Value, new StockReplayPhysicalEvent(first.Id, key, first.ToStdQty!.Value), ct);
            var anchor = await StockReplayWacRemovalPlanner.BuildAsync(read, company, branch, group.Key, physical, ct);
            var plan = ComposeReceiptItemRemoval(anchor, originals, owner);
            if (plan.Combined.Tail.LaterEvents.Count == 0 && plan.Combined.PhysicalPlan.LaterEvents.Count != 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt has a later movement without valuation.");
            if (plan.Combined.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    read, plan.Combined.Tail.LaterEvents, batch.TrxDtTime, ct) is string unsupported)
                return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
            plans.Add(group.Key, plan);
        }

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
        if (!IsRepeatedItemWacMrShape(locked, currentDetails) || currentHistories.Length != histories.Length
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
            || await HasReversedOutgoingAfterAsync(write, company, branch, items, batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt or its dependencies changed. Retry rollback.");
        if (await RejectReversedProductionReceiptAsync(write, company, branch, batchNo,
                IvTrxTypes.MiscellaneousReceipt, rollback: true, ct) is { } rejected)
            return rejected;
        var qaRestore = await _qaPhysical.RestoreStockOutAsync(write, company, branch, batchNo, ct);
        if (qaRestore is not null) return IvInventoryPostingBatchResult.Fail(batchNo, qaRestore);
        foreach (var plan in plans.Values)
            if (plan.Combined.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    write, plan.Combined.Tail.LaterEvents, batch.TrxDtTime, ct) is string changed)
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
        var refreshedPlans = new Dictionary<string, SameItemWacRemovalPlan>(StringComparer.OrdinalIgnoreCase);
        var permits = new Dictionary<string, StockReplayWacWritePermit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, plan) in plans)
        {
            await StockReplayWacRemovalPlanner.ValidateWithinRootAsync(context, plan.Anchor, ct);
            var originals = currentHistories.Where(h => string.Equals(h.ICode, item, StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.Id).ToArray();
            var refreshed = ComposeReceiptItemRemoval(plan.Anchor, originals, owner);
            if (refreshed.Combined.PlanHash != plan.Combined.PlanHash)
                throw MultiWacError("The receipt removal plan changed after planning.");
            var combined = refreshed.Combined;
            refreshedPlans.Add(item, refreshed);
            permits.Add(item, new StockReplayWacWritePermit(context, item, combined.PlanHash,
                combined.Tail.CurrentQuantity, combined.Tail.CurrentValue,
                combined.Projection.FinalQuantity, combined.Projection.FinalValue,
                combined.PhysicalPlan.BalLocId, combined.PhysicalPlan.Projection.FinalQuantity));
        }
        var result = await RollBackInventoryMRCoreAsync(write, company, branch, userId, batchNo,
            IvTrxTypes.MiscellaneousReceipt, ct, context);
        if (!result.Succeeded) return result;
        await write.SaveChangesAsync(ct);
        var reversals = await write.IvTrxHistories.Where(h => h.StockPostingId == context.Posting.Id
            && h.EntryRole == "REVERSAL").ToArrayAsync(ct);
        if (reversals.Length != histories.Length)
            throw MultiWacError("The receipt has incomplete reversal histories.");
        var activeFacts = await StockReplayValuationGenerationResolver.ResolveAsync(write, company, branch,
            currentHistories, ct);
        foreach (var plan in refreshedPlans.Values)
        {
            var combined = plan.Combined;
            var pile = await write.IvBalLocs.AsNoTracking().SingleAsync(b => b.Id == combined.PhysicalPlan.BalLocId
                && b.CompanyCode == company && b.BranchCode == branch, ct);
            if (pile.StdQty != combined.PhysicalPlan.Projection.FinalQuantity)
                throw MultiWacError("The receipt rollback does not reconcile with its destination pile.");
        }
        for (var i = 0; i < currentHistories.Length; i++)
        {
            var original = currentHistories[i];
            var reversal = reversals.SingleOrDefault(h => h.ReversesHistoryId == original.Id);
            var facts = activeFacts[original.Id];
            var source = refreshedPlans[original.ICode!].SourceEvents[original.Id];
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
        var required = new List<int>();
        var nextLine = currentHistories.Length + 1;
        foreach (var (item, refreshed) in refreshedPlans)
        {
            var combined = refreshed.Combined;
            var tail = await StockReplayWacTailRepricingWriter.AppendPreparedRemovalTailAsync(
                context, permits[item], combined, nextLine, ct);
            required.AddRange(tail.RepricedHistoryIds);
            nextLine = checked(nextLine + 2 * combined.Tail.LaterEvents.Count);
            await StockReplayWacCostStateWriter.ApplyAsync(context, permits[item], ct);
        }
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, ct);
        scope.MarkCompleted();
        await tx.CommitAsync(ct);
        return result;
    }

    private static SameItemWacRemovalPlan ComposeReceiptItemRemoval(
        StockReplayWacRemovalPlan anchor, IReadOnlyList<IvTrxHistory> originals, StockPosting owner)
    {
        if (originals.Count == 1 && originals[0].Id == anchor.RemovedActiveEvent.EventId
            && originals[0].ICode == anchor.ItemCode && originals[0].StockPostingId == owner.Id
            && originals[0].ToBalLocId == anchor.PhysicalPlan.BalLocId)
            return new(anchor, anchor, new Dictionary<int, StockReplayWacEvent>
            {
                [originals[0].Id] = anchor.RemovedActiveEvent
            });
        return ComposeSameItemWacRemoval(anchor, originals, owner);
    }

    private static SameItemWacRemovalPlan ComposeSameItemWacRemoval(
        StockReplayWacRemovalPlan anchor, IReadOnlyList<IvTrxHistory> originals, StockPosting owner)
    {
        if (originals.Count < 2 || originals.Select(h => h.Id).Distinct().Count() != originals.Count
            || originals.Any(h => h.ICode != anchor.ItemCode || h.ToBalLocId != anchor.PhysicalPlan.BalLocId
                || h.StockPostingId != owner.Id || h.ToStdQty is not > 0m)
            || originals.Min(h => h.Id) != anchor.RemovedActiveEvent.EventId)
            throw MultiWacError("The receipt lines do not share one verified item and pile.");
        var ids = originals.Select(h => (long)h.Id).ToHashSet();
        var physicalEvents = new[] { anchor.PhysicalPlan.RemovedActiveEvent }
            .Concat(anchor.PhysicalPlan.LaterEvents.Where(e => ids.Contains(e.EventId)))
            .OrderBy(e => e.Key).ToArray();
        if (physicalEvents.Length != originals.Count || physicalEvents.Select(e => e.EventId).Distinct().Count() != originals.Count
            || originals.Any(h => !physicalEvents.Any(e => e.EventId == h.Id && e.SignedDelta == h.ToStdQty
                && e.Key == new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                    owner.PostingSequence, $"H{h.Id:D10}", 0))))
            throw MultiWacError("The receipt lines do not have exact active physical events.");
        var lastKey = physicalEvents[^1].Key;
        if (anchor.PhysicalPlan.LaterEvents.Any(e => !ids.Contains(e.EventId) && e.Key.CompareTo(lastKey) <= 0)
            || anchor.Tail.LaterEvents.Any(e => !ids.Contains(e.EventId) && e.Key.CompareTo(lastKey) <= 0)
            || anchor.Tail.PhysicalByPile.Any(p => p.Key != anchor.PhysicalPlan.BalLocId
                && p.Value.Any(e => ids.Contains(e.EventId))))
            throw MultiWacError("Another movement overlaps the receipt lines.");
        var financialEvents = new[] { anchor.RemovedActiveEvent }
            .Concat(anchor.Tail.LaterEvents.Where(e => ids.Contains(e.EventId)))
            .ToDictionary(e => checked((int)e.EventId));
        if (financialEvents.Count != originals.Count || originals.Any(h =>
                !financialEvents.TryGetValue(h.Id, out var e)
                || e.Key != physicalEvents.Single(p => p.EventId == h.Id).Key
                || e.OldSignedQuantity != h.ToStdQty || e.ReplaySignedQuantity != h.ToStdQty
                || e.OldSignedValue < 0m || e.FixedSignedValue != e.OldSignedValue
                || e.AbsoluteCountQty is not null))
            throw MultiWacError("The receipt lines do not have exact active WAC values.");

        var quantity = IvQty.Round(physicalEvents.Sum(e => e.SignedDelta));
        var value = StockLedgerPrecision.Money(financialEvents.Values.Sum(e => e.OldSignedValue));
        var combinedPhysicalEvent = anchor.PhysicalPlan.RemovedActiveEvent with { SignedDelta = quantity };
        var laterPhysical = anchor.PhysicalPlan.LaterEvents.Where(e => !ids.Contains(e.EventId)).ToArray();
        var physicalProjection = StockReplayPhysicalSimulator.SimulateRemoval(
            anchor.PhysicalPlan.CurrentQuantity, combinedPhysicalEvent, laterPhysical);
        var combinedPhysical = anchor.PhysicalPlan with { RemovedActiveEvent = combinedPhysicalEvent,
            LaterEvents = laterPhysical, Projection = physicalProjection };
        var combinedEvent = anchor.RemovedActiveEvent with { OldSignedQuantity = quantity,
            OldSignedValue = value, ReplaySignedQuantity = quantity, FixedSignedValue = value };
        var combinedTail = anchor.Tail with
        {
            LaterEvents = anchor.Tail.LaterEvents.Where(e => !ids.Contains(e.EventId)).ToArray(),
            PhysicalByPile = anchor.Tail.PhysicalByPile.ToDictionary(p => p.Key,
                p => (IReadOnlyList<StockReplayPhysicalEvent>)p.Value.Where(e => !ids.Contains(e.EventId)).ToArray())
        };
        var projection = StockReplayWacTailProjector.SimulateSinglePileRemoval(
            combinedTail, combinedPhysical, combinedEvent);
        var hash = StockPostingFingerprint.Create(new { anchor.PlanHash,
            originals = physicalEvents, values = financialEvents.OrderBy(p => p.Key).Select(p => p.Value).ToArray(),
            combinedPhysicalEvent, combinedEvent, projection }, new { }).RequestFingerprint;
        var combined = anchor with { PhysicalPlan = combinedPhysical, RemovedActiveEvent = combinedEvent,
            Tail = combinedTail, Projection = projection, PlanHash = hash };
        return new(anchor, combined, financialEvents);
    }
}
