using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // Reversed issues are omitted from the active replay timeline. Do not
    // change an earlier receipt unless the method proves those historical values
    // remain correct. The caller holds the branch lock.
    private async Task<IvInventoryPostingBatchResult?> RejectReversedProductionReceiptAsync(
        AppDbContext db, string companyCode, string branchCode, int batchNo,
        string receiptType, bool rollback, CancellationToken cancellationToken,
        bool allowProvenReversedSale = false)
    {
        // A WAC issue can depend on a receipt on another pile even when replay
        // is disabled. In that case the pile-date guard alone cannot protect
        // the historical issue and reversal costs.
        if (receiptType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive))
            return null;

        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo && b.TrxType == receiptType, cancellationToken);
        if (batch is null)
            return null;

        var itemCodes = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id && d.CompanyCode == companyCode
                && d.BranchCode == branchCode && d.ICode != null && d.ICode != "")
            .Select(d => d.ICode!).Distinct().ToArrayAsync(cancellationToken);
        if (itemCodes.Length == 0)
            return null;

        if (!allowProvenReversedSale)
        {
            var wacItems = await db.StockCostStates.AsNoTracking()
                .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode
                    && itemCodes.Contains(s.ItemCode) && s.CostMethod == StockCostMethods.MovingAverage)
                .Select(s => s.ItemCode).ToArrayAsync(cancellationToken);
            if (wacItems.Length != 0 && await HasReversedSalesAfterAsync(
                    db, companyCode, branchCode, wacItems, batch.TrxDtTime, cancellationToken))
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    "This receipt has a later reversed sale that needs verified stock and cost replay.");
        }

        if (rollback && !await FifoReceiptChangePreservesReversedIssuesAsync(db,
                companyCode, branchCode, batch, itemCodes, cancellationToken))
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "This FIFO receipt cannot be rolled back until the later reversed Production issue's historical costs are reconciled.");

        long? receiptSequence = null;
        if (rollback)
        {
            var ownerIds = await db.IvTrxHistories.AsNoTracking().Where(h =>
                    h.CompanyCode == companyCode && h.BranchCode == branchCode
                    && h.BatchNo == batchNo && h.TrxType == receiptType
                    && h.LedgerVersion == 2 && h.EntryRole == "ORIGINAL" && h.StockPostingId != null
                    && !db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id))
                .Select(h => h.StockPostingId!.Value).Distinct().ToArrayAsync(cancellationToken);
            if (ownerIds.Length == 1)
            {
                var receiptId = batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                receiptSequence = await db.StockPostings.AsNoTracking().Where(p =>
                        p.Id == ownerIds[0] && p.CompanyCode == companyCode && p.BranchCode == branchCode
                        && p.SourceModule == "INVENTORY" && p.SourceDocumentType == receiptType
                        && p.SourceDocumentId == receiptId && p.PostingRole == "PRIMARY"
                        && p.SealedAtUtc != null && p.EffectiveAt == batch.TrxDtTime)
                    .Select(p => (long?)p.PostingSequence).SingleOrDefaultAsync(cancellationToken);
            }
        }

        // Standard-cost history is unaffected by an earlier receipt price only
        // when both the current state and the issue valuations prove that method.
        var standardItems = await db.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode
                && itemCodes.Contains(s.ItemCode) && s.CostMethod == StockCostMethods.Standard)
            .Select(s => s.ItemCode).ToArrayAsync(cancellationToken);
        var receiptSequenceValue = receiptSequence.GetValueOrDefault();
        var hasReversedIssue = await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
            h.CompanyCode == companyCode && h.BranchCode == branchCode
            && h.TrxType == IvTrxTypes.IssueToProduction
            && itemCodes.Contains(h.ICode)
            && (h.TrxDtTime > batch.TrxDtTime || rollback && h.TrxDtTime == batch.TrxDtTime)
            && !(h.LedgerVersion == 2 && h.EntryRole == "ORIGINAL" && h.BatchStatus == IvBatchStatuses.Posted
                && h.ReversesHistoryId == null && standardItems.Contains(h.ICode)
                && db.StockValuationFacts.Any(f => f.CompanyCode == companyCode
                    && f.BranchCode == branchCode && f.InventoryHistoryId == h.Id
                    && f.CostMethod == StockCostMethods.Standard)
                && !db.StockValuationFacts.Any(f => f.CompanyCode == companyCode
                    && f.BranchCode == branchCode && f.InventoryHistoryId == h.Id
                    && f.CostMethod != StockCostMethods.Standard)
                && db.StockPostings.Any(owner => owner.Id == h.StockPostingId
                    && owner.CompanyCode == companyCode && owner.BranchCode == branchCode
                    && owner.PostingRole == "PRIMARY" && owner.SealedAtUtc != null
                    && owner.SourceModule == "INVENTORY" && owner.SourceDocumentType == IvTrxTypes.IssueToProduction
                    && owner.LedgerEpochId == h.LedgerEpochId && owner.EffectiveAt == h.TrxDtTime)
                && db.ProductionBalLotMovements.Any(issue => issue.InventoryHistoryId == h.Id
                    && issue.CompanyCode == companyCode && issue.BranchCode == branchCode
                    && issue.StockPostingId == h.StockPostingId && issue.LedgerVersion == 2
                    && issue.LedgerEpochId == h.LedgerEpochId && issue.OriginalMovementId == null
                    && issue.MovementType == ProductionBalLotMovementTypes.Issue && issue.ItemCode == h.ICode
                    && issue.BaseQty == h.FrStdQty && issue.ValuationStatus == ProductionPoolValuationService.Verified
                    && !db.ProductionCostRestatementFacts.Any(f => f.ProductionBalLotMovementId == issue.Uid)
                    && db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id
                        && r.CompanyCode == companyCode && r.BranchCode == branchCode
                        && r.LedgerVersion == 2 && r.EntryRole == "REVERSAL" && r.BatchStatus == IvBatchStatuses.Posted
                        && r.ICode == h.ICode && r.TrxType == h.TrxType && r.BatchNo == h.BatchNo
                        && r.TrxLineNo == h.TrxLineNo && r.DocumentRevision == h.DocumentRevision
                        && r.LedgerEpochId == h.LedgerEpochId && r.FromBalLocId == h.ToBalLocId
                        && r.ToBalLocId == h.FromBalLocId && r.FrStdQty == h.ToStdQty && r.ToStdQty == h.FrStdQty
                        && db.StockPostings.Any(p => p.Id == r.StockPostingId
                            && p.CompanyCode == companyCode && p.BranchCode == branchCode
                            && p.SealedAtUtc != null && p.PostingRole == "REVERSAL"
                            && p.LedgerEpochId == h.LedgerEpochId && p.EffectiveAt == r.TrxDtTime
                            && p.ReversesPostingId == h.StockPostingId)
                        && db.ProductionBalLotMovements.Any(reverse => reverse.OriginalMovementId == issue.Uid
                            && reverse.CompanyCode == companyCode && reverse.BranchCode == branchCode
                            && reverse.StockPostingId == r.StockPostingId && reverse.LedgerVersion == 2
                            && reverse.LedgerEpochId == issue.LedgerEpochId
                            && reverse.ProductionBalLotId == issue.ProductionBalLotId && reverse.ItemCode == issue.ItemCode
                            && reverse.MovementType == ProductionBalLotMovementTypes.IssueReversal
                            && reverse.BaseQty == issue.BaseQty && reverse.TotalCost == issue.TotalCost
                            && reverse.UnitCost == issue.UnitCost
                            && !db.ProductionCostRestatementFacts.Any(f => f.ProductionBalLotMovementId == reverse.Uid)))))
            && db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id)
            // An ordinary receipt posted before an untouched, unconsumed issue
            // did not cause a later correction to that issue. Preserve its
            // usual rollback after verifying the exact issue/reversal pair.
            && (!rollback || receiptSequence == null
                || !db.StockPostings.Any(owner => owner.Id == h.StockPostingId
                    && owner.CompanyCode == companyCode && owner.BranchCode == branchCode
                    && owner.PostingRole == "PRIMARY" && owner.SealedAtUtc != null
                    && owner.SourceModule == "INVENTORY"
                    && owner.SourceDocumentType == IvTrxTypes.IssueToProduction
                    && owner.LedgerEpochId == h.LedgerEpochId && owner.EffectiveAt == h.TrxDtTime
                    && owner.PostingSequence > receiptSequenceValue
                    && h.LedgerVersion == 2 && h.EntryRole == "ORIGINAL" && h.BatchStatus == IvBatchStatuses.Posted
                    && h.ReversesHistoryId == null
                    && !db.StockValuationFacts.Any(f => f.InventoryHistoryId == h.Id
                        && f.OriginalValuationFactId != null)
                    && db.ProductionBalLotMovements.Any(issue =>
                        issue.CompanyCode == companyCode && issue.BranchCode == branchCode
                        && issue.InventoryHistoryId == h.Id && issue.StockPostingId == h.StockPostingId
                        && issue.LedgerVersion == 2 && issue.LedgerEpochId == h.LedgerEpochId
                        && issue.OriginalMovementId == null && issue.ItemCode == h.ICode
                        && issue.BaseQty == h.FrStdQty && issue.ValuationStatus == ProductionPoolValuationService.Verified
                        && issue.MovementType == ProductionBalLotMovementTypes.Issue
                        && db.ProductionBalLotMovements.Count(m =>
                            m.ProductionBalLotId == issue.ProductionBalLotId) == 2
                        && !db.ProductionCostRestatementFacts.Any(f => f.ProductionBalLotMovementId == issue.Uid)
                        && db.IvTrxHistories.Any(r => r.CompanyCode == companyCode && r.BranchCode == branchCode
                            && r.ReversesHistoryId == h.Id && r.LedgerVersion == 2 && r.EntryRole == "REVERSAL"
                            && r.BatchStatus == IvBatchStatuses.Posted && r.ICode == h.ICode
                            && r.TrxType == h.TrxType && r.BatchNo == h.BatchNo && r.TrxLineNo == h.TrxLineNo
                            && r.DocumentRevision == h.DocumentRevision && r.LedgerEpochId == h.LedgerEpochId
                            && r.FromBalLocId == h.ToBalLocId && r.ToBalLocId == h.FromBalLocId
                            && r.FrStdQty == h.ToStdQty && r.ToStdQty == h.FrStdQty
                            && db.StockPostings.Any(p => p.Id == r.StockPostingId
                                && p.CompanyCode == companyCode && p.BranchCode == branchCode
                                && p.SealedAtUtc != null && p.PostingRole == "REVERSAL"
                                && p.SourceModule == owner.SourceModule && p.SourceDocumentType == owner.SourceDocumentType
                                && p.SourceDocumentId == owner.SourceDocumentId && p.LedgerEpochId == owner.LedgerEpochId
                                && p.EffectiveAt == r.TrxDtTime && p.ReversesPostingId == h.StockPostingId)
                            && db.ProductionBalLotMovements.Any(reverse =>
                                reverse.CompanyCode == companyCode && reverse.BranchCode == branchCode
                                && reverse.OriginalMovementId == issue.Uid && reverse.StockPostingId == r.StockPostingId
                                && reverse.LedgerVersion == 2 && reverse.LedgerEpochId == issue.LedgerEpochId
                                && reverse.ProductionBalLotId == issue.ProductionBalLotId && reverse.ItemCode == issue.ItemCode
                                && reverse.MovementType == ProductionBalLotMovementTypes.IssueReversal
                                && reverse.BaseQty == issue.BaseQty && reverse.TotalCost == issue.TotalCost
                                && reverse.UnitCost == issue.UnitCost
                                && !db.ProductionCostRestatementFacts.Any(f => f.ProductionBalLotMovementId == reverse.Uid)))))),
            cancellationToken);

        if (!hasReversedIssue)
            return null;
        if (await HasUnchangedWacReversedIssueAsync(db, companyCode, branchCode,
                batch, itemCodes, rollback, cancellationToken))
            return null;
        if (_openPeriodBackdatedReplayEnabled && itemCodes.Length == 1
            && await db.StockCostStates.AsNoTracking().CountAsync(s =>
                s.CompanyCode == companyCode && s.BranchCode == branchCode
                && s.ItemCode == itemCodes[0] && s.CostMethod == StockCostMethods.Fifo,
                cancellationToken) == 1
            && await FifoReceiptChangePreservesReversedIssuesAsync(db, companyCode, branchCode,
                batch, itemCodes, cancellationToken))
            return null;
        return IvInventoryPostingBatchResult.Fail(batchNo,
            "This receipt cannot yet replay a reversed Production issue. The historical Production costs need reconciliation.");
    }
}
