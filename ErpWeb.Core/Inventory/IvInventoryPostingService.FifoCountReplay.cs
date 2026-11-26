using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private static async Task<bool> LockCountReplaySourceAsync(
        AppDbContext db, string company, string branch,
        StockReplayFifoSingleCountProof proof, CancellationToken ct) =>
        await LockCountReplaySourceAsync(db, company, branch,
            new CountReplayProof(proof.HeaderId, proof.LineId, proof.GenerationId, proof.Fingerprint), ct);

    private async Task<IvInventoryPostingBatchResult> TryPostFifoSingleCountMrAsync(
        string companyCode, string branchCode, int batchNo,
        string receiptType, StockReplayFifoMrReceiptAuthority authority,
        StockReplayPhysicalPlan physical, PoInvoice? replayInvoice,
        CancellationToken cancellationToken)
    {
        if (replayInvoice is not null)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO receipt cannot yet replay both a stock count and an invoice settlement.");
        var goodsReceipt = receiptType == IvTrxTypes.GoodsReceive;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await StockReplayFifoSingleCountReplay.PlanAsync(
            read, companyCode, branchCode, authority.ItemCode, physical.BalLocId,
            physical.Candidate.Key, authority.Quantity, isReceipt: true, isRemoval: false,
            authority.Value, authority.BaseUom, physical, null, null, cancellationToken);
        if (plan is null)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO receipt before a stock count requires one unchanged open-month single-pile count.");

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(write, companyCode, branchCode, cancellationToken);
        if (!await LockCountReplaySourceAsync(write, companyCode, branchCode, plan.Proof, cancellationToken))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The stock count is busy. Retry posting.");
        var lockedPlan = await StockReplayFifoSingleCountReplay.PlanAsync(
            write, companyCode, branchCode, authority.ItemCode, physical.BalLocId,
            physical.Candidate.Key, authority.Quantity, isReceipt: true, isRemoval: false,
            authority.Value, authority.BaseUom, physical, null, null, cancellationToken);
        if (lockedPlan?.Fingerprint != plan.Fingerprint)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The FIFO stock count changed. Retry posting.");

        Dictionary<string, ErpWeb.Model.Entities.Purchase.PoOrder>? grOrders = null;
        IReadOnlyList<IvTrxBatchDetail>? grDetails = null;
        if (goodsReceipt)
        {
            var snapshot = await LoadGoodsReceiptSnapshotAsync(write, companyCode, branchCode, batchNo, receiptType, cancellationToken);
            if (snapshot.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo, snapshot.Error);
            grDetails = snapshot.Details!;
            var locked = await LockGoodsReceiptPurchaseOrdersAsync(write, companyCode, branchCode, grDetails, cancellationToken);
            if (locked.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo, locked.Error);
            if (await AssertNewGoodsReceiptPoApprovalAsync(write, companyCode, branchCode, locked.Orders!.Values, cancellationToken) is string approvalErr)
                return IvInventoryPostingBatchResult.Fail(batchNo, approvalErr);
            grOrders = locked.Orders!;
            if (await ValidateGoodsReceiptPoQtyAsync(write, grOrders, grDetails, +1, receiptType, cancellationToken) is string poError)
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            if (await _qaReceipt.ClaimGoodsReceiptAsync(write, companyCode, branchCode, receiptType, snapshot.Batch!, grDetails, cancellationToken) is string qaClaim)
                return IvInventoryPostingBatchResult.Fail(batchNo, qaClaim);
        }

        var ledger = await BeginStandaloneLedgerAsync(write, companyCode, branchCode, batchNo, reversal: false, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return ledger.Fail;
        }
        var context = ledger.Context!;
        context.CostMethod = StockCostMethods.Fifo;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode, branchCode, authority.EffectiveAt, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, authority.EffectiveAt.Date, plan.CountKey.EffectiveAt.Date, cancellationToken);
        var countPermit = await StockReplayPhysicalPlanner.PrepareCountCorrectionsWithinRootAsync(
            context, physical, cancellationToken);
        var required = await StockReplayFifoSingleCountReplay.ApplyAsync(
            context, lockedPlan, countPermit, batchNo, cancellationToken, goodsReceipt);
        if (goodsReceipt)
            await ApplyGoodsReceiptPoQtyAsync(write, grOrders!, grDetails!, +1, authority.EffectiveAt, context.UserId, cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == authority.BatchId).Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult> TryRollBackFifoSingleCountMrAsync(
        string companyCode, string branchCode, int batchNo,
        string receiptType, IvTrxBatch batch, IvTrxHistory original,
        StockPosting owner, StockReplayPhysicalRemovalPlan physical, PoInvoice? replayInvoice,
        CancellationToken cancellationToken)
    {
        if (replayInvoice is not null)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO receipt rollback cannot yet replay both a stock count and an invoice settlement.");
        var goodsReceipt = receiptType == IvTrxTypes.GoodsReceive;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await StockReplayFifoSingleCountReplay.PlanAsync(
            read, companyCode, branchCode, original.ICode, physical.BalLocId,
            physical.RemovedActiveEvent.Key, original.ToStdQty!.Value,
            isReceipt: true, isRemoval: true, null, null, null, physical, owner.Id, cancellationToken);
        if (plan is null)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO receipt rollback before a stock count requires no other dependent later changes.");

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(write, companyCode, branchCode, cancellationToken);
        if (!await LockCountReplaySourceAsync(write, companyCode, branchCode, plan.Proof, cancellationToken))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The stock count is busy. Retry rollback.");
        var lockedPlan = await StockReplayFifoSingleCountReplay.PlanAsync(
            write, companyCode, branchCode, original.ICode, physical.BalLocId,
            physical.RemovedActiveEvent.Key, original.ToStdQty!.Value,
            isReceipt: true, isRemoval: true, null, null, null, physical, owner.Id, cancellationToken);
        if (lockedPlan?.Fingerprint != plan.Fingerprint)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The FIFO stock count changed. Retry rollback.");

        var plannedPile = await write.IvBalLocs.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.Id == physical.BalLocId && b.ICode == original.ICode, cancellationToken);
        if (plannedPile is null)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt stock balance changed before rollback.");
        var qaSlice = IvStockSliceKey.Create(plannedPile.CompanyCode, plannedPile.BranchCode,
            plannedPile.ICode, plannedPile.WhCode, plannedPile.LocCode, plannedPile.LotNo, plannedPile.IStatus);
        var lockedPiles = await _posting.LockBalanceSlicesAsync(write, [qaSlice], cancellationToken);
        if (!lockedPiles.TryGetValue(qaSlice, out var lockedPile) || lockedPile.Id != physical.BalLocId
            || lockedPile.StdQty != physical.CurrentQuantity
            || !lockedPile.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt stock balance changed before rollback.");
        if (await _qaPhysical.AssertRemainingCoversHoldsAsync(write,
                companyCode, branchCode, lockedPile.Id, physical.Projection.FinalQuantity, cancellationToken) is string qaHoldErr)
            return IvInventoryPostingBatchResult.Fail(batchNo, qaHoldErr);

        Dictionary<string, ErpWeb.Model.Entities.Purchase.PoOrder>? grOrders = null;
        IReadOnlyList<IvTrxBatchDetail>? grDetails = null;
        if (goodsReceipt)
        {
            var snapshot = await LoadGoodsReceiptSnapshotAsync(write, companyCode, branchCode, batchNo, receiptType, cancellationToken);
            if (snapshot.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo, snapshot.Error);
            grDetails = snapshot.Details!;
            if (await ValidateGoodsReceiptRollbackIdentityAsync(write, companyCode, branchCode, snapshot.Batch!, grDetails, cancellationToken) is string idErr)
                return IvInventoryPostingBatchResult.Fail(batchNo, idErr);
            if (await write.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s =>
                    s.CompanyCode == companyCode && s.BranchCode == branchCode
                    && s.ReceiptInventoryHistoryId == original.Id && s.ReversesSettlementId == null
                    && !write.PurchaseReceiptCostSettlements.Any(r => r.ReversesSettlementId == s.Id), cancellationToken))
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    "The goods receipt has invoice settlement and needs procurement replay before historical rollback.");
            var locked = await LockGoodsReceiptPurchaseOrdersAsync(write, companyCode, branchCode, grDetails, cancellationToken);
            if (locked.Error is not null)
                return IvInventoryPostingBatchResult.Fail(batchNo, locked.Error);
            grOrders = locked.Orders!;
            var poError = await ValidateGoodsReceiptPoQtyAsync(write, grOrders, grDetails, -1, receiptType, cancellationToken);
            if (poError is not null || grOrders.Values.SelectMany(o => o.Details).Any(d => d.InvoicedQty != 0m))
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    poError ?? "Historical GR rollback needs procurement replay while the purchase order is invoiced.");
            if (await _qaReceipt.RestoreGoodsReceiptAsync(write, companyCode, branchCode, batchNo, grDetails, cancellationToken) is string qaRestore)
                return IvInventoryPostingBatchResult.Fail(batchNo, qaRestore);
        }

        var ledger = await BeginStandaloneLedgerAsync(write, companyCode, branchCode, batchNo, reversal: true, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return ledger.Fail;
        }
        var context = ledger.Context!;
        context.CostMethod = StockCostMethods.Fifo;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode, branchCode, owner.EffectiveAt, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, owner.EffectiveAt.Date, plan.CountKey.EffectiveAt.Date, cancellationToken);
        var countPermit = await StockReplayPhysicalPlanner.PrepareCountCorrectionsWithinRootAsync(
            context, physical, cancellationToken);
        var required = await StockReplayFifoSingleCountReplay.ApplyAsync(
            context, lockedPlan, countPermit, batchNo, cancellationToken, goodsReceipt);
        if (goodsReceipt)
            await ApplyGoodsReceiptPoQtyAsync(write, grOrders!, grDetails!, -1, owner.EffectiveAt, context.UserId, cancellationToken);
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id).Select(b => b.RollbackOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult> TryPostFifoSingleCountMiAsync(
        string companyCode, string branchCode, int batchNo,
        string expectedTrxType, IvTrxBatch batch, IvTrxBatchDetail detail,
        StockReplayPhysicalPlan physical, CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        StockReplayFifoSingleCountPlan? plan;
        try
        {
            plan = await StockReplayFifoSingleCountReplay.PlanAsync(
                read, companyCode, branchCode, detail.ICode!, detail.FromBalLocId!.Value,
                physical.Candidate.Key, detail.FrStdQty!.Value, isReceipt: false, isRemoval: false,
                null, null, physical, null, null, cancellationToken);
        }
        catch (StockLedgerException ex)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, ex.Message);
        }
        if (plan is null)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO issue before a stock count requires one unchanged open-month single-pile count.");

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(write, companyCode, branchCode, cancellationToken);
        if (!await LockCountReplaySourceAsync(write, companyCode, branchCode, plan.Proof, cancellationToken))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The stock count is busy. Retry posting.");
        StockReplayFifoSingleCountPlan? lockedPlan;
        try
        {
            lockedPlan = await StockReplayFifoSingleCountReplay.PlanAsync(
                write, companyCode, branchCode, detail.ICode!, detail.FromBalLocId!.Value,
                physical.Candidate.Key, detail.FrStdQty!.Value, isReceipt: false, isRemoval: false,
                null, null, physical, null, null, cancellationToken);
        }
        catch (StockLedgerException ex)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, ex.Message);
        }
        if (lockedPlan?.Fingerprint != plan.Fingerprint)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The FIFO stock count changed. Retry posting.");

        var ledger = await BeginStandaloneLedgerAsync(write, companyCode, branchCode, batchNo, reversal: false, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return ledger.Fail;
        }
        var context = ledger.Context!;
        context.CostMethod = StockCostMethods.Fifo;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode, branchCode, batch.TrxDtTime, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        if (expectedTrxType == IvTrxTypes.VendorReturn)
        {
            var currentDetail = await write.IvTrxBatchDetails.AsNoTracking()
                .SingleAsync(d => d.BatchId == batch.Id, cancellationToken);
            if (currentDetail.PoNo != detail.PoNo || currentDetail.PoRelNo != detail.PoRelNo
                || currentDetail.PoLineNo != detail.PoLineNo || currentDetail.FrPurQty != detail.FrPurQty
                || currentDetail.FrPurUom != detail.FrPurUom)
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The FIFO Vendor Return PO line changed after replay planning."));
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, batch.TrxDtTime.Date, plan.CountKey.EffectiveAt.Date, cancellationToken);
        var writeDetails = await write.IvTrxBatchDetails.Where(d => d.BatchId == batch.Id).ToListAsync(cancellationToken);
        string? qaError;
        if (physical.CurrentQuantity < Math.Abs(physical.Candidate.SignedDelta))
        {
            qaError = await _qaPhysical.AssertRemainingCoversHoldsAsync(
                write, companyCode, branchCode, physical.BalLocId,
                physical.Projection.FinalQuantity, cancellationToken);
            if (qaError is null)
                qaError = await AssertQaPhysicalFromDetailsAsync(
                    write, companyCode, branchCode, expectedTrxType, batchNo, writeDetails,
                    cancellationToken, effectiveOnHandOverride: physical.Projection.Candidate.QuantityBefore);
        }
        else
        {
            qaError = await AssertQaPhysicalFromDetailsAsync(
                write, companyCode, branchCode, expectedTrxType, batchNo, writeDetails, cancellationToken);
        }
        if (qaError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, qaError);
        }
        var countPermit = await StockReplayPhysicalPlanner.PrepareCountCorrectionsWithinRootAsync(
            context, physical, cancellationToken);
        var required = await StockReplayFifoSingleCountReplay.ApplyAsync(
            context, lockedPlan, countPermit, batchNo, cancellationToken);
        if (expectedTrxType == IvTrxTypes.VendorReturn)
        {
            var postedDetails = await _posting.LoadDetailsForBatchAsync(write, batch.Id, cancellationToken);
            if (await ApplyVendorReturnPoQtyAsync(write, companyCode, branchCode, postedDetails, sign: +1, context.UserId, cancellationToken) is string poError)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            }
            await write.SaveChangesAsync(cancellationToken);
        }
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id).Select(b => b.PostingOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult> TryRollBackFifoSingleCountMiAsync(
        string companyCode, string branchCode, int batchNo,
        string expectedTrxType, IvTrxBatch batch, IvTrxHistory original,
        StockPosting owner, StockReplayPhysicalRemovalPlan physical,
        CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await StockReplayFifoSingleCountReplay.PlanAsync(
            read, companyCode, branchCode, original.ICode!, original.FromBalLocId!.Value,
            physical.RemovedActiveEvent.Key, original.FrStdQty!.Value,
            isReceipt: false, isRemoval: true, null, null, null, physical, owner.Id, cancellationToken);
        if (plan is null)
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "Backdated FIFO issue rollback before a stock count requires no other dependent later changes.");

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        await new BranchStockTransactionLock().AcquireAsync(write, companyCode, branchCode, cancellationToken);
        if (!await LockCountReplaySourceAsync(write, companyCode, branchCode, plan.Proof, cancellationToken))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The stock count is busy. Retry rollback.");
        var lockedPlan = await StockReplayFifoSingleCountReplay.PlanAsync(
            write, companyCode, branchCode, original.ICode!, original.FromBalLocId!.Value,
            physical.RemovedActiveEvent.Key, original.FrStdQty!.Value,
            isReceipt: false, isRemoval: true, null, null, null, physical, owner.Id, cancellationToken);
        if (lockedPlan?.Fingerprint != plan.Fingerprint)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The FIFO stock count changed. Retry rollback.");

        if (await _qaPhysical.RestoreStockOutAsync(write, companyCode, branchCode, batchNo, cancellationToken) is string qaRestore)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, qaRestore);
        }
        var ledger = await BeginStandaloneLedgerAsync(write, companyCode, branchCode, batchNo, reversal: true, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return ledger.Fail;
        }
        var context = ledger.Context!;
        context.CostMethod = StockCostMethods.Fifo;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, companyCode, branchCode, owner.EffectiveAt, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, lockedClosed);
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, owner.EffectiveAt.Date, plan.CountKey.EffectiveAt.Date, cancellationToken);
        var countPermit = await StockReplayPhysicalPlanner.PrepareCountCorrectionsWithinRootAsync(
            context, physical, cancellationToken);
        var required = await StockReplayFifoSingleCountReplay.ApplyAsync(
            context, lockedPlan, countPermit, batchNo, cancellationToken);
        if (expectedTrxType == IvTrxTypes.VendorReturn)
        {
            var postedDetails = await _posting.LoadDetailsForBatchAsync(write, batch.Id, cancellationToken);
            if (await ApplyVendorReturnPoQtyAsync(write, companyCode, branchCode, postedDetails, sign: -1, context.UserId, cancellationToken) is string poError)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, poError);
            }
            await write.SaveChangesAsync(cancellationToken);
        }
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, cancellationToken);
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var operation = await write.IvTrxBatches.AsNoTracking()
            .Where(b => b.Id == batch.Id).Select(b => b.RollbackOperationId).SingleAsync(cancellationToken);
        return IvInventoryPostingBatchResult.Ok(batchNo, operation ?? Guid.Empty);
    }
}
