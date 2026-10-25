using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedWacTrAsync(
        string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo && b.TrxType == IvTrxTypes.StockTransfer
            && b.BatchStatus == IvBatchStatuses.New, cancellationToken);
        if (batch is null)
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
        if (method != StockCostMethods.MovingAverage)
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        if (FuturePostDateError(batch) is string future)
            return IvInventoryPostingBatchResult.Fail(batchNo, future);
        if (detail.FromBalLocId is not > 0 || detail.FrStdQty is not > 0m)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated WAC transfer needs an existing source pile and positive quantity.");
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
                "Backdated WAC transfer needs a distinct existing non-lot destination pile.");
        var watermark = await read.StockPostingBranchSequences.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode)
            .Select(s => (long?)s.LastSequence)
            .SingleOrDefaultAsync(cancellationToken) ?? 0L;
        var key = new StockReplayEventKey(batch.TrxDtTime,
            StockReplayEventKey.OrdinaryClass, checked(watermark + 1),
            "H9999999999", 0);
        var quantity = detail.FrStdQty.Value;
        var sourcePlan = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            read, companyCode, branchCode, detail.FromBalLocId.Value,
            new StockReplayPhysicalEvent(long.MaxValue - 1, key, -quantity),
            cancellationToken);
        var destPlan = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            read, companyCode, branchCode, destinations[0],
            new StockReplayPhysicalEvent(long.MaxValue - 1, key, quantity),
            cancellationToken);
        var authority = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(
            read, companyCode, branchCode, batchNo,
            sourcePlan.BalLocId, destPlan.BalLocId, cancellationToken);
        var prepared = await ReadWacTransferInsertionTailAsync(read,
            authority, sourcePlan, destPlan, cancellationToken);
        if (prepared.Error is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
        var fingerprint = WacTransferInsertionFingerprint(
            sourcePlan, destPlan, authority,
            prepared.Tail!, prepared.Projection!);

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
        var root = ledger.Context;
        if (root is null || root.CostMethod != StockCostMethods.MovingAverage
            || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || root.Posting.SourceDocumentId
                != authority.BatchId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.SourceDocumentNo
                != batchNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.DocumentRevision != authority.DocumentRevision
            || root.Posting.PostingRole != "PRIMARY"
            || root.Posting.EffectiveAt != authority.EffectiveAt
            || root.Posting.PostingSequence != key.OriginalPostingSequence
            || root.Epoch.Id != sourcePlan.LedgerEpochId)
            throw InvalidWacTransfer("The WAC transfer root differs from its draft.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            root, sourcePlan, cancellationToken);
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            root, destPlan, cancellationToken);
        var currentDraft = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(
            write, companyCode, branchCode, batchNo,
            sourcePlan.BalLocId, destPlan.BalLocId, cancellationToken);
        if (currentDraft != authority)
            throw InvalidWacTransfer("The WAC transfer draft changed before posting.");
        var currentTail = await ReadWacTransferInsertionTailAsync(write,
            authority, sourcePlan, destPlan, cancellationToken);
        if (currentTail.Error is not null || currentTail.Tail is null
            || currentTail.Projection is null
            || WacTransferInsertionFingerprint(sourcePlan, destPlan,
                authority, currentTail.Tail, currentTail.Projection)
                != fingerprint)
            throw InvalidWacTransfer("The WAC transfer tail changed before posting.");
        var physical = await StockReplayFifoTransferPhysicalWriter
            .AppendPreparedAsync(root, sourcePlan, destPlan,
                authority, batchNo, cancellationToken);
        var value = await AppendWacTransferOriginalValueAsync(root,
            authority, currentTail.Projection, physical.HistoryId,
            cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(root,
            [physical.HistoryId], cancellationToken);
        await VerifyPostedWacTransferAsync(root, sourcePlan, destPlan,
            authority, currentTail.Tail, physical.HistoryId, value,
            cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id)
            .Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private static async Task<(StockReplayWacFinancialTail? Tail,
        StockReplayWacResult? Projection, string? Error)>
        ReadWacTransferInsertionTailAsync(AppDbContext db,
        StockReplayFifoTransferDraftAuthority authority,
        StockReplayPhysicalPlan source, StockReplayPhysicalPlan destination,
        CancellationToken cancellationToken)
    {
        var key = source.Candidate.Key;
        if (source.CompanyCode != destination.CompanyCode
            || source.BranchCode != destination.BranchCode
            || source.BalLocId != authority.SourceBalLocId
            || destination.BalLocId != authority.DestinationBalLocId
            || source.LedgerEpochId != destination.LedgerEpochId
            || source.BranchWatermark != destination.BranchWatermark
            || source.Candidate.EventId != destination.Candidate.EventId
            || source.Candidate.Key != destination.Candidate.Key
            || key.EffectiveAt != authority.EffectiveAt
            || key.ClassPriority != StockReplayEventKey.OrdinaryClass
            || source.Candidate.SignedDelta != -authority.Quantity
            || destination.Candidate.SignedDelta != authority.Quantity)
            return (null, null, "The backdated WAC transfer has unmatched physical legs.");
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db,
            source.CompanyCode, source.BranchCode, authority.ItemCode,
            key, cancellationToken);
        var projection = StockReplayWacSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue,
            new StockReplayWacEvent(source.Candidate.EventId, key,
                0m, 0m, 0m, 0m), tail.LaterEvents);
        if (projection.HistoricalQuantityBeforeCandidate < authority.Quantity
            || projection.FinalQuantity != tail.CurrentQuantity
            || projection.FinalValue != tail.CurrentValue)
            return (null, null,
                "The WAC transfer does not preserve the item cost pool.");
        if (StockReplayFifoTransferTailGuard.ChangesPhysicalTail(source)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(destination))
            return (null, null,
                "A later physical movement changes under the WAC transfer.");
        if (await CheckWacTransferDescendantTailAsync(db,
                source.CompanyCode, source.BranchCode, authority.ItemCode,
                key.EffectiveAt, source.BalLocId, destination.BalLocId,
                source.LaterEvents, destination.LaterEvents,
                tail, projection.LaterSteps, cancellationToken) is string error)
            return (null, null, error);
        return (tail, projection, null);
    }

    private static string WacTransferInsertionFingerprint(
        StockReplayPhysicalPlan source, StockReplayPhysicalPlan destination,
        StockReplayFifoTransferDraftAuthority authority,
        StockReplayWacFinancialTail tail, StockReplayWacResult projection) =>
        StockPostingFingerprint.Create(new
        {
            source.PlanHash, DestinationHash = destination.PlanHash,
            authority, tail.CurrentQuantity, tail.CurrentValue,
            tail.LaterEvents,
            Piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                .Select(p => new { p.Key, p.Value,
                    Events = tail.PhysicalByPile[p.Key] }).ToArray(),
            projection
        }, new { }).RequestFingerprint;

    private static decimal WacTransferAmount(decimal quantity,
        decimal poolQuantity, decimal poolValue) =>
        quantity == poolQuantity ? poolValue
            : StockLedgerPrecision.Money(quantity
                * StockLedgerPrecision.Money(poolValue / poolQuantity));
}
