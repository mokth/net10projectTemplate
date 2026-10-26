using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedStandardTrAsync(
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
        var method = await read.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == itemCode)
            .Select(s => s.CostMethod).SingleOrDefaultAsync(cancellationToken);
        if (method != StockCostMethods.Standard)
            return null;
        var hasLater = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == itemCode
                && f.EffectiveAt > batch.TrxDtTime
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLater)
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        if (FuturePostDateError(batch) is string future)
            return IvInventoryPostingBatchResult.Fail(batchNo, future);
        if (detail.FromBalLocId is not > 0 || detail.FrStdQty is not > 0m)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated Standard transfer needs an existing source pile and positive quantity.");
        if (await read.IvStockMasters.AsNoTracking().AnyAsync(m =>
                m.CompanyCode == companyCode && m.ICode == itemCode
                && m.LotControl, cancellationToken))
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated lot transfer requires a new destination lot and pile; historical replay for that creation is not supported yet.");
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
                "Backdated Standard transfer needs a distinct existing non-lot destination pile.");
        var quantity = detail.FrStdQty.Value;
        var (source, destination) = await StockReplayInventoryCandidatePlanner
            .BuildTransferAsync(read, companyCode, branchCode,
                detail.FromBalLocId.Value, destinations[0], batch.TrxDtTime,
                quantity, cancellationToken);
        var key = source.Candidate.Key;
        var authority = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(
            read, companyCode, branchCode, batchNo,
            source.BalLocId, destination.BalLocId, cancellationToken);
        var prepared = await ReadStandardTransferInsertionTailAsync(read,
            authority, source, destination, cancellationToken);
        if (prepared.Error is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
        var fingerprint = StandardTransferFingerprint(source, destination,
            authority, prepared.Tail!, prepared.Projection!, prepared.Revision!);

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
        if (root is not null)
            root.CostMethod = StockCostMethods.Standard;
        if (root is null || root.CostMethod != StockCostMethods.Standard
            || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || root.Posting.SourceDocumentId != authority.BatchId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.DocumentRevision != authority.DocumentRevision
            || root.Posting.PostingRole != "PRIMARY"
            || root.Posting.EffectiveAt != authority.EffectiveAt
            || root.Posting.PostingSequence != key.OriginalPostingSequence
            || root.Epoch.Id != source.LedgerEpochId)
            throw InvalidStandardTransfer("The Standard transfer root differs from its draft.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root,
            source, cancellationToken);
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root,
            destination, cancellationToken);
        var currentDraft = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(
            write, companyCode, branchCode, batchNo,
            source.BalLocId, destination.BalLocId, cancellationToken);
        if (currentDraft != authority)
            throw InvalidStandardTransfer("The Standard transfer draft changed before posting.");
        var current = await ReadStandardTransferInsertionTailAsync(write,
            authority, source, destination, cancellationToken);
        if (current.Error is not null || current.Tail is null
            || current.Projection is null || current.Revision is null
            || StandardTransferFingerprint(source, destination, authority,
                current.Tail, current.Projection, current.Revision) != fingerprint)
            throw InvalidStandardTransfer("The Standard transfer tail changed before posting.");
        var physical = await StockReplayFifoTransferPhysicalWriter
            .AppendPreparedAsync(root, source, destination,
                authority, batchNo, cancellationToken);
        var amount = await AppendStandardTransferValueAsync(root, authority,
            current.Revision, physical.HistoryId, cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(root,
            [physical.HistoryId], cancellationToken);
        await VerifyPostedStandardTransferAsync(root, source, destination,
            authority, current.Tail, physical.HistoryId, amount,
            cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id)
            .Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private static async Task<(StockReplayStandardFinancialTail? Tail,
        StockReplayStandardResult? Projection, EffectiveStandardCost? Revision,
        string? Error)> ReadStandardTransferInsertionTailAsync(
        AppDbContext db, StockReplayFifoTransferDraftAuthority authority,
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
            return (null, null, null, "The backdated Standard transfer has unmatched physical legs.");
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
                || p.First.OldSignedValue != p.Second.SignedValue)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(source)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(destination))
            return (null, null, null,
                "The Standard transfer changes a later quantity or value.");
        if (await CheckStandardTransferDescendantsAsync(db,
                source.CompanyCode, source.BranchCode, authority.ItemCode,
                key, tail.LaterEvents, cancellationToken) is string error)
            return (null, null, null, error);
        return (tail, projection, revision, null);
    }

    private static string StandardTransferFingerprint(
        StockReplayPhysicalPlan source, StockReplayPhysicalPlan destination,
        StockReplayFifoTransferDraftAuthority authority,
        StockReplayStandardFinancialTail tail,
        StockReplayStandardResult projection,
        EffectiveStandardCost revision) =>
        StockPostingFingerprint.Create(new
        {
            source.PlanHash, DestinationHash = destination.PlanHash,
            authority, revision.Id, revision.TotalStandardCost,
            tail.CurrentQuantity, tail.CurrentValue, tail.LaterEvents,
            Piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                .Select(p => new { p.Key, p.Value,
                    Events = tail.PhysicalByPile[p.Key] }).ToArray(),
            projection
        }, new { }).RequestFingerprint;

    private static async Task<string?> CheckStandardTransferDescendantsAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, StockReplayEventKey key,
        IReadOnlyList<StockReplayStandardEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count is 0 or > 100 || events.Any(e =>
                e.Kind is not (StockReplayStandardEventKinds.Movement
                    or StockReplayStandardEventKinds.Revaluation)
                || e.Kind == StockReplayStandardEventKinds.Movement
                    && e.EventId is <= 0 or > int.MaxValue
                || e.Key.EffectiveAt.Year != key.EffectiveAt.Year
                || e.Key.EffectiveAt.Month != key.EffectiveAt.Month
                || e.OldSignedQuantity != e.ReplaySignedQuantity))
            return "Backdated Standard transfer supports at most 100 later original Inventory movements or revaluations in the same open month.";
        var revaluations = events.Where(e =>
            e.Kind == StockReplayStandardEventKinds.Revaluation).ToArray();
        if (await CheckStandardTransferRevaluationsAsync(db,
                companyCode, branchCode, itemCode, revaluations,
                cancellationToken) is string valueError)
            return valueError;
        var ids = events.Where(e => e.Kind == StockReplayStandardEventKinds.Movement)
            .Select(e => checked((int)e.EventId)).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id)).ToArrayAsync(cancellationToken);
        if (histories.Length != ids.Length || histories.Any(h =>
                h.CompanyCode != companyCode || h.BranchCode != branchCode
                || h.ICode != itemCode || h.EntryRole != "ORIGINAL"
                || h.LedgerVersion != 2
                || h.ValuationStatus != StockValuationStatuses.Valued
                || h.BatchStatus != IvBatchStatuses.Posted
                || h.StockPostingId is not > 0
                || h.TrxType is not (IvTrxTypes.StockTransfer
                    or IvTrxTypes.MiscellaneousReceipt
                    or IvTrxTypes.GoodsReceive
                    or IvTrxTypes.MiscellaneousIssue
                    or IvTrxTypes.Scrap)))
            return "A later Standard movement has no original Inventory history.";
        var laterGr = histories.Where(h =>
            h.TrxType == IvTrxTypes.GoodsReceive).ToArray();
        if (laterGr.Any(h => h.ToStdQty is not > 0m
                || h.FromBalLocId is not null
                || string.IsNullOrWhiteSpace(h.PoNo)
                || h.PoRelNo is null || h.PoLineNo is null))
            return "A later Standard GR has no exact receipt and purchase-order line.";
        if (laterGr.Length > 0)
        {
            var laterGrIds = laterGr.Select(h => h.Id).ToArray();
            if (await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .AnyAsync(s => s.CompanyCode == companyCode
                    && s.BranchCode == branchCode
                    && s.ReceiptInventoryHistoryId != null
                    && laterGrIds.Contains(s.ReceiptInventoryHistoryId.Value),
                    cancellationToken))
                return "Backdated Standard replay needs procurement settlement replay for a later invoiced GR.";
            var poNos = laterGr.Select(h => h.PoNo!).Distinct().ToArray();
            var poLines = await db.PoOrderDetails.AsNoTracking()
                .Where(d => d.CompanyCode == companyCode
                    && d.BranchCode == branchCode
                    && poNos.Contains(d.PoNo))
                .Select(d => new { d.PoNo, d.PoRelNo, d.Line,
                    d.InvoicedQty })
                .ToArrayAsync(cancellationToken);
            if (laterGr.Any(h => poLines.Count(d =>
                    string.Equals(d.PoNo, h.PoNo,
                        StringComparison.OrdinalIgnoreCase)
                    && d.PoRelNo == h.PoRelNo
                    && d.Line == h.PoLineNo) != 1
                || poLines.Any(d => string.Equals(d.PoNo, h.PoNo,
                        StringComparison.OrdinalIgnoreCase)
                    && d.PoRelNo == h.PoRelNo
                    && d.Line == h.PoLineNo
                    && d.InvoicedQty != 0m)))
                return "Backdated Standard replay needs procurement replay for a later invoiced or unresolved GR purchase-order line.";
        }
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, companyCode, branchCode, histories, cancellationToken);
        if (laterGr.Any(h => active[h.Id].Count != 1
                || active[h.Id][0].SourceDocumentType
                    != IvTrxTypes.GoodsReceive
                || active[h.Id][0].SourceDocumentNo != h.BatchNo
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
                || active[h.Id][0].EffectiveAt != h.TrxDtTime
                || active[h.Id][0].Direction != 1
                || active[h.Id][0].ToBalLocId != h.ToBalLocId))
            return "A later Standard GR has inconsistent receipt valuation provenance.";
        if (histories.Any(h => active[h.Id].Any(f =>
                f.StockPostingId != h.StockPostingId
                || f.OriginalValuationFactId is not null
                || f.CostMethod != StockCostMethods.Standard
                || f.ValuationSource != StockValuationSources.Standard)))
            return "A later Standard movement has a corrected or inconsistent valuation.";
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
                p.SourceModule != "INVENTORY" || p.SealedAtUtc is null))
            return "A later Standard movement has a foreign or unsealed owner.";
        var byOwner = owners.ToDictionary(p => p.Id);
        var batchNumbers = histories.Select(h => h.BatchNo).Distinct().ToArray();
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && batchNumbers.Contains(b.BatchNo))
            .ToArrayAsync(cancellationToken);
        if (batches.Length != batchNumbers.Length
            || histories.Any(h => !byOwner.TryGetValue(
                    h.StockPostingId!.Value, out var owner)
                || !batches.Any(b => b.BatchNo == h.BatchNo
                    && b.TrxType == h.TrxType
                    && b.BatchStatus == IvBatchStatuses.Posted
                    && owner.SourceDocumentType == h.TrxType
                    && owner.SourceDocumentId == b.Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    && owner.SourceDocumentNo == h.BatchNo.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    && (h.TrxType != IvTrxTypes.GoodsReceive
                        || active[h.Id][0].SourceDocumentId
                            == owner.SourceDocumentId)
                    && owner.PostingRole == "PRIMARY"
                    && owner.CommandType == $"IV_POST_{h.TrxType}"
                    && owner.EffectiveAt == h.TrxDtTime)))
            return "A later Standard movement has no exact posted Inventory owner.";
        return null;
    }

    private static async Task<string?> CheckStandardTransferRevaluationsAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, IReadOnlyList<StockReplayStandardEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return null;
        if (events.Any(e => e.EventId <= 0
                || e.EventId >= long.MaxValue
                || long.MaxValue - e.EventId <= 0))
            return "A later Standard revaluation has an invalid event identity.";
        var ids = events.Select(e => long.MaxValue - e.EventId).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            return "A later Standard revaluation has a duplicate identity.";
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => ids.Contains(f.Id)).ToArrayAsync(cancellationToken);
        if (facts.Length != ids.Length || facts.Any(f =>
                f.CompanyCode != companyCode
                || f.BranchCode != branchCode || f.ItemCode != itemCode
                || f.OriginalValuationFactId is not null
                || f.ReversesValuationFactId is not null)
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.OriginalValuationFactId.HasValue
                    && ids.Contains(f.OriginalValuationFactId.Value)
                || f.ReversesValuationFactId.HasValue
                    && ids.Contains(f.ReversesValuationFactId.Value),
                cancellationToken))
            return "A later Standard revaluation is corrected or reversed.";
        var postingIds = facts.Select(f => f.StockPostingId).Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id)).ToArrayAsync(cancellationToken);
        if (owners.Length != postingIds.Length)
            return "A later Standard revaluation has no exact owner.";
        var byOwner = owners.ToDictionary(p => p.Id);
        var byFact = facts.ToDictionary(f => f.Id);
        var resolver = new ItemStandardCostResolver();
        foreach (var e in events)
        {
            var fact = byFact[long.MaxValue - e.EventId];
            var owner = byOwner[fact.StockPostingId];
            var revision = await resolver.ResolveAsync(db, companyCode,
                branchCode, itemCode, e.Key.EffectiveAt,
                cancellationToken);
            if (owner.CompanyCode != companyCode
                || owner.BranchCode != branchCode
                || owner.SourceModule != "INVENTORY_COSTING"
                || owner.CommandType != "STANDARD_COST_REVALUATION"
                || owner.SourceDocumentType != "STANDARD_COST_REVALUATION"
                || owner.PostingRole != "PRIMARY"
                || owner.SourceDocumentId != owner.RequestId.ToString("N")
                || owner.SourceDocumentNo != owner.SourceDocumentId
                || StockReplayStandardRevaluationEventFactory.Create(
                    fact, owner, revision) != e)
                return "A later Standard revaluation has no original approved owner.";
        }
        return null;
    }

    private static async Task<decimal> AppendStandardTransferValueAsync(
        StockPostingContext root,
        StockReplayFifoTransferDraftAuthority authority,
        EffectiveStandardCost revision, int historyId,
        CancellationToken cancellationToken)
    {
        root.EnsureUnsealed();
        var db = root.Db;
        var history = await db.IvTrxHistories.SingleAsync(h =>
            h.Id == historyId && h.CompanyCode == root.CompanyCode
            && h.BranchCode == root.BranchCode, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
            d.Id == authority.DetailId, cancellationToken);
        if (root.CostMethod != StockCostMethods.Standard
            || history.StockPostingId != root.Posting.Id
            || history.LedgerEpochId != root.Epoch.Id
            || history.LedgerVersion != 2
            || history.EntryRole != "ORIGINAL"
            || history.TrxType != IvTrxTypes.StockTransfer
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.ValuationStatus is not null
            || history.ICode != authority.ItemCode
            || history.BatchNo != authority.BatchNo
            || history.FromBalLocId != authority.SourceBalLocId
            || history.ToBalLocId != authority.DestinationBalLocId
            || history.FrStdQty != authority.Quantity
            || history.ToStdQty != authority.Quantity
            || detail.BatchId != authority.BatchId
            || detail.DocumentRevision != authority.DocumentRevision
            || detail.ToBalLocId != authority.DestinationBalLocId
            || revision.ItemCode != authority.ItemCode
            || revision.EffectiveFrom.Date > authority.EffectiveAt.Date
            || revision.EffectiveTo is DateTime end
                && end.Date <= authority.EffectiveAt.Date)
            throw InvalidStandardTransfer("The posted Standard transfer differs from its revision or draft.");
        if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.InventoryHistoryId == historyId
                || f.StockPostingId == root.Posting.Id
                    && f.PostingLineNo == 1, cancellationToken))
            throw InvalidStandardTransfer("The Standard transfer already has valuation facts.");
        var currency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == root.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(currency))
            throw InvalidStandardTransfer("The Standard transfer company has no base currency.");
        var amount = StockLedgerPrecision.Money(
            authority.Quantity * revision.TotalStandardCost);
        var now = DateTime.UtcNow;
        StockValuationFact Fact(int ordinal, int direction,
            string movement, string? warehouse, string? location) => new()
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
            SourceDocumentLine = history.TrxLineNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            EffectiveAt = root.Posting.EffectiveAt,
            BusinessDate = root.Posting.BusinessDate,
            PeriodKey = root.Posting.PeriodKey,
            ItemCode = authority.ItemCode,
            WarehouseCode = BlankWacTransferField(warehouse),
            LocationCode = BlankWacTransferField(location),
            ItemStatus = BlankWacTransferField(history.IStatus),
            BaseUom = authority.BaseUom,
            MovementCode = movement,
            Direction = direction,
            BaseQty = authority.Quantity,
            CostMethod = StockCostMethods.Standard,
            UnitCost = revision.TotalStandardCost,
            CostAmount = amount,
            BaseCurrency = currency,
            BaseCostAmount = amount,
            ValuationSource = StockValuationSources.Standard,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1,
            InventoryHistoryId = historyId,
            FromBalLocId = authority.SourceBalLocId,
            ToBalLocId = authority.DestinationBalLocId,
            CreatedAtUtc = now,
            CreatedBy = root.UserId
        };
        var outbound = Fact(0, -1, "TRANSFER_OUT",
            history.FrWarehouse, history.FrLocation);
        var inbound = Fact(1, 1, "TRANSFER_IN",
            history.ToWarehouse, history.ToLocation);
        inbound.TransactionCostAmount = 0m;
        db.StockValuationFacts.AddRange(outbound, inbound);
        history.ValuationStatus = StockValuationStatuses.Valued;
        return amount;
    }

    private static async Task VerifyPostedStandardTransferAsync(
        StockPostingContext root, StockReplayPhysicalPlan source,
        StockReplayPhysicalPlan destination,
        StockReplayFifoTransferDraftAuthority authority,
        StockReplayStandardFinancialTail before,
        int historyId, decimal amount,
        CancellationToken cancellationToken)
    {
        if (root.Posting.SealedAtUtc is null)
            throw InvalidStandardTransfer("The Standard transfer root was not sealed.");
        var db = root.Db;
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == root.CompanyCode
                && b.BranchCode == root.BranchCode
                && (b.Id == source.BalLocId
                    || b.Id == destination.BalLocId))
            .ToArrayAsync(cancellationToken);
        var state = await db.StockCostStates.AsNoTracking().SingleAsync(s =>
            s.CompanyCode == root.CompanyCode
            && s.BranchCode == root.BranchCode
            && s.ItemCode == authority.ItemCode, cancellationToken);
        var history = await db.IvTrxHistories.AsNoTracking().SingleAsync(h =>
            h.Id == historyId, cancellationToken);
        var facts = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, root.CompanyCode, root.BranchCode,
            [history], cancellationToken))[historyId];
        if (piles.Length != 2
            || piles.Single(b => b.Id == source.BalLocId).StdQty
                != source.Projection.FinalQuantity
            || piles.Single(b => b.Id == destination.BalLocId).StdQty
                != destination.Projection.FinalQuantity
            || state.OnHandBaseQty != before.CurrentQuantity
            || state.InventoryValue != before.CurrentValue
            || facts.Count != 2
            || facts.Any(f => f.StockPostingId != root.Posting.Id
                || f.CostMethod != StockCostMethods.Standard)
            || facts.Where(f => f.Direction < 0).Sum(f => f.CostAmount)
                != amount
            || facts.Where(f => f.Direction > 0).Sum(f => f.CostAmount)
                != amount)
            throw InvalidStandardTransfer("The sealed Standard transfer differs from its paired projection.");
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedStandardTrAsync(
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
        if (method != StockCostMethods.Standard)
            return null;
        var hasLater = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == prior[0].ICode
                && f.EffectiveAt > prior[0].TrxDtTime
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLater)
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
        if (root is not null)
            root.CostMethod = StockCostMethods.Standard;
        if (root is null || root.CostMethod != StockCostMethods.Standard)
            throw InvalidStandardTransfer("Standard transfer rollback needs an active root.");
        var batch = await _posting.LockBatchForUpdateAsync(db,
            companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null || batch.Id != candidate.Id
            || batch.BatchStatus != IvBatchStatuses.Posted
            || batch.TrxType != IvTrxTypes.StockTransfer
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null)
            throw InvalidStandardTransfer("The posted Standard transfer changed before rollback.");
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
            throw InvalidStandardTransfer("The transfer detail or history changed.");
        var detail = details[0];
        var history = current[0];
        var owner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == history.StockPostingId,
                cancellationToken);
        var idText = batch.Id.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var noText = batchNo.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
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
            throw InvalidStandardTransfer("The rollback root does not own this Standard transfer.");
        var balances = await db.IvBalLocs
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && (b.Id == history.FromBalLocId
                    || b.Id == history.ToBalLocId))
            .ToArrayAsync(cancellationToken);
        if (balances.Length != 2)
            throw InvalidStandardTransfer("The Standard transfer has no exact pair of piles.");
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
            throw InvalidStandardTransfer("The Standard transfer piles do not support rollback.");
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
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
            db, companyCode, branchCode, history.ICode, key,
            cancellationToken);
        var revision = await new ItemStandardCostResolver().ResolveAsync(db,
            companyCode, branchCode, history.ICode,
            history.TrxDtTime, cancellationToken);
        var removed = new StockReplayStandardEvent(history.Id, key,
            StockReplayStandardEventKinds.Movement, 0m, 0m, 0m,
            revision.TotalStandardCost, revision.Id);
        await StockReplayStandardRevisionAuthority.ValidateAsync(db,
            companyCode, branchCode, history.ICode,
            removed, tail.LaterEvents, cancellationToken);
        var projection = StockReplayStandardSimulator.SimulateRemoval(
            tail.CurrentQuantity, tail.CurrentValue,
            removed, tail.LaterEvents);
        if (sourcePlan.Projection.FinalQuantity != source.StdQty + quantity
            || destPlan.Projection.FinalQuantity != destination.StdQty - quantity
            || sourcePlan.LedgerEpochId != root.Epoch.Id
            || destPlan.LedgerEpochId != root.Epoch.Id
            || projection.FinalQuantity != tail.CurrentQuantity
            || projection.FinalValue != tail.CurrentValue
            || tail.LaterEvents.Count != projection.LaterSteps.Count
            || tail.LaterEvents.Zip(projection.LaterSteps).Any(p =>
                p.First.OldSignedQuantity != p.Second.SignedQuantity
                || p.First.OldSignedValue != p.Second.SignedValue)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(sourcePlan)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(destPlan)
            || await CheckStandardTransferDescendantsAsync(db,
                companyCode, branchCode, history.ICode,
                key, tail.LaterEvents, cancellationToken) is not null)
            throw InvalidStandardTransfer("The Standard transfer rollback changes a later movement.");
        var active = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, companyCode, branchCode, [history],
            cancellationToken))[history.Id]
            .OrderBy(f => f.PostingLineNo).ThenBy(f => f.SplitOrdinal)
            .ToArray();
        var originalEvent = StockReplayStandardHistoryEventFactory.Create(
            history, history.Id, key, active, revision);
        var amount = StockLedgerPrecision.Money(quantity * revision.TotalStandardCost);
        if (originalEvent.OldSignedQuantity != 0m
            || originalEvent.OldSignedValue != 0m
            || active.Length != 2
            || active.Any(f => f.StockPostingId != owner.Id
                || f.OriginalValuationFactId is not null
                || f.ValuationSource != StockValuationSources.Standard
                || f.BaseCostAmount != f.CostAmount
                || f.CostAmount != amount)
            || active.Count(f => f.Direction < 0
                && f.MovementCode == "TRANSFER_OUT"
                && f.FromBalLocId == source.Id
                && f.ToBalLocId == destination.Id) != 1
            || active.Count(f => f.Direction > 0
                && f.MovementCode == "TRANSFER_IN"
                && f.FromBalLocId == source.Id
                && f.ToBalLocId == destination.Id) != 1)
            throw InvalidStandardTransfer("The Standard transfer has no original paired valuation.");
        var state = await db.StockCostStates.AsNoTracking().SingleAsync(s =>
            s.CompanyCode == companyCode && s.BranchCode == branchCode
            && s.ItemCode == history.ICode, cancellationToken);
        if (state.CostMethod != StockCostMethods.Standard
            || state.OnHandBaseQty != tail.CurrentQuantity
            || state.InventoryValue != tail.CurrentValue)
            throw InvalidStandardTransfer("The Standard item state changed before rollback.");

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
        var finalState = await db.StockCostStates.AsNoTracking().SingleAsync(s =>
            s.CompanyCode == companyCode && s.BranchCode == branchCode
            && s.ItemCode == history.ICode, cancellationToken);
        if (finalState.OnHandBaseQty != tail.CurrentQuantity
            || finalState.InventoryValue != tail.CurrentValue
            || source.StdQty + destination.StdQty
                != sourcePlan.CurrentQuantity + destPlan.CurrentQuantity)
            throw InvalidStandardTransfer("The removed Standard transfer changed its item pool.");
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo,
            batch.RollbackOperationId ?? Guid.Empty);
    }

    private static StockLedgerException InvalidStandardTransfer(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
