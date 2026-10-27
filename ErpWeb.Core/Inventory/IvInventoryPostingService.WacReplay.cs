using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    /// <summary>
    /// Handles only one approved MR line whose later WAC descendants are
    /// ordinary Inventory MR/MI in the same open month. Everything else retains
    /// the existing chronology guard until its dependent corrections exist.
    /// </summary>
    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedWacMrAsync(
        string companyCode, string branchCode, int batchNo,
        CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != IvTrxTypes.MiscellaneousReceipt
            || batch.BatchStatus != IvBatchStatuses.New)
            return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1 || string.IsNullOrWhiteSpace(details[0].ICode))
            return null;
        var itemCode = details[0].ICode!;
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

        var detail = details[0];
        var pileId = await StockReplayInventoryReceiptPileLocator.FindUniqueAsync(
            read, companyCode, branchCode, detail, cancellationToken);
        if (pileId is not > 0)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated WAC MR needs one existing destination pile.");
        var authority = await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
            read, companyCode, branchCode, batchNo, pileId.Value, cancellationToken,
            allowExistingLot: true);
        var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(
            read, companyCode, branchCode, authority.BalLocId,
            authority.EffectiveAt, authority.Quantity, cancellationToken);
        var candidate = physical.Candidate;
        var key = candidate.Key;
        var financial = await StockReplayWacInsertionPlanner.BuildAsync(
            read, companyCode, branchCode, itemCode, physical,
            new StockReplayWacEvent(candidate.EventId, key, 0m, 0m,
                authority.Quantity, authority.Value), cancellationToken);
        if (await CheckSimpleWacTailAsync(read, financial.Tail.LaterEvents,
                batch.TrxDtTime, cancellationToken) is string unsupported)
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
        if (ledger.Context is null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerDisabled,
                "Backdated WAC replay requires an active V2 posting root."));
        var required = await StockReplayWacMrRootWriter.InsertAsync(
            ledger.Context, financial, batchNo, cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(
            ledger.Context, required, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == authority.BatchId)
            .Select(b => b.PostingOperationId)
            .SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo,
            operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedWacGrAsync(
        string companyCode, string branchCode, string userId, int batchNo,
        CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != IvTrxTypes.GoodsReceive
            || batch.BatchStatus != IvBatchStatuses.New)
            return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1 || string.IsNullOrWhiteSpace(details[0].ICode))
            return null;
        var itemCode = details[0].ICode!;
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
        var pileId = await StockReplayInventoryReceiptPileLocator.FindUniqueAsync(
            read, companyCode, branchCode, details[0], cancellationToken);
        if (pileId is not > 0)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated WAC GR needs one existing destination pile.");
        var authority = await StockReplayGrReceiptAuthorityReader.ReadAsync(
            read, companyCode, branchCode, batchNo, pileId.Value,
            cancellationToken, allowInvoiced: true);
        var invoiceCheck = await LoadSingleGrReplayInvoiceAsync(read, companyCode,
            branchCode, details[0].PoNo, details[0].PoRelNo,
            details[0].PoLineNo, itemCode, batch.TrxDtTime,
            cancellationToken, allowPostedCreditNote: true);
        if (invoiceCheck.Error is not null)
            return IvInventoryPostingBatchResult.Fail(batchNo, invoiceCheck.Error);
        var replayInvoice = invoiceCheck.Invoice;
        var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(
            read, companyCode, branchCode, authority.BalLocId,
            authority.EffectiveAt, authority.Quantity, cancellationToken);
        var candidate = physical.Candidate;
        var financial = await StockReplayWacInsertionPlanner.BuildAsync(
            read, companyCode, branchCode, itemCode, physical,
            new StockReplayWacEvent(candidate.EventId, candidate.Key, 0m, 0m,
                authority.Quantity, authority.Value), cancellationToken);
        if (await CheckSimpleWacTailAsync(read, financial.Tail.LaterEvents,
                batch.TrxDtTime, cancellationToken,
                replayInvoice) is string unsupported)
            return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(write,
            companyCode, branchCode, cancellationToken);
        var snapshot = await LoadGoodsReceiptSnapshotAsync(write, companyCode,
            branchCode, batchNo, IvTrxTypes.GoodsReceive, cancellationToken);
        if (snapshot.Error is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, snapshot.Error);
        }
        var locked = await LockGoodsReceiptPurchaseOrdersAsync(write,
            companyCode, branchCode, snapshot.Details!, cancellationToken);
        if (locked.Error is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, locked.Error);
        }
        var lockedPoNo = snapshot.Details![0].PoNo?.Trim();
        var lockedPoLine = locked.Orders!.GetValueOrDefault(lockedPoNo ?? string.Empty)?
            .Details.SingleOrDefault(d =>
                d.Line == snapshot.Details[0].PoLineNo);
        if (lockedPoLine is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "The GR invoice quantity changed after replay planning.");
        }
        var poError = await ValidateGoodsReceiptPoQtyAsync(write, locked.Orders!,
            snapshot.Details!, +1, IvTrxTypes.GoodsReceive, cancellationToken);
        if (poError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, poError);
        }
        var currentInvoice = await LoadSingleGrReplayInvoiceAsync(write,
            companyCode, branchCode, snapshot.Details![0].PoNo,
            snapshot.Details[0].PoRelNo, snapshot.Details[0].PoLineNo,
            itemCode, batch.TrxDtTime, cancellationToken,
            allowPostedCreditNote: true);
        if (currentInvoice.Error is not null
            || currentInvoice.Invoice?.DocNo != replayInvoice?.DocNo
            || currentInvoice.Invoice?.CostingRevision
                != replayInvoice?.CostingRevision)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo,
                currentInvoice.Error ?? "The posted invoice changed after GR replay planning.");
        }
        if (await CheckSimpleWacTailAsync(write,
                financial.Tail.LaterEvents, batch.TrxDtTime,
                cancellationToken, replayInvoice) is string changedTail)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, changedTail);
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
        if (ledger.Context is null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerDisabled,
                "Backdated WAC GR requires an active V2 posting root."));
        var required = await StockReplayWacMrRootWriter.InsertAsync(
            ledger.Context, financial, batchNo, cancellationToken,
            goodsReceipt: true, allowInvoiced: replayInvoice is not null);
        await ApplyGoodsReceiptPoQtyAsync(write, locked.Orders!,
            snapshot.Details!, +1, snapshot.Batch!.TrxDtTime, userId,
            cancellationToken);
        if (replayInvoice is not null)
        {
            var remap = await new PurchaseReceiptCostSettlementService()
                .RemapValueNeutralInvoiceAsync(ledger.Context, replayInvoice,
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
            ledger.Context, required, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == authority.BatchId)
            .Select(b => b.PostingOperationId)
            .SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedWacMiAsync(
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
        if (method != StockCostMethods.MovingAverage)
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
                "Backdated WAC MI needs one valid source pile and quantity.");
        var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(
            read, companyCode, branchCode, detail.FromBalLocId.Value,
            batch.TrxDtTime, -detail.FrStdQty.Value, cancellationToken);
        var candidate = physical.Candidate;
        var key = candidate.Key;
        var financial = await StockReplayWacInsertionPlanner.BuildAsync(
            read, companyCode, branchCode, itemCode, physical,
            new StockReplayWacEvent(candidate.EventId, key, 0m, 0m,
                candidate.SignedDelta), cancellationToken);
        if (await CheckSimpleWacTailAsync(read, financial.Tail.LaterEvents,
                batch.TrxDtTime, cancellationToken) is string unsupported)
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
        if (ledger.Context is null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerDisabled,
                "Backdated WAC replay requires an active V2 posting root."));
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
                    "The Vendor Return PO line changed after replay planning."));
        }
        var required = await StockReplayWacMiRootWriter.InsertAsync(
            ledger.Context, financial, batchNo, cancellationToken);
        if (expectedTrxType == IvTrxTypes.VendorReturn)
        {
            var postedDetails = await _posting.LoadDetailsForBatchAsync(
                write, batch.Id, cancellationToken);
            var poError = await ApplyVendorReturnPoQtyAsync(write,
                companyCode, branchCode, postedDetails, sign: +1,
                ledger.Context.UserId, cancellationToken);
            if (poError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            }
            await write.SaveChangesAsync(cancellationToken);
        }
        await _coordinator!.CompleteReplayInTransactionAsync(
            ledger.Context, required, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id)
            .Select(b => b.PostingOperationId)
            .SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo,
            operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedWacAsync(
        string companyCode, string branchCode, string userId, int batchNo,
        string trxType, CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != trxType
            || batch.BatchStatus != IvBatchStatuses.Posted)
            return null;
        var current = await _posting.LoadHistoryForBatchAsync(read,
            companyCode, branchCode, batchNo, cancellationToken);
        if (current.Count != 1)
            return null;
        var original = current[0];
        if (original.EntryRole != "ORIGINAL" || original.LedgerVersion != 2
            || original.StockPostingId is not > 0
            || original.ICode is null || original.TrxType != trxType
            || original.ValuationStatus != StockValuationStatuses.Valued)
            return null;
        var method = await read.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == original.ICode)
            .Select(s => s.CostMethod).SingleOrDefaultAsync(cancellationToken);
        if (method != StockCostMethods.MovingAverage)
            return null;
        var owner = await read.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == original.StockPostingId,
                cancellationToken);
        if (owner is null || owner.SealedAtUtc is null
            || owner.SourceModule != "INVENTORY"
            || owner.SourceDocumentType != trxType
            || owner.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || owner.EffectiveAt != original.TrxDtTime
            || owner.LedgerEpochId != original.LedgerEpochId)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "The posted Inventory origin has no exact sealed WAC owner.");
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
        var isReceipt = trxType is IvTrxTypes.MiscellaneousReceipt
            or IvTrxTypes.GoodsReceive;
        var pileId = isReceipt ? original.ToBalLocId : original.FromBalLocId;
        var quantity = isReceipt ? original.ToStdQty : original.FrStdQty;
        if (pileId is not > 0 || quantity is not > 0m)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "The posted Inventory origin has no single physical pile leg.");
        var key = new StockReplayEventKey(owner.EffectiveAt,
            StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
            $"H{original.Id:D10}", 0);
        var removed = new StockReplayPhysicalEvent(original.Id, key,
            isReceipt ? quantity.Value : -quantity.Value);
        var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            read, companyCode, branchCode, pileId.Value, removed,
            cancellationToken);
        var financial = await StockReplayWacRemovalPlanner.BuildAsync(
            read, companyCode, branchCode, original.ICode, physical,
            cancellationToken);
        if (financial.Tail.LaterEvents.Count == 0)
            return null;
        PoInvoice? replayInvoice = null;
        if (trxType == IvTrxTypes.GoodsReceive)
        {
            var invoiceCheck = await LoadSingleGrReplayInvoiceAsync(read,
                companyCode, branchCode, original.PoNo,
                original.PoRelNo, original.PoLineNo, original.ICode,
                original.TrxDtTime, cancellationToken,
                allowPostedCreditNote: true);
            if (invoiceCheck.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    invoiceCheck.Error);
            replayInvoice = invoiceCheck.Invoice;
        }
        if (await CheckSimpleWacTailAsync(read, financial.Tail.LaterEvents,
                original.TrxDtTime, cancellationToken,
                replayInvoice) is string unsupported)
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
        if (context is null || context.CostMethod != StockCostMethods.MovingAverage)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerDisabled,
                "Backdated WAC rollback requires an active V2 posting root."));
        if (context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != trxType
            || context.Posting.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "REVERSAL"
            || context.Posting.ReversesPostingId != owner.Id
            || context.Posting.EffectiveAt != key.EffectiveAt)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch,
                "The WAC rollback root does not reverse the posted Inventory owner."));
        var permit = await StockReplayWacRemovalPlanner.PrepareWithinRootAsync(
            context, financial, cancellationToken);
        var details = await write.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        var currentHistories = await _posting.LoadHistoryForBatchAsync(write,
            companyCode, branchCode, batchNo, cancellationToken);
        if (details.Length != 1 || currentHistories.Count != 1
            || currentHistories[0].Id != original.Id
            || details[0].DocumentRevision != context.Posting.DocumentRevision
            || details[0].TrxLineNo != original.TrxLineNo
            || details[0].ICode != original.ICode
            || trxType is not (IvTrxTypes.VendorReturn or IvTrxTypes.GoodsReceive)
                && (!string.IsNullOrWhiteSpace(details[0].PoNo)
                    || details[0].PoRelNo is not null)
            || (isReceipt
                ? details[0].ToBalLocId != pileId
                    || details[0].ToStdQty != quantity
                    || details[0].ToStdUom != original.ToStdUom
                    || details[0].ToWarehouse != original.ToWarehouse
                    || details[0].ToLocation != original.ToLocation
                    || details[0].ToLotId != original.ToLotId
                    || !string.Equals(details[0].ToLotNo ?? string.Empty,
                        original.ToLotNo ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase)
                    || details[0].IStatus != original.IStatus
                : details[0].FromBalLocId != pileId
                    || details[0].FrStdQty != quantity
                    || details[0].FrStdUom != original.FrStdUom
                    || details[0].FrWarehouse != original.FrWarehouse
                    || details[0].FrLocation != original.FrLocation
                    || details[0].IStatus != original.IStatus)
            || trxType == IvTrxTypes.VendorReturn
                && (string.IsNullOrWhiteSpace(original.PoNo)
                    || original.PoRelNo is null
                    || original.PoLineNo is null
                    || details[0].PoNo != original.PoNo
                    || details[0].PoRelNo != original.PoRelNo
                     || details[0].PoLineNo != original.PoLineNo
                     || details[0].FrPurQty != original.FrPurQty
                     || details[0].FrPurUom != original.FrPurUom))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch,
                "The posted Inventory line changed before WAC rollback."));

        if (!isReceipt)
        {
            var master = await write.IvStockMasters.AsNoTracking()
                .SingleOrDefaultAsync(m => m.CompanyCode == companyCode
                    && m.ICode == original.ICode, cancellationToken);
            var source = await write.IvBalLocs.AsNoTracking()
                .SingleOrDefaultAsync(b => b.CompanyCode == companyCode
                    && b.BranchCode == branchCode && b.Id == pileId.Value,
                    cancellationToken);
            if (master is null || source is null
                || !await StockReplayInventoryIssuePileAuthority.MatchesAsync(
                    write, master, source, details[0], cancellationToken,
                    requireActive: false)
                || original.FromLotId != source.LotId
                || !string.Equals(original.FrLotNo ?? string.Empty,
                    source.LotNo ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.LedgerMismatch,
                    "The posted Inventory issue has no exact source lot."));
        }
        else
        {
            var master = await write.IvStockMasters.AsNoTracking()
                .SingleOrDefaultAsync(m => m.CompanyCode == companyCode
                    && m.ICode == original.ICode, cancellationToken);
            var destination = await write.IvBalLocs.AsNoTracking()
                .SingleOrDefaultAsync(b => b.CompanyCode == companyCode
                    && b.BranchCode == branchCode && b.Id == pileId.Value,
                    cancellationToken);
            if (master is null || destination is null
                || destination.ICode != original.ICode
                || destination.LotId != original.ToLotId
                || !string.Equals(destination.LotNo ?? string.Empty,
                    original.ToLotNo ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                || master.LotControl != (destination.LotId is > 0)
                || destination.LotId is int lotId
                    && !await write.IvLots.AsNoTracking().AnyAsync(l =>
                        l.Id == lotId && l.CompanyCode == companyCode
                        && l.ICode == original.ICode
                        && l.LotNo == destination.LotNo, cancellationToken))
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.LedgerMismatch,
                    "The posted Inventory receipt has no exact destination lot."));
        }

        Dictionary<string, ErpWeb.Model.Entities.Purchase.PoOrder>? grOrders = null;
        if (trxType == IvTrxTypes.GoodsReceive)
        {
            var identityError = await ValidateGoodsReceiptRollbackIdentityAsync(
                write, companyCode, branchCode, batch, details,
                cancellationToken);
            if (identityError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, identityError);
            }
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
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    "The goods receipt has invoice settlement and needs procurement replay before historical rollback.");
            }
            var grLocked = await LockGoodsReceiptPurchaseOrdersAsync(write,
                companyCode, branchCode, details, cancellationToken);
            if (grLocked.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, grLocked.Error);
            }
            grOrders = grLocked.Orders!;
            var currentInvoice = await LoadSingleGrReplayInvoiceAsync(write,
                companyCode, branchCode, details[0].PoNo,
                details[0].PoRelNo, details[0].PoLineNo,
                original.ICode, original.TrxDtTime,
                cancellationToken, allowPostedCreditNote: true);
            if (currentInvoice.Error is not null
                || currentInvoice.Invoice?.DocNo != replayInvoice?.DocNo
                || currentInvoice.Invoice?.CostingRevision
                    != replayInvoice?.CostingRevision)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    currentInvoice.Error ?? "The posted invoice changed after GR rollback planning.");
            }
            if (await CheckSimpleWacTailAsync(write,
                    financial.Tail.LaterEvents, original.TrxDtTime,
                    cancellationToken, replayInvoice) is string changedTail)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, changedTail);
            }
            var grError = await ValidateGoodsReceiptPoQtyAsync(write,
                grOrders, details, -1, trxType, cancellationToken);
            if (grError is not null || grOrders.Values.SelectMany(o => o.Details)
                    .Any(d => d.InvoicedQty != 0m)
                    && replayInvoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    grError ?? "Historical GR rollback needs procurement replay while the purchase order is invoiced.");
            }
        }

        var result = isReceipt
            ? await RollBackInventoryMRCoreAsync(write, companyCode,
                branchCode, userId, batchNo, trxType, cancellationToken,
                context)
            : await RollBackInventoryMICoreAsync(write, companyCode,
                branchCode, userId, batchNo, trxType, cancellationToken,
                context);
        if (!result.Succeeded)
        {
            await tx.RollbackAsync(cancellationToken);
            return result;
        }
        if (trxType == IvTrxTypes.VendorReturn)
        {
            var poError = await ApplyVendorReturnPoQtyAsync(write,
                companyCode, branchCode, details, sign: -1,
                userId, cancellationToken);
            if (poError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            }
        }
        if (grOrders is not null)
            await ApplyGoodsReceiptPoQtyAsync(write, grOrders, details,
                -1, batch.TrxDtTime, userId, cancellationToken);
        await write.SaveChangesAsync(cancellationToken);
        var balance = await write.IvBalLocs.AsNoTracking()
            .SingleAsync(b => b.Id == pileId.Value
                && b.CompanyCode == companyCode
                && b.BranchCode == branchCode, cancellationToken);
        if (balance.StdQty != physical.Projection.FinalQuantity)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch,
                "The physical WAC rollback differs from its prepared projection."));
        var reversals = await write.IvTrxHistories
            .Where(h => h.StockPostingId == context.Posting.Id
                && h.ReversesHistoryId == original.Id
                && h.EntryRole == "REVERSAL")
            .ToArrayAsync(cancellationToken);
        if (reversals.Length != 1
            || (isReceipt
                ? reversals[0].FromBalLocId != pileId
                    || reversals[0].FrStdQty != quantity
                    || reversals[0].FromLotId != original.ToLotId
                : reversals[0].ToBalLocId != pileId
                    || reversals[0].ToStdQty != quantity))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch,
                "WAC rollback has no exact Inventory reversal history."));
        var activeFacts = (await StockReplayValuationGenerationResolver.ResolveAsync(
            write, companyCode, branchCode, [currentHistories[0]],
            cancellationToken))[original.Id];
        if (activeFacts.Count != 1
            || activeFacts[0].SignedQty != financial.RemovedActiveEvent.OldSignedQuantity
            || activeFacts[0].SignedValue != financial.RemovedActiveEvent.OldSignedValue
            || isReceipt && (activeFacts[0].LotId != original.ToLotId
                || !string.Equals(activeFacts[0].LotNo ?? string.Empty,
                    original.ToLotNo ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
            || !isReceipt && (activeFacts[0].LotId != original.FromLotId
                || !string.Equals(activeFacts[0].LotNo ?? string.Empty,
                    original.FrLotNo ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
            || activeFacts[0].CostMethod != StockCostMethods.MovingAverage)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch,
                "The removed WAC value differs from its active Inventory fact."));
        await StockReplayValuationReversalWriter.AppendAsync(context,
            original.Id, [new StockReplayValuationReversalLine(
                activeFacts[0].Id, 1, 0, reversals[0].Id)],
            cancellationToken);
        reversals[0].ValuationStatus = StockValuationStatuses.Reversed;
        var tail = await StockReplayWacTailRepricingWriter
            .AppendPreparedRemovalTailAsync(context, permit, financial, 2,
                cancellationToken);
        await StockReplayWacCostStateWriter.ApplyAsync(
            context, permit, cancellationToken);
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
            context, tail.RepricedHistoryIds, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<(PoInvoice? Invoice, string? Error)>
        LoadSingleGrReplayInvoiceAsync(
        ErpWeb.Model.Data.AppDbContext db,
        string companyCode, string branchCode, string? poNo,
        short? poRelNo, short? poLineNo, string itemCode,
        DateTime grDate, CancellationToken cancellationToken,
        bool allowPostedCreditNote = false)
    {
        if (string.IsNullOrWhiteSpace(poNo)
            || poRelNo is null || poLineNo is null)
            return (null, "The historical GR has no exact purchase-order line.");
        var line = await db.PoOrderDetails.AsNoTracking()
            .SingleOrDefaultAsync(d => d.CompanyCode == companyCode
                && d.BranchCode == branchCode && d.PoNo == poNo
                && d.PoRelNo == poRelNo && d.Line == poLineNo,
                cancellationToken);
        if (line is null || line.ICode != itemCode)
            return (null, "The historical GR purchase-order line changed.");
        if (line.InvoicedQty == 0m)
        {
            // A posted PI may be fully credited, leaving zero net PO quantity
            // while its PI/CN settlement lineage still depends on later GRs.
            var hasPostedInvoice = await db.PoInvoices.AsNoTracking()
                .AnyAsync(x =>
                    x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.Type == PoInvoiceTypes.Invoice
                    && x.Status == PoInvoiceStatuses.Posted
                    && x.Details.Any(d => d.PoNo == poNo
                        && d.PoRelNo == poRelNo
                        && d.PoLineNo == poLineNo),
                    cancellationToken);
            if (hasPostedInvoice && !allowPostedCreditNote)
                return (null,
                    "Historical GR has a posted PI on this PO line even though net invoice quantity is zero; its CN settlement needs replay.");
            if (!hasPostedInvoice)
                return (null, null);
        }
        if (_purchaseCostAdjustments is null
            || line.InvoicedQty < 0m
            || !string.Equals(line.PurchaseUom, line.StdUom,
                StringComparison.OrdinalIgnoreCase))
            return (null, "Historical GR before a posted PI needs the bounded same-UOM purchase settlement replay service.");

        var invoices = await db.PoInvoices.AsNoTracking()
            .Include(x => x.Details)
            .Where(x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.Type == PoInvoiceTypes.Invoice
                && x.DeletedAtUtc == null
                && x.Details.Any(d => d.PoNo == poNo
                    && d.PoRelNo == poRelNo
                    && d.PoLineNo == poLineNo)
                && (x.Status == PoInvoiceStatuses.New
                    || x.Status == PoInvoiceStatuses.Posted))
            .ToArrayAsync(cancellationToken);
        if (invoices.Length != 1
            || invoices[0].Type != PoInvoiceTypes.Invoice
            || invoices[0].Status != PoInvoiceStatuses.Posted
            || invoices[0].DocDate.Year != grDate.Year
            || invoices[0].DocDate.Month != grDate.Month
            || invoices[0].Details.Count != 1)
            return (null, "Historical GR PI replay needs one posted single-line invoice in the same open month.");
        var invoice = invoices[0];
        var postedNotes = await db.PoInvoices.AsNoTracking()
            .Include(x => x.Details)
            .Where(x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.Type == PoInvoiceTypes.CreditNote
                && x.InvNo == invoice.DocNo
                && x.Status == PoInvoiceStatuses.Posted)
            .ToArrayAsync(cancellationToken);
        if (postedNotes.Length > 0 && !allowPostedCreditNote)
            return (null,
                "A posted purchase credit/debit note depends on this invoice; historical GR needs CN generation replay.");
        if (postedNotes.Length > 1
            || postedNotes.Length == 1 && (postedNotes[0].DeletedAtUtc is not null
                || postedNotes[0].DocDate.Year != grDate.Year
                || postedNotes[0].DocDate.Month != grDate.Month
                || postedNotes[0].Details.Count != 1
                || postedNotes[0].Details.Single().ReferencedInvoiceLineNo
                    != invoice.Details.Single().Line
                || postedNotes[0].Details.Single().ICode != itemCode
                || postedNotes[0].Details.Single().PoNo != poNo
                || postedNotes[0].Details.Single().PoRelNo != poRelNo
                || postedNotes[0].Details.Single().PoLineNo != poLineNo
                || postedNotes[0].Details.Single().Qty <= 0m
                || postedNotes[0].Details.Single().Qty
                    != postedNotes[0].Details.Single().StdQty
                || await IvPeriodCloseGuard.EnsureOpenAsync(db,
                    companyCode, branchCode, postedNotes[0].DocDate,
                    cancellationToken) is not null))
            return (null,
                "Historical GR credit/debit note replay needs one exact unarchived posted single-line note in the same open month.");
        var detail = invoice.Details.Single();
        if (detail.ICode != itemCode
            || detail.PoNo != poNo
            || detail.PoRelNo != poRelNo
            || detail.PoLineNo != poLineNo
            || detail.Qty <= 0m
            || detail.Qty != detail.StdQty
            || detail.Qty - (postedNotes.Length == 1
                ? postedNotes[0].Details.Single().Qty : 0m)
                != line.InvoicedQty
            || await IvPeriodCloseGuard.EnsureOpenAsync(db, companyCode,
                branchCode, invoice.DocDate, cancellationToken) is not null)
            return (null, "Historical GR PI replay needs an exact posted open-period invoice quantity and purchase-order line.");
        return (invoice, null);
    }

    private static async Task<string?> CheckSimpleWacTailAsync(
        ErpWeb.Model.Data.AppDbContext db,
        IReadOnlyList<StockReplayWacEvent> events,
        DateTime candidateDate,
        CancellationToken cancellationToken,
        PoInvoice? replayInvoice = null)
    {
        if (events.Count == 0 || events.Count > 100
            || events.Any(e => e.EventId <= 0 || e.EventId > int.MaxValue
                || e.Key.EffectiveAt.Year != candidateDate.Year
                || e.Key.EffectiveAt.Month != candidateDate.Month))
            return "Backdated WAC replay supports at most 100 later Inventory MR/GR/MI/Scrap/transfer movements in the same open month.";
        var ids = events.Select(e => checked((int)e.EventId)).ToArray();
        var eventById = events.ToDictionary(e => checked((int)e.EventId));
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.TrxType, h.StockPostingId,
                h.CompanyCode, h.BranchCode, h.BatchNo,
                h.PoNo, h.PoRelNo, h.PoLineNo })
            .ToArrayAsync(cancellationToken);
        if (histories.Length != ids.Length
            || histories.Any(h => h.StockPostingId is null
                || (h.TrxType == IvTrxTypes.GoodsReceive
                    ? string.IsNullOrWhiteSpace(h.PoNo)
                        || h.PoRelNo is null || h.PoLineNo is null
                        || eventById[h.Id].OldSignedQuantity <= 0m
                    : !string.IsNullOrWhiteSpace(h.PoNo)
                        || h.PoRelNo is not null
                        || h.PoLineNo is not null
                        || (h.TrxType == IvTrxTypes.MiscellaneousReceipt
                            ? eventById[h.Id].OldSignedQuantity <= 0m
                            : StockReplayFifoInventoryIssueKinds.IsSupported(h.TrxType)
                                ? eventById[h.Id].OldSignedQuantity >= 0m
                                : h.TrxType != IvTrxTypes.StockTransfer
                                    || eventById[h.Id].OldSignedQuantity != 0m))))
            return "Backdated WAC replay has a dependent movement outside supported Inventory MR/GR/MI/Scrap/transfer.";
        var laterGrIds = histories.Where(h => h.TrxType == IvTrxTypes.GoodsReceive)
            .Select(h => h.Id).ToArray();
        var laterSettlements = await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.CompanyCode == histories[0].CompanyCode
                    && s.BranchCode == histories[0].BranchCode
                    && s.ReceiptInventoryHistoryId != null
                    && laterGrIds.Contains(s.ReceiptInventoryHistoryId.Value)
                    && s.ReversesSettlementId == null
                    && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id))
                .Select(s => new { s.ReceiptInventoryHistoryId,
                    s.PiDocNo, s.PiCostingRevision })
                .ToArrayAsync(cancellationToken);
        if (laterSettlements.Any(s => replayInvoice is null
                || s.PiDocNo != replayInvoice.DocNo
                || s.PiCostingRevision != replayInvoice.CostingRevision
                || !histories.Any(h => h.Id == s.ReceiptInventoryHistoryId
                    && replayInvoice.Details.Any(d =>
                        d.PoNo == h.PoNo && d.PoRelNo == h.PoRelNo
                        && d.PoLineNo == h.PoLineNo))))
            return "Backdated WAC replay needs procurement settlement replay for a later invoiced GR.";
        if (laterGrIds.Length > 0)
        {
            var laterGr = histories.Where(h => h.TrxType == IvTrxTypes.GoodsReceive)
                .ToArray();
            var poNos = laterGr.Select(h => h.PoNo!).Distinct().ToArray();
            var poLines = await db.PoOrderDetails.AsNoTracking()
                .Where(d => d.CompanyCode == histories[0].CompanyCode
                    && d.BranchCode == histories[0].BranchCode
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
                    && d.InvoicedQty != 0m
                    && (replayInvoice is null
                        || !replayInvoice.Details.Any(line =>
                            line.PoNo == h.PoNo
                            && line.PoRelNo == h.PoRelNo
                            && line.PoLineNo == h.PoLineNo)))))
                return "Backdated WAC replay needs procurement replay for a later invoiced or unresolved GR purchase-order line.";
        }
        var ownerIds = histories.Select(h => h.StockPostingId!.Value)
            .Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.SourceModule, p.SourceDocumentType,
                p.SourceDocumentId, p.SourceDocumentNo, p.PostingRole,
                p.CommandType, p.SealedAtUtc })
            .ToArrayAsync(cancellationToken);
        var typeByOwner = histories.GroupBy(h => h.StockPostingId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(h => h.TrxType).Distinct().ToArray());
        if (owners.Length != ownerIds.Length
            || owners.Any(p => p.SourceModule != "INVENTORY"
                || p.SealedAtUtc is null
                || !typeByOwner.TryGetValue(p.Id, out var types)
                || types.Length != 1 || p.SourceDocumentType != types[0]
                || p.CommandType != $"IV_POST_{types[0]}"))
            return "Backdated WAC replay has a dependent posting outside supported Inventory MR/GR/MI/Scrap/transfer.";
        var batchNumbers = histories.Select(h => h.BatchNo)
            .Distinct().ToArray();
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == histories[0].CompanyCode
                && b.BranchCode == histories[0].BranchCode
                && batchNumbers.Contains(b.BatchNo))
            .Select(b => new { b.Id, b.BatchNo, b.TrxType, b.BatchStatus })
            .ToArrayAsync(cancellationToken);
        if (batches.Length != batchNumbers.Length
            || histories.Any(h => h.CompanyCode != histories[0].CompanyCode
                || h.BranchCode != histories[0].BranchCode
                || !batches.Any(b => b.BatchNo == h.BatchNo
                    && b.TrxType == h.TrxType
                    && b.BatchStatus == IvBatchStatuses.Posted
                    && owners.Any(p => p.Id == h.StockPostingId
                        && p.PostingRole == "PRIMARY"
                        && p.SourceDocumentId == b.Id.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        && p.SourceDocumentNo == h.BatchNo.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)))))
            return "Backdated WAC replay has a movement without its exact posted Inventory owner.";
        return null;
    }
}
