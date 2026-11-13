using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Checks the owner and complete audit pair of a purchase-invoice issue reprice.</summary>
internal static class StockReplayInvoiceIssueAudit
{
    internal const string EffectKind = "PO_INVOICE_ISSUE_REPRICE";

    internal static async Task<bool> ValidateForSnapshotAsync(AppDbContext db, string company,
        string branch, DateTime exclusiveEnd, long watermark, CancellationToken ct)
    {
        var facts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.CompanyCode == company && f.BranchCode == branch && f.InventoryHistoryId != null
            && f.EffectiveAt < exclusiveEnd && f.StockPosting!.SealedAtUtc != null
            && f.StockPosting.PostingSequence <= watermark && f.StockPosting.SourceModule == "PROCUREMENT"
            && f.StockPosting.SourceDocumentType == "PO_INVOICE")
            .Select(f => f.StockPostingId).Distinct().ToArrayAsync(ct);
        var runs = await db.StockReplayRuns.AsNoTracking().Where(r => r.CompanyCode == company
            && r.BranchCode == branch && r.EarliestEffectiveAt < exclusiveEnd
            && db.StockPostings.Any(p => p.Id == r.RootStockPostingId && p.CompanyCode == company
                && p.BranchCode == branch && p.SourceModule == "PROCUREMENT"
                && p.SourceDocumentType == "PO_INVOICE" && p.SealedAtUtc != null
                && p.PostingSequence <= watermark)).Select(r => r.RootStockPostingId).ToArrayAsync(ct);
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e => e.CompanyCode == company
            && e.BranchCode == branch && e.EffectKind == EffectKind
            && db.StockPostings.Any(p => p.Id == e.RootStockPostingId && p.CompanyCode == company
                && p.BranchCode == branch && p.EffectiveAt < exclusiveEnd && p.SealedAtUtc != null
                && p.PostingSequence <= watermark)).Select(e => e.RootStockPostingId).ToArrayAsync(ct);
        return await ValidateRootsAsync(db, company, branch, facts.Concat(runs).Concat(effects).Distinct().ToArray(), null, ct);
    }

    internal static async Task<bool> ValidateForHistoriesAsync(AppDbContext db, string company,
        string branch, int[] histories, long? unsealedRootId, CancellationToken ct)
    {
        var roots = await db.StockValuationFacts.AsNoTracking().Where(f => f.CompanyCode == company
            && f.BranchCode == branch && f.InventoryHistoryId.HasValue && histories.Contains(f.InventoryHistoryId.Value)
            && f.StockPosting!.SourceModule == "PROCUREMENT" && f.StockPosting.SourceDocumentType == "PO_INVOICE")
            .Select(f => f.StockPostingId).Distinct().ToArrayAsync(ct);
        return await ValidateRootsAsync(db, company, branch, roots, unsealedRootId, ct);
    }

    private static async Task<bool> ValidateRootsAsync(AppDbContext db, string company, string branch,
        IReadOnlyCollection<long> rootIds, long? unsealedRootId, CancellationToken ct)
    {
        foreach (var id in rootIds)
        {
            var root = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id
                && p.CompanyCode == company && p.BranchCode == branch, ct);
            var runs = await db.StockReplayRuns.AsNoTracking().Where(r => r.RootStockPostingId == id).ToArrayAsync(ct);
            var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
                e.RootStockPostingId == id && e.EffectKind == EffectKind).ToArrayAsync(ct);
            var facts = await db.StockValuationFacts.AsNoTracking().Where(f => f.StockPostingId == id).ToArrayAsync(ct);
            var adjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a => a.StockPostingId == id).ToArrayAsync(ct);
            var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s => s.StockPostingId == id).ToArrayAsync(ct);
            if (root is null || root.SourceModule != "PROCUREMENT" || root.SourceDocumentType != "PO_INVOICE"
                || root.PostingRole is not ("PRIMARY" or "REVERSAL")
                || root.CommandType != $"PROCUREMENT_PO_INVOICE_{root.PostingRole}"
                || (root.PostingRole == "PRIMARY") != (root.ReversesPostingId is null)
                || root.SealedAtUtc is null && (unsealedRootId != root.Id || db.Database.CurrentTransaction is null)
                || root.PostingSequence <= 0 || string.IsNullOrWhiteSpace(root.SourceSnapshotJson)
                || runs.Length != 1 || effects.Length != 2 || facts.Length != 3
                || adjustments.Length != 1 || settlements.Length != 1)
                return false;
            try
            {
                if (StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(root.SourceSnapshotJson))
                    != root.SourceSnapshotHash)
                    return false;
            }
            catch (System.Text.Json.JsonException) { return false; }
            var run = runs[0];
            if (run.CompanyCode != company || run.BranchCode != branch || run.RootRequestId != root.RequestId
                || run.RootDocumentType != root.SourceDocumentType || run.RootDocumentId != root.SourceDocumentId
                || run.EarliestEffectiveAt != root.EffectiveAt || run.LatestEffectiveAt < root.EffectiveAt
                || run.LatestEffectiveAt.Year != root.EffectiveAt.Year || run.LatestEffectiveAt.Month != root.EffectiveAt.Month
                || run.PlanWatermark != root.PostingSequence - 1 || run.PlanHash.Length != 64
                || run.AffectedHistoryCount != 1 || run.AffectedItemCount != 1 || run.AffectedProductionMovementCount != 0)
                return false;
            var issueFacts = facts.Where(f => f.InventoryHistoryId.HasValue).ToArray();
            var valueFacts = facts.Where(f => f.InventoryHistoryId is null).ToArray();
            if (issueFacts.Length != 2 || valueFacts.Length != 1
                || issueFacts.Select(f => f.InventoryHistoryId).Distinct().Count() != 1)
                return false;
            var reverse = issueFacts.SingleOrDefault(f => f.ReversesValuationFactId.HasValue);
            var replacement = issueFacts.SingleOrDefault(f => f.ReversesValuationFactId is null);
            if (reverse?.ReversesValuationFactId is not long priorId || replacement is null)
                return false;
            var prior = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f => f.Id == priorId
                && f.CompanyCode == company && f.BranchCode == branch, ct);
            var history = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h =>
                h.Id == replacement.InventoryHistoryId && h.CompanyCode == company && h.BranchCode == branch, ct);
            var owner = history?.StockPostingId is long ownerId
                ? await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == ownerId
                    && p.CompanyCode == company && p.BranchCode == branch, ct) : null;
            if (prior is null || history is null || owner is null || owner.SealedAtUtc is null
                || history.LedgerVersion != 2 || history.LedgerEpochId != root.LedgerEpochId
                || history.EntryRole != "ORIGINAL" || history.TrxType != IvTrxTypes.MiscellaneousIssue
                || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != IvTrxTypes.MiscellaneousIssue
                || owner.CommandType != "IV_POST_MI" || owner.PostingRole != "PRIMARY"
                || owner.Id != history.StockPostingId || owner.EffectiveAt != history.TrxDtTime
                || run.LatestEffectiveAt != history.TrxDtTime || prior.InventoryHistoryId != history.Id
                || prior.Direction != -1 || prior.CostMethod != StockCostMethods.MovingAverage
                || prior.ValuationStatus != StockValuationStatuses.Valued || prior.ReversesValuationFactId is not null
                || reverse.ValuationStatus != StockValuationStatuses.Reversed
                || reverse.ValuationSource != StockValuationSources.OriginalReversal
                || reverse.Direction != 1 || reverse.BaseQty != prior.BaseQty || reverse.CostAmount != prior.CostAmount
                || reverse.OriginalValuationFactId != (prior.OriginalValuationFactId ?? prior.Id)
                || reverse.ValuationVersion != prior.ValuationVersion
                || replacement.ValuationStatus != StockValuationStatuses.Valued || replacement.Direction != -1
                || replacement.ValuationSource != StockValuationSources.MovingAverage || replacement.BaseQty != prior.BaseQty
                || replacement.OriginalValuationFactId != (prior.OriginalValuationFactId ?? prior.Id)
                || replacement.ValuationVersion != prior.ValuationVersion + 1
                || issueFacts.Any(f => f.CompanyCode != company || f.BranchCode != branch
                    || f.LedgerEpochId != root.LedgerEpochId || f.ItemCode != history.ICode
                    || f.EffectiveAt != history.TrxDtTime || f.CostMethod != StockCostMethods.MovingAverage
                    || f.SourceLineId != prior.SourceLineId || f.SourceDocumentType != prior.SourceDocumentType
                    || f.SourceDocumentId != prior.SourceDocumentId || f.SourceDocumentNo != prior.SourceDocumentNo
                    || f.SourceDocumentLine != prior.SourceDocumentLine || f.MovementCode != prior.MovementCode
                    || f.BaseUom != prior.BaseUom || f.FromBalLocId != prior.FromBalLocId
                    || f.BaseCostAmount != f.CostAmount || f.CostAmount < 0m)
                || effects.Any(e => e.CompanyCode != company || e.BranchCode != branch || e.ReplayRunId != run.Id
                    || e.BusinessOwnerPostingId != owner.Id || e.AffectedEntityType != StockReplayEffectTypes.StockValuationFact
                    || e.OriginalEntityId != prior.Id)
                || !effects.Select(e => e.AffectedEntityId).Order().SequenceEqual(issueFacts.Select(f => f.Id).Order()))
                return false;
            var value = valueFacts[0];
            var adjustment = adjustments[0];
            var settlement = settlements[0];
            if (value.CompanyCode != company || value.BranchCode != branch || value.LedgerEpochId != root.LedgerEpochId
                || value.SourceDocumentType != "PO_INVOICE" || value.SourceDocumentId != root.SourceDocumentId
                || value.SourceDocumentNo != root.SourceDocumentNo || value.EffectiveAt != root.EffectiveAt
                || value.BusinessDate != root.BusinessDate || value.PeriodKey != root.PeriodKey
                || value.ItemCode != history.ICode || value.BaseQty != 0m || value.CostMethod != StockCostMethods.MovingAverage
                || value.BaseCostAmount != value.CostAmount || value.CostAmount < 0m || value.Direction is not (-1 or 1)
                || value.ValuationSource != StockValuationSources.PurchasePriceVariance
                || value.MovementCode != (value.Direction > 0 ? "PURCHASE_PRICE_VARIANCE_IN" : "PURCHASE_PRICE_VARIANCE_OUT")
                || value.ValuationStatus != (root.PostingRole == "PRIMARY" ? StockValuationStatuses.Valued : StockValuationStatuses.Reversed)
                || adjustment.InventoryAdjustmentFactId != value.Id || adjustment.InventoryAdjustmentAmount != value.SignedValue
                || adjustment.CompanyCode != company || adjustment.BranchCode != branch
                || adjustment.SourceDocumentType != "PO_INVOICE" || adjustment.SourceDocumentNo != root.SourceDocumentNo
                || adjustment.SourceCostingRevision != root.DocumentRevision || adjustment.ItemCode != value.ItemCode
                || settlement.PiDocNo != root.SourceDocumentNo || settlement.PiCostingRevision != root.DocumentRevision
                || settlement.ItemCode != value.ItemCode || settlement.SettledBaseQty != adjustment.BaseQty
                || (root.PostingRole == "PRIMARY") != (value.ReversesValuationFactId is null)
                || (root.PostingRole == "PRIMARY") != (adjustment.ReversesAdjustmentId is null)
                || (root.PostingRole == "PRIMARY") != (settlement.ReversesSettlementId is null))
                return false;
            if (root.PostingRole == "PRIMARY")
            {
                if (value.OriginalValuationFactId != settlement.ReceiptValuationFactId)
                    return false;
            }
            else
            {
                if (adjustment.ReversesAdjustmentId is not long priorAdjustmentId
                    || settlement.ReversesSettlementId is not long priorSettlementId
                    || value.ReversesValuationFactId is not long priorValueId
                    || value.OriginalValuationFactId != priorValueId)
                    return false;
                var priorAdjustment = await db.PurchaseCostAdjustments.AsNoTracking().SingleOrDefaultAsync(a =>
                    a.Id == priorAdjustmentId && a.CompanyCode == company && a.BranchCode == branch, ct);
                var priorSettlement = await db.PurchaseReceiptCostSettlements.AsNoTracking().SingleOrDefaultAsync(s =>
                    s.Id == priorSettlementId && s.CompanyCode == company && s.BranchCode == branch, ct);
                var priorValue = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f =>
                    f.Id == priorValueId && f.CompanyCode == company && f.BranchCode == branch, ct);
                if (priorAdjustment is null || priorSettlement is null || priorValue is null
                    || priorAdjustment.StockPostingId != root.ReversesPostingId
                    || priorSettlement.StockPostingId != root.ReversesPostingId
                    || priorValue.StockPostingId != root.ReversesPostingId
                    || priorAdjustment.InventoryAdjustmentFactId != priorValue.Id
                    || adjustment.InventoryAdjustmentAmount != -priorAdjustment.InventoryAdjustmentAmount
                    || adjustment.TotalAdjustmentAmount != -priorAdjustment.TotalAdjustmentAmount
                    || adjustment.ConsumedVarianceAmount != -priorAdjustment.ConsumedVarianceAmount
                    || value.Direction != -priorValue.Direction || value.CostAmount != priorValue.CostAmount
                    || settlement.ReceiptValuationFactId != priorSettlement.ReceiptValuationFactId
                    || settlement.SettledBaseQty != priorSettlement.SettledBaseQty)
                    return false;
            }
        }
        return true;
    }
}
