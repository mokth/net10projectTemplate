using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Checks the owner and complete audit pair of a purchase-invoice issue reprice.</summary>
internal static class StockReplayInvoiceIssueAudit
{
    internal const string EffectKind = "PO_INVOICE_ISSUE_REPRICE";
    internal const string CreditNoteEffectKind = "PO_CDN_ISSUE_REPRICE";
    internal const int MaxIssueCount = 100;

    internal static async Task<bool> ValidateForSnapshotAsync(AppDbContext db, string company,
        string branch, DateTime exclusiveEnd, long watermark, CancellationToken ct)
    {
        var facts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.CompanyCode == company && f.BranchCode == branch && f.InventoryHistoryId != null
            && f.EffectiveAt < exclusiveEnd && f.StockPosting!.SealedAtUtc != null
            && f.StockPosting.PostingSequence <= watermark && f.StockPosting.SourceModule == "PROCUREMENT"
            && (f.StockPosting.SourceDocumentType == "PO_INVOICE" || f.StockPosting.SourceDocumentType == "PO_CDN"))
            .Select(f => f.StockPostingId).Distinct().ToArrayAsync(ct);
        var runs = await db.StockReplayRuns.AsNoTracking().Where(r => r.CompanyCode == company
            && r.BranchCode == branch && r.EarliestEffectiveAt < exclusiveEnd
            && db.StockPostings.Any(p => p.Id == r.RootStockPostingId && p.CompanyCode == company
                && p.BranchCode == branch && p.SourceModule == "PROCUREMENT"
                && (p.SourceDocumentType == "PO_INVOICE" || p.SourceDocumentType == "PO_CDN") && p.SealedAtUtc != null
                && p.PostingSequence <= watermark)).Select(r => r.RootStockPostingId).ToArrayAsync(ct);
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e => e.CompanyCode == company
            && e.BranchCode == branch && (e.EffectKind == EffectKind || e.EffectKind == CreditNoteEffectKind)
            && db.StockPostings.Any(p => p.Id == e.RootStockPostingId && p.CompanyCode == company
                && p.BranchCode == branch && p.EffectiveAt < exclusiveEnd && p.SealedAtUtc != null
                && p.PostingSequence <= watermark)).Select(e => e.RootStockPostingId).ToArrayAsync(ct);
        var roots = facts.Concat(runs).Concat(effects).Distinct().ToArray();
        // Canceled-sale roots have value-only sale correction pairs rather than
        // active issue replacements. The snapshot's reversed-sale audit checks
        // those roots and their complete correction/undo lineage separately.
        var saleFactRoots = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.CompanyCode == company && f.BranchCode == branch && roots.Contains(f.StockPostingId)
            && (f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValue
                || f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValueUndo))
            .Select(f => f.StockPostingId).Distinct().ToArrayAsync(ct);
        var saleEffectRoots = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.CompanyCode == company && e.BranchCode == branch && roots.Contains(e.RootStockPostingId)
            && (e.EffectKind == StockReplayEffectTypes.SalesReversalValue
                || e.EffectKind == StockReplayEffectTypes.SalesReversalValueUndo))
            .Select(e => e.RootStockPostingId).Distinct().ToArrayAsync(ct);
        var saleRoots = saleFactRoots.Concat(saleEffectRoots).ToHashSet();
        return await ValidateRootsAsync(db, company, branch, roots.Where(id => !saleRoots.Contains(id)).ToArray(), null, ct);
    }

    internal static async Task<bool> ValidateForHistoriesAsync(AppDbContext db, string company,
        string branch, int[] histories, long? unsealedRootId, CancellationToken ct)
    {
        var roots = await db.StockValuationFacts.AsNoTracking().Where(f => f.CompanyCode == company
            && f.BranchCode == branch && f.InventoryHistoryId.HasValue && histories.Contains(f.InventoryHistoryId.Value)
            && f.StockPosting!.SourceModule == "PROCUREMENT"
            && (f.StockPosting.SourceDocumentType == "PO_INVOICE" || f.StockPosting.SourceDocumentType == "PO_CDN"))
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
            ids.Contains(e.RootStockPostingId) && (e.EffectKind == EffectKind || e.EffectKind == CreditNoteEffectKind)).ToArrayAsync(ct);
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
            if (root is null)
                return false;
            var isCreditNote = root.SourceDocumentType == "PO_CDN";
            if (root.SourceModule != "PROCUREMENT" || root.SourceDocumentType is not ("PO_INVOICE" or "PO_CDN")
                || root.PostingRole is not ("PRIMARY" or "REVERSAL")
                || root.CommandType != $"PROCUREMENT_{root.SourceDocumentType}_{root.PostingRole}"
                || (root.PostingRole == "PRIMARY") != (root.ReversesPostingId is null)
                || root.SealedAtUtc is null && (unsealedRootId != root.Id || db.Database.CurrentTransaction is null)
                || root.PostingSequence <= 0 || string.IsNullOrWhiteSpace(root.SourceSnapshotJson)
                || runs.Length != 1
                || adjustments.Length != 1 || settlements.Length != (isCreditNote ? 0 : 1)
                || effects.Any(e => e.EffectKind != (isCreditNote ? CreditNoteEffectKind : EffectKind)))
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
            if (saleCount > 1)
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
                if (history.TrxType == IvTrxTypes.SalesOut)
                {
                    var delivery = !string.IsNullOrWhiteSpace(history.DoNo);
                    var salesNo = delivery ? history.DoNo : history.InvNo;
                    if (string.IsNullOrWhiteSpace(salesNo) || history.SoLineNo is not > 0
                        || delivery && history.InvNo != history.DoNo || prior.MovementCode != "SALE_OUT"
                        || prior.SourceDocumentType != (delivery ? "SA_DO" : "SA_INVOICE")
                        || prior.SourceDocumentId != salesNo || prior.SourceDocumentNo != salesNo
                        || prior.SourceDocumentLine != history.SoLineNo.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture))
                        return false;
                }
                if (history.TrxDtTime > latestIssue)
                    latestIssue = history.TrxDtTime;
            }
            if (run.LatestEffectiveAt != latestIssue)
                return false;
            var adjustment = adjustments[0];
            if (value.CompanyCode != company || value.BranchCode != branch || value.LedgerEpochId != root.LedgerEpochId
                || value.SourceDocumentType != root.SourceDocumentType || value.SourceDocumentId != root.SourceDocumentId
                || value.SourceDocumentNo != root.SourceDocumentNo || value.EffectiveAt != root.EffectiveAt
                || value.BusinessDate != root.BusinessDate || value.PeriodKey != root.PeriodKey
                || value.BaseQty != 0m || value.CostMethod != StockCostMethods.MovingAverage
                || value.BaseCostAmount != value.CostAmount || value.CostAmount < 0m || value.Direction is not (-1 or 1)
                || value.ValuationSource != StockValuationSources.PurchasePriceVariance
                || value.MovementCode != (value.Direction > 0 ? "PURCHASE_PRICE_VARIANCE_IN" : "PURCHASE_PRICE_VARIANCE_OUT")
                || value.ValuationStatus != (root.PostingRole == "PRIMARY" ? StockValuationStatuses.Valued : StockValuationStatuses.Reversed)
                || adjustment.InventoryAdjustmentFactId != value.Id || adjustment.InventoryAdjustmentAmount != value.SignedValue
                || adjustment.CompanyCode != company || adjustment.BranchCode != branch
                || adjustment.SourceDocumentType != root.SourceDocumentType || adjustment.SourceDocumentNo != root.SourceDocumentNo
                || adjustment.SourceCostingRevision != root.DocumentRevision || adjustment.ItemCode != value.ItemCode
                || (root.PostingRole == "PRIMARY") != (value.ReversesValuationFactId is null)
                || (root.PostingRole == "PRIMARY") != (adjustment.ReversesAdjustmentId is null))
                return false;
            if (isCreditNote)
            {
                if (!await ValidateCdnAuthorityAsync(db, company, branch, root, adjustment, value, ct))
                    return false;
                continue;
            }
            var settlement = settlements[0];
            if (settlement.PiDocNo != root.SourceDocumentNo || settlement.PiCostingRevision != root.DocumentRevision
                || settlement.ItemCode != value.ItemCode || settlement.SettledBaseQty != adjustment.BaseQty
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

    internal static async Task<bool> ValidateEarlierCdnAsync(AppDbContext db, string company, string branch,
        StockPosting owner, StockValuationFact value, CancellationToken ct)
    {
        if (owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
            || owner.SourceModule != "PROCUREMENT" || owner.SourceDocumentType != "PO_CDN"
            || owner.CommandType != "PROCUREMENT_PO_CDN_PRIMARY" || owner.PostingRole != "PRIMARY"
            || owner.ReversesPostingId is not null || owner.PostingSequence <= 0
            || value.StockPostingId != owner.Id || value.ReversesValuationFactId is not null
            || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == owner.Id, ct))
            return false;
        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking()
            .Where(a => a.StockPostingId == owner.Id).ToArrayAsync(ct);
        if (adjustments.Length != 1 || adjustments[0].InventoryAdjustmentFactId != value.Id
            || !await ValidateCdnAuthorityAsync(db, company, branch, owner, adjustments[0], value, ct))
            return false;
        var hasRun = await db.StockReplayRuns.AsNoTracking().AnyAsync(r => r.RootStockPostingId == owner.Id, ct);
        var hasEffects = await db.StockReplayEffects.AsNoTracking().AnyAsync(e => e.RootStockPostingId == owner.Id, ct);
        var hasIssueFacts = await db.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.StockPostingId == owner.Id && f.InventoryHistoryId != null, ct);
        if (hasRun || hasEffects || hasIssueFacts)
            return await ValidateRootsAsync(db, company, branch, [owner.Id], null, ct);
        return await db.StockValuationFacts.AsNoTracking().CountAsync(f => f.StockPostingId == owner.Id, ct) == 1
            && !await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s => s.StockPostingId == owner.Id, ct);
    }

    internal static async Task<bool> ValidateCdnAuthorityAsync(AppDbContext db, string company, string branch,
        StockPosting root, ErpWeb.Model.Entities.Purchase.PurchaseCostAdjustment adjustment,
        StockValuationFact value, CancellationToken ct)
    {
        var primary = root.PostingRole == "PRIMARY" ? root : await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == root.ReversesPostingId, ct);
        if (primary is null || primary.CompanyCode != company || primary.BranchCode != branch
            || primary.LedgerEpochId != root.LedgerEpochId || primary.SourceModule != "PROCUREMENT"
            || primary.SourceDocumentType != "PO_CDN" || primary.CommandType != "PROCUREMENT_PO_CDN_PRIMARY"
            || primary.PostingRole != "PRIMARY" || primary.ReversesPostingId is not null
            || primary.SourceDocumentId != root.SourceDocumentId || primary.SourceDocumentNo != root.SourceDocumentNo
            || primary.DocumentRevision != root.DocumentRevision || primary.EffectiveAt != root.EffectiveAt
            || root.PostingRole == "REVERSAL" && (primary.SealedAtUtc is null || primary.PostingSequence >= root.PostingSequence))
            return false;
        try
        {
            if (StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(primary.SourceSnapshotJson)) != primary.SourceSnapshotHash)
                return false;
            using var document = System.Text.Json.JsonDocument.Parse(primary.SourceSnapshotJson);
            var snapshot = document.RootElement;
            if (!snapshot.TryGetProperty("docNo", out var docNo) || docNo.GetString() != root.SourceDocumentNo
                || !snapshot.TryGetProperty("docDate", out var docDate) || docDate.GetDateTime() != root.EffectiveAt
                || !snapshot.TryGetProperty("type", out var type) || type.GetString() is not ("CN" or "DN")
                || !snapshot.TryGetProperty("invNo", out var invoiceNo) || string.IsNullOrWhiteSpace(invoiceNo.GetString())
                || !snapshot.TryGetProperty("referencedInvoiceRevision", out var revision) || !revision.TryGetInt32(out var invoiceRevision)
                || !snapshot.TryGetProperty("currRate", out var rate) || !rate.TryGetDecimal(out var currencyRate) || currencyRate <= 0m
                || !snapshot.TryGetProperty("returnStock", out var returnStock) || returnStock.GetBoolean()
                || !snapshot.TryGetProperty("lines", out var lines) || lines.ValueKind != System.Text.Json.JsonValueKind.Array
                || lines.GetArrayLength() != 1)
                return false;
            var line = lines[0];
            if (!line.TryGetProperty("line", out var lineNo) || !lineNo.TryGetInt32(out var sourceLine)
                || !line.TryGetProperty("invLineNo", out var invoiceLine) || !invoiceLine.TryGetInt32(out var invoiceLineNo)
                || !line.TryGetProperty("iCode", out var item) || item.GetString() != adjustment.ItemCode
                || !line.TryGetProperty("stdQty", out var qty) || !qty.TryGetDecimal(out var quantity) || quantity <= 0m
                || !line.TryGetProperty("netAmount", out var net) || !net.TryGetDecimal(out var netAmount) || netAmount <= 0m
                || !line.TryGetProperty("isStockReturn", out var stockReturn) || stockReturn.GetBoolean()
                || adjustment.SourceDocumentLine != sourceLine || adjustment.BaseQty != quantity)
                return false;
            ErpWeb.Model.Entities.Purchase.PurchaseCostAdjustment? primaryAdjustment;
            StockValuationFact? primaryValue;
            ErpWeb.Model.Entities.Purchase.PurchaseCostAdjustment? reversedPriorAdjustment = null;
            StockValuationFact? reversedPriorValue = null;
            if (root.PostingRole == "PRIMARY")
            {
                primaryAdjustment = adjustment;
                primaryValue = value;
            }
            else
            {
                reversedPriorAdjustment = await db.PurchaseCostAdjustments.AsNoTracking().SingleOrDefaultAsync(a =>
                    a.Id == adjustment.ReversesAdjustmentId, ct);
                reversedPriorValue = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f =>
                    f.Id == value.ReversesValuationFactId, ct);
                if (reversedPriorAdjustment is null || reversedPriorValue is null)
                    return false;
                if (reversedPriorAdjustment.StockPostingId == primary.Id)
                {
                    primaryAdjustment = reversedPriorAdjustment;
                    primaryValue = reversedPriorValue;
                }
                else
                {
                    var chainReverse = new List<ErpWeb.Model.Entities.Purchase.PurchaseCostAdjustment> { reversedPriorAdjustment };
                    var seenIds = new HashSet<long> { reversedPriorAdjustment.Id };
                    while (chainReverse[^1].ReversesAdjustmentId is long parentId)
                    {
                        if (chainReverse.Count >= 101 || !seenIds.Add(parentId))
                            return false;
                        var parent = await db.PurchaseCostAdjustments.AsNoTracking()
                            .SingleOrDefaultAsync(a => a.Id == parentId, ct);
                        if (parent is null)
                            return false;
                        chainReverse.Add(parent);
                    }
                    chainReverse.Reverse();
                    var generation = await StockReplayPurchaseVarianceGenerationReader.TryReadAsync(db, chainReverse, ct);
                    if (generation is null || generation.Original.StockPostingId != primary.Id
                        || generation.Active.Id != reversedPriorAdjustment.Id
                        || generation.ActiveFact.Id != reversedPriorValue.Id)
                        return false;
                    primaryAdjustment = generation.Original;
                    primaryValue = generation.OriginalFact;
                }
            }
            if (primaryAdjustment is null || primaryValue is null || primaryAdjustment.StockPostingId != primary.Id
                || primaryValue.StockPostingId != primary.Id || primaryAdjustment.InventoryAdjustmentFactId != primaryValue.Id
                || primaryAdjustment.CompanyCode != company || primaryAdjustment.BranchCode != branch
                || primaryAdjustment.SourceDocumentType != "PO_CDN" || primaryAdjustment.SourceDocumentNo != primary.SourceDocumentNo
                || primaryAdjustment.SourceCostingRevision != primary.DocumentRevision
                || primaryAdjustment.SourceDocumentLine != sourceLine || primaryAdjustment.ItemCode != adjustment.ItemCode
                || primaryAdjustment.BaseQty != quantity || primaryAdjustment.CostMethod != StockCostMethods.MovingAverage
                || primaryAdjustment.ReversesAdjustmentId is not null || primaryValue.OriginalValuationFactId is not long receiptId
                || primaryValue.ReversesValuationFactId is not null || primaryValue.ValuationStatus != StockValuationStatuses.Valued
                || primaryValue.CompanyCode != company || primaryValue.BranchCode != branch
                || primaryValue.LedgerEpochId != root.LedgerEpochId || primaryValue.SourceDocumentType != "PO_CDN"
                || primaryValue.SourceDocumentId != primary.SourceDocumentId || primaryValue.SourceDocumentNo != primary.SourceDocumentNo
                || primaryValue.EffectiveAt != primary.EffectiveAt || primaryValue.ItemCode != adjustment.ItemCode
                || primaryValue.InventoryHistoryId is not null || primaryValue.BaseQty != 0m
                || primaryValue.CostMethod != StockCostMethods.MovingAverage || primaryValue.ValuationSource != StockValuationSources.PurchasePriceVariance
                || primaryValue.Direction is not (-1 or 1) || primaryValue.CostAmount < 0m
                || primaryValue.BaseCostAmount != primaryValue.CostAmount || primaryValue.UnitCost != 0m
                || primaryValue.ValuationVersion != 1 || primaryValue.TransactionCurrency is not null
                || primaryValue.TransactionCostAmount is not null || primaryValue.ExchangeRate is not null
                || primaryValue.SourceDocumentLine != sourceLine.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || primaryValue.MovementCode != (primaryValue.Direction > 0 ? "PURCHASE_PRICE_VARIANCE_IN" : "PURCHASE_PRICE_VARIANCE_OUT")
                || primaryAdjustment.TotalAdjustmentAmount == 0m
                || primaryAdjustment.InventoryAdjustmentAmount == 0m
                || Costing.StockLedgerPrecision.Money(primaryAdjustment.InventoryAdjustmentAmount + primaryAdjustment.ConsumedVarianceAmount)
                    != primaryAdjustment.TotalAdjustmentAmount
                || primaryAdjustment.InventoryAdjustmentAmount * Math.Sign(primaryAdjustment.TotalAdjustmentAmount) < 0m
                || Math.Abs(primaryAdjustment.InventoryAdjustmentAmount) > Math.Abs(primaryAdjustment.TotalAdjustmentAmount)
                || primaryValue.SignedValue != primaryAdjustment.InventoryAdjustmentAmount)
                return false;
            var referencedInvoiceNo = invoiceNo.GetString()!;
            var noteType = type.GetString()!;
            var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
                s.CompanyCode == company && s.BranchCode == branch && s.PiDocNo == referencedInvoiceNo
                && s.PiCostingRevision == invoiceRevision && s.PiLineNo == invoiceLineNo
                && s.ReceiptValuationFactId == receiptId && s.ReversesSettlementId == null).ToArrayAsync(ct);
            if (settlements.Length != 1 || settlements[0].ItemCode != adjustment.ItemCode
                || settlements[0].SettledBaseQty < quantity || settlements[0].SettledBaseQty <= 0m
                || await db.PurchaseReceiptCostSettlements.AsNoTracking().CountAsync(s => s.CompanyCode == company
                    && s.BranchCode == branch && s.PiDocNo == referencedInvoiceNo && s.PiCostingRevision == invoiceRevision
                    && s.PiLineNo == invoiceLineNo && s.ReversesSettlementId == null, ct) != 1
                || await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s => s.ReversesSettlementId == settlements[0].Id, ct))
                return false;
            var invoiceOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p =>
                p.Id == settlements[0].StockPostingId && p.CompanyCode == company && p.BranchCode == branch
                && p.LedgerEpochId == root.LedgerEpochId && p.SourceModule == "PROCUREMENT"
                && p.SourceDocumentType == "PO_INVOICE" && p.CommandType == "PROCUREMENT_PO_INVOICE_PRIMARY"
                && p.SourceDocumentId == referencedInvoiceNo && p.DocumentRevision == invoiceRevision
                && p.PostingRole == "PRIMARY" && p.ReversesPostingId == null && p.SealedAtUtc != null, ct);
            if (invoiceOwner is null || invoiceOwner.EffectiveAt > primary.EffectiveAt
                || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(invoiceOwner.SourceSnapshotJson)) != invoiceOwner.SourceSnapshotHash
                || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == invoiceOwner.Id, ct))
                return false;
            using var invoiceDocument = System.Text.Json.JsonDocument.Parse(invoiceOwner.SourceSnapshotJson);
            var invoiceSnapshot = invoiceDocument.RootElement;
            if (!invoiceSnapshot.TryGetProperty("docNo", out var piNo) || piNo.GetString() != referencedInvoiceNo
                || !invoiceSnapshot.TryGetProperty("costingRevision", out var piRevision) || !piRevision.TryGetInt32(out var piRev)
                || piRev != invoiceRevision || !invoiceSnapshot.TryGetProperty("currRate", out var piRate)
                || !piRate.TryGetDecimal(out var invoiceRate) || invoiceRate <= 0m
                || !invoiceSnapshot.TryGetProperty("lines", out var piLines) || piLines.ValueKind != System.Text.Json.JsonValueKind.Array)
                return false;
            var matchingInvoiceLines = piLines.EnumerateArray().Where(x => x.TryGetProperty("line", out var n)
                && n.TryGetInt32(out var number) && number == invoiceLineNo).ToArray();
            if (matchingInvoiceLines.Length != 1 || !matchingInvoiceLines[0].TryGetProperty("iCode", out var piItem)
                || piItem.GetString() != adjustment.ItemCode || !matchingInvoiceLines[0].TryGetProperty("stdQty", out var piQty)
                || !piQty.TryGetDecimal(out var invoiceQty) || invoiceQty != settlements[0].SettledBaseQty
                || !matchingInvoiceLines[0].TryGetProperty("netAmount", out var piNet) || !piNet.TryGetDecimal(out var invoiceNet)
                || settlements[0].AllocatedActualBaseAmount != Costing.StockLedgerPrecision.Money(invoiceNet * invoiceRate))
                return false;
            var receipt = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f => f.Id == receiptId, ct);
            if (receipt is null || receipt.CompanyCode != company || receipt.BranchCode != branch
                || receipt.LedgerEpochId != root.LedgerEpochId || receipt.ItemCode != adjustment.ItemCode
                || receipt.CostMethod != StockCostMethods.MovingAverage || receipt.Direction != 1
                || receipt.InventoryHistoryId is not int receiptHistoryId || receipt.EffectiveAt > root.EffectiveAt
                || receipt.ValuationStatus != StockValuationStatuses.Valued || receipt.ValuationSource != StockValuationSources.ReceiptActual
                || receipt.ReversesValuationFactId is not null || receipt.OriginalValuationFactId is not null
                || receipt.BaseQty != settlements[0].SettledBaseQty || receipt.BaseUom != primaryValue.BaseUom
                || receipt.CostAmount != Costing.StockLedgerPrecision.Money(receipt.BaseQty * receipt.UnitCost)
                || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.ReversesValuationFactId == receipt.Id, ct))
                return false;
            var receiptOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == receipt.StockPostingId
                && p.CompanyCode == company && p.BranchCode == branch && p.LedgerEpochId == root.LedgerEpochId
                && p.SourceModule == "INVENTORY" && p.SourceDocumentType == IvTrxTypes.GoodsReceive
                && p.CommandType == "IV_POST_GR" && p.PostingRole == "PRIMARY" && p.SealedAtUtc != null, ct);
            var receiptHistory = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h => h.Id == receiptHistoryId
                && h.CompanyCode == company && h.BranchCode == branch && h.StockPostingId == receipt.StockPostingId
                && h.LedgerVersion == 2 && h.LedgerEpochId == root.LedgerEpochId
                && h.ICode == adjustment.ItemCode && h.TrxType == IvTrxTypes.GoodsReceive && h.EntryRole == "ORIGINAL"
                && h.BatchStatus == IvBatchStatuses.Posted && h.ToStdUom == receipt.BaseUom && h.TrxDtTime == receipt.EffectiveAt
                && h.PoNo == settlements[0].PoNo && h.PoRelNo == settlements[0].PoRelNo && h.PoLineNo == settlements[0].PoLineNo, ct);
            if (receiptOwner is null || receiptHistory is null || receiptHistory.BaseUnitPrices is not decimal commercialUnit
                || commercialUnit < 0m || receiptHistory.ToStdQty != receipt.BaseQty
                || receiptOwner.EffectiveAt != receipt.EffectiveAt || receiptOwner.ReversesPostingId is not null
                || await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.ReversesHistoryId == receiptHistory.Id, ct)
                || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(receiptOwner.SourceSnapshotJson)) != receiptOwner.SourceSnapshotHash
                || settlements[0].ReceiptInventoryHistoryId != receiptHistory.Id || settlements[0].ReceiptBatchNo != receiptHistory.BatchNo
                || settlements[0].ReceiptCommercialUnitCost != Costing.StockLedgerPrecision.Money(commercialUnit)
                || settlements[0].ReceiptCommercialBaseAmount != Costing.StockLedgerPrecision.Money(receipt.BaseQty * commercialUnit)
                || settlements[0].ReceiptValuationUnitCost != receipt.UnitCost || settlements[0].ReceiptValuationBaseAmount != receipt.CostAmount
                || settlements[0].CommercialVarianceAmount != Costing.StockLedgerPrecision.Money(
                    settlements[0].AllocatedActualBaseAmount - settlements[0].ReceiptCommercialBaseAmount)
                || settlements[0].ValuationVarianceAmount != Costing.StockLedgerPrecision.Money(
                    settlements[0].AllocatedActualBaseAmount - settlements[0].ReceiptValuationBaseAmount))
                return false;
            var actual = Costing.StockLedgerPrecision.Money(netAmount * currencyRate);
            var expectedTotal = noteType == "CN" ? -actual : actual;
            var reference = Costing.StockLedgerPrecision.Money(settlements[0].ReceiptCommercialBaseAmount
                * quantity / settlements[0].SettledBaseQty);
            if (primaryAdjustment.AdjustmentType != (noteType == "CN" ? "PURCHASE_CN_PRICE_ADJUSTMENT" : "PURCHASE_DN_PRICE_ADJUSTMENT")
                || primaryAdjustment.ActualBaseAmount != actual || primaryAdjustment.ReferenceBaseAmount != reference
                || primaryAdjustment.CommercialReferenceAmount != reference || primaryAdjustment.TotalAdjustmentAmount != expectedTotal
                || primaryAdjustment.PoNo != settlements[0].PoNo || primaryAdjustment.PoRelNo != settlements[0].PoRelNo
                || primaryAdjustment.PoLineNo != settlements[0].PoLineNo || primaryValue.OriginalValuationFactId != receiptId)
                return false;
            if (root.PostingRole == "PRIMARY")
                return true;
            var targetAdjustment = reversedPriorAdjustment!;
            var targetValue = reversedPriorValue!;
            return adjustment.ReversesAdjustmentId == targetAdjustment.Id
                && value.ReversesValuationFactId == targetValue.Id && value.OriginalValuationFactId == targetValue.Id
                && adjustment.AdjustmentType == targetAdjustment.AdjustmentType
                && adjustment.SourceDocumentLine == targetAdjustment.SourceDocumentLine
                && adjustment.CostMethod == targetAdjustment.CostMethod
                && adjustment.PoNo == targetAdjustment.PoNo && adjustment.PoRelNo == targetAdjustment.PoRelNo
                && adjustment.PoLineNo == targetAdjustment.PoLineNo
                && adjustment.ActualBaseAmount == targetAdjustment.ActualBaseAmount
                && adjustment.ReferenceBaseAmount == targetAdjustment.ReferenceBaseAmount
                && adjustment.CommercialReferenceAmount == targetAdjustment.CommercialReferenceAmount
                && adjustment.TotalAdjustmentAmount == -targetAdjustment.TotalAdjustmentAmount
                && adjustment.InventoryAdjustmentAmount == -targetAdjustment.InventoryAdjustmentAmount
                && adjustment.ConsumedVarianceAmount == -targetAdjustment.ConsumedVarianceAmount
                && adjustment.BaseQty == targetAdjustment.BaseQty && value.Direction == -targetValue.Direction
                && value.CostAmount == targetValue.CostAmount && value.BaseUom == targetValue.BaseUom
                && value.ValuationVersion == targetValue.ValuationVersion;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }
}
