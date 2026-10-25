using System.Collections.Immutable;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private static async Task<bool> HasBackdatedStandardTailAsync(
        AppDbContext db, string companyCode, string branchCode,
        DateTime effectiveAt, IEnumerable<string?> itemCodes,
        CancellationToken cancellationToken)
    {
        var items = itemCodes.Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i!).Distinct(StringComparer.Ordinal).ToArray();
        if (items.Length == 0)
            return false;
        var standardItems = await db.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode
                && items.Contains(s.ItemCode)
                && s.CostMethod == StockCostMethods.Standard)
            .Select(s => s.ItemCode).ToArrayAsync(cancellationToken);
        return standardItems.Length > 0
            && await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == companyCode
                && f.BranchCode == branchCode
                && standardItems.Contains(f.ItemCode)
                && f.EffectiveAt > effectiveAt
                && f.StockPosting!.SealedAtUtc != null,
                cancellationToken);
    }

    private sealed record StandardMiDraft(
        int BatchId, int BatchNo, string TrxType, int DetailId, int Revision,
        string ItemCode, int PileId, DateTime EffectiveAt,
        decimal Quantity, string BaseUom, int? LotId, string LotNo);

    private sealed record StandardMiInsertion(
        StockReplayStandardFinancialTail Tail,
        StockReplayStandardResult Projection,
        EffectiveStandardCost Revision,
        string Fingerprint);

    private static async Task<StandardMiDraft> ReadStandardMiDraftAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, string expectedTrxType,
        CancellationToken cancellationToken)
    {
        var batch = await db.IvTrxBatches.AsNoTracking().SingleAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (batch.TrxType != expectedTrxType
            || batch.BatchStatus != IvBatchStatuses.New
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null
            || details.Length != 1)
            throw InvalidStandardTransfer("Standard MI needs one active NEW issue line.");
        var detail = details[0];
        if (detail.CompanyCode != companyCode
            || detail.BranchCode != branchCode || detail.BatchNo != batchNo
            || detail.TrxType != expectedTrxType
            || detail.DocumentRevision < 0 || detail.TrxLineNo <= 0
            || string.IsNullOrWhiteSpace(detail.ICode)
            || string.IsNullOrWhiteSpace(detail.FrStdUom)
            || detail.FromBalLocId is not > 0
            || detail.FrStdQty is not > 0m
            || IvQty.Round(detail.FrStdQty.Value) != detail.FrStdQty
            || detail.ToBalLocId is not null || detail.ToStdQty is not null)
            throw InvalidStandardTransfer("The Standard MI draft is not one outbound leg.");
        var master = await db.IvStockMasters.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CompanyCode == companyCode
                && m.ICode == detail.ICode, cancellationToken);
        var balance = await db.IvBalLocs.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == detail.FromBalLocId
                && b.CompanyCode == companyCode
                && b.BranchCode == branchCode, cancellationToken);
        if (master is null || balance is null
            || !await StockReplayInventoryIssuePileAuthority.MatchesAsync(
                db, master, balance, detail, cancellationToken))
            throw InvalidStandardTransfer("The Standard MI source differs from its exact pile or lot.");
        return new(batch.Id, batchNo, expectedTrxType,
            detail.Id, detail.DocumentRevision,
            detail.ICode!, balance.Id, batch.TrxDtTime,
            detail.FrStdQty.Value, detail.FrStdUom!,
            balance.LotId, balance.LotNo ?? string.Empty);
    }

    private static async Task<(StandardMiInsertion? Plan, string? Error)>
        ReadStandardMiInsertionAsync(AppDbContext db,
            StandardMiDraft authority, StockReplayPhysicalPlan physical,
            CancellationToken cancellationToken)
    {
        var key = physical.Candidate.Key;
        if (physical.BalLocId != authority.PileId
            || key.EffectiveAt != authority.EffectiveAt
            || physical.Candidate.SignedDelta != -authority.Quantity
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(physical))
            return (null, "The Standard MI changes a later physical movement.");
        var revision = await new ItemStandardCostResolver().ResolveAsync(db,
            physical.CompanyCode, physical.BranchCode,
            authority.ItemCode, key.EffectiveAt, cancellationToken);
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(db,
            physical.CompanyCode, physical.BranchCode,
            authority.ItemCode, key, cancellationToken);
        if (!tail.CurrentQuantityByPile.TryGetValue(physical.BalLocId,
                out var currentPileQuantity)
            || currentPileQuantity != physical.CurrentQuantity)
            return (null, "The Standard MI item tail differs from its source pile.");
        var candidate = new StockReplayStandardEvent(physical.Candidate.EventId,
            key, StockReplayStandardEventKinds.Movement, 0m, 0m,
            -authority.Quantity, revision.TotalStandardCost, revision.Id);
        await StockReplayStandardRevisionAuthority.ValidateAsync(db,
            physical.CompanyCode, physical.BranchCode,
            authority.ItemCode, candidate, tail.LaterEvents,
            cancellationToken);
        var projection = StockReplayStandardSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue,
            candidate, tail.LaterEvents);
        var amount = StockLedgerPrecision.Money(
            authority.Quantity * revision.TotalStandardCost);
        if (projection.Candidate.SignedQuantity != -authority.Quantity
            || projection.Candidate.SignedValue != -amount
            || projection.FinalQuantity != tail.CurrentQuantity
                - authority.Quantity
            || projection.FinalValue != tail.CurrentValue - amount
            || tail.LaterEvents.Count != projection.LaterSteps.Count
            || tail.LaterEvents.Zip(projection.LaterSteps).Any(p =>
                p.First.OldSignedQuantity != p.Second.SignedQuantity
                || p.First.OldSignedValue != p.Second.SignedValue)
            || await CheckStandardTransferDescendantsAsync(db,
                physical.CompanyCode, physical.BranchCode,
                authority.ItemCode, key, tail.LaterEvents,
                cancellationToken) is not null)
            return (null, "The Standard MI changes a later cost or has an unsupported descendant.");
        var fingerprint = StockPostingFingerprint.Create(new
        {
            physical.PlanHash, authority, revision.Id,
            revision.TotalStandardCost,
            tail.CurrentQuantity, tail.CurrentValue, tail.LaterEvents,
            Piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                .Select(p => new { p.Key, p.Value,
                    Events = tail.PhysicalByPile[p.Key] }).ToArray(),
            projection
        }, new { }).RequestFingerprint;
        return (new(tail, projection, revision, fingerprint), null);
    }

    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedStandardMiAsync(
        string companyCode, string branchCode, int batchNo,
        string expectedTrxType, CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo
            && b.TrxType == expectedTrxType
            && b.BatchStatus == IvBatchStatuses.New, cancellationToken);
        if (batch is null)
            return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1 || string.IsNullOrWhiteSpace(details[0].ICode))
            return await HasBackdatedStandardTailAsync(read, companyCode,
                branchCode, batch.TrxDtTime,
                details.Select(d => d.ICode), cancellationToken)
                ? IvInventoryPostingBatchResult.Fail(batchNo,
                    "Backdated Standard MI currently needs one valid issue line.")
                : null;
        var method = await read.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode
                && s.ItemCode == details[0].ICode)
            .Select(s => s.CostMethod).SingleOrDefaultAsync(cancellationToken);
        if (method != StockCostMethods.Standard)
            return null;
        var hasLater = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode
                && f.ItemCode == details[0].ICode
                && f.EffectiveAt > batch.TrxDtTime
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLater)
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        if (FuturePostDateError(batch) is string future)
            return IvInventoryPostingBatchResult.Fail(batchNo, future);
        var authority = await ReadStandardMiDraftAsync(read,
            companyCode, branchCode, batchNo, expectedTrxType,
            cancellationToken);
        var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(
            read, companyCode, branchCode, authority.PileId,
            authority.EffectiveAt, -authority.Quantity, cancellationToken);
        var key = physical.Candidate.Key;
        var prepared = await ReadStandardMiInsertionAsync(read,
            authority, physical, cancellationToken);
        if (prepared.Error is string unsupported)
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
        var root = ledger.Context;
        if (root is null)
            throw InvalidStandardTransfer("Standard MI needs an active V2 root.");
        root.CostMethod = StockCostMethods.Standard;
        if (root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != expectedTrxType
            || root.Posting.SourceDocumentId != authority.BatchId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.PostingRole != "PRIMARY"
            || root.Posting.DocumentRevision != authority.Revision
            || root.Posting.EffectiveAt != authority.EffectiveAt
            || root.Posting.PostingSequence != key.OriginalPostingSequence
            || root.Epoch.Id != physical.LedgerEpochId)
            throw InvalidStandardTransfer("The Standard MI root differs from its draft.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, authority.EffectiveAt, cancellationToken)
            is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root,
            physical, cancellationToken);
        var currentAuthority = await ReadStandardMiDraftAsync(write,
            companyCode, branchCode, batchNo, expectedTrxType,
            cancellationToken);
        var current = await ReadStandardMiInsertionAsync(write,
            currentAuthority, physical, cancellationToken);
        if (currentAuthority != authority || current.Plan is null
            || current.Plan.Fingerprint != prepared.Plan!.Fingerprint)
            throw InvalidStandardTransfer("The Standard MI tail changed before posting.");
        var historyId = await AppendStandardMiPhysicalAsync(root,
            physical, authority, cancellationToken);
        await AppendStandardMiValueAsync(root, authority,
            current.Plan.Revision, historyId, cancellationToken);
        var permit = new StockReplayStandardWritePermit(root,
            authority.ItemCode, current.Plan.Fingerprint,
            current.Plan.Tail.CurrentQuantity,
            current.Plan.Tail.CurrentValue,
            current.Plan.Projection.FinalQuantity,
            current.Plan.Projection.FinalValue,
            ImmutableDictionary<int, decimal>.Empty.Add(
                physical.BalLocId, physical.Projection.FinalQuantity));
        await StockReplayStandardCostStateWriter.ApplyAsync(root,
            permit, cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(root,
            [historyId], cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == authority.BatchId)
            .Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private static async Task<int> AppendStandardMiPhysicalAsync(
        StockPostingContext root, StockReplayPhysicalPlan physical,
        StandardMiDraft authority, CancellationToken cancellationToken)
    {
        var db = root.Db;
        var batch = await db.IvTrxBatches.SingleAsync(b =>
            b.Id == authority.BatchId, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
            d.Id == authority.DetailId, cancellationToken);
        var balance = await db.IvBalLocs.SingleAsync(b =>
            b.Id == authority.PileId, cancellationToken);
        var master = await db.IvStockMasters.AsNoTracking().SingleAsync(m =>
            m.CompanyCode == root.CompanyCode
            && m.ICode == authority.ItemCode, cancellationToken);
        db.ChangeTracker.DetectChanges();
        if (batch.BatchStatus != IvBatchStatuses.New
            || detail.DocumentRevision != root.Posting.DocumentRevision
            || db.Entry(batch).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || !await StockReplayInventoryIssuePileAuthority.MatchesAsync(
                db, master, balance, detail, cancellationToken)
            || balance.LotId != authority.LotId
            || balance.LotNo != authority.LotNo
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == root.CompanyCode
                && h.BranchCode == root.BranchCode
                && ((h.BatchNo == authority.BatchNo
                        && h.DocumentRevision == detail.DocumentRevision)
                    || h.StockPostingId == root.Posting.Id),
                cancellationToken))
            throw InvalidStandardTransfer("The Standard MI draft or pile changed.");
        var now = DateTime.UtcNow;
        var user = root.UserId.Length > 10 ? root.UserId[..10] : root.UserId;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Count == 0
            ? physical.Candidate.Key.EffectiveAt
            : physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        detail.FromLotId = balance.LotId;
        detail.PriceEvidence = balance.PriceEvidence;
        var history = new IvTrxHistory
        {
            CompanyCode = root.CompanyCode,
            BranchCode = root.BranchCode,
            BatchNo = authority.BatchNo,
            TrxLineNo = detail.TrxLineNo,
            TrxType = authority.TrxType,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode,
            ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = authority.ItemCode,
            IDesc = detail.IDesc,
            FrWarehouse = detail.FrWarehouse,
            FrLocation = detail.FrLocation,
            FrLotNo = detail.FrLotNo,
            FrStdQty = detail.FrStdQty,
            FrStdUom = detail.FrStdUom,
            FrPurQty = detail.FrPurQty,
            FrPurUom = detail.FrPurUom,
            IStatus = detail.IStatus,
            Remarks = detail.Remarks,
            PriceEvidence = detail.PriceEvidence,
            CostEvidenceType = detail.CostEvidenceType,
            CostOverrideReason = detail.CostOverrideReason,
            CostApprovedBy = detail.CostApprovedBy,
            CostApprovedAtUtc = detail.CostApprovedAtUtc,
            UnitPrice = detail.UnitPrice,
            Cost = detail.Cost,
            CostPrice = detail.CostPrice,
            BaseUnitPrices = detail.BaseUnitPrices,
            LocationCode = detail.LocationCode,
            FromBalLocId = balance.Id,
            FromLotId = balance.LotId,
            CreatedDate = now,
            CreatedBy = user
        };
        new IvInventoryHistoryWriter().StampGeneration(root,
            [history], root.Posting.DocumentRevision);
        db.IvTrxHistories.Add(history);
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = user;
        batch.PostedCount += 1;
        batch.PostingOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        if (history.StockPostingId != root.Posting.Id
            || history.LedgerEpochId != root.Epoch.Id
            || history.EntryRole != "ORIGINAL"
            || history.FrStdQty != authority.Quantity
            || history.TrxDtTime != authority.EffectiveAt)
            throw InvalidStandardTransfer("The saved Standard MI history differs from its plan.");
        return history.Id;
    }

    private static async Task AppendStandardMiValueAsync(
        StockPostingContext root, StandardMiDraft authority,
        EffectiveStandardCost revision, int historyId,
        CancellationToken cancellationToken)
    {
        var db = root.Db;
        var history = await db.IvTrxHistories.SingleAsync(h =>
            h.Id == historyId, cancellationToken);
        var currency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == root.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(currency))
            throw InvalidStandardTransfer("The Standard MI company has no base currency.");
        var amount = StockLedgerPrecision.Money(
            authority.Quantity * revision.TotalStandardCost);
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = root.CompanyCode,
            BranchCode = root.BranchCode,
            LedgerEpochId = root.Epoch.Id,
            StockPostingId = root.Posting.Id,
            PostingLineNo = 1,
            SplitOrdinal = 0,
            SourceLineId = $"{root.Posting.SourceDocumentType}:{root.Posting.SourceDocumentId}:{history.TrxLineNo}:1:0",
            SourceDocumentType = root.Posting.SourceDocumentType,
            SourceDocumentId = root.Posting.SourceDocumentId,
            SourceDocumentNo = root.Posting.SourceDocumentNo,
            SourceDocumentLine = history.TrxLineNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            EffectiveAt = root.Posting.EffectiveAt,
            BusinessDate = root.Posting.BusinessDate,
            PeriodKey = root.Posting.PeriodKey,
            ItemCode = authority.ItemCode,
            WarehouseCode = history.FrWarehouse,
            LocationCode = history.FrLocation,
            LotId = history.FromLotId,
            LotNo = string.IsNullOrWhiteSpace(history.FrLotNo)
                ? null : history.FrLotNo,
            ItemStatus = history.IStatus,
            BaseUom = authority.BaseUom,
            MovementCode = StockReplayFifoInventoryIssueKinds.MovementCode(
                authority.TrxType),
            Direction = -1,
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
            FromBalLocId = authority.PileId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = root.UserId
        });
        history.ValuationStatus = StockValuationStatuses.Valued;
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedStandardMiAsync(
        string companyCode, string branchCode, string userId, int batchNo,
        string expectedTrxType, CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo
            && b.TrxType == expectedTrxType
            && b.BatchStatus == IvBatchStatuses.Posted, cancellationToken);
        if (batch is null)
            return null;
        var prior = await _posting.LoadHistoryForBatchAsync(read,
            companyCode, branchCode, batchNo, cancellationToken);
        if (prior.Count != 1 || prior[0].ICode is null
            || prior[0].EntryRole != "ORIGINAL"
            || prior[0].LedgerVersion != 2
            || prior[0].StockPostingId is not > 0)
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
        var root = ledger.Context;
        if (root is null)
            throw InvalidStandardTransfer("Standard MI rollback needs an active root.");
        root.CostMethod = StockCostMethods.Standard;
        var currentBatch = await _posting.LockBatchForUpdateAsync(write,
            companyCode, branchCode, batchNo, cancellationToken);
        var details = currentBatch is null ? []
            : await _posting.LoadDetailsForBatchAsync(write,
                currentBatch.Id, cancellationToken);
        var histories = await _posting.LoadHistoryForBatchAsync(write,
            companyCode, branchCode, batchNo, cancellationToken);
        if (currentBatch is null || currentBatch.Id != batch.Id
            || currentBatch.BatchStatus != IvBatchStatuses.Posted
            || currentBatch.DeletedAtUtc is not null
            || currentBatch.ForceCloseDate is not null
            || details.Count != 1 || histories.Count != 1
            || histories[0].Id != prior[0].Id)
            throw InvalidStandardTransfer("The posted Standard MI changed before rollback.");
        var detail = details[0];
        var history = histories[0];
        var owner = await write.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == history.StockPostingId,
                cancellationToken);
        var pileId = history.FromBalLocId;
        var quantity = history.FrStdQty;
        if (history.EntryRole != "ORIGINAL" || history.LedgerVersion != 2
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.TrxType != expectedTrxType
            || history.ICode is null || pileId is not > 0
            || quantity is not > 0m || history.ToBalLocId is not null
            || detail.DocumentRevision != root.Posting.DocumentRevision
            || detail.ICode != history.ICode || detail.FromBalLocId != pileId
            || detail.FrStdQty != quantity
            || detail.FrStdUom != history.FrStdUom
            || detail.FrWarehouse != history.FrWarehouse
            || detail.FrLocation != history.FrLocation
            || detail.FromLotId != history.FromLotId
            || !string.Equals(detail.FrLotNo ?? string.Empty,
                history.FrLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || detail.IStatus != history.IStatus
            || currentBatch.TrxDtTime != history.TrxDtTime
            || owner is null || owner.SealedAtUtc is null
            || owner.CompanyCode != companyCode
            || owner.BranchCode != branchCode
            || owner.SourceModule != "INVENTORY"
            || owner.SourceDocumentType != expectedTrxType
            || owner.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.PostingRole != "PRIMARY"
            || owner.CommandType != $"IV_POST_{expectedTrxType}"
            || owner.EffectiveAt != history.TrxDtTime
            || owner.LedgerEpochId != history.LedgerEpochId
            || root.Posting.PostingRole != "REVERSAL"
            || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != expectedTrxType
            || root.Posting.SourceDocumentId != owner.SourceDocumentId
            || root.Posting.SourceDocumentNo != owner.SourceDocumentNo
            || root.Posting.ReversesPostingId != owner.Id
            || root.Posting.EffectiveAt != history.TrxDtTime
            || root.Epoch.Id != history.LedgerEpochId)
            throw InvalidStandardTransfer("The rollback root does not own this Standard MI.");
        var balance = await write.IvBalLocs.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == pileId.Value
                && b.CompanyCode == companyCode
                && b.BranchCode == branchCode, cancellationToken);
        var master = await write.IvStockMasters.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CompanyCode == companyCode
                && m.ICode == history.ICode, cancellationToken);
        if (balance is null || master is null
            || !await StockReplayInventoryIssuePileAuthority.MatchesAsync(
                write, master, balance, detail, cancellationToken,
                requireActive: false)
            || balance.LotId != history.FromLotId
            || !string.Equals(balance.LotNo ?? string.Empty,
                history.FrLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            throw InvalidStandardTransfer("The Standard MI pile differs from its original issue.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, history.TrxDtTime, cancellationToken) is string closed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        }
        var key = new StockReplayEventKey(history.TrxDtTime,
            StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
            $"H{history.Id:D10}", 0);
        var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            write, companyCode, branchCode, pileId.Value,
            new StockReplayPhysicalEvent(history.Id, key, -quantity.Value),
            cancellationToken);
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
            write, companyCode, branchCode, history.ICode, key,
            cancellationToken);
        var revision = await new ItemStandardCostResolver().ResolveAsync(write,
            companyCode, branchCode, history.ICode,
            history.TrxDtTime, cancellationToken);
        var active = (await StockReplayValuationGenerationResolver.ResolveAsync(
            write, companyCode, branchCode, [history],
            cancellationToken))[history.Id];
        var original = StockReplayStandardHistoryEventFactory.Create(
            history, history.Id, key, active, revision);
        var amount = StockLedgerPrecision.Money(
            quantity.Value * revision.TotalStandardCost);
        var projection = StockReplayStandardSimulator.SimulateRemoval(
            tail.CurrentQuantity, tail.CurrentValue, original,
            tail.LaterEvents);
        if (active.Count != 1 || active[0].StockPostingId != owner.Id
            || active[0].OriginalValuationFactId is not null
            || active[0].ValuationSource != StockValuationSources.Standard
            || active[0].MovementCode !=
                StockReplayFifoInventoryIssueKinds.MovementCode(expectedTrxType)
            || active[0].FromBalLocId != pileId
            || active[0].LotId != history.FromLotId
            || !string.Equals(active[0].LotNo ?? string.Empty,
                history.FrLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || active[0].Direction != -1
            || active[0].BaseQty != quantity.Value
            || active[0].CostAmount != amount
            || active[0].BaseCostAmount != amount
            || original.OldSignedQuantity != -quantity.Value
            || original.OldSignedValue != -amount
            || physical.Projection.FinalQuantity
                != physical.CurrentQuantity + quantity.Value
            || projection.FinalQuantity != tail.CurrentQuantity
                + quantity.Value
            || projection.FinalValue != tail.CurrentValue + amount
            || tail.LaterEvents.Count != projection.LaterSteps.Count
            || tail.LaterEvents.Zip(projection.LaterSteps).Any(p =>
                p.First.OldSignedQuantity != p.Second.SignedQuantity
                || p.First.OldSignedValue != p.Second.SignedValue)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(physical)
            || await CheckStandardTransferDescendantsAsync(write,
                companyCode, branchCode, history.ICode, key,
                tail.LaterEvents, cancellationToken) is not null)
            throw InvalidStandardTransfer("The Standard MI rollback changes a later movement.");
        var result = await RollBackInventoryMICoreAsync(write,
            companyCode, branchCode, userId, batchNo,
            expectedTrxType, cancellationToken, root);
        if (!result.Succeeded)
        {
            await tx.RollbackAsync(cancellationToken);
            return result;
        }
        await write.SaveChangesAsync(cancellationToken);
        var finalPile = await write.IvBalLocs.AsNoTracking()
            .SingleAsync(b => b.Id == pileId.Value, cancellationToken);
        var reversals = await write.IvTrxHistories.Where(h =>
            h.StockPostingId == root.Posting.Id
            && h.ReversesHistoryId == history.Id
            && h.EntryRole == "REVERSAL").ToArrayAsync(cancellationToken);
        if (finalPile.StdQty != physical.Projection.FinalQuantity
            || reversals.Length != 1
            || reversals[0].ToBalLocId != pileId
            || reversals[0].ToStdQty != quantity)
            throw InvalidStandardTransfer("The Standard MI physical reversal differs from its projection.");
        await StockReplayValuationReversalWriter.AppendAsync(root,
            history.Id, [new StockReplayValuationReversalLine(
                active[0].Id, 1, 0, reversals[0].Id)],
            cancellationToken);
        reversals[0].ValuationStatus = StockValuationStatuses.Reversed;
        var permit = new StockReplayStandardWritePermit(root,
            history.ICode, physical.PlanHash,
            tail.CurrentQuantity, tail.CurrentValue,
            projection.FinalQuantity, projection.FinalValue,
            ImmutableDictionary<int, decimal>.Empty.Add(
                pileId.Value, physical.Projection.FinalQuantity));
        await StockReplayStandardCostStateWriter.ApplyAsync(root,
            permit, cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(root,
            [], cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        return result;
    }
}
