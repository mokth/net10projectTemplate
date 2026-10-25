using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // Reverses a transfer while preserving a proven unchanged open-month tail.
    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedWacTrAsync(
        string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var candidate = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo && b.TrxType == IvTrxTypes.StockTransfer
            && b.BatchStatus == IvBatchStatuses.Posted, cancellationToken);
        if (candidate is null)
            return null;
        var prior = await _posting.LoadHistoryForBatchAsync(read,
            companyCode, branchCode, batchNo, cancellationToken);
        if (prior.Count != 1 || prior[0].EntryRole != "ORIGINAL"
            || prior[0].LedgerVersion != 2
            || prior[0].StockPostingId is not > 0
            || prior[0].ICode is null)
            return null;
        var method = await read.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == prior[0].ICode)
            .Select(s => s.CostMethod).SingleOrDefaultAsync(cancellationToken);
        if (method != StockCostMethods.MovingAverage)
            return null;
        var observed = (await StockReplayValuationGenerationResolver.ResolveAsync(
            read, companyCode, branchCode, prior,
            cancellationToken))[prior[0].Id];
        var observedCorrection = observed.Any(f =>
            f.StockPostingId != prior[0].StockPostingId);
        var hasLaterValue = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == prior[0].ICode
                && f.EffectiveAt > prior[0].TrxDtTime
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!observedCorrection && !hasLaterValue)
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(db,
            companyCode, branchCode, cancellationToken);
        var ledger = await BeginStandaloneLedgerAsync(db, companyCode,
            branchCode, batchNo, reversal: true, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(
            _coordinator, ledger.Context);
        if (ledger.Fail is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return ledger.Fail;
        }
        var root = ledger.Context;
        if (root is null || root.CostMethod != StockCostMethods.MovingAverage)
            throw InvalidWacTransfer("Replay-aware transfer rollback needs an active WAC root.");
        var batch = await _posting.LockBatchForUpdateAsync(db,
            companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null || batch.Id != candidate.Id
            || batch.BatchStatus != IvBatchStatuses.Posted
            || batch.TrxType != IvTrxTypes.StockTransfer
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null)
            throw InvalidWacTransfer("The posted transfer changed before rollback.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string closed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        }
        var details = await _posting.LoadDetailsForBatchAsync(db,
            batch.Id, cancellationToken);
        var current = await _posting.LoadHistoryForBatchAsync(db,
            companyCode, branchCode, batchNo, cancellationToken);
        if (details.Count != 1 || current.Count != 1
            || current[0].Id != prior[0].Id
            || ValidateTrHistoryIntegrity(details, current,
                companyCode, branchCode, batchNo) is not null)
            throw InvalidWacTransfer("The transfer detail or original history changed.");
        var detail = details[0];
        var history = current[0];
        var owner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == history.StockPostingId,
                cancellationToken);
        var idText = batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var noText = batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (history.EntryRole != "ORIGINAL" || history.LedgerVersion != 2
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.ICode is null || history.FromBalLocId is not > 0
            || history.ToBalLocId is not > 0
            || history.FromBalLocId == history.ToBalLocId
            || history.FrStdQty is not > 0m
            || history.ToStdQty != history.FrStdQty
            || detail.DocumentRevision != root.Posting.DocumentRevision
            || detail.ICode != history.ICode
            || detail.FrStdUom != history.FrStdUom
            || detail.ToStdUom != history.ToStdUom
            || !SameWacTransferSlicePart(detail.FrWarehouse, history.FrWarehouse)
            || !SameWacTransferSlicePart(detail.FrLocation, history.FrLocation)
            || !SameWacTransferSlicePart(detail.ToWarehouse, history.ToWarehouse)
            || !SameWacTransferSlicePart(detail.ToLocation, history.ToLocation)
            || !SameWacTransferSlicePart(detail.IStatus, history.IStatus)
            || batch.TrxDtTime != history.TrxDtTime
            || owner is null || owner.SealedAtUtc is null
            || owner.CompanyCode != companyCode || owner.BranchCode != branchCode
            || owner.SourceModule != "INVENTORY"
            || owner.SourceDocumentType != IvTrxTypes.StockTransfer
            || owner.SourceDocumentId != idText
            || owner.SourceDocumentNo != noText
            || owner.PostingRole != "PRIMARY"
            || owner.EffectiveAt != history.TrxDtTime
            || owner.LedgerEpochId != history.LedgerEpochId
            || root.Posting.ReversesPostingId != owner.Id
            || root.Posting.PostingRole != "REVERSAL"
            || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || root.Posting.SourceDocumentId != idText
            || root.Posting.SourceDocumentNo != noText
            || root.Posting.EffectiveAt != history.TrxDtTime
            || root.Epoch.Id != history.LedgerEpochId)
            throw InvalidWacTransfer("The rollback root does not own this transfer.");
        var balances = await db.IvBalLocs
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && (b.Id == history.FromBalLocId
                    || b.Id == history.ToBalLocId))
            .ToArrayAsync(cancellationToken);
        if (balances.Length != 2)
            throw InvalidWacTransfer("The transfer has no exact pair of piles.");
        var source = balances.Single(b => b.Id == history.FromBalLocId);
        var destination = balances.Single(b => b.Id == history.ToBalLocId);
        var quantity = history.FrStdQty.Value;
        if (source.ICode != history.ICode || destination.ICode != history.ICode
            || source.LotId is not null || destination.LotId is not null
            || !string.IsNullOrWhiteSpace(source.LotNo)
            || !string.IsNullOrWhiteSpace(destination.LotNo)
            || source.StdUom != history.FrStdUom
            || destination.StdUom != history.ToStdUom
            || !SameWacTransferSlicePart(source.WhCode, history.FrWarehouse)
            || !SameWacTransferSlicePart(source.LocCode, history.FrLocation)
            || !SameWacTransferSlicePart(destination.WhCode, history.ToWarehouse)
            || !SameWacTransferSlicePart(destination.LocCode, history.ToLocation)
            || !SameWacTransferSlicePart(source.IStatus, history.IStatus)
            || !SameWacTransferSlicePart(destination.IStatus, history.IStatus)
            || source.StdQty < 0m || destination.StdQty < quantity)
            throw InvalidWacTransfer("The transfer piles do not support bounded rollback.");
        var key = new StockReplayEventKey(history.TrxDtTime,
            StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
            $"H{history.Id:D10}", 0);
        var sourcePlan = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            db, companyCode, branchCode, source.Id,
            new StockReplayPhysicalEvent(history.Id, key, -quantity),
            cancellationToken);
        var destPlan = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            db, companyCode, branchCode, destination.Id,
            new StockReplayPhysicalEvent(history.Id, key, quantity),
            cancellationToken);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, companyCode, branchCode, history.ICode, key,
            cancellationToken);
        var tailError = await CheckWacTransferRemovalTailAsync(db, history,
            sourcePlan, destPlan, tail, cancellationToken);
        if (sourcePlan.Projection.FinalQuantity != source.StdQty + quantity
            || destPlan.Projection.FinalQuantity != destination.StdQty - quantity
            || sourcePlan.LedgerEpochId != root.Epoch.Id
            || destPlan.LedgerEpochId != root.Epoch.Id
            || tailError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo,
                tailError ?? "The WAC transfer physical removal changed its projection.");
        }
        var active = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, companyCode, branchCode, [history],
            cancellationToken))[history.Id]
            .OrderBy(f => f.PostingLineNo).ThenBy(f => f.SplitOrdinal)
            .ToArray();
        var activeOwnerIds = active.Select(f => f.StockPostingId).Distinct().ToArray();
        var activeOwner = activeOwnerIds.Length == 1
            ? await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(
                p => p.Id == activeOwnerIds[0], cancellationToken)
            : null;
        var corrected = activeOwner is not null && activeOwner.Id != owner.Id;
        var wacEvent = StockReplayWacHistoryEventFactory.Create(
            history, history.Id, key, active);
        if (active.Length != 2 || activeOwner is null
            || activeOwner.SealedAtUtc is null
            || activeOwner.CompanyCode != companyCode
            || activeOwner.BranchCode != branchCode
            || activeOwner.LedgerEpochId != root.Epoch.Id
            || activeOwner.SourceModule != "INVENTORY"
            || active.Count(f => f.Direction < 0
                && f.MovementCode == "TRANSFER_OUT"
                && f.FromBalLocId == source.Id
                && f.ToBalLocId == destination.Id) != 1
            || active.Count(f => f.Direction > 0
                && f.MovementCode == "TRANSFER_IN"
                && f.FromBalLocId == source.Id
                && f.ToBalLocId == destination.Id) != 1
            || active.Any(f => f.ValuationSource
                != StockValuationSources.MovingAverage
                || f.BaseCostAmount != f.CostAmount)
            || wacEvent.OldSignedQuantity != 0m
            || wacEvent.OldSignedValue != 0m)
            throw InvalidWacTransfer("The transfer has no complete active WAC generation.");
        if (corrected)
        {
            var correctionBatch = int.TryParse(activeOwner.SourceDocumentId,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var correctionBatchId)
                ? await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
                    b.Id == correctionBatchId && b.CompanyCode == companyCode
                    && b.BranchCode == branchCode, cancellationToken)
                : null;
            if (activeOwner.SourceDocumentType is not
                    (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.MiscellaneousIssue)
                || activeOwner.PostingRole is not ("PRIMARY" or "REVERSAL")
                || activeOwner.CommandType != $"IV_{(activeOwner.PostingRole == "REVERSAL"
                    ? "ROLLBACK" : "POST")}_{activeOwner.SourceDocumentType}"
                || correctionBatch is null
                || correctionBatch.TrxType != activeOwner.SourceDocumentType
                || activeOwner.SourceDocumentNo
                    != correctionBatch.BatchNo.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                || active.Any(f => f.OriginalValuationFactId is not > 0))
                throw InvalidWacTransfer("The corrected WAC transfer has no exact owner.");
            var originIds = active.Select(f => f.OriginalValuationFactId!.Value)
                .ToArray();
            var origins = await db.StockValuationFacts.AsNoTracking()
                .Where(f => originIds.Contains(f.Id)).ToArrayAsync(cancellationToken);
            if (origins.Length != 2 || origins.Any(f =>
                    f.StockPostingId != owner.Id
                    || f.InventoryHistoryId != history.Id
                    || f.OriginalValuationFactId is not null
                    || f.Direction is not (-1 or 1))
                || active.Any(f => !origins.Any(o =>
                    o.Id == f.OriginalValuationFactId
                    && o.Direction == f.Direction && o.BaseQty == f.BaseQty)))
                throw InvalidWacTransfer("The corrected transfer has no exact original generation.");
        }
        else if (activeOwner.Id != owner.Id
            || activeOwner.SourceDocumentType != IvTrxTypes.StockTransfer
            || activeOwner.SourceDocumentId != idText
            || activeOwner.SourceDocumentNo != noText
            || activeOwner.PostingRole != "PRIMARY"
            || active.Any(f => f.OriginalValuationFactId is not null))
            throw InvalidWacTransfer("The original transfer generation changed.");
        var state = await db.StockCostStates.AsNoTracking()
            .SingleAsync(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == history.ICode,
                cancellationToken);
        if (state.CostMethod != StockCostMethods.MovingAverage
            || state.OnHandBaseQty != tail.CurrentQuantity
            || state.InventoryValue != tail.CurrentValue)
            throw InvalidWacTransfer("The WAC item state changed before transfer rollback.");

        var now = DateTime.UtcNow;
        var user = root.UserId.Length > 10 ? root.UserId[..10] : root.UserId;
        source.StdQty = sourcePlan.Projection.FinalQuantity;
        destination.StdQty = destPlan.Projection.FinalQuantity;
        source.TransDate = await ResolveRollbackTransDateAsync(db,
            companyCode, branchCode, source.Id, batchNo,
            source.TransDate, source.StdQty, cancellationToken);
        destination.TransDate = await ResolveRollbackTransDateAsync(db,
            companyCode, branchCode, destination.Id, batchNo,
            destination.TransDate, destination.StdQty, cancellationToken);
        source.ModifiedDate = now;
        destination.ModifiedDate = now;
        var reversal = _historyWriter!.AppendReversal(root,
            [history], root.Posting.DocumentRevision).Single();
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
        await StockReplayValuationReversalWriter.AppendAsync(root,
            history.Id, active.Select((f, i) =>
                new StockReplayValuationReversalLine(f.Id, 1, i,
                    reversal.Id)).ToArray(), cancellationToken);
        reversal.ValuationStatus = StockValuationStatuses.Reversed;
        await _coordinator!.CompleteReplayInTransactionAsync(root,
            [], cancellationToken);
        var finalState = await db.StockCostStates.AsNoTracking()
            .SingleAsync(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == history.ICode,
                cancellationToken);
        if (finalState.OnHandBaseQty != tail.CurrentQuantity
            || finalState.InventoryValue != tail.CurrentValue
            || source.StdQty + destination.StdQty
                != sourcePlan.CurrentQuantity + destPlan.CurrentQuantity)
            throw InvalidWacTransfer("The removed transfer changed its item WAC pool.");
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo,
            batch.RollbackOperationId ?? Guid.Empty);
    }

    private static StockLedgerException InvalidWacTransfer(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static async Task<string?> CheckWacTransferRemovalTailAsync(
        AppDbContext db, IvTrxHistory removed,
        StockReplayPhysicalRemovalPlan source,
        StockReplayPhysicalRemovalPlan destination,
        StockReplayWacFinancialTail tail,
        CancellationToken cancellationToken)
    {
        if (source.LaterEvents.Count == 0
            && destination.LaterEvents.Count == 0
            && tail.LaterEvents.Count == 0)
            return null;
        var projected = StockReplayWacSimulator.SimulateRemoval(
            tail.CurrentQuantity, tail.CurrentValue,
            new StockReplayWacEvent(removed.Id,
                source.RemovedActiveEvent.Key, 0m, 0m, 0m, 0m),
            tail.LaterEvents);
        if (projected.FinalQuantity != tail.CurrentQuantity
            || projected.FinalValue != tail.CurrentValue
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(source)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(destination))
            return "Replay-aware WAC transfer rollback changes a later movement.";
        return await CheckWacTransferDescendantTailAsync(db,
            source.CompanyCode, source.BranchCode, removed.ICode!,
            removed.TrxDtTime, source.BalLocId, destination.BalLocId,
            source.LaterEvents, destination.LaterEvents,
            tail, projected.LaterSteps, cancellationToken);
    }

    private static bool SameWacTransferSlicePart(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
}
