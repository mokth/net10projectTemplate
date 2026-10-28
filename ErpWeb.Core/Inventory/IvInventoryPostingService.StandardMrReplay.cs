using System.Collections.Immutable;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedStandardMrAsync(
        string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken,
        string receiptType = IvTrxTypes.MiscellaneousReceipt,
        string? userId = null)
    {
        var goodsReceipt = receiptType == IvTrxTypes.GoodsReceive;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo && b.TrxType == receiptType
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
                    $"Backdated Standard {receiptType} currently needs one valid receipt line.")
                : null;
        var detail = details[0];
        var method = await read.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == detail.ICode)
            .Select(s => s.CostMethod).SingleOrDefaultAsync(cancellationToken);
        if (method != StockCostMethods.Standard)
            return null;
        var hasLater = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == detail.ICode
                && f.EffectiveAt > batch.TrxDtTime
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLater)
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        if (FuturePostDateError(batch) is string future)
            return IvInventoryPostingBatchResult.Fail(batchNo, future);
        var pileId = await StockReplayInventoryReceiptPileLocator.FindUniqueAsync(
            read, companyCode, branchCode, detail, cancellationToken);
        if (pileId is not > 0)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                $"Backdated Standard {receiptType} needs one existing destination pile.");
        var authority = goodsReceipt
            ? await StockReplayGrReceiptAuthorityReader.ReadAsync(
                read, companyCode, branchCode, batchNo, pileId.Value,
                cancellationToken, allowInvoiced: true)
            : await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
                read, companyCode, branchCode, batchNo, pileId.Value,
                cancellationToken, allowExistingLot: true);
        PoInvoice? replayInvoice = null;
        if (goodsReceipt)
        {
            var invoiceCheck = await LoadSingleGrReplayInvoiceAsync(read,
                companyCode, branchCode, detail.PoNo, detail.PoRelNo,
                detail.PoLineNo, authority.ItemCode, batch.TrxDtTime,
                cancellationToken, allowPostedCreditNote: true);
            if (invoiceCheck.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    invoiceCheck.Error);
            replayInvoice = invoiceCheck.Invoice;
        }
        var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(
            read, companyCode, branchCode, authority.BalLocId,
            authority.EffectiveAt, authority.Quantity, cancellationToken);
        var key = physical.Candidate.Key;
        var prepared = await ReadStandardMrInsertionAsync(read,
            authority, physical, cancellationToken, replayInvoice);
        if (prepared.Error is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
        var fingerprint = StandardMrFingerprint(physical, authority,
            prepared.Tail!, prepared.Projection!, prepared.Revision!);

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(write,
            companyCode, branchCode, cancellationToken);
        Dictionary<string, ErpWeb.Model.Entities.Purchase.PoOrder>? grOrders = null;
        IReadOnlyList<IvTrxBatchDetail>? grDetails = null;
        if (goodsReceipt)
        {
            var snapshot = await LoadGoodsReceiptSnapshotAsync(write,
                companyCode, branchCode, batchNo, receiptType,
                cancellationToken);
            if (snapshot.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo, snapshot.Error);
            grDetails = snapshot.Details!;
            var locked = await LockGoodsReceiptPurchaseOrdersAsync(write,
                companyCode, branchCode, grDetails, cancellationToken);
            if (locked.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo, locked.Error);
            grOrders = locked.Orders!;
            var poError = await ValidateGoodsReceiptPoQtyAsync(write,
                grOrders, grDetails, +1, receiptType, cancellationToken);
            if (poError is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            var currentInvoice = await LoadSingleGrReplayInvoiceAsync(write,
                companyCode, branchCode, grDetails[0].PoNo,
                grDetails[0].PoRelNo, grDetails[0].PoLineNo,
                authority.ItemCode, batch.TrxDtTime, cancellationToken,
                allowPostedCreditNote: true);
            if (currentInvoice.Error is not null
                || currentInvoice.Invoice?.DocNo != replayInvoice?.DocNo
                || currentInvoice.Invoice?.CostingRevision
                    != replayInvoice?.CostingRevision)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    currentInvoice.Error ?? "The posted invoice changed after Standard GR planning.");
        }
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
        if (root is null || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != receiptType
            || root.Posting.SourceDocumentId != authority.BatchId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || root.Posting.PostingRole != "PRIMARY"
            || root.Posting.DocumentRevision != authority.DocumentRevision
            || root.Posting.EffectiveAt != authority.EffectiveAt
            || root.Posting.PostingSequence != key.OriginalPostingSequence
            || root.Epoch.Id != physical.LedgerEpochId)
            throw InvalidStandardTransfer("The Standard receipt root differs from its draft.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, authority.EffectiveAt, cancellationToken)
            is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root,
            physical, cancellationToken);
        var currentAuthority = goodsReceipt
            ? await StockReplayGrReceiptAuthorityReader.ReadAsync(
                write, companyCode, branchCode, batchNo, physical.BalLocId,
                cancellationToken, allowInvoiced: replayInvoice is not null)
            : await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
                write, companyCode, branchCode, batchNo,
                physical.BalLocId, cancellationToken,
                allowExistingLot: true);
        var current = await ReadStandardMrInsertionAsync(write,
            currentAuthority, physical, cancellationToken, replayInvoice);
        if (currentAuthority != authority || current.Error is not null
            || current.Tail is null || current.Projection is null
            || current.Revision is null
            || StandardMrFingerprint(physical, currentAuthority,
                current.Tail, current.Projection, current.Revision)
                != fingerprint)
            throw InvalidStandardTransfer("The Standard receipt tail changed before posting.");
        var historyId = await AppendStandardMrPhysicalAsync(root,
            physical, authority, cancellationToken, receiptType);
        await AppendStandardMrValueAsync(root, authority,
            current.Revision, historyId, cancellationToken);
        var permit = new StockReplayStandardWritePermit(root,
            authority.ItemCode, fingerprint,
            current.Tail.CurrentQuantity, current.Tail.CurrentValue,
            current.Projection.FinalQuantity, current.Projection.FinalValue,
            ImmutableDictionary<int, decimal>.Empty.Add(
                physical.BalLocId, physical.Projection.FinalQuantity));
        await StockReplayStandardCostStateWriter.ApplyAsync(root,
            permit, cancellationToken);
        if (goodsReceipt)
            await ApplyGoodsReceiptPoQtyAsync(write, grOrders!, grDetails!,
                +1, authority.EffectiveAt, userId ?? root.UserId,
                cancellationToken);
        if (replayInvoice is not null)
        {
            var remap = await new PurchaseReceiptCostSettlementService()
                .RemapValueNeutralInvoiceAsync(root, replayInvoice,
                    _purchaseCostAdjustments!, cancellationToken,
                    allowPostedCreditNote: true,
                    replayPlanHash: fingerprint,
                    replayBranchWatermark: physical.BranchWatermark);
            if (!remap.Succeeded)
                throw InvalidStandardTransfer(remap.Error
                    ?? "The posted invoice could not be remapped.");
        }
        await _coordinator!.CompleteReplayInTransactionAsync(root,
            [historyId], cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == authority.BatchId)
            .Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private static async Task<(StockReplayStandardFinancialTail? Tail,
        StockReplayStandardResult? Projection, EffectiveStandardCost? Revision,
        string? Error)> ReadStandardMrInsertionAsync(
        AppDbContext db, StockReplayFifoMrReceiptAuthority authority,
        StockReplayPhysicalPlan physical, CancellationToken cancellationToken,
        PoInvoice? replayInvoice = null)
    {
        var key = physical.Candidate.Key;
        if (physical.BalLocId != authority.BalLocId
            || key.EffectiveAt != authority.EffectiveAt
            || physical.Candidate.SignedDelta != authority.Quantity
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(physical))
            return (null, null, null,
                "The Standard MR changes a later physical movement.");
        var revision = await new ItemStandardCostResolver().ResolveAsync(db,
            physical.CompanyCode, physical.BranchCode,
            authority.ItemCode, key.EffectiveAt, cancellationToken);
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(db,
            physical.CompanyCode, physical.BranchCode,
            authority.ItemCode, key, cancellationToken);
        if (!tail.CurrentQuantityByPile.TryGetValue(physical.BalLocId,
                out var currentPileQuantity)
            || currentPileQuantity != physical.CurrentQuantity)
            return (null, null, null,
                "The Standard MR item tail differs from its destination pile.");
        var candidate = new StockReplayStandardEvent(physical.Candidate.EventId,
            key, StockReplayStandardEventKinds.Movement, 0m, 0m,
            authority.Quantity, revision.TotalStandardCost, revision.Id);
        await StockReplayStandardRevisionAuthority.ValidateAsync(db,
            physical.CompanyCode, physical.BranchCode,
            authority.ItemCode, candidate, tail.LaterEvents,
            cancellationToken);
        var projection = StockReplayStandardSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue,
            candidate, tail.LaterEvents);
        if (projection.Candidate.SignedQuantity != authority.Quantity
            || projection.Candidate.SignedValue != StockLedgerPrecision.Money(
                authority.Quantity * revision.TotalStandardCost)
            || projection.FinalQuantity != tail.CurrentQuantity
                + authority.Quantity
            || projection.FinalValue != tail.CurrentValue
                + projection.Candidate.SignedValue
            || tail.LaterEvents.Count != projection.LaterSteps.Count
            || tail.LaterEvents.Zip(projection.LaterSteps).Any(p =>
                p.First.OldSignedQuantity != p.Second.SignedQuantity
                || p.First.OldSignedValue != p.Second.SignedValue)
            || tail.LaterEvents.Count == 0 && physical.LaterEvents.Count != 0
            || tail.LaterEvents.Count > 0
                && await CheckStandardTransferDescendantsAsync(db,
                    physical.CompanyCode, physical.BranchCode,
                    authority.ItemCode, key, tail.LaterEvents,
                    cancellationToken, replayInvoice, allowBoundedSales: true) is not null)
            return (null, null, null,
                "The Standard MR changes a later cost or has an unsupported descendant.");
        return (tail, projection, revision, null);
    }

    private static string StandardMrFingerprint(
        StockReplayPhysicalPlan physical,
        StockReplayFifoMrReceiptAuthority authority,
        StockReplayStandardFinancialTail tail,
        StockReplayStandardResult projection,
        EffectiveStandardCost revision) =>
        StockPostingFingerprint.Create(new
        {
            physical.PlanHash, authority,
            revision.Id, revision.TotalStandardCost,
            tail.CurrentQuantity, tail.CurrentValue, tail.LaterEvents,
            Piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                .Select(p => new { p.Key, p.Value,
                    Events = tail.PhysicalByPile[p.Key] }).ToArray(),
            projection
        }, new { }).RequestFingerprint;

    private static string StandardMrRemovalFingerprint(
        StockReplayPhysicalRemovalPlan physical,
        IvTrxHistory history,
        StockReplayStandardFinancialTail tail,
        EffectiveStandardCost revision,
        IReadOnlyList<StockValuationFact> active,
        StockReplayStandardEvent original,
        StockReplayStandardRemovalResult projection) =>
        StockPostingFingerprint.Create(new
        {
            physical.PlanHash,
            HistoryId = history.Id,
            RevisionId = revision.Id, revision.TotalStandardCost,
            Active = active.OrderBy(f => f.Id).Select(f => new
            {
                f.Id, f.StockPostingId, f.ValuationSource,
                f.MovementCode, f.Direction, f.BaseQty,
                f.CostAmount, f.BaseCostAmount, f.ToBalLocId,
                f.LotId, f.LotNo
            }).ToArray(),
            original,
            tail.CurrentQuantity, tail.CurrentValue, tail.LaterEvents,
            Piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                .Select(p => new { p.Key, p.Value,
                    Events = tail.PhysicalByPile[p.Key] }).ToArray(),
            projection
        }, new { }).RequestFingerprint;

    private static async Task<int> AppendStandardMrPhysicalAsync(
        StockPostingContext root, StockReplayPhysicalPlan plan,
        StockReplayFifoMrReceiptAuthority authority,
        CancellationToken cancellationToken,
        string receiptType = IvTrxTypes.MiscellaneousReceipt)
    {
        var db = root.Db;
        var batch = await db.IvTrxBatches.SingleAsync(b =>
            b.Id == authority.BatchId, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
            d.Id == authority.DetailId, cancellationToken);
        var balance = await db.IvBalLocs.SingleAsync(b =>
            b.Id == authority.BalLocId, cancellationToken);
        db.ChangeTracker.DetectChanges();
        if (batch.BatchStatus != IvBatchStatuses.New
            || detail.DocumentRevision != root.Posting.DocumentRevision
            || db.Entry(batch).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || balance.StdQty != plan.CurrentQuantity
            || balance.LotId != authority.LotId
            || !string.Equals(balance.LotNo ?? string.Empty,
                authority.LotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || !balance.RowVersion.SequenceEqual(plan.BalanceRowVersion)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == root.CompanyCode
                && h.BranchCode == root.BranchCode
                && ((h.BatchNo == authority.BatchNo
                        && h.DocumentRevision == detail.DocumentRevision)
                    || h.StockPostingId == root.Posting.Id),
                cancellationToken))
            throw InvalidStandardTransfer("The Standard MR draft or pile changed.");
        var now = DateTime.UtcNow;
        var user = root.UserId.Length > 10 ? root.UserId[..10] : root.UserId;
        if (balance.StdQty == 0m)
        {
            balance.UnitPrice = authority.UnitCost;
            balance.Cost = authority.UnitCost;
            balance.PriceEvidence = "EXPLICIT_COMPANY_BASE_PRICE";
        }
        else if (balance.UnitPrice != authority.UnitCost)
            balance.PriceEvidence = null;
        balance.StdQty = plan.Projection.FinalQuantity;
        balance.TransDate = plan.LaterEvents.Count == 0
            ? plan.Candidate.Key.EffectiveAt
            : plan.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        detail.ToBalLocId = balance.Id;
        detail.ToLotId = authority.LotId;
        var history = new IvTrxHistory
        {
            CompanyCode = root.CompanyCode,
            BranchCode = root.BranchCode,
            BatchNo = authority.BatchNo,
            TrxLineNo = detail.TrxLineNo,
            TrxType = receiptType,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode,
            ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = authority.ItemCode,
            IDesc = detail.IDesc,
            ToWarehouse = detail.ToWarehouse,
            ToLocation = detail.ToLocation,
            ToLotNo = detail.ToLotNo,
            ToStdQty = detail.ToStdQty,
            ToStdUom = detail.ToStdUom,
            ToPurQty = detail.ToPurQty,
            ToPurUom = detail.ToPurUom,
            IStatus = detail.IStatus,
            DoNo = detail.DoNo,
            PoNo = detail.PoNo,
            PoRelNo = detail.PoRelNo,
            PoLineNo = detail.PoLineNo,
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
            ToBalLocId = balance.Id,
            ToLotId = authority.LotId,
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
        return history.Id;
    }

    private static async Task AppendStandardMrValueAsync(
        StockPostingContext root, StockReplayFifoMrReceiptAuthority authority,
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
            throw InvalidStandardTransfer("The Standard MR company has no base currency.");
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
            WarehouseCode = history.ToWarehouse,
            LocationCode = history.ToLocation,
            LotId = authority.LotId,
            LotNo = authority.LotNo,
            ItemStatus = history.IStatus,
            BaseUom = authority.BaseUom,
            MovementCode = "RECEIPT_IN",
            Direction = 1,
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
            ToBalLocId = authority.BalLocId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = root.UserId
        });
        history.ValuationStatus = StockValuationStatuses.Valued;
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedStandardMrAsync(
        string companyCode, string branchCode, string userId, int batchNo,
        CancellationToken cancellationToken,
        string receiptType = IvTrxTypes.MiscellaneousReceipt)
    {
        var goodsReceipt = receiptType == IvTrxTypes.GoodsReceive;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo
            && b.TrxType == receiptType
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
        var priorHistory = prior[0];
        if (priorHistory.ToBalLocId is not > 0
            || priorHistory.ToStdQty is not > 0m)
            return null;
        var preparedOwner = await read.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == priorHistory.StockPostingId,
                cancellationToken);
        if (preparedOwner is null)
            return null;
        var preparedKey = new StockReplayEventKey(priorHistory.TrxDtTime,
            StockReplayEventKey.OrdinaryClass,
            preparedOwner.PostingSequence,
            $"H{priorHistory.Id:D10}", 0);
        var preparedPhysical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            read, companyCode, branchCode, priorHistory.ToBalLocId.Value,
            new StockReplayPhysicalEvent(priorHistory.Id, preparedKey,
                priorHistory.ToStdQty.Value), cancellationToken);
        var preparedTail = await StockReplayStandardFinancialTailReader
            .ReadLaterAsync(read, companyCode, branchCode,
                priorHistory.ICode, preparedKey, cancellationToken);
        var preparedRevision = await new ItemStandardCostResolver().ResolveAsync(
            read, companyCode, branchCode, priorHistory.ICode,
            priorHistory.TrxDtTime, cancellationToken);
        var preparedActive = (await StockReplayValuationGenerationResolver
            .ResolveAsync(read, companyCode, branchCode, [priorHistory],
                cancellationToken))[priorHistory.Id];
        var preparedOriginal = StockReplayStandardHistoryEventFactory.Create(
            priorHistory, priorHistory.Id, preparedKey, preparedActive,
            preparedRevision);
        var preparedProjection = StockReplayStandardSimulator.SimulateRemoval(
            preparedTail.CurrentQuantity, preparedTail.CurrentValue,
            preparedOriginal, preparedTail.LaterEvents);
        var preparedFingerprint = StandardMrRemovalFingerprint(
            preparedPhysical, priorHistory, preparedTail,
            preparedRevision, preparedActive, preparedOriginal,
            preparedProjection);

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
            throw InvalidStandardTransfer("Standard MR rollback needs an active root.");
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
            throw InvalidStandardTransfer("The posted Standard MR changed before rollback.");
        var detail = details[0];
        var history = histories[0];
        PoInvoice? replayInvoice = null;
        if (goodsReceipt)
        {
            var invoiceCheck = await LoadSingleGrReplayInvoiceAsync(write,
                companyCode, branchCode, history.PoNo,
                history.PoRelNo, history.PoLineNo,
                history.ICode!, history.TrxDtTime,
                cancellationToken, allowPostedCreditNote: true);
            if (invoiceCheck.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    invoiceCheck.Error);
            replayInvoice = invoiceCheck.Invoice;
        }
        var owner = await write.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == history.StockPostingId,
                cancellationToken);
        var pileId = history.ToBalLocId;
        var quantity = history.ToStdQty;
        if (history.EntryRole != "ORIGINAL" || history.LedgerVersion != 2
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.TrxType != receiptType
            || history.ICode is null || pileId is not > 0
            || quantity is not > 0m || history.FromBalLocId is not null
            || detail.DocumentRevision != root.Posting.DocumentRevision
            || detail.ICode != history.ICode || detail.ToBalLocId != pileId
            || detail.ToStdQty != quantity
            || detail.ToStdUom != history.ToStdUom
            || detail.ToWarehouse != history.ToWarehouse
            || detail.ToLocation != history.ToLocation
            || detail.ToLotId != history.ToLotId
            || !string.Equals(detail.ToLotNo ?? string.Empty,
                history.ToLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || detail.IStatus != history.IStatus
            || currentBatch.TrxDtTime != history.TrxDtTime
            || owner is null || owner.SealedAtUtc is null
            || owner.CompanyCode != companyCode
            || owner.BranchCode != branchCode
            || owner.SourceModule != "INVENTORY"
            || owner.SourceDocumentType != receiptType
            || owner.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.PostingRole != "PRIMARY"
            || owner.CommandType != $"IV_POST_{receiptType}"
            || owner.EffectiveAt != history.TrxDtTime
            || owner.LedgerEpochId != history.LedgerEpochId
            || root.Posting.PostingRole != "REVERSAL"
            || root.Posting.SourceModule != "INVENTORY"
            || root.Posting.SourceDocumentType != receiptType
            || root.Posting.SourceDocumentId != owner.SourceDocumentId
            || root.Posting.SourceDocumentNo != owner.SourceDocumentNo
            || root.Posting.ReversesPostingId != owner.Id
            || root.Posting.EffectiveAt != history.TrxDtTime
            || root.Epoch.Id != history.LedgerEpochId)
            throw InvalidStandardTransfer("The rollback root does not own this Standard MR.");
        var balance = await write.IvBalLocs.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == pileId.Value
                && b.CompanyCode == companyCode
                && b.BranchCode == branchCode, cancellationToken);
        if (balance is null || balance.ICode != history.ICode
            || balance.LotId != history.ToLotId
            || !string.Equals(balance.LotNo ?? string.Empty,
                history.ToLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || balance.WhCode != history.ToWarehouse
            || balance.LocCode != (history.ToLocation ?? string.Empty)
            || balance.IStatus != history.IStatus
            || balance.StdUom != history.ToStdUom)
            throw InvalidStandardTransfer("The Standard MR pile differs from its original receipt.");
        var master = await write.IvStockMasters.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CompanyCode == companyCode
                && m.ICode == history.ICode, cancellationToken);
        if (master is null || !master.StockControl
            || master.LotControl != (balance.LotId is > 0)
            || balance.LotId is int lotId
                && !await write.IvLots.AsNoTracking().AnyAsync(l =>
                    l.Id == lotId && l.CompanyCode == companyCode
                    && l.ICode == history.ICode && l.LotNo == balance.LotNo,
                    cancellationToken))
            throw InvalidStandardTransfer("The Standard MR destination lot changed.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, history.TrxDtTime, cancellationToken) is string closed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        }
        if (owner.Id != preparedOwner.Id
            || owner.PostingSequence != preparedOwner.PostingSequence
            || pileId.Value != preparedPhysical.BalLocId
            || quantity.Value != preparedPhysical.RemovedActiveEvent.SignedDelta)
            throw InvalidStandardTransfer(
                "The Standard receipt changed after rollback planning.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root,
            preparedPhysical, cancellationToken);
        var key = preparedKey;
        var physical = preparedPhysical;
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
        if (StandardMrRemovalFingerprint(physical, history, tail,
                revision, active, original, projection) != preparedFingerprint
            || active.Count != 1 || active[0].StockPostingId != owner.Id
            || active[0].OriginalValuationFactId is not null
            || active[0].ValuationSource != StockValuationSources.Standard
            || active[0].MovementCode != "RECEIPT_IN"
            || active[0].ToBalLocId != pileId
            || active[0].LotId != history.ToLotId
            || !string.Equals(active[0].LotNo ?? string.Empty,
                history.ToLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || active[0].Direction != 1
            || active[0].BaseQty != quantity.Value
            || active[0].CostAmount != amount
            || active[0].BaseCostAmount != amount
            || original.OldSignedQuantity != quantity.Value
            || original.OldSignedValue != amount
            || physical.Projection.FinalQuantity
                != physical.CurrentQuantity - quantity.Value
            || projection.FinalQuantity != tail.CurrentQuantity
                - quantity.Value
            || projection.FinalValue != tail.CurrentValue - amount
            || tail.LaterEvents.Count != projection.LaterSteps.Count
            || tail.LaterEvents.Zip(projection.LaterSteps).Any(p =>
                p.First.OldSignedQuantity != p.Second.SignedQuantity
                || p.First.OldSignedValue != p.Second.SignedValue)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(physical)
            || tail.LaterEvents.Count == 0 && physical.LaterEvents.Count != 0
            || tail.LaterEvents.Count > 0
                && await CheckStandardTransferDescendantsAsync(write,
                    companyCode, branchCode, history.ICode, key,
                    tail.LaterEvents, cancellationToken,
                    replayInvoice, allowBoundedSales: true) is not null)
            throw InvalidStandardTransfer("The Standard MR rollback changes a later movement.");
        Dictionary<string, ErpWeb.Model.Entities.Purchase.PoOrder>? grOrders = null;
        IReadOnlyList<IvTrxBatchDetail>? grDetails = null;
        if (goodsReceipt)
        {
            var snapshot = await LoadGoodsReceiptSnapshotAsync(write,
                companyCode, branchCode, batchNo, receiptType,
                cancellationToken);
            if (snapshot.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    snapshot.Error);
            grDetails = snapshot.Details!;
            var identityError = await ValidateGoodsReceiptRollbackIdentityAsync(
                write, companyCode, branchCode, currentBatch, grDetails,
                cancellationToken);
            if (identityError is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    identityError);
            if (await write.PurchaseReceiptCostSettlements.AsNoTracking()
                .AnyAsync(s => s.CompanyCode == companyCode
                    && s.BranchCode == branchCode
                    && s.ReceiptInventoryHistoryId == history.Id
                    && s.ReversesSettlementId == null
                    && !write.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id)
                    && (replayInvoice == null
                        || s.PiDocNo != replayInvoice.DocNo
                        || s.PiCostingRevision != replayInvoice.CostingRevision),
                    cancellationToken))
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    "The goods receipt has invoice settlement and needs procurement replay before historical rollback.");
            var locked = await LockGoodsReceiptPurchaseOrdersAsync(write,
                companyCode, branchCode, grDetails, cancellationToken);
            if (locked.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    locked.Error);
            grOrders = locked.Orders!;
            var poError = await ValidateGoodsReceiptPoQtyAsync(write,
                grOrders, grDetails, -1, receiptType, cancellationToken);
            if (poError is not null || grOrders.Values.SelectMany(o => o.Details)
                    .Any(d => d.InvoicedQty != 0m)
                    && replayInvoice is null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    poError ?? "Historical GR rollback needs procurement replay while the purchase order is invoiced.");
        }
        var result = await RollBackInventoryMRCoreAsync(write,
            companyCode, branchCode, userId, batchNo,
            receiptType, cancellationToken, root);
        if (!result.Succeeded)
        {
            await tx.RollbackAsync(cancellationToken);
            return result;
        }
        if (goodsReceipt)
            await ApplyGoodsReceiptPoQtyAsync(write, grOrders!, grDetails!,
                -1, history.TrxDtTime, userId, cancellationToken);
        await write.SaveChangesAsync(cancellationToken);
        var finalPile = await write.IvBalLocs.AsNoTracking()
            .SingleAsync(b => b.Id == pileId.Value, cancellationToken);
        var reversals = await write.IvTrxHistories.Where(h =>
            h.StockPostingId == root.Posting.Id
            && h.ReversesHistoryId == history.Id
            && h.EntryRole == "REVERSAL").ToArrayAsync(cancellationToken);
        if (finalPile.StdQty != physical.Projection.FinalQuantity
            || reversals.Length != 1
            || reversals[0].FromBalLocId != pileId
            || reversals[0].FrStdQty != quantity
            || reversals[0].FromLotId != history.ToLotId)
            throw InvalidStandardTransfer("The Standard MR physical reversal differs from its projection.");
        await StockReplayValuationReversalWriter.AppendAsync(root,
            history.Id, [new StockReplayValuationReversalLine(
                active[0].Id, 1, 0, reversals[0].Id)],
            cancellationToken);
        reversals[0].ValuationStatus = StockValuationStatuses.Reversed;
        var permit = new StockReplayStandardWritePermit(root,
            history.ICode, preparedFingerprint,
            tail.CurrentQuantity, tail.CurrentValue,
            projection.FinalQuantity, projection.FinalValue,
            ImmutableDictionary<int, decimal>.Empty.Add(
                pileId.Value, physical.Projection.FinalQuantity));
        await StockReplayStandardCostStateWriter.ApplyAsync(root,
            permit, cancellationToken);
        if (replayInvoice is not null)
        {
            var remap = await new PurchaseReceiptCostSettlementService()
                .RemapValueNeutralInvoiceAsync(root, replayInvoice,
                    _purchaseCostAdjustments!, cancellationToken,
                    removingReceipt: true,
                    allowPostedCreditNote: true,
                    replayPlanHash: preparedFingerprint,
                    replayBranchWatermark: physical.BranchWatermark);
            if (!remap.Succeeded)
                throw InvalidStandardTransfer(remap.Error
                    ?? "The posted invoice could not be remapped.");
        }
        await _coordinator!.CompleteReplayInTransactionAsync(root,
            [], cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        return result;
    }
}
