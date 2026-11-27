using System.Globalization;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    internal sealed record NewDestTransferAuthority(
        int BatchId, int BatchNo, int DetailId, int DocumentRevision,
        string ItemCode, int SourceBalLocId, bool LotControl,
        int? SourceLotId, string SourceLotNo, string DestinationLotNo,
        string SourceWarehouse, string SourceLocation,
        string DestinationWarehouse, string DestinationLocation,
        string ItemStatus, DateTime EffectiveAt, decimal Quantity,
        string BaseUom, string? Currency);

    /// <summary>
    /// Posts a backdated 1-line transfer whose destination pile does not yet
    /// exist (both non-lot new piles and lot-controlled new destination lot +
    /// pile) across Moving Average, FIFO, and Standard costing.
    /// </summary>
    private async Task<IvInventoryPostingBatchResult> TryPostBackdatedNewDestinationTrAsync(
        string companyCode, string branchCode, int batchNo, string costMethod,
        CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var readAuthority = await ReadNewDestTransferAuthorityAsync(
            read, companyCode, branchCode, batchNo, cancellationToken);
        if (readAuthority.Error is string authorityError)
            return IvInventoryPostingBatchResult.Fail(batchNo, authorityError);
        var authority = readAuthority.Authority!;

        var sourcePlan = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(
            read, companyCode, branchCode, authority.SourceBalLocId,
            authority.EffectiveAt, -authority.Quantity, cancellationToken);
        if (StockReplayFifoTransferTailGuard.ChangesPhysicalTail(sourcePlan))
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "A later physical movement changes under the backdated transfer.");

        var key = sourcePlan.Candidate.Key;
        StockReplayWacFinancialTail? wacTail = null;
        StockReplayWacResult? wacProjection = null;
        StockReplayFifoActiveLayerSnapshot? fifoSnapshot = null;
        StockReplayFifoTailProjection? fifoProjection = null;
        StockReplayStandardFinancialTail? standardTail = null;
        StockReplayStandardResult? standardProjection = null;
        EffectiveStandardCost? standardRevision = null;
        string fingerprint;

        if (costMethod == StockCostMethods.MovingAverage)
        {
            var prepared = await ReadWacNewDestTransferTailAsync(
                read, authority, sourcePlan, cancellationToken);
            if (prepared.Error is string error)
                return IvInventoryPostingBatchResult.Fail(batchNo, error);
            wacTail = prepared.Tail!;
            wacProjection = prepared.Projection!;
            fingerprint = NewDestWacTransferFingerprint(
                sourcePlan, authority, wacTail, wacProjection);
        }
        else if (costMethod == StockCostMethods.Fifo)
        {
            var prepared = await ReadFifoNewDestTransferTailAsync(
                read, authority, sourcePlan, cancellationToken);
            if (prepared.Error is string error)
                return IvInventoryPostingBatchResult.Fail(batchNo, error);
            fifoSnapshot = prepared.Snapshot!;
            fifoProjection = prepared.Projection!;
            fingerprint = NewDestFifoTransferFingerprint(
                sourcePlan, authority, fifoSnapshot, fifoProjection);
        }
        else if (costMethod == StockCostMethods.Standard)
        {
            var prepared = await ReadStandardNewDestTransferTailAsync(
                read, authority, sourcePlan, cancellationToken);
            if (prepared.Error is string error)
                return IvInventoryPostingBatchResult.Fail(batchNo, error);
            standardTail = prepared.Tail!;
            standardProjection = prepared.Projection!;
            standardRevision = prepared.Revision!;
            fingerprint = NewDestStandardTransferFingerprint(
                sourcePlan, authority, standardTail, standardProjection, standardRevision);
        }
        else
        {
            return IvInventoryPostingBatchResult.Fail(batchNo,
                $"Backdated transfer to a new destination pile is not supported for cost method '{costMethod}'.");
        }

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
        if (root is not null && costMethod is StockCostMethods.Standard or StockCostMethods.Fifo)
            root.CostMethod = costMethod;
        if (root is null || root.CostMethod != costMethod
            || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || root.Posting.SourceDocumentId
                != authority.BatchId.ToString(CultureInfo.InvariantCulture)
            || root.Posting.SourceDocumentNo
                != batchNo.ToString(CultureInfo.InvariantCulture)
            || root.Posting.DocumentRevision != authority.DocumentRevision
            || root.Posting.PostingRole != "PRIMARY"
            || root.Posting.EffectiveAt != authority.EffectiveAt
            || root.Epoch.Id != sourcePlan.LedgerEpochId)
            throw InvalidNewDestTransfer("The backdated transfer root differs from its draft.");
        if (root.Posting.PostingSequence != key.OriginalPostingSequence)
            throw new StockLedgerException(new StockLedgerError(
                StockLedgerErrorCodes.StaleDocument,
                "The branch posting sequence changed after replay planning.",
                Retryable: true) { IsBranchWatermarkDrift = true });

        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, authority.EffectiveAt, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }

        var pileError = await LockQaSinglePileAsync(write, companyCode, branchCode,
            sourcePlan, cancellationToken);
        if (pileError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, pileError);
        }
        var writeDetails = await write.IvTrxBatchDetails
            .Where(d => d.BatchId == authority.BatchId)
            .ToListAsync(cancellationToken);
        var qaError = await AssertQaPhysicalFromDetailsAsync(
            write, companyCode, branchCode, IvTrxTypes.StockTransfer,
            batchNo, writeDetails, cancellationToken);
        if (qaError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, qaError);
        }

        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            root, sourcePlan, cancellationToken);
        var lockedAuthority = await ReadNewDestTransferAuthorityAsync(
            write, companyCode, branchCode, batchNo, cancellationToken);
        if (lockedAuthority.Error is string lockedErr)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedErr);
        }
        if (lockedAuthority.Authority != authority)
            throw InvalidNewDestTransfer("The backdated transfer draft changed before posting.");

        if (costMethod == StockCostMethods.MovingAverage)
        {
            var current = await ReadWacNewDestTransferTailAsync(
                write, authority, sourcePlan, cancellationToken);
            if (current.Error is not null || current.Tail is null || current.Projection is null
                || NewDestWacTransferFingerprint(sourcePlan, authority, current.Tail, current.Projection) != fingerprint)
                throw InvalidNewDestTransfer("The WAC transfer tail changed before posting.");
            wacTail = current.Tail;
            wacProjection = current.Projection;
        }
        else if (costMethod == StockCostMethods.Fifo)
        {
            var first = sourcePlan.Candidate.Key.EffectiveAt.Date;
            var last = sourcePlan.LaterEvents.Select(e => e.Key.EffectiveAt.Date).Append(first).Max();
            await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
                root, first, last, cancellationToken);
            var current = await ReadFifoNewDestTransferTailAsync(
                write, authority, sourcePlan, cancellationToken);
            if (current.Error is not null || current.Snapshot is null || current.Projection is null
                || !StockReplayFifoActiveLayerReader.SameSnapshot(fifoSnapshot!, current.Snapshot)
                || NewDestFifoTransferFingerprint(sourcePlan, authority, current.Snapshot, current.Projection) != fingerprint)
                throw InvalidNewDestTransfer("The FIFO transfer tail changed before posting.");
            fifoSnapshot = current.Snapshot;
            fifoProjection = current.Projection;
        }
        else
        {
            var current = await ReadStandardNewDestTransferTailAsync(
                write, authority, sourcePlan, cancellationToken);
            if (current.Error is not null || current.Tail is null
                || current.Projection is null || current.Revision is null
                || NewDestStandardTransferFingerprint(sourcePlan, authority, current.Tail,
                    current.Projection, current.Revision) != fingerprint)
                throw InvalidNewDestTransfer("The Standard transfer tail changed before posting.");
            standardTail = current.Tail;
            standardProjection = current.Projection;
            standardRevision = current.Revision;
        }

        var written = await WriteNewDestTransferPhysicalAsync(
            root, sourcePlan, authority, cancellationToken);
        if (written.Error is string writeErr)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, writeErr);
        }

        if (costMethod == StockCostMethods.MovingAverage)
        {
            var amount = await AppendNewDestWacTransferValueAsync(
                root, authority, wacProjection!, written.HistoryId,
                written.DestinationBalLocId, written.DestinationLotId,
                cancellationToken);
            await _coordinator!.CompleteReplayInTransactionAsync(
                root, [written.HistoryId], cancellationToken);
            await VerifyNewDestPostedWacOrStandardTransferAsync(
                root, sourcePlan, written.DestinationBalLocId, authority,
                StockCostMethods.MovingAverage,
                wacTail!.CurrentQuantity, wacTail.CurrentValue,
                written.HistoryId, amount, cancellationToken);
        }
        else if (costMethod == StockCostMethods.Standard)
        {
            var amount = await AppendNewDestStandardTransferValueAsync(
                root, authority, standardRevision!, written.HistoryId,
                written.DestinationBalLocId, written.DestinationLotId,
                cancellationToken);
            await _coordinator!.CompleteReplayInTransactionAsync(
                root, [written.HistoryId], cancellationToken);
            await VerifyNewDestPostedWacOrStandardTransferAsync(
                root, sourcePlan, written.DestinationBalLocId, authority,
                StockCostMethods.Standard,
                standardTail!.CurrentQuantity, standardTail.CurrentValue,
                written.HistoryId, amount, cancellationToken);
        }
        else
        {
            await AppendNewDestFifoTransferValueAsync(
                root, sourcePlan, authority, fifoSnapshot!, fifoProjection!,
                written.HistoryId, written.DestinationBalLocId,
                written.DestinationLotId, cancellationToken);
            await _coordinator!.CompleteReplayInTransactionAsync(
                root, [written.HistoryId], cancellationToken);
            await VerifyNewDestPostedFifoTransferAsync(
                root, sourcePlan, written.DestinationBalLocId, authority,
                fifoProjection!, written.HistoryId, cancellationToken);
        }

        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == authority.BatchId)
            .Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private async Task<(NewDestTransferAuthority? Authority, string? Error)>
        ReadNewDestTransferAuthorityAsync(
            AppDbContext db, string companyCode, string branchCode,
            int batchNo, CancellationToken cancellationToken)
    {
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.BatchNo == batchNo)
            .ToArrayAsync(cancellationToken);
        if (batches.Length != 1)
            return (null, "Backdated transfer has no unique draft batch.");
        var batch = batches[0];
        if (batch.TrxType != IvTrxTypes.StockTransfer
            || batch.BatchStatus != IvBatchStatuses.New
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null)
            return (null, "Backdated transfer needs an active NEW batch.");
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1)
            return (null, "Backdated transfer replay currently needs one draft line.");
        var detail = details[0];
        if (detail.CompanyCode != companyCode
            || detail.BranchCode != branchCode
            || detail.BatchNo != batchNo
            || detail.TrxType != IvTrxTypes.StockTransfer
            || detail.DocumentRevision < 0
            || detail.TrxLineNo <= 0
            || string.IsNullOrWhiteSpace(detail.ICode)
            || detail.FromBalLocId is not int sourceBalLocId || sourceBalLocId <= 0
            || detail.ToBalLocId is not null
            || detail.FromLotId is not null
            || detail.ToLotId is not null
            || detail.FrStdQty is not decimal quantity
            || quantity <= 0m || IvQty.Round(quantity) != quantity
            || detail.ToStdQty != quantity
            || string.IsNullOrWhiteSpace(detail.FrStdUom)
            || !string.Equals(detail.FrStdUom, detail.ToStdUom, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(detail.IStatus))
            return (null, "The draft transfer is not one supported single-line movement.");

        var itemCode = detail.ICode.Trim();
        var master = await db.IvStockMasters.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CompanyCode == companyCode
                && m.ICode == itemCode, cancellationToken);
        if (master is null || !master.IsActive || !master.StockControl)
            return (null, $"Item '{itemCode}' is missing, inactive, or not stock-controlled.");

        var source = await db.IvBalLocs.AsNoTracking()
            .SingleOrDefaultAsync(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.Id == sourceBalLocId,
                cancellationToken);
        if (source is null
            || !string.Equals(source.ICode, itemCode, StringComparison.OrdinalIgnoreCase)
            || !SameWacTransferSlicePart(source.WhCode, detail.FrWarehouse)
            || !SameWacTransferSlicePart(source.LocCode, detail.FrLocation)
            || !SameWacTransferSlicePart(source.IStatus, detail.IStatus)
            || !string.Equals(source.StdUom?.Trim(), detail.FrStdUom.Trim(), StringComparison.OrdinalIgnoreCase))
            return (null, "The draft transfer no longer matches its source pile.");

        var toWh = (detail.ToWarehouse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toWh))
            return (null, "Destination warehouse is required.");
        if (await _common.GetActiveWarehouseAsync(db, companyCode, branchCode, toWh, cancellationToken) is null)
            return (null, $"Warehouse '{toWh}' was not found for this branch.");
        var toLocInput = (detail.ToLocation ?? string.Empty).Trim();
        var locValidation = await IvInventoryLocationValidation.ValidateDestinationAsync(
            db, _common, companyCode, branchCode, toWh, toLocInput,
            $"Line {detail.TrxLineNo}", "location", cancellationToken);
        if (locValidation.Error is not null)
            return (null, locValidation.Error);
        var toLoc = locValidation.Location;

        var iStatus = detail.IStatus.Trim();
        if (!await db.IvStatuses.AsNoTracking().AnyAsync(s =>
                s.CompanyCode == companyCode && s.IStatus == iStatus && s.IsActive,
                cancellationToken))
            return (null, $"Item status '{iStatus}' is missing or inactive.");

        var frLot = (detail.FrLotNo ?? string.Empty).Trim();
        var toLot = (detail.ToLotNo ?? string.Empty).Trim();
        if (master.LotControl)
        {
            if (string.IsNullOrWhiteSpace(frLot))
                return (null, $"Line {detail.TrxLineNo}: source lot number is required.");
            if (string.IsNullOrWhiteSpace(toLot))
                return (null, $"Line {detail.TrxLineNo}: destination lot number is required.");
            if (string.Equals(frLot, toLot, StringComparison.OrdinalIgnoreCase))
                return (null, $"Line {detail.TrxLineNo}: destination lot must differ from source lot.");
            if (source.LotId is not > 0 || !SameWacTransferSlicePart(source.LotNo, frLot))
                return (null, "The source pile does not match the lot-controlled transfer line.");
            var sourceLot = await db.IvLots.AsNoTracking()
                .SingleOrDefaultAsync(l => l.Id == source.LotId.Value
                    && l.CompanyCode == companyCode && l.ICode == itemCode,
                    cancellationToken);
            if (sourceLot is null || !sourceLot.IsActive
                || !SameWacTransferSlicePart(sourceLot.LotNo, frLot))
                return (null, "The source lot is missing or inactive.");
            if (await db.IvLots.AsNoTracking().AnyAsync(l =>
                    l.CompanyCode == companyCode && l.ICode == itemCode
                    && l.LotNo == toLot, cancellationToken))
                return (null, $"Destination lot '{toLot}' already exists for item '{itemCode}'.");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(frLot) || !string.IsNullOrWhiteSpace(toLot)
                || source.LotId is not null || !string.IsNullOrWhiteSpace(source.LotNo))
                return (null, $"Line {detail.TrxLineNo}: lot number is not allowed for non-lot-controlled item.");
            frLot = string.Empty;
            toLot = string.Empty;
        }

        var fromSlice = IvStockSliceKey.Create(
            companyCode, branchCode, itemCode, source.WhCode, source.LocCode, frLot, iStatus);
        var toSlice = IvStockSliceKey.Create(
            companyCode, branchCode, itemCode, toWh, toLoc, toLot, iStatus);
        if (fromSlice.Equals(toSlice))
            return (null, $"Line {detail.TrxLineNo}: source and destination stock slice must be different.");

        var existingDest = await db.IvBalLocs.AsNoTracking()
            .AnyAsync(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && b.ICode == itemCode
                && b.WhCode == toWh
                && b.LocCode == toLoc
                && b.IStatus == iStatus
                && (master.LotControl
                    ? b.LotNo == toLot
                    : b.LotId == null && (b.LotNo == null || b.LotNo == "")),
                cancellationToken);
        if (existingDest)
            return (null, "The destination pile already exists; retry under the existing-pile transfer route.");

        return (new NewDestTransferAuthority(
            batch.Id, batchNo, detail.Id, detail.DocumentRevision,
            itemCode, sourceBalLocId, master.LotControl,
            source.LotId, frLot, toLot,
            source.WhCode, source.LocCode, toWh, toLoc,
            iStatus, batch.TrxDtTime, quantity,
            source.StdUom!, detail.Currency), null);
    }

    private static async Task<(StockReplayWacFinancialTail? Tail,
        StockReplayWacResult? Projection, string? Error)>
        ReadWacNewDestTransferTailAsync(
            AppDbContext db, NewDestTransferAuthority authority,
            StockReplayPhysicalPlan source, CancellationToken cancellationToken)
    {
        var key = source.Candidate.Key;
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
            return (null, null, "The WAC transfer does not preserve the item cost pool.");
        if (await CheckWacTransferDescendantTailAsync(db,
                source.CompanyCode, source.BranchCode, authority.ItemCode,
                key.EffectiveAt, source.BalLocId, destinationPileId: null,
                source.LaterEvents, destinationLater: [],
                tail, projection.LaterSteps, cancellationToken) is string error)
            return (null, null, error);
        return (tail, projection, null);
    }

    private static async Task<(StockReplayFifoActiveLayerSnapshot? Snapshot,
        StockReplayFifoTailProjection? Projection, string? Error)>
        ReadFifoNewDestTransferTailAsync(
            AppDbContext db, NewDestTransferAuthority authority,
            StockReplayPhysicalPlan source, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(authority.Currency))
            return (null, null, "Backdated FIFO transfer requires company-base valuation.");
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            source.CompanyCode, source.BranchCode, authority.ItemCode, cancellationToken);
        var projection = StockReplayFifoTailProjector.InsertTransfer(
            snapshot, source.Candidate.Key, authority.Quantity);
        var candidate = projection.Issues.SingleOrDefault(i => i.IsCandidate);
        if (candidate is null || candidate.DepletesPool
            || candidate.Quantity != authority.Quantity
            || projection.FinalQuantity != snapshot.CurrentQuantity
            || projection.FinalValue != snapshot.CurrentValue
            || StockReplayFifoMiRootWriter.ChangedIssues(projection).Count != 0)
            return (null, null, "The FIFO transfer changes the item pool or a later issue.");
        if (await CheckSimpleFifoTransferTailAsync(db,
                source.CompanyCode, source.BranchCode, authority.ItemCode,
                source.Candidate.Key, source.BalLocId, destinationPileId: null,
                source.LaterEvents, destinationLater: [],
                cancellationToken) is string unsupported)
            return (null, null, unsupported);
        return (snapshot, projection, null);
    }

    private static async Task<(StockReplayStandardFinancialTail? Tail,
        StockReplayStandardResult? Projection, EffectiveStandardCost? Revision,
        string? Error)> ReadStandardNewDestTransferTailAsync(
            AppDbContext db, NewDestTransferAuthority authority,
            StockReplayPhysicalPlan source, CancellationToken cancellationToken)
    {
        var key = source.Candidate.Key;
        var revision = await new ItemStandardCostResolver().ResolveAsync(db,
            source.CompanyCode, source.BranchCode, authority.ItemCode,
            key.EffectiveAt, cancellationToken);
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(db,
            source.CompanyCode, source.BranchCode, authority.ItemCode,
            key, cancellationToken);
        var candidate = new StockReplayStandardEvent(source.Candidate.EventId,
            key, StockReplayStandardEventKinds.Movement,
            0m, 0m, 0m, revision.TotalStandardCost, revision.Id);
        await StockReplayStandardRevisionAuthority.ValidateAsync(db,
            source.CompanyCode, source.BranchCode, authority.ItemCode,
            candidate, tail.LaterEvents, cancellationToken);
        var projection = StockReplayStandardSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue,
            candidate, tail.LaterEvents);
        if (projection.HistoricalQuantityBeforeCandidate < authority.Quantity
            || projection.FinalQuantity != tail.CurrentQuantity
            || projection.FinalValue != tail.CurrentValue
            || tail.LaterEvents.Count != projection.LaterSteps.Count
            || tail.LaterEvents.Zip(projection.LaterSteps).Any(p =>
                p.First.OldSignedQuantity != p.Second.SignedQuantity
                || p.First.OldSignedValue != p.Second.SignedValue))
            return (null, null, null, "The Standard transfer changes a later quantity or value.");
        if (await CheckStandardTransferDescendantsAsync(db,
                source.CompanyCode, source.BranchCode, authority.ItemCode,
                key, tail.LaterEvents, cancellationToken) is string error)
            return (null, null, null, error);
        return (tail, projection, revision, null);
    }

    private async Task<(int HistoryId, int DestinationBalLocId, int? DestinationLotId, string? Error)>
        WriteNewDestTransferPhysicalAsync(
            StockPostingContext root, StockReplayPhysicalPlan sourcePlan,
            NewDestTransferAuthority authority, CancellationToken cancellationToken)
    {
        root.EnsureUnsealed();
        var db = root.Db;
        var batch = await db.IvTrxBatches.SingleAsync(
            b => b.Id == authority.BatchId, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(
            d => d.Id == authority.DetailId, cancellationToken);
        var source = await db.IvBalLocs.SingleAsync(b =>
            b.CompanyCode == root.CompanyCode
            && b.BranchCode == root.BranchCode
            && b.Id == authority.SourceBalLocId, cancellationToken);
        if (db.Entry(batch).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || db.Entry(source).State != EntityState.Unchanged
            || source.StdQty != sourcePlan.CurrentQuantity
            || !source.RowVersion.SequenceEqual(sourcePlan.BalanceRowVersion)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == root.CompanyCode
                && h.BranchCode == root.BranchCode
                && (h.BatchNo == authority.BatchNo
                    && h.DocumentRevision == authority.DocumentRevision
                    || h.StockPostingId == root.Posting.Id),
                cancellationToken))
            throw InvalidNewDestTransfer("The transfer changed before its physical write.");

        var now = DateTime.UtcNow;
        var user = Truncate(root.UserId, 10);

        IvLot? destLot = null;
        if (authority.LotControl)
        {
            var existingLot = await _posting.TryLockLotAsync(
                db, root.CompanyCode, authority.ItemCode,
                authority.DestinationLotNo, cancellationToken);
            if (existingLot is not null)
                return (0, 0, null,
                    $"Destination lot '{authority.DestinationLotNo}' already exists for item '{authority.ItemCode}'.");
            destLot = _posting.StageNewTransferLot(
                db, root.CompanyCode, authority.ItemCode,
                authority.DestinationLotNo, IvTrxTypes.StockTransfer,
                authority.BatchNo.ToString(CultureInfo.InvariantCulture),
                authority.EffectiveAt.Date, user);
            await db.SaveChangesAsync(cancellationToken);
        }

        var toSlice = IvStockSliceKey.Create(
            root.CompanyCode, root.BranchCode, authority.ItemCode,
            authority.DestinationWarehouse, authority.DestinationLocation,
            authority.LotControl ? authority.DestinationLotNo : string.Empty,
            authority.ItemStatus);
        var destination = await _posting.FindOrCreateBalLocAsync(
            db, toSlice, lotId: destLot?.Id, authority.BaseUom, root.UserId,
            authority.EffectiveAt, batch.LocationCode, cancellationToken);
        if (destination.StdQty != 0m)
            throw InvalidNewDestTransfer("The destination pile appeared with stock before posting.");
        if (authority.LotControl && destLot is not null && destination.LotId is null)
            destination.Lot = destLot;

        ApplyDestCostFromSource(destination, source.Cost, source.UnitPrice);
        destination.PriceEvidence = source.PriceEvidence;
        destination.StdQty = authority.Quantity;
        destination.TransDate = authority.EffectiveAt;
        destination.ModifiedDate = now;
        if (string.IsNullOrWhiteSpace(destination.StdUom))
            destination.StdUom = Truncate(authority.BaseUom, 10);

        source.StdQty = sourcePlan.Projection.FinalQuantity;
        source.TransDate = sourcePlan.LaterEvents.Count == 0
            ? authority.EffectiveAt
            : sourcePlan.LaterEvents.Max(e => e.Key.EffectiveAt);
        source.ModifiedDate = now;

        // Materialize database-generated IDs for the new destination balance (and lot)
        // inside the posting transaction before binding detail, history, and facts.
        await db.SaveChangesAsync(cancellationToken);

        detail.FromLotId = source.LotId;
        detail.ToBalLocId = destination.Id;
        detail.ToLotId = authority.LotControl ? destination.LotId : null;
        detail.FrWarehouse = source.WhCode;
        detail.FrLocation = source.LocCode;
        detail.FrLotNo = authority.LotControl ? source.LotNo : string.Empty;
        detail.ToWarehouse = destination.WhCode;
        detail.ToLocation = destination.LocCode;
        detail.ToLotNo = authority.LotControl ? destination.LotNo : string.Empty;
        detail.PriceEvidence = source.PriceEvidence;

        var history = new IvTrxHistory
        {
            CompanyCode = root.CompanyCode,
            BranchCode = root.BranchCode,
            BatchNo = authority.BatchNo,
            TrxLineNo = detail.TrxLineNo,
            TrxType = IvTrxTypes.StockTransfer,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode,
            ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = authority.ItemCode,
            IDesc = detail.IDesc,
            FrWarehouse = source.WhCode,
            FrLocation = source.LocCode,
            FrLotNo = authority.LotControl ? source.LotNo : string.Empty,
            FrStdQty = authority.Quantity,
            FrStdUom = authority.BaseUom,
            ToWarehouse = destination.WhCode,
            ToLocation = destination.LocCode,
            ToLotNo = authority.LotControl ? destination.LotNo : string.Empty,
            ToStdQty = authority.Quantity,
            ToStdUom = authority.BaseUom,
            IStatus = authority.ItemStatus,
            Remarks = detail.Remarks,
            PriceEvidence = source.PriceEvidence,
            UnitPrice = source.UnitPrice,
            Cost = source.Cost,
            CostPrice = detail.CostPrice,
            BaseUnitPrices = detail.BaseUnitPrices,
            LocationCode = detail.LocationCode,
            FromBalLocId = source.Id,
            FromLotId = source.LotId,
            ToBalLocId = destination.Id,
            ToLotId = authority.LotControl ? destination.LotId : null,
            CreatedDate = now,
            CreatedBy = user
        };
        new IvInventoryHistoryWriter().StampGeneration(
            root, [history], authority.DocumentRevision);
        db.IvTrxHistories.Add(history);
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = user;
        batch.PostedCount += 1;
        batch.PostingOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return (history.Id, destination.Id, destination.LotId, null);
    }

    private static async Task<decimal> AppendNewDestWacTransferValueAsync(
        StockPostingContext root, NewDestTransferAuthority authority,
        StockReplayWacResult projection, int historyId,
        int destinationBalLocId, int? destinationLotId,
        CancellationToken cancellationToken)
    {
        root.EnsureUnsealed();
        var db = root.Db;
        var history = await db.IvTrxHistories.SingleAsync(h =>
            h.Id == historyId && h.CompanyCode == root.CompanyCode
            && h.BranchCode == root.BranchCode, cancellationToken);
        if (history.ValuationStatus is not null
            || history.ToBalLocId != destinationBalLocId
            || projection.HistoricalQuantityBeforeCandidate < authority.Quantity)
            throw InvalidNewDestTransfer("The posted WAC transfer differs from its historical projection.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == root.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw InvalidNewDestTransfer("The WAC transfer company has no base currency.");
        var amount = WacTransferAmount(authority.Quantity,
            projection.HistoricalQuantityBeforeCandidate,
            projection.HistoricalValueBeforeCandidate);
        var unitCost = StockLedgerPrecision.Money(amount / authority.Quantity);
        var now = DateTime.UtcNow;
        StockValuationFact Fact(int ordinal, int direction, string movement,
            string? warehouse, string? location, int? lotId, string? lotNo) => new()
        {
            CompanyCode = root.CompanyCode,
            BranchCode = root.BranchCode,
            LedgerEpochId = root.Epoch.Id,
            StockPostingId = root.Posting.Id,
            PostingLineNo = 1,
            SplitOrdinal = ordinal,
            SourceLineId = $"{root.Posting.SourceDocumentType}:{root.Posting.SourceDocumentId}:{history.TrxLineNo}:1:{ordinal}",
            SourceDocumentType = root.Posting.SourceDocumentType,
            SourceDocumentId = root.Posting.SourceDocumentId,
            SourceDocumentNo = root.Posting.SourceDocumentNo,
            SourceDocumentLine = history.TrxLineNo.ToString(CultureInfo.InvariantCulture),
            EffectiveAt = root.Posting.EffectiveAt,
            BusinessDate = root.Posting.BusinessDate,
            PeriodKey = root.Posting.PeriodKey,
            ItemCode = authority.ItemCode,
            WarehouseCode = BlankWacTransferField(warehouse),
            LocationCode = BlankWacTransferField(location),
            LotId = lotId,
            LotNo = BlankWacTransferField(lotNo),
            ItemStatus = BlankWacTransferField(history.IStatus),
            BaseUom = authority.BaseUom,
            MovementCode = movement,
            Direction = direction,
            BaseQty = authority.Quantity,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = unitCost,
            CostAmount = amount,
            BaseCurrency = baseCurrency,
            BaseCostAmount = amount,
            ValuationSource = StockValuationSources.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1,
            InventoryHistoryId = historyId,
            FromBalLocId = authority.SourceBalLocId,
            ToBalLocId = destinationBalLocId,
            CreatedAtUtc = now,
            CreatedBy = root.UserId
        };
        var outbound = Fact(0, -1, "TRANSFER_OUT", history.FrWarehouse, history.FrLocation,
            authority.SourceLotId, authority.SourceLotNo);
        var inbound = Fact(1, 1, "TRANSFER_IN", history.ToWarehouse, history.ToLocation,
            destinationLotId, authority.DestinationLotNo);
        inbound.TransactionCostAmount = 0m;
        db.StockValuationFacts.AddRange(outbound, inbound);
        history.ValuationStatus = StockValuationStatuses.Valued;
        return amount;
    }

    private static async Task<decimal> AppendNewDestStandardTransferValueAsync(
        StockPostingContext root, NewDestTransferAuthority authority,
        EffectiveStandardCost revision, int historyId,
        int destinationBalLocId, int? destinationLotId,
        CancellationToken cancellationToken)
    {
        root.EnsureUnsealed();
        var db = root.Db;
        var history = await db.IvTrxHistories.SingleAsync(h =>
            h.Id == historyId && h.CompanyCode == root.CompanyCode
            && h.BranchCode == root.BranchCode, cancellationToken);
        if (history.ValuationStatus is not null
            || history.ToBalLocId != destinationBalLocId
            || revision.ItemCode != authority.ItemCode
            || revision.EffectiveFrom.Date > authority.EffectiveAt.Date
            || revision.EffectiveTo is DateTime end && end.Date <= authority.EffectiveAt.Date)
            throw InvalidNewDestTransfer("The posted Standard transfer differs from its revision.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == root.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw InvalidNewDestTransfer("The Standard transfer company has no base currency.");
        var amount = StockLedgerPrecision.Money(authority.Quantity * revision.TotalStandardCost);
        var now = DateTime.UtcNow;
        StockValuationFact Fact(int ordinal, int direction, string movement,
            string? warehouse, string? location, int? lotId, string? lotNo) => new()
        {
            CompanyCode = root.CompanyCode,
            BranchCode = root.BranchCode,
            LedgerEpochId = root.Epoch.Id,
            StockPostingId = root.Posting.Id,
            PostingLineNo = 1,
            SplitOrdinal = ordinal,
            SourceLineId = $"{root.Posting.SourceDocumentType}:{root.Posting.SourceDocumentId}:{history.TrxLineNo}:1:{ordinal}",
            SourceDocumentType = root.Posting.SourceDocumentType,
            SourceDocumentId = root.Posting.SourceDocumentId,
            SourceDocumentNo = root.Posting.SourceDocumentNo,
            SourceDocumentLine = history.TrxLineNo.ToString(CultureInfo.InvariantCulture),
            EffectiveAt = root.Posting.EffectiveAt,
            BusinessDate = root.Posting.BusinessDate,
            PeriodKey = root.Posting.PeriodKey,
            ItemCode = authority.ItemCode,
            WarehouseCode = BlankWacTransferField(warehouse),
            LocationCode = BlankWacTransferField(location),
            LotId = lotId,
            LotNo = BlankWacTransferField(lotNo),
            ItemStatus = BlankWacTransferField(history.IStatus),
            BaseUom = authority.BaseUom,
            MovementCode = movement,
            Direction = direction,
            BaseQty = authority.Quantity,
            CostMethod = StockCostMethods.Standard,
            UnitCost = revision.TotalStandardCost,
            CostAmount = amount,
            BaseCurrency = baseCurrency,
            BaseCostAmount = amount,
            ValuationSource = StockValuationSources.Standard,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1,
            InventoryHistoryId = historyId,
            FromBalLocId = authority.SourceBalLocId,
            ToBalLocId = destinationBalLocId,
            CreatedAtUtc = now,
            CreatedBy = root.UserId
        };
        var outbound = Fact(0, -1, "TRANSFER_OUT", history.FrWarehouse, history.FrLocation,
            authority.SourceLotId, authority.SourceLotNo);
        var inbound = Fact(1, 1, "TRANSFER_IN", history.ToWarehouse, history.ToLocation,
            destinationLotId, authority.DestinationLotNo);
        inbound.TransactionCostAmount = 0m;
        db.StockValuationFacts.AddRange(outbound, inbound);
        history.ValuationStatus = StockValuationStatuses.Valued;
        return amount;
    }

    private static async Task AppendNewDestFifoTransferValueAsync(
        StockPostingContext root, StockReplayPhysicalPlan sourcePlan,
        NewDestTransferAuthority authority,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection plannedProjection,
        int historyId, int destinationBalLocId, int? destinationLotId,
        CancellationToken cancellationToken)
    {
        root.EnsureUnsealed();
        var db = root.Db;
        var history = await db.IvTrxHistories.SingleAsync(h =>
            h.Id == historyId && h.CompanyCode == root.CompanyCode
            && h.BranchCode == root.BranchCode, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(
            d => d.Id == authority.DetailId, cancellationToken);
        if (history.ValuationStatus is not null
            || history.ToBalLocId != destinationBalLocId)
            throw InvalidNewDestTransfer("The posted FIFO transfer history changed before valuation.");
        var boundKey = sourcePlan.Candidate.Key with { LineIdentity = $"H{historyId:D10}" };
        var bound = StockReplayFifoTailProjector.InsertTransfer(
            snapshot, boundKey, authority.Quantity);
        var planned = plannedProjection.Issues.Single(i => i.IsCandidate);
        var candidate = bound.Issues.Single(i => i.IsCandidate);
        if (candidate.DepletesPool || candidate.Quantity != authority.Quantity
            || candidate.Value != planned.Value
            || !candidate.Allocations.Select(a => (a.LayerId, a.Quantity, a.Value))
                .SequenceEqual(planned.Allocations.Select(a => (a.LayerId, a.Quantity, a.Value)))
            || bound.FinalQuantity != plannedProjection.FinalQuantity
            || bound.FinalValue != plannedProjection.FinalValue)
            throw InvalidNewDestTransfer("The bound FIFO transfer differs from its prepared allocation.");
        var layerIds = candidate.Allocations.Select(a => a.LayerId).Distinct().ToArray();
        var knownLayers = await db.StockFifoLayers.AsNoTracking()
            .Where(l => l.CompanyCode == root.CompanyCode
                && l.BranchCode == root.BranchCode
                && l.ItemCode == authority.ItemCode
                && layerIds.Contains(l.Id))
            .Select(l => l.Id).ToArrayAsync(cancellationToken);
        if (candidate.Allocations.Count == 0 || knownLayers.Length != layerIds.Length)
            throw InvalidNewDestTransfer("The FIFO transfer references a missing receipt layer.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == root.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw InvalidNewDestTransfer("The FIFO transfer company has no base currency.");
        var now = DateTime.UtcNow;
        var sourceLine = history.TrxLineNo.ToString(CultureInfo.InvariantCulture);
        StockValuationFact Fact(int ordinal, string movement, int direction,
            decimal qty, decimal value, decimal unitCost,
            string? wh, string? loc, int? lotId, string? lotNo) => new()
        {
            CompanyCode = root.CompanyCode,
            BranchCode = root.BranchCode,
            LedgerEpochId = root.Epoch.Id,
            StockPostingId = root.Posting.Id,
            PostingLineNo = 1,
            SplitOrdinal = ordinal,
            SourceLineId = $"{root.Posting.SourceDocumentType}:{root.Posting.SourceDocumentId}:{sourceLine}:1:{ordinal}",
            SourceDocumentType = root.Posting.SourceDocumentType,
            SourceDocumentId = root.Posting.SourceDocumentId,
            SourceDocumentNo = root.Posting.SourceDocumentNo,
            SourceDocumentLine = sourceLine,
            EffectiveAt = root.Posting.EffectiveAt,
            BusinessDate = root.Posting.BusinessDate,
            PeriodKey = root.Posting.PeriodKey,
            ItemCode = authority.ItemCode,
            WarehouseCode = BlankWacTransferField(wh),
            LocationCode = BlankWacTransferField(loc),
            LotId = lotId,
            LotNo = BlankWacTransferField(lotNo),
            ItemStatus = BlankWacTransferField(history.IStatus),
            BaseUom = authority.BaseUom,
            MovementCode = movement,
            Direction = direction,
            BaseQty = qty,
            CostMethod = StockCostMethods.Fifo,
            UnitCost = unitCost,
            CostAmount = value,
            BaseCurrency = baseCurrency,
            BaseCostAmount = value,
            ValuationSource = StockValuationSources.Fifo,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1,
            InventoryHistoryId = historyId,
            FromBalLocId = authority.SourceBalLocId,
            ToBalLocId = destinationBalLocId,
            CreatedAtUtc = now,
            CreatedBy = root.UserId
        };
        var facts = candidate.Allocations.Select((allocation, ordinal) =>
            Fact(ordinal, "TRANSFER_OUT", -1, allocation.Quantity, allocation.Value,
                allocation.UnitCost, history.FrWarehouse, history.FrLocation,
                authority.SourceLotId, authority.SourceLotNo)).ToList();
        var inbound = Fact(facts.Count, "TRANSFER_IN", 1, authority.Quantity,
            candidate.Value, StockLedgerPrecision.Money(candidate.Value / authority.Quantity),
            history.ToWarehouse, history.ToLocation,
            destinationLotId, authority.DestinationLotNo);
        var transactionUnit = detail.CostPrice ?? history.CostPrice
            ?? detail.UnitPrice ?? history.UnitPrice;
        inbound.TransactionCostAmount = transactionUnit is decimal unit
            ? StockLedgerPrecision.Money(authority.Quantity * unit) : null;
        inbound.ExchangeRate = inbound.TransactionCostAmount is > 0m
            ? decimal.Round(candidate.Value / inbound.TransactionCostAmount.Value, 8,
                MidpointRounding.AwayFromZero) : null;
        facts.Add(inbound);
        db.StockValuationFacts.AddRange(facts);
        for (var i = 0; i < candidate.Allocations.Count; i++)
        {
            var allocation = candidate.Allocations[i];
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = root.CompanyCode,
                BranchCode = root.BranchCode,
                IssueValuationFact = facts[i],
                FifoLayerId = allocation.LayerId,
                ConsumedQty = allocation.Quantity,
                ConsumedValue = allocation.Value,
                SplitOrdinal = i,
                CreatedAtUtc = now,
                CreatedBy = root.UserId
            });
        }
        history.ValuationStatus = StockValuationStatuses.Valued;
    }

    private static async Task VerifyNewDestPostedWacOrStandardTransferAsync(
        StockPostingContext root, StockReplayPhysicalPlan source,
        int destinationBalLocId, NewDestTransferAuthority authority,
        string costMethod, decimal expectedQuantity, decimal expectedValue,
        int historyId, decimal amount, CancellationToken cancellationToken)
    {
        if (root.Posting.SealedAtUtc is null)
            throw InvalidNewDestTransfer("The transfer root was not sealed.");
        var db = root.Db;
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == root.CompanyCode
                && b.BranchCode == root.BranchCode
                && (b.Id == source.BalLocId || b.Id == destinationBalLocId))
            .ToArrayAsync(cancellationToken);
        var state = await db.StockCostStates.AsNoTracking().SingleAsync(s =>
            s.CompanyCode == root.CompanyCode
            && s.BranchCode == root.BranchCode
            && s.ItemCode == authority.ItemCode, cancellationToken);
        var history = await db.IvTrxHistories.AsNoTracking().SingleAsync(h =>
            h.Id == historyId, cancellationToken);
        var facts = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, root.CompanyCode, root.BranchCode, [history], cancellationToken))[historyId];
        if (piles.Length != 2
            || piles.Single(b => b.Id == source.BalLocId).StdQty != source.Projection.FinalQuantity
            || piles.Single(b => b.Id == destinationBalLocId).StdQty != authority.Quantity
            || state.OnHandBaseQty != expectedQuantity
            || state.InventoryValue != expectedValue
            || facts.Count != 2
            || facts.Any(f => f.StockPostingId != root.Posting.Id || f.CostMethod != costMethod)
            || facts.Where(f => f.Direction < 0).Sum(f => f.CostAmount) != amount
            || facts.Where(f => f.Direction > 0).Sum(f => f.CostAmount) != amount)
            throw InvalidNewDestTransfer("The sealed transfer differs from its paired projection.");
    }

    private static async Task VerifyNewDestPostedFifoTransferAsync(
        StockPostingContext root, StockReplayPhysicalPlan source,
        int destinationBalLocId, NewDestTransferAuthority authority,
        StockReplayFifoTailProjection plannedProjection, int historyId,
        CancellationToken cancellationToken)
    {
        if (root.Posting.SealedAtUtc is null)
            throw InvalidNewDestTransfer("The FIFO transfer root was not sealed.");
        var db = root.Db;
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == root.CompanyCode
                && b.BranchCode == root.BranchCode
                && (b.Id == source.BalLocId || b.Id == destinationBalLocId))
            .ToArrayAsync(cancellationToken);
        if (piles.Length != 2
            || piles.Single(b => b.Id == source.BalLocId).StdQty != source.Projection.FinalQuantity
            || piles.Single(b => b.Id == destinationBalLocId).StdQty != authority.Quantity)
            throw InvalidNewDestTransfer("FIFO transfer physical piles differ from their projection.");
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            db, root.CompanyCode, root.BranchCode, authority.ItemCode, cancellationToken);
        var candidate = plannedProjection.Issues.Single(i => i.IsCandidate);
        var actual = snapshot.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity == $"H{historyId:D10}")
            .OrderBy(c => c.SplitOrdinal).ToArray();
        if (snapshot.CurrentQuantity != plannedProjection.FinalQuantity
            || snapshot.CurrentValue != plannedProjection.FinalValue
            || actual.Length != candidate.Allocations.Count
            || actual.Any(c => c.DepletesPool)
            || !actual.Select(c => (c.LayerId, c.Quantity, c.Value))
                .SequenceEqual(candidate.Allocations.Select(a => (a.LayerId, a.Quantity, a.Value))))
            throw InvalidNewDestTransfer("FIFO transfer sealed allocations differ from the plan.");
    }

    private static string NewDestWacTransferFingerprint(
        StockReplayPhysicalPlan source, NewDestTransferAuthority authority,
        StockReplayWacFinancialTail tail, StockReplayWacResult projection) =>
        StockPostingFingerprint.Create(new
        {
            source.PlanHash, authority,
            tail.CurrentQuantity, tail.CurrentValue, tail.LaterEvents,
            Piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                .Select(p => new { p.Key, p.Value, Events = tail.PhysicalByPile[p.Key] }).ToArray(),
            projection
        }, new { }).RequestFingerprint;

    private static string NewDestFifoTransferFingerprint(
        StockReplayPhysicalPlan source, NewDestTransferAuthority authority,
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayFifoTailProjection projection) =>
        StockPostingFingerprint.Create(new
        {
            source.PlanHash, authority, snapshot, projection
        }, new { }).RequestFingerprint;

    private static string NewDestStandardTransferFingerprint(
        StockReplayPhysicalPlan source, NewDestTransferAuthority authority,
        StockReplayStandardFinancialTail tail, StockReplayStandardResult projection,
        EffectiveStandardCost revision) =>
        StockPostingFingerprint.Create(new
        {
            source.PlanHash, authority, revision.Id, revision.TotalStandardCost,
            tail.CurrentQuantity, tail.CurrentValue, tail.LaterEvents,
            Piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                .Select(p => new { p.Key, p.Value, Events = tail.PhysicalByPile[p.Key] }).ToArray(),
            projection
        }, new { }).RequestFingerprint;

    private static bool MatchesTransferRollbackLotConsistency(
        IvBalLoc source, IvBalLoc destination,
        IvTrxBatchDetail detail, IvTrxHistory history)
    {
        var anyLot = source.LotId is not null || destination.LotId is not null
            || !string.IsNullOrWhiteSpace(source.LotNo)
            || !string.IsNullOrWhiteSpace(destination.LotNo)
            || history.FromLotId is not null || history.ToLotId is not null
            || !string.IsNullOrWhiteSpace(history.FrLotNo)
            || !string.IsNullOrWhiteSpace(history.ToLotNo);
        if (!anyLot)
            return true;
        return source.LotId is > 0 && destination.LotId is > 0
            && source.LotId != destination.LotId
            && source.LotId == history.FromLotId
            && destination.LotId == history.ToLotId
            && SameWacTransferSlicePart(source.LotNo, history.FrLotNo)
            && SameWacTransferSlicePart(destination.LotNo, history.ToLotNo)
            && SameWacTransferSlicePart(detail.FrLotNo, history.FrLotNo)
            && SameWacTransferSlicePart(detail.ToLotNo, history.ToLotNo);
    }

    private static StockLedgerException InvalidNewDestTransfer(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
