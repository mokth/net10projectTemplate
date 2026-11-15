using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ErpWeb.Core.StockLedger;

internal sealed record StockReplayPurchaseVarianceGeneration(
    PurchaseCostAdjustment Original, PurchaseCostAdjustment Active,
    StockValuationFact OriginalFact, StockValuationFact ActiveFact,
    long[] ChainIds, string Fingerprint);

/// <summary>Proves one active purchase-variance generation and its original invoice owner.</summary>
internal static class StockReplayPurchaseVarianceGenerationReader
{
    internal const string ReplacementEffect = "PO_INVOICE_COST_REPLACEMENT";

    internal static async Task<StockReplayPurchaseVarianceGeneration?> TryReadAsync(
        AppDbContext db, IReadOnlyList<PurchaseCostAdjustment> chain,
        CancellationToken cancellationToken)
    {
        if (chain.Count == 0 || chain.Count > 101 || chain.Count % 2 == 0)
            return null;
        var original = chain[0];
        if (original.ReversesAdjustmentId is not null
            || original.InventoryAdjustmentFactId is not long originalFactId
            || original.CostMethod != StockCostMethods.MovingAverage
            || original.SourceDocumentType != "PO_INVOICE"
            || original.AdjustmentType != "PURCHASE_PRICE_VARIANCE"
            || original.BaseQty <= 0m || original.TotalAdjustmentAmount == 0m
            || StockLedgerPrecision.Quantity(original.BaseQty) != original.BaseQty
            || StockLedgerPrecision.Money(original.TotalAdjustmentAmount) != original.TotalAdjustmentAmount
            || StockLedgerPrecision.Money(original.InventoryAdjustmentAmount) != original.InventoryAdjustmentAmount
            || StockLedgerPrecision.Money(original.ConsumedVarianceAmount) != original.ConsumedVarianceAmount
            || original.InventoryAdjustmentAmount == 0m
            || original.InventoryAdjustmentAmount * Math.Sign(original.TotalAdjustmentAmount) <= 0m
            || Math.Abs(original.InventoryAdjustmentAmount) > Math.Abs(original.TotalAdjustmentAmount)
            || StockLedgerPrecision.Money(original.ActualBaseAmount - original.ReferenceBaseAmount)
                != original.TotalAdjustmentAmount
            || StockLedgerPrecision.Money(original.InventoryAdjustmentAmount + original.ConsumedVarianceAmount)
                != original.TotalAdjustmentAmount)
            return null;
        var factIds = chain.Where(a => a.InventoryAdjustmentFactId.HasValue)
            .Select(a => a.InventoryAdjustmentFactId!.Value).Distinct().ToArray();
        if (factIds.Length != chain.Count)
            return null;
        var facts = await db.StockValuationFacts.AsNoTracking().Where(f => factIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        if (facts.Count != factIds.Length || !facts.TryGetValue(originalFactId, out var originalFact)
            || originalFact.StockPostingId != original.StockPostingId
            || originalFact.SignedValue != original.InventoryAdjustmentAmount
            || originalFact.OriginalValuationFactId is not long receiptFactId)
            return null;
        var invoiceOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p =>
            p.Id == original.StockPostingId && p.CompanyCode == original.CompanyCode
            && p.BranchCode == original.BranchCode, cancellationToken);
        var receipt = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f =>
            f.Id == receiptFactId && f.CompanyCode == original.CompanyCode
            && f.BranchCode == original.BranchCode, cancellationToken);
        var history = receipt?.InventoryHistoryId is int historyId
            ? await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h => h.Id == historyId
                && h.CompanyCode == original.CompanyCode && h.BranchCode == original.BranchCode, cancellationToken)
            : null;
        var receiptOwner = receipt is null ? null : await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p =>
            p.Id == receipt.StockPostingId && p.CompanyCode == original.CompanyCode
            && p.BranchCode == original.BranchCode, cancellationToken);
        if (invoiceOwner is null || receipt is null || history is null || receiptOwner is null)
            return null;
        try
        {
            StockReplayWacValueEventFactory.CreatePurchaseInvoiceAdjustment(
                originalFact, receipt, history, receiptOwner, invoiceOwner);
        }
        catch (StockLedgerException) { return null; }

        var correctionIds = chain.Skip(1).Select(a => a.StockPostingId).Distinct().ToArray();
        if (correctionIds.Length != (chain.Count - 1) / 2)
            return null;
        var owners = await db.StockPostings.AsNoTracking().Where(p => correctionIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var correctionHistories = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.StockPostingId.HasValue && correctionIds.Contains(h.StockPostingId.Value)).ToArrayAsync(cancellationToken);
        var batchNos = correctionHistories.Select(h => h.BatchNo).Distinct().ToArray();
        var batches = await db.IvTrxBatches.AsNoTracking().Where(b => b.CompanyCode == original.CompanyCode
            && b.BranchCode == original.BranchCode && batchNos.Contains(b.BatchNo)).ToArrayAsync(cancellationToken);
        var reversedHistoryIds = correctionHistories.Where(h => h.ReversesHistoryId.HasValue)
            .Select(h => h.ReversesHistoryId!.Value).Distinct().ToArray();
        var reversedHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => reversedHistoryIds.Contains(h.Id)).ToArrayAsync(cancellationToken);
        var runs = await db.StockReplayRuns.AsNoTracking().Where(r => correctionIds.Contains(r.RootStockPostingId))
            .ToDictionaryAsync(r => r.RootStockPostingId, cancellationToken);
        var runIds = runs.Values.Select(r => r.Id).ToArray();
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e => runIds.Contains(e.ReplayRunId)
            && e.EffectKind == ReplacementEffect).ToArrayAsync(cancellationToken);
        var prior = original;
        var priorFact = originalFact;
        for (var i = 1; i < chain.Count; i += 2)
        {
            var reversal = chain[i];
            var replacement = chain[i + 1];
            if (!MatchesAdjustment(reversal, original) || !MatchesAdjustment(replacement, original)
                || reversal.ReversesAdjustmentId != prior.Id
                || replacement.ReversesAdjustmentId != reversal.Id
                || reversal.StockPostingId != replacement.StockPostingId
                || reversal.TotalAdjustmentAmount != -prior.TotalAdjustmentAmount
                || reversal.InventoryAdjustmentAmount != -prior.InventoryAdjustmentAmount
                || reversal.ConsumedVarianceAmount != -prior.ConsumedVarianceAmount
                || replacement.TotalAdjustmentAmount != original.TotalAdjustmentAmount
                || replacement.InventoryAdjustmentAmount == 0m
                || StockLedgerPrecision.Money(replacement.InventoryAdjustmentAmount) != replacement.InventoryAdjustmentAmount
                || StockLedgerPrecision.Money(replacement.ConsumedVarianceAmount) != replacement.ConsumedVarianceAmount
                || StockLedgerPrecision.Money(replacement.InventoryAdjustmentAmount + replacement.ConsumedVarianceAmount)
                    != original.TotalAdjustmentAmount
                || replacement.InventoryAdjustmentAmount * Math.Sign(original.TotalAdjustmentAmount) <= 0m
                || Math.Abs(replacement.InventoryAdjustmentAmount) > Math.Abs(original.TotalAdjustmentAmount)
                || reversal.InventoryAdjustmentFactId is not long reverseFactId
                || replacement.InventoryAdjustmentFactId is not long replacementFactId
                || !facts.TryGetValue(reverseFactId, out var reverseFact)
                || !facts.TryGetValue(replacementFactId, out var replacementFact)
                || !owners.TryGetValue(replacement.StockPostingId, out var owner)
                || !runs.TryGetValue(replacement.StockPostingId, out var run)
                || owner.SealedAtUtc is null || owner.CompanyCode != original.CompanyCode
                || owner.BranchCode != original.BranchCode || owner.LedgerEpochId != invoiceOwner.LedgerEpochId
                || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
                || owner.PostingRole is not ("PRIMARY" or "REVERSAL")
                || owner.CommandType != $"IV_{(owner.PostingRole == "PRIMARY" ? "POST" : "ROLLBACK")}_{IvTrxTypes.MiscellaneousReceipt}".ToUpperInvariant()
                || (owner.PostingRole == "PRIMARY") != (owner.ReversesPostingId is null)
                || owner.EffectiveAt > invoiceOwner.EffectiveAt || owner.PeriodKey != invoiceOwner.PeriodKey
                || run.CompanyCode != original.CompanyCode || run.BranchCode != original.BranchCode
                || run.RootRequestId != owner.RequestId || run.RootDocumentType != owner.SourceDocumentType
                || run.RootDocumentId != owner.SourceDocumentId || run.PlanHash.Length != 64
                || run.EarliestEffectiveAt != owner.EffectiveAt || run.LatestEffectiveAt < invoiceOwner.EffectiveAt
                || run.LatestEffectiveAt.Year != owner.EffectiveAt.Year || run.LatestEffectiveAt.Month != owner.EffectiveAt.Month
                || run.PlanWatermark < 0 || run.AffectedHistoryCount <= 0
                || run.AffectedItemCount != 1 || run.AffectedProductionMovementCount != 0
                || !HasReceiptHistory(owner)
                || !MatchesFact(reverseFact, originalFact, owner.Id)
                || reverseFact.ValuationStatus != StockValuationStatuses.Reversed
                || reverseFact.ValuationSource != StockValuationSources.OriginalReversal
                || reverseFact.ReversesValuationFactId != priorFact.Id
                || reverseFact.OriginalValuationFactId != originalFact.Id
                || reverseFact.Direction != -priorFact.Direction || reverseFact.CostAmount != priorFact.CostAmount
                || reverseFact.ValuationVersion != priorFact.ValuationVersion
                || !MatchesFact(replacementFact, originalFact, owner.Id)
                || replacementFact.ValuationStatus != StockValuationStatuses.Valued
                || replacementFact.ValuationSource != StockValuationSources.PurchasePriceVariance
                || replacementFact.OriginalValuationFactId != originalFact.Id
                || replacementFact.ReversesValuationFactId is not null
                || replacementFact.SignedValue != replacement.InventoryAdjustmentAmount
                || replacementFact.ValuationVersion <= priorFact.ValuationVersion
                || effects.Count(e => e.ReplayRunId == run.Id) != 4
                || !HasEffect(run, reversal.Id, StockReplayEffectTypes.PurchaseCostAdjustment, prior.Id)
                || !HasEffect(run, replacement.Id, StockReplayEffectTypes.PurchaseCostAdjustment, prior.Id)
                || !HasEffect(run, reverseFact.Id, StockReplayEffectTypes.StockValuationFact, priorFact.Id)
                || !HasEffect(run, replacementFact.Id, StockReplayEffectTypes.StockValuationFact, priorFact.Id))
                return null;
            prior = replacement;
            priorFact = replacementFact;
        }

        var fingerprint = StockPostingFingerprint.Create(new { Kind = "WAC_PURCHASE_VARIANCE_GENERATION" }, new
        {
            invoiceOwner.Id, receiptFactId,
            Adjustments = chain.Select(a => new { a.Id, a.StockPostingId, a.ReversesAdjustmentId,
                a.BaseQty, a.ActualBaseAmount, a.ReferenceBaseAmount, a.CommercialReferenceAmount,
                a.TotalAdjustmentAmount, a.InventoryAdjustmentAmount, a.ConsumedVarianceAmount,
                a.InventoryAdjustmentFactId }).ToArray(),
            Facts = facts.Values.OrderBy(f => f.Id).Select(f => new { f.Id, f.StockPostingId,
                f.OriginalValuationFactId, f.ReversesValuationFactId, f.SignedValue,
                f.ValuationStatus, f.ValuationVersion, f.SourceLineId, f.SourceDocumentType,
                f.SourceDocumentId, f.SourceDocumentNo, f.SourceDocumentLine, f.EffectiveAt,
                f.BusinessDate, f.PeriodKey, f.ItemCode, f.BaseUom, f.MovementCode, f.CostMethod,
                f.BaseCostAmount, f.BaseCurrency, f.TransactionCurrency, f.TransactionCostAmount,
                f.ExchangeRate, f.WarehouseCode, f.LocationCode, f.LotId, f.LotNo, f.ItemStatus }).ToArray(),
            Owners = owners.Values.OrderBy(o => o.Id).Select(o => new { o.Id, o.RequestId, o.CommandType,
                o.SourceModule, o.SourceDocumentType, o.SourceDocumentId, o.SourceDocumentNo, o.PostingRole,
                o.DocumentRevision, o.EffectiveAt, o.PeriodKey, o.LedgerEpochId, o.SealedAtUtc, o.ReversesPostingId,
                o.SourceSnapshotJson, o.SourceSnapshotHash }).ToArray(),
            Histories = correctionHistories.OrderBy(h => h.Id).Select(h => new { h.Id, h.StockPostingId,
                h.ReversesHistoryId, h.BatchNo, h.TrxType, h.ICode, h.TrxDtTime, h.LedgerVersion,
                h.LedgerEpochId, h.EntryRole, h.ValuationStatus, h.DocumentRevision,
                h.ToStdQty, h.FrStdQty, h.ToBalLocId, h.FromBalLocId, h.ToStdUom, h.FrStdUom,
                h.ToWarehouse, h.FrWarehouse, h.ToLocation, h.FrLocation, h.ToLotNo, h.FrLotNo }).ToArray(),
            ReversedHistories = reversedHistories.OrderBy(h => h.Id).Select(h => new { h.Id,
                h.StockPostingId, h.BatchNo, h.ICode, h.LedgerEpochId, h.TrxType, h.EntryRole,
                h.ToStdQty, h.ToBalLocId }).ToArray(),
            Batches = batches.OrderBy(b => b.Id).Select(b => new { b.Id, b.BatchNo }).ToArray(),
            Runs = runs.Values.OrderBy(r => r.Id).Select(r => new { r.Id, r.RootStockPostingId,
                r.PlanHash, r.PlanWatermark }).ToArray(),
            Effects = effects.OrderBy(e => e.Id).Select(e => new { e.Id, e.ReplayRunId,
                e.BusinessOwnerPostingId, e.AffectedEntityType, e.AffectedEntityId, e.OriginalEntityId }).ToArray()
        }).SourceSnapshotHash;
        return new(original, prior, originalFact, priorFact, chain.Select(a => a.Id).ToArray(), fingerprint);

        bool HasEffect(StockReplayRun run, long id, string type, long priorId) => effects.Count(e =>
            e.ReplayRunId == run.Id && e.RootStockPostingId == run.RootStockPostingId
            && e.CompanyCode == original.CompanyCode && e.BranchCode == original.BranchCode
            && e.BusinessOwnerPostingId == invoiceOwner.Id && e.AffectedEntityType == type
            && e.AffectedEntityId == id && e.OriginalEntityId == priorId) == 1;

        bool HasReceiptHistory(StockPosting owner)
        {
            var found = correctionHistories.Where(h => h.StockPostingId == owner.Id).ToArray();
            if (found.Length != 1)
                return false;
            var h = found[0];
            var primary = owner.PostingRole == "PRIMARY";
            if (h.CompanyCode != original.CompanyCode || h.BranchCode != original.BranchCode
                || h.LedgerEpochId != invoiceOwner.LedgerEpochId || h.LedgerVersion != 2
                || h.TrxType != IvTrxTypes.MiscellaneousReceipt || h.ICode != original.ItemCode
                || h.TrxDtTime != owner.EffectiveAt || h.BatchStatus != IvBatchStatuses.Posted
                || h.EntryRole != (primary ? "ORIGINAL" : "REVERSAL")
                || h.ValuationStatus != (primary ? StockValuationStatuses.Valued : StockValuationStatuses.Reversed)
                || h.DocumentRevision != owner.DocumentRevision
                || owner.SourceDocumentNo != h.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || !batches.Any(b => b.BatchNo == h.BatchNo && owner.SourceDocumentId ==
                    b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                || !MatchesReceiptSnapshot(owner, h, primary))
                return false;
            if (primary)
                return h.ReversesHistoryId is null && h.ToStdQty is > 0m && h.FrStdQty is not > 0m;
            if (h.ReversesHistoryId is not int priorId || h.FrStdQty is not > 0m || h.ToStdQty is > 0m)
                return false;
            var prior = reversedHistories.SingleOrDefault(x => x.Id == priorId);
            return prior is not null && prior.StockPostingId == owner.ReversesPostingId
                && prior.CompanyCode == h.CompanyCode && prior.BranchCode == h.BranchCode
                && prior.LedgerEpochId == h.LedgerEpochId && prior.TrxType == h.TrxType
                && prior.BatchNo == h.BatchNo && prior.ICode == h.ICode && prior.EntryRole == "ORIGINAL"
                && prior.ToStdQty == h.FrStdQty && prior.ToBalLocId == h.FromBalLocId;
        }
    }

    internal static async Task<long[]> ValidatedActiveFactIdsAsync(AppDbContext db,
        string company, string branch, string invoiceNo, int revision, CancellationToken ct)
    {
        var rows = await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
            a.CompanyCode == company && a.BranchCode == branch && a.SourceDocumentType == "PO_INVOICE"
            && a.SourceDocumentNo == invoiceNo && a.SourceCostingRevision == revision).ToArrayAsync(ct);
        var groups = rows.Where(a => a.ReversesAdjustmentId.HasValue)
            .GroupBy(a => a.ReversesAdjustmentId!.Value).ToArray();
        if (groups.Any(g => g.Count() != 1))
            return [];
        var children = groups.ToDictionary(g => g.Key, g => g.Single());
        var result = new List<long>();
        var visited = new HashSet<long>();
        foreach (var root in rows.Where(a => a.ReversesAdjustmentId is null))
        {
            var chain = new List<PurchaseCostAdjustment> { root };
            while (children.TryGetValue(chain[^1].Id, out var child))
            {
                if (chain.Count >= 101 || chain.Any(a => a.Id == child.Id))
                    return [];
                chain.Add(child);
            }
            if (chain.Any(a => !visited.Add(a.Id)))
                return [];
            if (chain.Count == 1)
                continue;
            var ids = chain.Select(a => a.Id).ToArray();
            if (await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(a =>
                    a.ReversesAdjustmentId.HasValue && ids.Contains(a.ReversesAdjustmentId.Value)
                    && !ids.Contains(a.Id), ct))
                continue;
            var generation = await TryReadAsync(db, chain, ct);
            if (generation is not null)
                result.Add(generation.ActiveFact.Id);
        }
        return visited.Count == rows.Length ? result.Distinct().ToArray() : [];
    }

    internal static async Task<bool> ValidateForSnapshotAsync(AppDbContext db,
        string company, string branch, DateTime exclusiveEnd, long watermark, CancellationToken ct)
    {
        var factRoots = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.CompanyCode == company && f.BranchCode == branch && f.EffectiveAt < exclusiveEnd
            && f.SourceDocumentType == "PO_INVOICE" && f.InventoryHistoryId == null
            && f.StockPosting!.SealedAtUtc != null && f.StockPosting.PostingSequence <= watermark
            && f.StockPosting.SourceModule == "INVENTORY"
            && f.StockPosting.SourceDocumentType == IvTrxTypes.MiscellaneousReceipt)
            .Select(f => f.StockPostingId).Distinct().ToArrayAsync(ct);
        var adjustmentRoots = await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
            a.CompanyCode == company && a.BranchCode == branch && a.SourceDocumentType == "PO_INVOICE"
            && a.StockPosting!.SealedAtUtc != null && a.StockPosting.PostingSequence <= watermark
            && a.StockPosting.EffectiveAt < exclusiveEnd && a.StockPosting.SourceModule == "INVENTORY"
            && a.StockPosting.SourceDocumentType == IvTrxTypes.MiscellaneousReceipt)
            .Select(a => a.StockPostingId).Distinct().ToArrayAsync(ct);
        var effectRoots = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.CompanyCode == company && e.BranchCode == branch && e.EffectKind == ReplacementEffect
            && db.StockPostings.Any(p => p.Id == e.RootStockPostingId && p.CompanyCode == company
                && p.BranchCode == branch && p.SealedAtUtc != null && p.PostingSequence <= watermark
                && p.EffectiveAt < exclusiveEnd))
            .Select(e => e.RootStockPostingId).Distinct().ToArrayAsync(ct);
        var rootIds = factRoots.Concat(adjustmentRoots).Concat(effectRoots).Distinct().ToArray();
        if (rootIds.Length == 0)
            return true;

        var corrections = new List<PurchaseCostAdjustment>();
        var valueFacts = new List<StockValuationFact>();
        foreach (var chunk in rootIds.Chunk(500))
        {
            corrections.AddRange(await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
                a.CompanyCode == company && a.BranchCode == branch && a.SourceDocumentType == "PO_INVOICE"
                && chunk.Contains(a.StockPostingId)).ToArrayAsync(ct));
            valueFacts.AddRange(await db.StockValuationFacts.AsNoTracking().Where(f =>
                f.CompanyCode == company && f.BranchCode == branch && f.SourceDocumentType == "PO_INVOICE"
                && f.InventoryHistoryId == null && chunk.Contains(f.StockPostingId)).ToArrayAsync(ct));
        }
        var byId = corrections.ToDictionary(a => a.Id);
        for (var depth = 0; depth <= 101; depth++)
        {
            var missing = byId.Values.Where(a => a.ReversesAdjustmentId.HasValue
                    && !byId.ContainsKey(a.ReversesAdjustmentId.Value))
                .Select(a => a.ReversesAdjustmentId!.Value).Distinct().ToArray();
            if (missing.Length == 0)
                break;
            if (depth == 101)
                return false;
            var parents = new List<PurchaseCostAdjustment>();
            foreach (var chunk in missing.Chunk(500))
                parents.AddRange(await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
                    a.CompanyCode == company && a.BranchCode == branch && chunk.Contains(a.Id)).ToArrayAsync(ct));
            if (parents.Count != missing.Length)
                return false;
            foreach (var parent in parents)
                byId.Add(parent.Id, parent);
        }

        var correctionsByRoot = corrections.GroupBy(a => a.StockPostingId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var factsByRoot = valueFacts.GroupBy(f => f.StockPostingId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var longestByInvoiceRoot = new Dictionary<long, PurchaseCostAdjustment[]>();
        foreach (var replayRoot in rootIds)
        {
            var pair = correctionsByRoot.GetValueOrDefault(replayRoot) ?? [];
            var factsForRoot = factsByRoot.GetValueOrDefault(replayRoot) ?? [];
            if (pair.Length != 2 || factsForRoot.Length != 2
                || pair.Any(a => a.InventoryAdjustmentFactId is null)
                || !pair.Select(a => a.InventoryAdjustmentFactId!.Value).Order()
                    .SequenceEqual(factsForRoot.Select(f => f.Id).Order()))
                return false;
            var replacements = pair.Where(a => a.ReversesAdjustmentId.HasValue
                && pair.Any(other => other.Id == a.ReversesAdjustmentId.Value)).ToArray();
            if (replacements.Length != 1)
                return false;
            var reverseChain = new List<PurchaseCostAdjustment> { replacements[0] };
            var visited = new HashSet<long> { replacements[0].Id };
            while (reverseChain[^1].ReversesAdjustmentId is long parentId)
            {
                if (reverseChain.Count >= 101 || !visited.Add(parentId)
                    || !byId.TryGetValue(parentId, out var parent))
                    return false;
                reverseChain.Add(parent);
            }
            reverseChain.Reverse();
            var chain = reverseChain.ToArray();
            if (chain.Length % 2 == 0 || chain[0].SourceDocumentType != "PO_INVOICE")
                return false;
            if (longestByInvoiceRoot.TryGetValue(chain[0].Id, out var previous))
            {
                var shorter = previous.Length <= chain.Length ? previous : chain;
                var longer = previous.Length > chain.Length ? previous : chain;
                if (!shorter.Select(a => a.Id).SequenceEqual(longer.Take(shorter.Length).Select(a => a.Id)))
                    return false;
                longestByInvoiceRoot[chain[0].Id] = longer;
            }
            else
                longestByInvoiceRoot.Add(chain[0].Id, chain);
        }
        foreach (var chain in longestByInvoiceRoot.Values)
            if (await TryReadAsync(db, chain, ct) is null)
                return false;
        return true;
    }

    private static bool MatchesAdjustment(PurchaseCostAdjustment row, PurchaseCostAdjustment root) =>
        row.CompanyCode == root.CompanyCode && row.BranchCode == root.BranchCode
        && row.AdjustmentType == root.AdjustmentType && row.SourceDocumentType == root.SourceDocumentType
        && row.SourceDocumentNo == root.SourceDocumentNo && row.SourceDocumentLine == root.SourceDocumentLine
        && row.SourceCostingRevision == root.SourceCostingRevision && row.ItemCode == root.ItemCode
        && row.CostMethod == root.CostMethod && row.PoNo == root.PoNo && row.PoRelNo == root.PoRelNo
        && row.PoLineNo == root.PoLineNo && row.BaseQty == root.BaseQty
        && row.ActualBaseAmount == root.ActualBaseAmount && row.ReferenceBaseAmount == root.ReferenceBaseAmount
        && row.CommercialReferenceAmount == root.CommercialReferenceAmount;

    private static bool MatchesFact(StockValuationFact fact, StockValuationFact root, long owner) =>
        fact.CompanyCode == root.CompanyCode && fact.BranchCode == root.BranchCode
        && fact.LedgerEpochId == root.LedgerEpochId && fact.StockPostingId == owner
        && fact.SourceLineId == root.SourceLineId && fact.SourceDocumentType == root.SourceDocumentType
        && fact.SourceDocumentId == root.SourceDocumentId && fact.SourceDocumentNo == root.SourceDocumentNo
        && fact.SourceDocumentLine == root.SourceDocumentLine && fact.EffectiveAt == root.EffectiveAt
        && fact.BusinessDate == root.BusinessDate && fact.PeriodKey == root.PeriodKey
        && fact.ItemCode == root.ItemCode && fact.BaseUom == root.BaseUom
        && fact.WarehouseCode == root.WarehouseCode && fact.LocationCode == root.LocationCode
        && fact.LotId == root.LotId && fact.LotNo == root.LotNo && fact.ItemStatus == root.ItemStatus
        && fact.MovementCode == root.MovementCode && fact.CostMethod == root.CostMethod
        && fact.InventoryHistoryId is null && fact.FromBalLocId is null && fact.ToBalLocId is null
        && fact.ProductionMovementId == root.ProductionMovementId && fact.WorkOrderId == root.WorkOrderId
        && fact.WorkOrderOperationId == root.WorkOrderOperationId
        && fact.ProductionPostingLinkId == root.ProductionPostingLinkId
        && fact.BaseQty == 0m && fact.UnitCost == 0m && fact.Direction is (-1 or 1)
        && fact.CostAmount > 0m && StockLedgerPrecision.Money(fact.CostAmount) == fact.CostAmount
        && fact.BaseCostAmount == fact.CostAmount
        && fact.BaseCurrency == root.BaseCurrency && fact.TransactionCurrency == root.TransactionCurrency
        && fact.TransactionCostAmount == root.TransactionCostAmount && fact.ExchangeRate == root.ExchangeRate;

    private static bool MatchesReceiptSnapshot(StockPosting owner,
        ErpWeb.Model.Entities.Inventory.IvTrxHistory history, bool primary)
    {
        try
        {
            if (StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(owner.SourceSnapshotJson))
                != owner.SourceSnapshotHash)
                return false;
            using var document = JsonDocument.Parse(owner.SourceSnapshotJson);
            var snapshot = document.RootElement;
            var lines = snapshot.GetProperty("lines");
            if (lines.GetArrayLength() != 1 || snapshot.GetProperty("id").GetInt32().ToString(
                    System.Globalization.CultureInfo.InvariantCulture) != owner.SourceDocumentId
                || snapshot.GetProperty("trxDtTime").GetDateTime() != owner.EffectiveAt)
                return false;
            var line = lines[0];
            return line.GetProperty("documentRevision").GetInt32() == owner.DocumentRevision
                && line.GetProperty("trxLineNo").GetInt32() == history.TrxLineNo
                && line.GetProperty("iCode").GetString() == history.ICode
                && line.GetProperty("toStdQty").GetDecimal() == (primary ? history.ToStdQty : history.FrStdQty)
                && line.GetProperty("toStdUom").GetString() == (primary ? history.ToStdUom : history.FrStdUom)
                && line.GetProperty("toWarehouse").GetString() == (primary ? history.ToWarehouse : history.FrWarehouse)
                && line.GetProperty("toLocation").GetString() == (primary ? history.ToLocation : history.FrLocation)
                && line.GetProperty("toLotNo").GetString() == (primary ? history.ToLotNo : history.FrLotNo);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or ArgumentException)
        {
            return false;
        }
    }
}
