using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedFifoMrAsync(
        string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken,
        string receiptType = IvTrxTypes.MiscellaneousReceipt,
        string? userId = null)
    {
        var goodsReceipt = receiptType == IvTrxTypes.GoodsReceive;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != receiptType
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
        var pileId = await StockReplayInventoryReceiptPileLocator.FindUniqueAsync(
            read, companyCode, branchCode, detail, cancellationToken);
        if (pileId is not > 0)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                $"Backdated FIFO {receiptType} needs one existing destination pile.");
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
                detail.PoLineNo, itemCode, batch.TrxDtTime,
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
        var financial = await StockReplayFifoInsertionPlanner.BuildMiscReceiptAsync(
            read, companyCode, branchCode, batchNo, physical,
            cancellationToken, goodsReceipt,
            allowInvoiced: replayInvoice is not null);
        if (await CheckSimpleFifoInventoryTailAsync(read, financial, key,
                cancellationToken, replayInvoice) is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);

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
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    snapshot.Error);
            grDetails = snapshot.Details!;
            var locked = await LockGoodsReceiptPurchaseOrdersAsync(write,
                companyCode, branchCode, grDetails, cancellationToken);
            if (locked.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    locked.Error);
            grOrders = locked.Orders!;
            var poError = await ValidateGoodsReceiptPoQtyAsync(write,
                grOrders, grDetails, +1, receiptType, cancellationToken);
            if (poError is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            var currentInvoice = await LoadSingleGrReplayInvoiceAsync(write,
                companyCode, branchCode, grDetails[0].PoNo,
                grDetails[0].PoRelNo, grDetails[0].PoLineNo,
                itemCode, batch.TrxDtTime, cancellationToken,
                allowPostedCreditNote: true);
            if (currentInvoice.Error is not null
                || currentInvoice.Invoice?.DocNo != replayInvoice?.DocNo
                || currentInvoice.Invoice?.CostingRevision
                    != replayInvoice?.CostingRevision)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    currentInvoice.Error ?? "The posted invoice changed after FIFO GR planning.");
            if (await CheckSimpleFifoInventoryTailAsync(write,
                    financial, key, cancellationToken,
                    replayInvoice) is string changedTail)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    changedTail);
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
        var context = ledger.Context;
        if (context is null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerDisabled,
                "Backdated FIFO MR requires an active V2 FIFO posting root."));
        context.CostMethod = StockCostMethods.Fifo;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, authority.EffectiveAt, cancellationToken)
            is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, authority.EffectiveAt.Date,
            new DateTime(authority.EffectiveAt.Year,
                authority.EffectiveAt.Month,
                DateTime.DaysInMonth(authority.EffectiveAt.Year,
                    authority.EffectiveAt.Month)), cancellationToken);
        var permit = await StockReplayFifoInsertionPlanner
            .PrepareMiscReceiptWithinRootAsync(context, financial, batchNo,
                cancellationToken, goodsReceipt,
                allowInvoiced: replayInvoice is not null);
        var written = await StockReplayFifoMrRootWriter.InsertAsync(
            context, financial, permit, batchNo, cancellationToken,
            goodsReceipt, allowInvoiced: replayInvoice is not null);
        if (goodsReceipt)
            await ApplyGoodsReceiptPoQtyAsync(write, grOrders!, grDetails!,
                +1, authority.EffectiveAt, userId ?? context.UserId,
                cancellationToken);
        if (replayInvoice is not null)
        {
            var remap = await new PurchaseReceiptCostSettlementService()
                .RemapValueNeutralInvoiceAsync(context, replayInvoice,
                    _purchaseCostAdjustments!, cancellationToken,
                    allowPostedCreditNote: true,
                    replayPlanHash: financial.PlanHash,
                    replayBranchWatermark: physical.BranchWatermark);
            if (!remap.Succeeded)
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.ReversalDependency,
                    remap.Error ?? "The posted invoice could not be remapped."));
        }
        await _coordinator!.CompleteReplayInTransactionAsync(
            context, written.RequiredActiveHistoryIds, cancellationToken);
        await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
            context, financial, permit, written.Receipt, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == authority.BatchId)
            .Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo,
            operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedFifoMrAsync(
        string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken,
        string receiptType = IvTrxTypes.MiscellaneousReceipt,
        string? userId = null)
    {
        var goodsReceipt = receiptType == IvTrxTypes.GoodsReceive;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != receiptType
            || batch.BatchStatus != IvBatchStatuses.Posted)
            return null;
        var histories = await _posting.LoadHistoryForBatchAsync(read,
            companyCode, branchCode, batchNo, cancellationToken);
        if (histories.Count != 1)
            return null;
        var original = histories[0];
        if (original.EntryRole != "ORIGINAL" || original.LedgerVersion != 2
            || original.StockPostingId is not > 0
            || original.ICode is null
            || original.TrxType != receiptType
            || original.ToBalLocId is not > 0
            || original.ToStdQty is not > 0m)
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
            || owner.SourceDocumentType != receiptType
            || owner.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.EffectiveAt != original.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "The posted FIFO receipt has no exact sealed origin.");
        var hasLaterValue = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == original.ICode
                && (f.EffectiveAt > owner.EffectiveAt
                    || f.EffectiveAt == owner.EffectiveAt
                        && f.StockPosting!.PostingSequence > owner.PostingSequence)
                && !(f.InventoryHistoryId == original.Id
                    && f.EffectiveAt == owner.EffectiveAt)
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLaterValue)
            return null;
        var key = new StockReplayEventKey(owner.EffectiveAt,
            StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
            $"H{original.Id:D10}", 0);
        var removed = new StockReplayPhysicalEvent(original.Id, key,
            original.ToStdQty.Value);
        var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            read, companyCode, branchCode, original.ToBalLocId.Value,
            removed, cancellationToken);
        var financial = await StockReplayFifoRemovalPlanner.BuildAsync(
            read, companyCode, branchCode, original.ICode, physical,
            cancellationToken);
        PoInvoice? replayInvoice = null;
        if (goodsReceipt)
        {
            var invoiceCheck = await LoadSingleGrReplayInvoiceAsync(read,
                companyCode, branchCode, original.PoNo,
                original.PoRelNo, original.PoLineNo,
                original.ICode, original.TrxDtTime,
                cancellationToken, allowPostedCreditNote: true);
            if (invoiceCheck.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    invoiceCheck.Error);
            replayInvoice = invoiceCheck.Invoice;
        }
        if (await CheckSimpleFifoInventoryTailAsync(read, original.ICode,
                financial.Snapshot, financial.Projection, key,
                cancellationToken, replayInvoice) is string unsupported)
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
                "Backdated FIFO MR rollback needs the original V2 FIFO owner."));
        context.CostMethod = StockCostMethods.Fifo;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, owner.EffectiveAt, cancellationToken)
            is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, owner.EffectiveAt.Date,
            new DateTime(owner.EffectiveAt.Year, owner.EffectiveAt.Month,
                DateTime.DaysInMonth(owner.EffectiveAt.Year,
                    owner.EffectiveAt.Month)), cancellationToken);
        var permit = await StockReplayFifoRemovalPlanner.PrepareWithinRootAsync(
            context, financial, cancellationToken);
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
                write, companyCode, branchCode, snapshot.Batch!, grDetails,
                cancellationToken);
            if (identityError is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    identityError);
            if (await write.PurchaseReceiptCostSettlements.AsNoTracking()
                .AnyAsync(s => s.CompanyCode == companyCode
                    && s.BranchCode == branchCode
                    && s.ReceiptInventoryHistoryId == original.Id
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
            var currentInvoice = await LoadSingleGrReplayInvoiceAsync(write,
                companyCode, branchCode, grDetails[0].PoNo,
                grDetails[0].PoRelNo, grDetails[0].PoLineNo,
                original.ICode, original.TrxDtTime,
                cancellationToken, allowPostedCreditNote: true);
            if (currentInvoice.Error is not null
                || currentInvoice.Invoice?.DocNo != replayInvoice?.DocNo
                || currentInvoice.Invoice?.CostingRevision
                    != replayInvoice?.CostingRevision)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    currentInvoice.Error ?? "The posted invoice changed after FIFO GR rollback planning.");
            if (await CheckSimpleFifoInventoryTailAsync(write,
                    original.ICode, financial.Snapshot,
                    financial.Projection, key, cancellationToken,
                    replayInvoice) is string changedTail)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    changedTail);
            var poError = await ValidateGoodsReceiptPoQtyAsync(write,
                grOrders, grDetails, -1, receiptType, cancellationToken);
            if (poError is not null || grOrders.Values.SelectMany(o => o.Details)
                    .Any(d => d.InvoicedQty != 0m)
                    && replayInvoice is null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    poError ?? "Historical GR rollback needs procurement replay while the purchase order is invoiced.");
        }
        var written = await StockReplayFifoMrRemovalRootWriter.RemoveAsync(
            context, financial, permit, batchNo, cancellationToken,
            goodsReceipt);
        if (goodsReceipt)
            await ApplyGoodsReceiptPoQtyAsync(write, grOrders!, grDetails!,
                -1, original.TrxDtTime, userId ?? context.UserId,
                cancellationToken);
        if (replayInvoice is not null)
        {
            var remap = await new PurchaseReceiptCostSettlementService()
                .RemapValueNeutralInvoiceAsync(context, replayInvoice,
                    _purchaseCostAdjustments!, cancellationToken,
                    removingReceipt: true,
                    allowPostedCreditNote: true,
                    replayPlanHash: financial.PlanHash,
                    replayBranchWatermark: physical.BranchWatermark);
            if (!remap.Succeeded)
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.ReversalDependency,
                    remap.Error ?? "The posted invoice could not be remapped."));
        }
        await _coordinator!.CompleteReplayInTransactionAsync(
            context, written.RequiredActiveHistoryIds, cancellationToken);
        await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
            context, financial, permit, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id)
            .Select(b => b.RollbackOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo,
            operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedFifoMiAsync(
        string companyCode, string branchCode, int batchNo,
        string expectedTrxType, CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != expectedTrxType
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
        if (detail.FromBalLocId is not > 0
            || detail.FrStdQty is not > 0m
            || IvQty.Round(detail.FrStdQty.Value) != detail.FrStdQty.Value)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO MI needs one valid source pile and quantity.");
        var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(
            read, companyCode, branchCode, detail.FromBalLocId.Value,
            batch.TrxDtTime, -detail.FrStdQty.Value, cancellationToken);
        var key = physical.Candidate.Key;
        var financial = await StockReplayFifoInsertionPlanner.BuildAsync(
            read, companyCode, branchCode, itemCode, physical,
            cancellationToken: cancellationToken);
        if (await CheckSimpleFifoInventoryTailAsync(read, financial, key,
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
                "Backdated FIFO MI requires an active V2 posting root."));
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, batch.TrxDtTime, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        if (expectedTrxType == IvTrxTypes.VendorReturn)
        {
            var currentDetail = await write.IvTrxBatchDetails.AsNoTracking()
                .SingleAsync(d => d.BatchId == batch.Id, cancellationToken);
            if (currentDetail.PoNo != detail.PoNo
                || currentDetail.PoRelNo != detail.PoRelNo
                || currentDetail.PoLineNo != detail.PoLineNo
                || currentDetail.FrPurQty != detail.FrPurQty
                || currentDetail.FrPurUom != detail.FrPurUom)
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.LedgerMismatch,
                    "The FIFO Vendor Return PO line changed after replay planning."));
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, batch.TrxDtTime.Date,
            new DateTime(batch.TrxDtTime.Year, batch.TrxDtTime.Month,
                DateTime.DaysInMonth(batch.TrxDtTime.Year,
                    batch.TrxDtTime.Month)), cancellationToken);
        var permit = await StockReplayFifoInsertionPlanner.PrepareWithinRootAsync(
            context, financial, cancellationToken);
        var written = await StockReplayFifoMiRootWriter.InsertAsync(
            context, financial, permit, batchNo, cancellationToken);
        if (expectedTrxType == IvTrxTypes.VendorReturn)
        {
            var postedDetails = await _posting.LoadDetailsForBatchAsync(
                write, batch.Id, cancellationToken);
            var poError = await ApplyVendorReturnPoQtyAsync(write,
                companyCode, branchCode, postedDetails, sign: +1,
                context.UserId, cancellationToken);
            if (poError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            }
            await write.SaveChangesAsync(cancellationToken);
        }
        await _coordinator!.CompleteReplayInTransactionAsync(
            context, written.RequiredActiveHistoryIds, cancellationToken);
        await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
            context, financial, permit, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id)
            .Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo,
            operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedFifoMiAsync(
        string companyCode, string branchCode, int batchNo,
        string expectedTrxType, CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != expectedTrxType
            || batch.BatchStatus != IvBatchStatuses.Posted)
            return null;
        var histories = await _posting.LoadHistoryForBatchAsync(read,
            companyCode, branchCode, batchNo, cancellationToken);
        if (histories.Count != 1)
            return null;
        var original = histories[0];
        if (original.EntryRole != "ORIGINAL" || original.LedgerVersion != 2
            || original.StockPostingId is not > 0
            || original.ICode is null
            || original.TrxType != expectedTrxType
            || original.FromBalLocId is not > 0
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
            || owner.SourceDocumentType != expectedTrxType
            || owner.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.EffectiveAt != original.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "The posted FIFO MI has no exact sealed origin.");
        var hasLaterValue = await read.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == original.ICode
                && (f.EffectiveAt > owner.EffectiveAt
                    || f.EffectiveAt == owner.EffectiveAt
                        && f.StockPosting!.PostingSequence > owner.PostingSequence)
                && !(f.InventoryHistoryId == original.Id
                    && f.EffectiveAt == owner.EffectiveAt)
                && f.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLaterValue)
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, companyCode,
                branchCode, owner.EffectiveAt, cancellationToken) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        var key = new StockReplayEventKey(owner.EffectiveAt,
            StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
            $"H{original.Id:D10}", 0);
        var removed = new StockReplayPhysicalEvent(original.Id, key,
            -original.FrStdQty.Value);
        var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            read, companyCode, branchCode, original.FromBalLocId.Value,
            removed, cancellationToken);
        var financial = await StockReplayFifoRemovalPlanner.BuildAsync(
            read, companyCode, branchCode, original.ICode, physical,
            cancellationToken);
        if (await CheckSimpleFifoInventoryTailAsync(read, original.ICode,
                financial.Snapshot, financial.Projection, key,
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
                "Backdated FIFO MI rollback needs the original V2 owner."));
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode,
                branchCode, owner.EffectiveAt, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, owner.EffectiveAt.Date,
            new DateTime(owner.EffectiveAt.Year, owner.EffectiveAt.Month,
                DateTime.DaysInMonth(owner.EffectiveAt.Year,
                    owner.EffectiveAt.Month)), cancellationToken);
        var permit = await StockReplayFifoRemovalPlanner.PrepareWithinRootAsync(
            context, financial, cancellationToken);
        var written = await StockReplayFifoMiRootWriter.RemoveAsync(
            context, financial, permit, batchNo, cancellationToken);
        if (expectedTrxType == IvTrxTypes.VendorReturn)
        {
            var postedDetails = await _posting.LoadDetailsForBatchAsync(
                write, batch.Id, cancellationToken);
            var poError = await ApplyVendorReturnPoQtyAsync(write,
                companyCode, branchCode, postedDetails, sign: -1,
                context.UserId, cancellationToken);
            if (poError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            }
            await write.SaveChangesAsync(cancellationToken);
        }
        await _coordinator!.CompleteReplayInTransactionAsync(
            context, written.RequiredActiveHistoryIds, cancellationToken);
        await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
            context, financial, permit, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id)
            .Select(b => b.RollbackOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo,
            operation ?? Guid.Empty);
    }

    private static async Task<string?> CheckSimpleFifoInventoryTailAsync(
        ErpWeb.Model.Data.AppDbContext db,
        StockReplayFifoInsertionPlan plan, StockReplayEventKey candidateKey,
        CancellationToken cancellationToken,
        PoInvoice? replayInvoice = null) =>
        await CheckSimpleFifoInventoryTailAsync(db, plan.ItemCode,
            plan.Snapshot, plan.Projection, candidateKey, cancellationToken,
            replayInvoice);

    private static async Task<string?> CheckSimpleFifoInventoryTailAsync(
        ErpWeb.Model.Data.AppDbContext db, string itemCode,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection,
        StockReplayEventKey candidateKey,
        CancellationToken cancellationToken,
        PoInvoice? replayInvoice = null)
    {
        var laterIssues = projection.Issues.Where(i =>
            !i.IsCandidate && i.Key.CompareTo(candidateKey) > 0).ToArray();
        var laterReceipts = snapshot.Layers.Where(l =>
            l.ReceiptKey is { } key && key.CompareTo(candidateKey) > 0)
            .ToArray();
        if (laterIssues.Length + laterReceipts.Length is 0 or > 100
            || laterIssues.Any(i => !i.DepletesPool
                || i.Key.EffectiveAt.Year != candidateKey.EffectiveAt.Year
                || i.Key.EffectiveAt.Month != candidateKey.EffectiveAt.Month)
            || laterReceipts.Any(l =>
                l.ReceiptKey!.Value.EffectiveAt.Year
                    != candidateKey.EffectiveAt.Year
                || l.ReceiptKey.Value.EffectiveAt.Month
                    != candidateKey.EffectiveAt.Month))
            return "Backdated FIFO Inventory replay supports at most 100 later FIFO receipts and Inventory MI/Scrap movements in the same open month.";
        var ids = laterIssues.Select(i =>
            StockReplayFifoMiRootWriter.HistoryId(i.Key)).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.BatchNo, h.TrxDtTime,
                h.TrxType, h.ICode, h.EntryRole, h.BatchStatus,
                h.StockPostingId, h.LedgerEpochId })
            .ToArrayAsync(cancellationToken);
        if (histories.Length != ids.Length || histories.Any(h =>
                !StockReplayFifoInventoryIssueKinds.IsSupported(h.TrxType)
                || h.ICode != itemCode || h.EntryRole != "ORIGINAL"
                || h.BatchStatus != IvBatchStatuses.Posted
                || h.StockPostingId is null || h.LedgerEpochId is null))
            return "Backdated FIFO Inventory replay has a dependent movement outside Inventory MI/Scrap.";
        var issueByHistory = laterIssues.ToDictionary(i =>
            StockReplayFifoMiRootWriter.HistoryId(i.Key));
        if (histories.Any(h => !issueByHistory.TryGetValue(h.Id,
                out var issue) || issue.Key.EffectiveAt != h.TrxDtTime))
            return "Backdated FIFO Inventory replay has a mismatched later issue key.";
        var ownerIds = histories.Select(h => h.StockPostingId!.Value)
            .Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.SourceModule,
                p.SourceDocumentType, p.SourceDocumentId,
                p.SourceDocumentNo, p.PostingRole, p.CommandType,
                p.EffectiveAt, p.PostingSequence, p.LedgerEpochId,
                p.SealedAtUtc })
            .ToArrayAsync(cancellationToken);
        if (owners.Length != ownerIds.Length || owners.Any(p =>
                p.SourceModule != "INVENTORY"
                || !StockReplayFifoInventoryIssueKinds.IsSupported(
                    p.SourceDocumentType)
                || p.SealedAtUtc is null))
            return "Backdated FIFO Inventory replay has an unsealed or foreign issue descendant.";
        var byOwner = owners.ToDictionary(p => p.Id);
        var batchNumbers = histories.Select(h => h.BatchNo).Distinct().ToArray();
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == snapshot.CompanyCode
                && b.BranchCode == snapshot.BranchCode
                && batchNumbers.Contains(b.BatchNo))
            .Select(b => new { b.Id, b.BatchNo, b.TrxType,
                b.TrxDtTime, b.BatchStatus })
            .ToArrayAsync(cancellationToken);
        if (batches.Length != batchNumbers.Length || histories.Any(h =>
                !byOwner.TryGetValue(h.StockPostingId!.Value,
                    out var owner)
                || !issueByHistory.TryGetValue(h.Id, out var issue)
                || !batches.Any(b => b.BatchNo == h.BatchNo
                    && b.TrxType == h.TrxType
                    && b.TrxDtTime == h.TrxDtTime
                    && b.BatchStatus == IvBatchStatuses.Posted
                    && owner.SourceDocumentType == h.TrxType
                    && owner.SourceDocumentId == b.Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    && owner.SourceDocumentNo == h.BatchNo.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    && owner.PostingRole == "PRIMARY"
                    && owner.CommandType == $"IV_POST_{h.TrxType}"
                    && owner.EffectiveAt == h.TrxDtTime
                    && owner.PostingSequence == issue.Key.OriginalPostingSequence
                    && owner.LedgerEpochId == h.LedgerEpochId)))
            return "Backdated FIFO Inventory replay has no exact later issue owner.";
        var receiptOwnerIds = laterReceipts.Select(l => l.OriginPostingId)
            .Distinct().ToArray();
        var receiptOwners = await db.StockPostings.AsNoTracking()
            .Where(p => receiptOwnerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.SourceModule,
                p.SourceDocumentType, p.SourceDocumentId,
                p.SourceDocumentNo, p.PostingRole, p.CommandType,
                p.EffectiveAt, p.PostingSequence, p.SealedAtUtc })
            .ToArrayAsync(cancellationToken);
        if (receiptOwners.Length != receiptOwnerIds.Length
            || receiptOwners.Any(p => p.SourceModule != "INVENTORY"
                || p.SourceDocumentType != "OPENING"
                    && p.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
                    && p.SourceDocumentType != IvTrxTypes.GoodsReceive
                || p.SealedAtUtc is null))
            return "Backdated FIFO Inventory replay has a later receipt outside opening stock or Inventory MR/GR.";
        var laterGrOwnerIds = receiptOwners.Where(p =>
            p.SourceDocumentType == IvTrxTypes.GoodsReceive)
            .Select(p => p.Id).ToHashSet();
        var laterGrFactIds = laterReceipts.Where(l =>
            laterGrOwnerIds.Contains(l.OriginPostingId))
            .Select(l => l.OriginFactId).ToArray();
        if (laterGrFactIds.Length > 0)
        {
            var grFacts = await db.StockValuationFacts.AsNoTracking()
                .Where(f => laterGrFactIds.Contains(f.Id))
                .Select(f => new { f.Id, f.CompanyCode, f.BranchCode,
                    f.ItemCode, f.InventoryHistoryId, f.StockPostingId,
                    f.LedgerEpochId, f.SourceDocumentType,
                    f.SourceDocumentId, f.SourceDocumentNo,
                    f.EffectiveAt, f.ToBalLocId, f.BaseQty,
                    f.Direction, f.CostMethod, f.ValuationSource })
                .ToArrayAsync(cancellationToken);
            var grHistoryIds = grFacts.Where(f =>
                f.InventoryHistoryId is not null)
                .Select(f => f.InventoryHistoryId!.Value).ToArray();
            var grHistories = await db.IvTrxHistories.AsNoTracking()
                .Where(h => grHistoryIds.Contains(h.Id))
                .ToArrayAsync(cancellationToken);
            if (grFacts.Length != laterGrFactIds.Length
                || grHistories.Length != grFacts.Length
                || grFacts.Any(f => f.InventoryHistoryId is not int id
                    || f.CompanyCode != snapshot.CompanyCode
                    || f.BranchCode != snapshot.BranchCode
                    || f.ItemCode != itemCode
                    || f.CostMethod != StockCostMethods.Fifo
                    || f.ValuationSource != StockValuationSources.Fifo
                    || f.SourceDocumentType != IvTrxTypes.GoodsReceive
                    || f.Direction != 1 || f.BaseQty <= 0m
                    || !laterGrOwnerIds.Contains(f.StockPostingId)
                    || grHistories.Count(h => h.Id == id
                        && h.CompanyCode == snapshot.CompanyCode
                        && h.BranchCode == snapshot.BranchCode
                        && h.ICode == itemCode
                        && h.TrxType == IvTrxTypes.GoodsReceive
                        && h.EntryRole == "ORIGINAL"
                        && h.BatchStatus == IvBatchStatuses.Posted
                        && h.StockPostingId == f.StockPostingId
                        && h.LedgerEpochId == f.LedgerEpochId
                        && h.TrxDtTime == f.EffectiveAt
                        && h.ToBalLocId == f.ToBalLocId
                        && h.ToStdQty == f.BaseQty
                        && h.FromBalLocId is null
                        && !string.IsNullOrWhiteSpace(h.PoNo)
                        && h.PoRelNo is not null
                        && h.PoLineNo is not null) != 1))
                return "A later FIFO GR has no exact receipt and purchase-order line.";
            var grBatchNos = grHistories.Select(h => h.BatchNo)
                .Distinct().ToArray();
            var grBatches = await db.IvTrxBatches.AsNoTracking()
                .Where(b => b.CompanyCode == snapshot.CompanyCode
                    && b.BranchCode == snapshot.BranchCode
                    && grBatchNos.Contains(b.BatchNo))
                .Select(b => new { b.Id, b.BatchNo, b.TrxType,
                    b.TrxDtTime, b.BatchStatus })
                .ToArrayAsync(cancellationToken);
            var grOwnersById = receiptOwners.ToDictionary(p => p.Id);
            if (grBatches.Length != grBatchNos.Length
                || grHistories.Any(h => h.StockPostingId is not long ownerId
                    || !grOwnersById.TryGetValue(ownerId, out var owner)
                    || !grBatches.Any(b => b.BatchNo == h.BatchNo
                        && b.TrxType == IvTrxTypes.GoodsReceive
                        && b.BatchStatus == IvBatchStatuses.Posted
                        && b.TrxDtTime == h.TrxDtTime
                        && owner.SourceDocumentId == b.Id.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        && owner.SourceDocumentNo == h.BatchNo.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        && grFacts.Any(f => f.InventoryHistoryId == h.Id
                            && f.SourceDocumentId == owner.SourceDocumentId
                            && f.SourceDocumentNo == owner.SourceDocumentNo)
                        && owner.PostingRole == "PRIMARY"
                        && owner.CommandType == $"IV_POST_{IvTrxTypes.GoodsReceive}"
                        && owner.EffectiveAt == h.TrxDtTime
                        && laterReceipts.Any(l =>
                            l.OriginPostingId == owner.Id
                            && l.OriginPostingSequence
                                == owner.PostingSequence
                            && l.OriginFactId == grFacts.Single(f =>
                                f.InventoryHistoryId == h.Id).Id))))
                return "A later FIFO GR has no exact sealed posting owner.";
            var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.CompanyCode == snapshot.CompanyCode
                    && s.BranchCode == snapshot.BranchCode
                    && s.ReceiptInventoryHistoryId != null
                    && grHistoryIds.Contains(s.ReceiptInventoryHistoryId.Value)
                    && s.ReversesSettlementId == null
                    && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id))
                .Select(s => new { s.ReceiptInventoryHistoryId,
                    s.PiDocNo, s.PiCostingRevision })
                .ToArrayAsync(cancellationToken);
            if (settlements.Any(s => replayInvoice is null
                    || s.PiDocNo != replayInvoice.DocNo
                    || s.PiCostingRevision != replayInvoice.CostingRevision
                    || !grHistories.Any(h =>
                        h.Id == s.ReceiptInventoryHistoryId
                        && replayInvoice.Details.Any(d =>
                            d.PoNo == h.PoNo && d.PoRelNo == h.PoRelNo
                            && d.PoLineNo == h.PoLineNo))))
                return "Backdated FIFO replay needs procurement settlement replay for a later invoiced GR.";
            var poNos = grHistories.Select(h => h.PoNo!).Distinct().ToArray();
            var poLines = await db.PoOrderDetails.AsNoTracking()
                .Where(d => d.CompanyCode == snapshot.CompanyCode
                    && d.BranchCode == snapshot.BranchCode
                    && poNos.Contains(d.PoNo))
                .Select(d => new { d.PoNo, d.PoRelNo, d.Line,
                    d.InvoicedQty })
                .ToArrayAsync(cancellationToken);
            if (grHistories.Any(h => poLines.Count(d =>
                    string.Equals(d.PoNo, h.PoNo,
                        StringComparison.OrdinalIgnoreCase)
                    && d.PoRelNo == h.PoRelNo
                    && d.Line == h.PoLineNo) != 1
                || poLines.Any(d => string.Equals(d.PoNo, h.PoNo,
                        StringComparison.OrdinalIgnoreCase)
                    && d.PoRelNo == h.PoRelNo
                    && d.Line == h.PoLineNo
                    && d.InvoicedQty != 0m
                    && (replayInvoice is null
                        || !replayInvoice.Details.Any(line =>
                            line.PoNo == h.PoNo
                            && line.PoRelNo == h.PoRelNo
                            && line.PoLineNo == h.PoLineNo)))))
                return "Backdated FIFO replay needs procurement replay for a later invoiced or unresolved GR purchase-order line.";
        }
        var receiptFactIds = laterReceipts.Select(l => l.OriginFactId)
            .ToHashSet();
        var issueHistoryIds = ids.ToHashSet();
        var laterFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.CompanyCode == snapshot.CompanyCode
                && f.BranchCode == snapshot.BranchCode
                && f.ItemCode == itemCode
                && (f.EffectiveAt > candidateKey.EffectiveAt
                    || f.EffectiveAt == candidateKey.EffectiveAt
                        && f.StockPosting!.PostingSequence
                            > candidateKey.OriginalPostingSequence)
                && f.StockPosting!.SealedAtUtc != null)
            .ToArrayAsync(cancellationToken);
        var neutralizedFactIds = await ReadExactFifoReversalPairsAsync(
            db, snapshot.CompanyCode, snapshot.BranchCode, itemCode,
            laterFacts, cancellationToken);
        if (neutralizedFactIds is null)
            return "Backdated FIFO Inventory replay has an invalid later valuation reversal pair.";
        var reversalIds = laterFacts.Where(f =>
                !receiptFactIds.Contains(f.Id)
                && !neutralizedFactIds.Contains(f.Id)
                && f.InventoryHistoryId is int historyId
                && !issueHistoryIds.Contains(historyId))
            .Select(f => f.InventoryHistoryId!.Value).Distinct().ToArray();
        var reversalHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => reversalIds.Contains(h.Id))
            .Select(h => new { h.Id, h.EntryRole, h.ReversesHistoryId })
            .ToArrayAsync(cancellationToken);
        var validReversalIds = reversalHistories.Where(h =>
                h.EntryRole == "REVERSAL"
                && h.ReversesHistoryId is int originalId
                && issueHistoryIds.Contains(originalId))
            .Select(h => h.Id).ToHashSet();
        if (laterFacts.Any(f => !receiptFactIds.Contains(f.Id)
                && !neutralizedFactIds.Contains(f.Id)
                && (f.InventoryHistoryId is not int historyId
                    || !issueHistoryIds.Contains(historyId)
                        && !validReversalIds.Contains(historyId))))
            return "Backdated FIFO Inventory replay has a later valuation outside its verified receipt and MI/Scrap tail.";
        return null;
    }

    private static async Task<HashSet<long>?> ReadExactFifoReversalPairsAsync(
        ErpWeb.Model.Data.AppDbContext db, string companyCode,
        string branchCode, string itemCode,
        IReadOnlyCollection<StockValuationFact> laterFacts,
        CancellationToken cancellationToken)
    {
        var laterFactIds = laterFacts.Select(f => f.Id).ToArray();
        var parentFactIds = laterFacts.Where(f =>
                f.ReversesValuationFactId is not null)
            .Select(f => f.ReversesValuationFactId!.Value).Distinct().ToArray();
        var pairedFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == itemCode
                && f.StockPosting!.SealedAtUtc != null
                && (parentFactIds.Contains(f.Id)
                    || f.ReversesValuationFactId != null
                        && laterFactIds.Contains(f.ReversesValuationFactId.Value)))
            .ToArrayAsync(cancellationToken);
        var factsById = laterFacts.Concat(pairedFacts)
            .DistinctBy(f => f.Id).ToDictionary(f => f.Id);
        var neutralizedFactIds = new HashSet<long>();
        var reversalParents = new HashSet<long>();
        foreach (var reversal in factsById.Values.Where(f =>
                     f.ReversesValuationFactId is not null))
        {
            if (!factsById.TryGetValue(
                    reversal.ReversesValuationFactId!.Value, out var parent)
                || !reversalParents.Add(parent.Id)
                || parent.ReversesValuationFactId is not null
                || reversal.ValuationStatus != StockValuationStatuses.Reversed
                || reversal.CompanyCode != parent.CompanyCode
                || reversal.BranchCode != parent.BranchCode
                || reversal.ItemCode != parent.ItemCode
                || reversal.CostMethod != parent.CostMethod
                || reversal.Direction != -parent.Direction
                || reversal.BaseQty != parent.BaseQty
                || reversal.CostAmount != parent.CostAmount)
                return null;
            neutralizedFactIds.Add(parent.Id);
            neutralizedFactIds.Add(reversal.Id);
        }
        return neutralizedFactIds;
    }
}
