using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedFifoTrAsync(
        string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != IvTrxTypes.StockTransfer
            || batch.BatchStatus != IvBatchStatuses.New)
            return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1 || string.IsNullOrWhiteSpace(details[0].ICode))
            return null;
        var detail = details[0];
        var itemCode = detail.ICode!;
        var hasLaterValue = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == itemCode
                && f.EffectiveAt > batch.TrxDtTime
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLaterValue)
            return null;
        var method = await read.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == itemCode)
            .Select(s => s.CostMethod).SingleOrDefaultAsync(cancellationToken);
        if (method != StockCostMethods.Fifo)
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        if (FuturePostDateError(batch) is string future)
            return IvInventoryPostingBatchResult.Fail(batchNo, future);
        if (detail.FromBalLocId is not > 0)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO transfer needs an existing source pile.");
        var destinations = await read.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.ICode == itemCode
                && b.WhCode == detail.ToWarehouse
                && b.LocCode == (detail.ToLocation ?? string.Empty)
                && b.IStatus == detail.IStatus && b.LotId == null
                && (b.LotNo == null || b.LotNo == ""))
            .Select(b => b.Id).ToArrayAsync(cancellationToken);
        if (destinations.Length != 1
            || destinations[0] == detail.FromBalLocId.Value)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO transfer needs a distinct existing non-lot destination pile.");
        var watermark = await read.StockPostingBranchSequences.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode)
            .Select(s => (long?)s.LastSequence)
            .SingleOrDefaultAsync(cancellationToken) ?? 0L;
        var key = new StockReplayEventKey(batch.TrxDtTime,
            StockReplayEventKey.OrdinaryClass, checked(watermark + 1),
            "H9999999999", 0);
        if (detail.FrStdQty is not > 0m)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO transfer needs a positive quantity.");
        var quantity = detail.FrStdQty.Value;
        var source = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            read, companyCode, branchCode, detail.FromBalLocId.Value,
            new StockReplayPhysicalEvent(long.MaxValue - 1, key, -quantity),
            cancellationToken);
        var destination = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            read, companyCode, branchCode, destinations[0],
            new StockReplayPhysicalEvent(long.MaxValue - 1, key, quantity),
            cancellationToken);
        var plan = await StockReplayFifoTransferInsertionPlanner.BuildDraftAsync(
            read, companyCode, branchCode, batchNo, source, destination,
            cancellationToken);
        if (await CheckSimpleFifoTransferTailAsync(read, companyCode,
                branchCode, itemCode, key,
                source.BalLocId, destination.BalLocId,
                source.LaterEvents, destination.LaterEvents,
                cancellationToken) is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(write,
            companyCode, branchCode, cancellationToken);
        var ledger = await BeginStandaloneLedgerAsync(write, companyCode,
            branchCode, batchNo, reversal: false, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(
            _coordinator, ledger.Context);
        if (ledger.Fail is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return ledger.Fail;
        }
        var context = ledger.Context;
        if (context is null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerDisabled,
                "Backdated FIFO transfer needs an active V2 posting root."));
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        if (await CheckSimpleFifoTransferTailAsync(write, companyCode,
                branchCode, itemCode, key,
                source.BalLocId, destination.BalLocId,
                source.LaterEvents, destination.LaterEvents,
                cancellationToken) is string changedTail)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch, changedTail));
        var written = await StockReplayFifoTransferRootWriter.InsertAsync(
            context, plan, batchNo, cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(
            context, written.RequiredActiveHistoryIds, cancellationToken);
        await StockReplayFifoTransferReconciliationGate.VerifySealedWithinRootAsync(
            context, plan, written.RootHistoryId, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id)
            .Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedFifoTrAsync(
        string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != IvTrxTypes.StockTransfer
            || batch.BatchStatus != IvBatchStatuses.Posted)
            return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1)
            return null;
        var histories = await read.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == companyCode
                && h.BranchCode == branchCode && h.BatchNo == batchNo
                && h.DocumentRevision == details[0].DocumentRevision)
            .ToArrayAsync(cancellationToken);
        if (histories.Length != 1)
            return null;
        var original = histories[0];
        if (original.EntryRole != "ORIGINAL" || original.LedgerVersion != 2
            || original.StockPostingId is not > 0 || original.ICode is null
            || original.FromBalLocId is not > 0
            || original.ToBalLocId is not > 0
            || original.FrStdQty is not > 0m)
            return null;
        var method = await read.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == original.ICode)
            .Select(s => s.CostMethod).SingleOrDefaultAsync(cancellationToken);
        if (method != StockCostMethods.Fifo)
            return null;
        var owner = await read.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == original.StockPostingId,
                cancellationToken);
        if (owner is null || owner.SealedAtUtc is null
            || owner.SourceModule != "INVENTORY"
            || owner.SourceDocumentType != IvTrxTypes.StockTransfer
            || owner.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.EffectiveAt != original.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "The posted FIFO transfer has no exact sealed origin.");
        var hasLaterValue = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == original.ICode
                && (f.EffectiveAt > owner.EffectiveAt
                    || f.EffectiveAt == owner.EffectiveAt
                        && f.StockPosting!.PostingSequence > owner.PostingSequence)
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLaterValue)
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, companyCode,
                branchCode, owner.EffectiveAt, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        var key = new StockReplayEventKey(owner.EffectiveAt,
            StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
            $"H{original.Id:D10}", 0);
        var source = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            read, companyCode, branchCode, original.FromBalLocId.Value,
            new StockReplayPhysicalEvent(original.Id, key,
                -original.FrStdQty.Value), cancellationToken);
        var destination = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            read, companyCode, branchCode, original.ToBalLocId.Value,
            new StockReplayPhysicalEvent(original.Id, key,
                original.FrStdQty.Value), cancellationToken);
        var plan = await StockReplayFifoTransferRemovalPlanner.BuildAsync(
            read, companyCode, branchCode, original.ICode, source,
            destination, cancellationToken);
        if (await CheckSimpleFifoTransferTailAsync(read, companyCode,
                branchCode, original.ICode, key,
                source.BalLocId, destination.BalLocId,
                source.LaterEvents, destination.LaterEvents,
                cancellationToken) is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(write,
            companyCode, branchCode, cancellationToken);
        var ledger = await BeginStandaloneLedgerAsync(write, companyCode,
            branchCode, batchNo, reversal: true, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(
            _coordinator, ledger.Context);
        if (ledger.Fail is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return ledger.Fail;
        }
        var context = ledger.Context;
        if (context is null || context.Posting.ReversesPostingId != owner.Id)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch,
                "Backdated FIFO transfer rollback needs its original owner."));
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, owner.EffectiveAt, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        if (await CheckSimpleFifoTransferTailAsync(write, companyCode,
                branchCode, original.ICode, key,
                source.BalLocId, destination.BalLocId,
                source.LaterEvents, destination.LaterEvents,
                cancellationToken) is string changedTail)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch, changedTail));
        var written = await StockReplayFifoTransferRemovalRootWriter.RemoveAsync(
            context, plan, batchNo, cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(
            context, written.RequiredActiveHistoryIds, cancellationToken);
        await StockReplayFifoTransferReconciliationGate.VerifySealedWithinRootAsync(
            context, plan, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id)
            .Select(b => b.RollbackOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private static async Task<string?> CheckSimpleFifoTransferTailAsync(
        ErpWeb.Model.Data.AppDbContext db,
        string companyCode, string branchCode, string itemCode,
        StockReplayEventKey candidateKey,
        int sourcePileId, int destinationPileId,
        IReadOnlyList<StockReplayPhysicalEvent> sourceLater,
        IReadOnlyList<StockReplayPhysicalEvent> destinationLater,
        CancellationToken cancellationToken)
    {
        var grouped = sourceLater.Select(e => (PileId: sourcePileId, Event: e))
            .Concat(destinationLater.Select(e =>
                (PileId: destinationPileId, Event: e)))
            .GroupBy(x => x.Event.EventId).ToArray();
        if (grouped.Length is 0 or > 100
            || grouped.Any(g => g.Key is <= 0 or > int.MaxValue
                || g.Count() > 2
                || g.Select(x => x.Event.Key).Distinct().Count() != 1
                || g.Any(x =>
                    x.Event.Key.EffectiveAt.Year != candidateKey.EffectiveAt.Year
                    || x.Event.Key.EffectiveAt.Month != candidateKey.EffectiveAt.Month
                    || x.Event.AbsoluteCountQty is not null)))
            return "Backdated FIFO transfer supports at most 100 later Inventory MR/MI or transfer movements in the same open month.";
        var ids = grouped.Select(g => checked((int)g.Key)).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .ToArrayAsync(cancellationToken);
        var legsById = grouped.ToDictionary(g => checked((int)g.Key),
            g => g.ToArray());
        bool MatchesPhysical(IvTrxHistory h)
        {
            var legs = legsById[h.Id];
            return h.TrxType switch
            {
                IvTrxTypes.StockTransfer => h.FrStdQty is > 0m
                    && h.ToStdQty == h.FrStdQty
                    && h.FromBalLocId is > 0 && h.ToBalLocId is > 0
                    && h.FromBalLocId != h.ToBalLocId
                    && legs.Length == (h.FromBalLocId == sourcePileId
                        || h.FromBalLocId == destinationPileId ? 1 : 0)
                        + (h.ToBalLocId == sourcePileId
                            || h.ToBalLocId == destinationPileId ? 1 : 0)
                    && legs.All(x => x.PileId == h.FromBalLocId
                        ? x.Event.SignedDelta == -h.FrStdQty
                        : x.PileId == h.ToBalLocId
                            && x.Event.SignedDelta == h.ToStdQty),
                IvTrxTypes.MiscellaneousIssue => h.FrStdQty is > 0m
                    && h.FromBalLocId is > 0 && h.ToBalLocId is null
                    && legs.Length == 1
                    && legs[0].PileId == h.FromBalLocId
                    && legs[0].Event.SignedDelta == -h.FrStdQty,
                IvTrxTypes.MiscellaneousReceipt => h.ToStdQty is > 0m
                    && h.ToBalLocId is > 0 && h.FromBalLocId is null
                    && legs.Length == 1
                    && legs[0].PileId == h.ToBalLocId
                    && legs[0].Event.SignedDelta == h.ToStdQty,
                _ => false
            };
        }
        if (histories.Length != ids.Length || histories.Any(h =>
                h.CompanyCode != companyCode || h.BranchCode != branchCode
                || h.ICode != itemCode
                || h.EntryRole != "ORIGINAL" || h.LedgerVersion != 2
                || h.ValuationStatus != StockValuationStatuses.Valued
                || h.BatchStatus != IvBatchStatuses.Posted
                || h.StockPostingId is not > 0
                || !MatchesPhysical(h)))
            return "Backdated FIFO transfer has a later movement outside unchanged Inventory MR/MI/transfer tails.";
        var thirdLegs = histories.Where(h => h.TrxType == IvTrxTypes.StockTransfer)
            .SelectMany(h => new[]
            {
                (PileId: h.FromBalLocId!.Value, Delta: -h.FrStdQty!.Value,
                    HistoryId: h.Id, Uom: h.FrStdUom,
                    Warehouse: h.FrWarehouse, Location: h.FrLocation,
                    Status: h.IStatus),
                (PileId: h.ToBalLocId!.Value, Delta: h.ToStdQty!.Value,
                    HistoryId: h.Id, Uom: h.ToStdUom,
                    Warehouse: h.ToWarehouse, Location: h.ToLocation,
                    Status: h.IStatus)
            })
            .Where(x => x.PileId != sourcePileId
                && x.PileId != destinationPileId).ToArray();
        var thirdIds = thirdLegs.Select(x => x.PileId).Distinct().ToArray();
        if (thirdIds.Length > 8)
            return "Backdated FIFO transfer supports at most eight verified third piles in its later transfer tail.";
        foreach (var thirdId in thirdIds)
        {
            var third = await db.IvBalLocs.AsNoTracking()
                .SingleOrDefaultAsync(b => b.Id == thirdId
                    && b.CompanyCode == companyCode
                    && b.BranchCode == branchCode
                    && b.ICode == itemCode, cancellationToken);
            if (third is null || third.LotId is not null
                || !string.IsNullOrWhiteSpace(third.LotNo)
                || thirdLegs.Where(x => x.PileId == thirdId).Any(x =>
                    third.StdUom != x.Uom
                    || third.WhCode != x.Warehouse
                    || third.LocCode != x.Location
                    || third.IStatus != x.Status))
                return "A later FIFO transfer has no exact third item pile.";
            var events = await StockReplayPhysicalTimelineReader.ReadLaterAsync(db,
                companyCode, branchCode, third.Id, candidateKey,
                cancellationToken);
            if (thirdLegs.Where(x => x.PileId == thirdId).Any(x =>
                    events.Count(e => e.EventId == x.HistoryId
                    && e.Key == legsById[x.HistoryId][0].Event.Key
                    && e.SignedDelta == x.Delta) != 1))
                return "A later FIFO transfer has no active matching third physical leg.";
        }
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(db,
            companyCode, branchCode, histories, cancellationToken);
        if (histories.Any(h => active[h.Id].Any(f =>
                f.StockPostingId != h.StockPostingId
                || f.OriginalValuationFactId is not null
                || f.CostMethod != StockCostMethods.Fifo
                || f.ValuationSource != StockValuationSources.Fifo)))
            return "Backdated FIFO transfer has a corrected or inconsistent later valuation.";
        var ownerIds = histories.Select(h => h.StockPostingId!.Value)
            .Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.SourceModule,
                p.SourceDocumentType, p.SourceDocumentId,
                p.SourceDocumentNo, p.PostingRole,
                p.CommandType, p.EffectiveAt, p.SealedAtUtc })
            .ToArrayAsync(cancellationToken);
        if (owners.Length != ownerIds.Length || owners.Any(p =>
                p.SourceModule != "INVENTORY"
                || p.SealedAtUtc is null))
            return "Backdated FIFO transfer has a foreign or unsealed descendant.";
        var ownerById = owners.ToDictionary(p => p.Id);
        var batchNumbers = histories.Select(h => h.BatchNo).Distinct().ToArray();
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode && b.BranchCode == branchCode
                && batchNumbers.Contains(b.BatchNo))
            .ToArrayAsync(cancellationToken);
        if (batches.Length != batchNumbers.Length
            || histories.Any(h => !ownerById.TryGetValue(
                    h.StockPostingId!.Value, out var owner)
                || !batches.Any(b => b.BatchNo == h.BatchNo
                    && b.TrxType == h.TrxType
                    && b.BatchStatus == IvBatchStatuses.Posted
                    && owner.SourceDocumentType == h.TrxType
                    && owner.SourceDocumentId == b.Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    && owner.SourceDocumentNo == h.BatchNo.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    && owner.PostingRole == "PRIMARY"
                    && owner.CommandType == $"IV_POST_{h.TrxType}"
                    && owner.EffectiveAt == h.TrxDtTime)))
            return "Backdated FIFO transfer has a descendant without its exact posted Inventory owner.";
        var laterFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == itemCode
                && (f.EffectiveAt > candidateKey.EffectiveAt
                    || f.EffectiveAt == candidateKey.EffectiveAt
                        && f.StockPosting!.PostingSequence
                            > candidateKey.OriginalPostingSequence)
                && f.StockPosting!.SealedAtUtc != null)
            .Select(f => f.InventoryHistoryId)
            .ToArrayAsync(cancellationToken);
        if (laterFacts.Any(id => id is not int historyId
                || !ids.Contains(historyId)))
            return "Backdated FIFO transfer has a later valuation outside its verified transfer tail.";
        return null;
    }
}
