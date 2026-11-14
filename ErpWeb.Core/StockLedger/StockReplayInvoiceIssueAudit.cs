using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Checks the owner and complete audit pair of a purchase-invoice issue reprice.</summary>
internal static class StockReplayInvoiceIssueAudit
{
    internal const string EffectKind = "PO_INVOICE_ISSUE_REPRICE";
    internal const int MaxIssueCount = 100;

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
        if (rootIds.Count == 0)
            return true;
        // Load each evidence set once for the affected roots. A replay can
        // correct many issues and be replayed repeatedly; validating one row at
        // a time would hold the branch lock for an unbounded number of queries.
        var ids = rootIds.Distinct().ToArray();
        var roots = await db.StockPostings.AsNoTracking().Where(p => ids.Contains(p.Id)
            && p.CompanyCode == company && p.BranchCode == branch).ToDictionaryAsync(p => p.Id, ct);
        var allRuns = await db.StockReplayRuns.AsNoTracking().Where(r => ids.Contains(r.RootStockPostingId)).ToArrayAsync(ct);
        var allEffects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            ids.Contains(e.RootStockPostingId) && e.EffectKind == EffectKind).ToArrayAsync(ct);
        var allFacts = await db.StockValuationFacts.AsNoTracking().Where(f => ids.Contains(f.StockPostingId)).ToArrayAsync(ct);
        var allAdjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a => ids.Contains(a.StockPostingId)).ToArrayAsync(ct);
        var allSettlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s => ids.Contains(s.StockPostingId)).ToArrayAsync(ct);
        var priorFactIds = allFacts.Where(f => f.ReversesValuationFactId.HasValue)
            .Select(f => f.ReversesValuationFactId!.Value).Distinct().ToArray();
        var historyIds = allFacts.Where(f => f.InventoryHistoryId.HasValue)
            .Select(f => f.InventoryHistoryId!.Value).Distinct().ToArray();
        var priorAdjustmentIds = allAdjustments.Where(a => a.ReversesAdjustmentId.HasValue)
            .Select(a => a.ReversesAdjustmentId!.Value).Distinct().ToArray();
        var priorSettlementIds = allSettlements.Where(s => s.ReversesSettlementId.HasValue)
            .Select(s => s.ReversesSettlementId!.Value).Distinct().ToArray();
        var priorFacts = await db.StockValuationFacts.AsNoTracking().Where(f => priorFactIds.Contains(f.Id)
            && f.CompanyCode == company && f.BranchCode == branch).ToDictionaryAsync(f => f.Id, ct);
        var histories = await db.IvTrxHistories.AsNoTracking().Where(h => historyIds.Contains(h.Id)
            && h.CompanyCode == company && h.BranchCode == branch).ToDictionaryAsync(h => h.Id, ct);
        var ownerIds = histories.Values.Where(h => h.StockPostingId.HasValue)
            .Select(h => h.StockPostingId!.Value).Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking().Where(p => ownerIds.Contains(p.Id)
            && p.CompanyCode == company && p.BranchCode == branch).ToDictionaryAsync(p => p.Id, ct);
        var priorAdjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
            priorAdjustmentIds.Contains(a.Id) && a.CompanyCode == company && a.BranchCode == branch)
            .ToDictionaryAsync(a => a.Id, ct);
        var priorSettlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
            priorSettlementIds.Contains(s.Id) && s.CompanyCode == company && s.BranchCode == branch)
            .ToDictionaryAsync(s => s.Id, ct);
        var runsByRoot = allRuns.ToLookup(r => r.RootStockPostingId);
        var effectsByRoot = allEffects.ToLookup(e => e.RootStockPostingId);
        var factsByRoot = allFacts.ToLookup(f => f.StockPostingId);
        var adjustmentsByRoot = allAdjustments.ToLookup(a => a.StockPostingId);
        var settlementsByRoot = allSettlements.ToLookup(s => s.StockPostingId);
        foreach (var id in ids)
        {
            var root = roots.GetValueOrDefault(id);
            var runs = runsByRoot[id].ToArray();
            var effects = effectsByRoot[id].ToArray();
            var facts = factsByRoot[id].ToArray();
            var adjustments = adjustmentsByRoot[id].ToArray();
            var settlements = settlementsByRoot[id].ToArray();
            if (root is null || root.SourceModule != "PROCUREMENT" || root.SourceDocumentType != "PO_INVOICE"
                || root.PostingRole is not ("PRIMARY" or "REVERSAL")
                || root.CommandType != $"PROCUREMENT_PO_INVOICE_{root.PostingRole}"
                || (root.PostingRole == "PRIMARY") != (root.ReversesPostingId is null)
                || root.SealedAtUtc is null && (unsealedRootId != root.Id || db.Database.CurrentTransaction is null)
                || root.PostingSequence <= 0 || string.IsNullOrWhiteSpace(root.SourceSnapshotJson)
                || runs.Length != 1
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
                || run.PlanWatermark != root.PostingSequence - 1 || run.PlanHash is null || run.PlanHash.Length != 64
                || run.AffectedHistoryCount is < 1 or > MaxIssueCount || run.AffectedItemCount != 1 || run.AffectedProductionMovementCount != 0)
                return false;
            var issueFacts = facts.Where(f => f.InventoryHistoryId.HasValue).ToArray();
            var valueFacts = facts.Where(f => f.InventoryHistoryId is null).ToArray();
            var issueGroups = issueFacts.GroupBy(f => f.InventoryHistoryId!.Value).ToArray();
            if (issueFacts.Length != run.AffectedHistoryCount * 2 || valueFacts.Length != 1
                || issueGroups.Length != run.AffectedHistoryCount || effects.Length != issueFacts.Length
                || !effects.Select(e => e.AffectedEntityId).Order().SequenceEqual(issueFacts.Select(f => f.Id).Order()))
                return false;
            var saleCount = issueGroups.Count(g => histories.TryGetValue(g.Key, out var history)
                && history.TrxType == IvTrxTypes.SalesOut);
            if (saleCount > 0 && (saleCount != 1 || issueGroups.Length != 1))
                return false;
            var value = valueFacts[0];
            var latestIssue = DateTime.MinValue;
            foreach (var group in issueGroups)
            {
                var reversals = group.Where(f => f.ReversesValuationFactId.HasValue).ToArray();
                var replacements = group.Where(f => f.ReversesValuationFactId is null).ToArray();
                if (reversals.Length != 1 || replacements.Length != 1
                    || reversals[0].ReversesValuationFactId is not long priorId)
                    return false;
                var reverse = reversals[0];
                var replacement = replacements[0];
                var prior = priorFacts.GetValueOrDefault(priorId);
                var history = histories.GetValueOrDefault(group.Key);
                var owner = history?.StockPostingId is long ownerId
                    ? owners.GetValueOrDefault(ownerId) : null;
                var groupEffects = effects.Where(e => e.AffectedEntityId == reverse.Id
                    || e.AffectedEntityId == replacement.Id).ToArray();
                if (prior is null || history is null || owner is null || owner.SealedAtUtc is null
                    || owner.LedgerEpochId != root.LedgerEpochId || history.ICode != value.ItemCode
                    || history.LedgerVersion != 2 || history.LedgerEpochId != root.LedgerEpochId
                    || history.EntryRole != "ORIGINAL" || history.TrxType is not (IvTrxTypes.MiscellaneousIssue or IvTrxTypes.SalesOut)
                    || history.TrxDtTime < root.EffectiveAt || history.TrxDtTime.Year != root.EffectiveAt.Year
                    || history.TrxDtTime.Month != root.EffectiveAt.Month
                    || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != history.TrxType
                    || owner.CommandType != $"IV_POST_{history.TrxType}" || owner.PostingRole != "PRIMARY"
                    || owner.ReversesPostingId is not null || owner.Id != history.StockPostingId
                    || owner.EffectiveAt != history.TrxDtTime
                    || prior.InventoryHistoryId != history.Id
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
                    || group.Any(f => f.CompanyCode != company || f.BranchCode != branch
                        || f.LedgerEpochId != root.LedgerEpochId || f.ItemCode != history.ICode
                        || f.EffectiveAt != history.TrxDtTime || f.CostMethod != StockCostMethods.MovingAverage
                        || f.BusinessDate != prior.BusinessDate || f.PeriodKey != prior.PeriodKey
                        || f.SourceLineId != prior.SourceLineId || f.SourceDocumentType != prior.SourceDocumentType
                        || f.SourceDocumentId != prior.SourceDocumentId || f.SourceDocumentNo != prior.SourceDocumentNo
                        || f.SourceDocumentLine != prior.SourceDocumentLine || f.MovementCode != prior.MovementCode
                        || f.BaseUom != prior.BaseUom || f.FromBalLocId != prior.FromBalLocId
                        || f.BaseCostAmount != f.CostAmount || f.CostAmount < 0m)
                    || groupEffects.Length != 2 || groupEffects.Any(e => e.CompanyCode != company
                        || e.BranchCode != branch || e.ReplayRunId != run.Id || e.BusinessOwnerPostingId != owner.Id
                        || e.AffectedEntityType != StockReplayEffectTypes.StockValuationFact || e.OriginalEntityId != prior.Id))
                    return false;
                if (history.TrxType == IvTrxTypes.SalesOut
                    && (!string.IsNullOrWhiteSpace(history.DoNo) || string.IsNullOrWhiteSpace(history.InvNo)
                        || history.SoLineNo is not > 0 || prior.MovementCode != "SALE_OUT"
                        || prior.SourceDocumentType != "SA_INVOICE" || prior.SourceDocumentId != history.InvNo
                        || prior.SourceDocumentNo != history.InvNo
                        || prior.SourceDocumentLine != history.SoLineNo.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)))
                    return false;
                if (history.TrxDtTime > latestIssue)
                    latestIssue = history.TrxDtTime;
            }
            if (run.LatestEffectiveAt != latestIssue)
                return false;
            var adjustment = adjustments[0];
            var settlement = settlements[0];
            if (value.CompanyCode != company || value.BranchCode != branch || value.LedgerEpochId != root.LedgerEpochId
                || value.SourceDocumentType != "PO_INVOICE" || value.SourceDocumentId != root.SourceDocumentId
                || value.SourceDocumentNo != root.SourceDocumentNo || value.EffectiveAt != root.EffectiveAt
                || value.BusinessDate != root.BusinessDate || value.PeriodKey != root.PeriodKey
                || value.BaseQty != 0m || value.CostMethod != StockCostMethods.MovingAverage
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
                var priorAdjustment = priorAdjustments.GetValueOrDefault(priorAdjustmentId);
                var priorSettlement = priorSettlements.GetValueOrDefault(priorSettlementId);
                var priorValue = priorFacts.GetValueOrDefault(priorValueId);
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
