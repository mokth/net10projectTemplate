using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

/// <summary>A transaction-bound replay plan prepared for one Subcontract custody transfer.</summary>
public sealed class SubconWacTransferReplayPlan
{
    internal SubconWacTransferReplayPlan() { }
    internal AppDbContext Db { get; init; } = null!;
    internal object Transaction { get; init; } = null!;
    internal StockReplayPhysicalPlan Source { get; init; } = null!;
    internal StockReplayPhysicalPlan? Destination { get; init; }
    internal StockReplayFifoTransferDraftAuthority? Authority { get; init; }
    internal IvInventoryPostingService.NewDestTransferAuthority? NewDestinationAuthority { get; init; }
    internal StockReplayWacFinancialTail Tail { get; init; } = null!;
    internal string Fingerprint { get; init; } = null!;
}

internal sealed record SubconWacTransferReplayVerification(
    SubconWacTransferReplayPlan Plan, int HistoryId, decimal Amount, int? NewDestinationId = null);

public sealed partial class IvInventoryPostingService
{
    public async Task<(SubconWacTransferReplayPlan? Plan, string? Error)> PrepareSubconTransferReplayInTransactionAsync(
        AppDbContext db, string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("An existing transaction is required.");
        if (!_openPeriodBackdatedReplayEnabled || _coordinator is null)
            return (null, null);
        await new BranchStockTransactionLock().AcquireAsync(db, companyCode, branchCode, cancellationToken);
        if (!(await StockReplayInventoryRouteSelector.ReadAsync(db, companyCode, branchCode,
                batchNo, IvTrxTypes.StockTransfer, cancellationToken)).RequiresReplay)
            return (null, null);

        const string unsupported = "Backdated Subcontract replay currently supports one non-lot WAC transfer.";
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode && b.BatchNo == batchNo
            && b.TrxType == IvTrxTypes.StockTransfer && b.BatchStatus == IvBatchStatuses.New, cancellationToken);
        if (batch is null) return (null, unsupported);
        if (FuturePostDateError(batch) is string future) return (null, future);
        var details = await db.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1 || details[0].FromBalLocId is not > 0 || details[0].FrStdQty is not > 0m
            || string.IsNullOrWhiteSpace(details[0].ICode))
            return (null, unsupported);
        var detail = details[0];
        var state = await db.StockCostStates.AsNoTracking().SingleOrDefaultAsync(s =>
            s.CompanyCode == companyCode && s.BranchCode == branchCode && s.ItemCode == detail.ICode, cancellationToken);
        if (state?.CostMethod != StockCostMethods.MovingAverage)
            return (null, unsupported);
        var destinations = await db.IvBalLocs.AsNoTracking().Where(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode && b.ICode == detail.ICode
            && b.WhCode == detail.ToWarehouse && b.LocCode == (detail.ToLocation ?? string.Empty)
            && b.IStatus == detail.IStatus && b.LotId == null && (b.LotNo == null || b.LotNo == ""))
            .Select(b => b.Id).ToArrayAsync(cancellationToken);
        if (destinations.Length > 1 || destinations.Length == 1 && destinations[0] == detail.FromBalLocId.Value)
            return (null, unsupported);
        try
        {
            if (destinations.Length == 0)
            {
                var resolved = await ReadNewDestTransferAuthorityAsync(db, companyCode, branchCode, batchNo, cancellationToken);
                if (resolved.Error is not null || resolved.Authority is null || resolved.Authority.LotControl)
                    return (null, resolved.Error ?? unsupported);
                var newSource = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(db, companyCode, branchCode,
                    resolved.Authority.SourceBalLocId, resolved.Authority.EffectiveAt, -resolved.Authority.Quantity, cancellationToken);
                if (StockReplayFifoTransferTailGuard.ChangesPhysicalTail(newSource))
                    return (null, "A later physical movement changes under the backdated Subcontract transfer.");
                var tail = await ReadWacNewDestTransferTailAsync(db, resolved.Authority, newSource, cancellationToken);
                if (tail.Error is not null || tail.Tail is null || tail.Projection is null)
                    return (null, tail.Error ?? unsupported);
                return (new SubconWacTransferReplayPlan
                {
                    Db = db, Transaction = transaction, Source = newSource, NewDestinationAuthority = resolved.Authority,
                    Tail = tail.Tail, Fingerprint = NewDestWacTransferFingerprint(newSource, resolved.Authority,
                        tail.Tail, tail.Projection)
                }, null);
            }
            var (source, destination) = await StockReplayInventoryCandidatePlanner.BuildTransferAsync(
                db, companyCode, branchCode, detail.FromBalLocId.Value, destinations[0],
                batch.TrxDtTime, detail.FrStdQty.Value, cancellationToken);
            var authority = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(db,
                companyCode, branchCode, batchNo, source.BalLocId, destination.BalLocId, cancellationToken);
            var prepared = await ReadWacTransferInsertionTailAsync(db, authority, source, destination,
                cancellationToken, allowIndependentPiles: true);
            if (prepared.Error is not null || prepared.Tail is null || prepared.Projection is null)
                return (null, prepared.Error ?? unsupported);
            return (new SubconWacTransferReplayPlan
            {
                Db = db, Transaction = transaction, Source = source, Destination = destination,
                Authority = authority, Tail = prepared.Tail,
                Fingerprint = WacTransferInsertionFingerprint(source, destination, authority,
                    prepared.Tail, prepared.Projection)
            }, null);
        }
        catch (StockLedgerException ex) { return (null, ex.Error.Message); }
        catch (ArgumentException) { return (null, unsupported); }
    }

    private async Task<IvInventoryPostingBatchResult> PostPreparedSubconWacTransferAsync(
        StockPostingContext root, AppDbContext db, int batchNo,
        SubconWacTransferReplayPlan plan, CancellationToken cancellationToken)
    {
        if (plan.NewDestinationAuthority is not null)
            return await PostPreparedSubconWacNewDestinationAsync(root, db, batchNo, plan, cancellationToken);
        var authority = plan.Authority ?? throw InvalidWacTransfer("The Subcontract transfer has no prepared draft.");
        var destination = plan.Destination ?? throw InvalidWacTransfer("The Subcontract transfer has no prepared destination.");
        if (!ReferenceEquals(plan.Db, db) || !ReferenceEquals(plan.Transaction, db.Database.CurrentTransaction)
            || !ReferenceEquals(root.Db, db) || root.CompanyCode != plan.Source.CompanyCode
            || root.BranchCode != plan.Source.BranchCode || root.Epoch.Id != plan.Source.LedgerEpochId
            || root.CostMethod != StockCostMethods.MovingAverage || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || root.Posting.SourceDocumentId != authority.BatchId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.SourceDocumentNo != batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.DocumentRevision != authority.DocumentRevision || root.Posting.PostingRole != "PRIMARY"
            || root.Posting.EffectiveAt != authority.EffectiveAt
            || root.Posting.PostingSequence != plan.Source.Candidate.Key.OriginalPostingSequence)
            throw InvalidWacTransfer("The Subcontract transfer root differs from its prepared draft.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, root.CompanyCode, root.BranchCode,
                authority.EffectiveAt, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        var pileError = await LockQaTransferPilesAsync(db, root.CompanyCode, root.BranchCode,
            plan.Source, destination, cancellationToken);
        if (pileError is not null) return IvInventoryPostingBatchResult.Fail(batchNo, pileError);
        var details = await db.IvTrxBatchDetails.Where(d => d.BatchId == authority.BatchId).ToListAsync(cancellationToken);
        var qaError = await AssertQaPhysicalFromDetailsAsync(db, root.CompanyCode, root.BranchCode,
            IvTrxTypes.StockTransfer, batchNo, details, cancellationToken, isAuthorizedSubconCaller: true);
        if (qaError is not null) return IvInventoryPostingBatchResult.Fail(batchNo, qaError);
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root, plan.Source, cancellationToken);
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root, destination, cancellationToken);
        var currentAuthority = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(db,
            root.CompanyCode, root.BranchCode, batchNo, plan.Source.BalLocId, destination.BalLocId, cancellationToken);
        var current = await ReadWacTransferInsertionTailAsync(db, currentAuthority, plan.Source, destination,
            cancellationToken, allowIndependentPiles: true);
        if (currentAuthority != authority || current.Error is not null || current.Tail is null || current.Projection is null
            || WacTransferInsertionFingerprint(plan.Source, destination, currentAuthority, current.Tail, current.Projection) != plan.Fingerprint)
            throw InvalidWacTransfer("The Subcontract transfer or its later history changed before posting.");
        var physical = await StockReplayFifoTransferPhysicalWriter.AppendPreparedAsync(root,
            plan.Source, destination, authority, batchNo, cancellationToken);
        var amount = await AppendWacTransferOriginalValueAsync(root, authority, current.Projection, physical.HistoryId, cancellationToken);
        var batch = await db.IvTrxBatches.SingleAsync(b => b.Id == authority.BatchId, cancellationToken);
        return new IvInventoryPostingBatchResult
        {
            Succeeded = true, BatchNo = batchNo, OperationId = batch.PostingOperationId,
            ReplayActiveHistoryIds = [physical.HistoryId],
            SubconReplayVerification = new(plan, physical.HistoryId, amount)
        };
    }

    private async Task<IvInventoryPostingBatchResult> PostPreparedSubconWacNewDestinationAsync(
        StockPostingContext root, AppDbContext db, int batchNo,
        SubconWacTransferReplayPlan plan, CancellationToken cancellationToken)
    {
        var authority = plan.NewDestinationAuthority!;
        if (!ReferenceEquals(plan.Db, db) || !ReferenceEquals(plan.Transaction, db.Database.CurrentTransaction)
            || !ReferenceEquals(root.Db, db) || root.CompanyCode != plan.Source.CompanyCode
            || root.BranchCode != plan.Source.BranchCode || root.Epoch.Id != plan.Source.LedgerEpochId
            || root.CostMethod != StockCostMethods.MovingAverage || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || root.Posting.SourceDocumentId != authority.BatchId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.SourceDocumentNo != batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.DocumentRevision != authority.DocumentRevision || root.Posting.PostingRole != "PRIMARY"
            || root.Posting.EffectiveAt != authority.EffectiveAt || authority.LotControl
            || root.Posting.PostingSequence != plan.Source.Candidate.Key.OriginalPostingSequence)
            throw InvalidWacTransfer("The Subcontract transfer root differs from its prepared draft.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, root.CompanyCode, root.BranchCode,
                authority.EffectiveAt, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        var pileError = await LockQaSinglePileAsync(db, root.CompanyCode, root.BranchCode, plan.Source, cancellationToken);
        if (pileError is not null) return IvInventoryPostingBatchResult.Fail(batchNo, pileError);
        var details = await db.IvTrxBatchDetails.Where(d => d.BatchId == authority.BatchId).ToListAsync(cancellationToken);
        var qaError = await AssertQaPhysicalFromDetailsAsync(db, root.CompanyCode, root.BranchCode,
            IvTrxTypes.StockTransfer, batchNo, details, cancellationToken, isAuthorizedSubconCaller: true);
        if (qaError is not null) return IvInventoryPostingBatchResult.Fail(batchNo, qaError);
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root, plan.Source, cancellationToken);
        var locked = await ReadNewDestTransferAuthorityAsync(db, root.CompanyCode, root.BranchCode, batchNo, cancellationToken);
        if (locked.Error is not null || locked.Authority != authority)
            throw InvalidWacTransfer("The Subcontract transfer destination or draft changed before posting.");
        var current = await ReadWacNewDestTransferTailAsync(db, authority, plan.Source, cancellationToken);
        if (current.Error is not null || current.Tail is null || current.Projection is null
            || NewDestWacTransferFingerprint(plan.Source, authority, current.Tail, current.Projection) != plan.Fingerprint)
            throw InvalidWacTransfer("The Subcontract transfer or its later history changed before posting.");
        var written = await WriteNewDestTransferPhysicalAsync(root, plan.Source, authority, cancellationToken);
        if (written.Error is not null) return IvInventoryPostingBatchResult.Fail(batchNo, written.Error);
        var amount = await AppendNewDestWacTransferValueAsync(root, authority, current.Projection,
            written.HistoryId, written.DestinationBalLocId, written.DestinationLotId, cancellationToken);
        var batch = await db.IvTrxBatches.SingleAsync(b => b.Id == authority.BatchId, cancellationToken);
        return new IvInventoryPostingBatchResult
        {
            Succeeded = true, BatchNo = batchNo, OperationId = batch.PostingOperationId,
            ReplayActiveHistoryIds = [written.HistoryId],
            SubconReplayVerification = new(plan, written.HistoryId, amount, written.DestinationBalLocId)
        };
    }

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
        if (await read.IvStockMasters.AsNoTracking().AnyAsync(m =>
                m.CompanyCode == companyCode && m.ICode == itemCode
                && m.LotControl, cancellationToken))
            return await TryPostBackdatedNewDestinationTrAsync(
                companyCode, branchCode, batchNo, StockCostMethods.MovingAverage,
                cancellationToken);
        var destinations = await read.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.ICode == itemCode
                && b.WhCode == detail.ToWarehouse
                && b.LocCode == (detail.ToLocation ?? string.Empty)
                && b.IStatus == detail.IStatus && b.LotId == null
                && (b.LotNo == null || b.LotNo == ""))
            .Select(b => b.Id).ToArrayAsync(cancellationToken);
        if (destinations.Length == 0)
            return await TryPostBackdatedNewDestinationTrAsync(
                companyCode, branchCode, batchNo, StockCostMethods.MovingAverage,
                cancellationToken);
        if (destinations.Length != 1
            || destinations[0] == detail.FromBalLocId.Value)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated WAC transfer needs a distinct existing non-lot destination pile.");
        var quantity = detail.FrStdQty.Value;
        var (sourcePlan, destPlan) = await StockReplayInventoryCandidatePlanner
            .BuildTransferAsync(read, companyCode, branchCode,
                detail.FromBalLocId.Value, destinations[0], batch.TrxDtTime,
                quantity, cancellationToken);
        var key = sourcePlan.Candidate.Key;
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
            || root.Epoch.Id != sourcePlan.LedgerEpochId)
            throw InvalidWacTransfer("The WAC transfer root differs from its draft.");
        if (root.Posting.PostingSequence != key.OriginalPostingSequence)
            throw new StockLedgerException(new StockLedgerError(
                StockLedgerErrorCodes.StaleDocument,
                "The branch posting sequence changed after replay planning.",
                Retryable: true) { IsBranchWatermarkDrift = true });
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        var pileError = await LockQaTransferPilesAsync(write, companyCode, branchCode,
            sourcePlan, destPlan, cancellationToken);
        if (pileError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, pileError);
        }
        var writeDetails = await write.IvTrxBatchDetails
            .Where(d => d.BatchId == batch.Id)
            .ToListAsync(cancellationToken);
        var qaError = await AssertQaPhysicalFromDetailsAsync(
            write, companyCode, branchCode, IvTrxTypes.StockTransfer, batchNo, writeDetails, cancellationToken);
        if (qaError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, qaError);
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
        CancellationToken cancellationToken, bool allowIndependentPiles = false)
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
        // For an authorized Subcontract transfer, an untouched destination can
        // coexist with unchanged later movements on another item pile. Check its
        // empty tail explicitly before applying the existing independent-pile guard.
        var destinationForGuard = (int?)destination.BalLocId;
        if (allowIndependentPiles && destination.LaterEvents.Count == 0
            && tail.PhysicalByPile.TryGetValue(destination.BalLocId, out var destinationFinancial)
            && destinationFinancial.Count == 0)
            destinationForGuard = null;
        if (await CheckWacTransferDescendantTailAsync(db,
                source.CompanyCode, source.BranchCode, authority.ItemCode,
                key.EffectiveAt, source.BalLocId, destinationForGuard,
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
