using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record FifoReceiptCycles(IvTrxHistory? Active, string EvidenceHash,
        IReadOnlyList<long> LaterRootIds);

    // Verify every retired source receipt and its exact reversal before allowing
    // another generation of an already-corrected issue. This does not change
    // receipt or PO data, and accepts only one line on one unchanged pile.
    private static async Task<FifoReceiptCycles?> ReadFifoReceiptCyclesAsync(
        AppDbContext db, string company, string branch, IvTrxBatch batch, IvTrxBatchDetail detail,
        int pileId, IvTrxHistory issue, long issueSequence, long reversalSequence,
        bool removal, CancellationToken ct)
    {
        var histories = await db.IvTrxHistories.AsNoTracking().Where(h => h.CompanyCode == company
            && h.BranchCode == branch && h.BatchNo == batch.BatchNo && h.TrxType == batch.TrxType).ToArrayAsync(ct);
        if (histories.Any(h => h.LedgerVersion != 2 || h.BatchStatus != IvBatchStatuses.Posted
                || h.EntryRole is not ("ORIGINAL" or "REVERSAL") || h.StockPostingId is not > 0
                || h.LedgerEpochId != issue.LedgerEpochId || h.ICode != issue.ICode
                || h.TrxDtTime != batch.TrxDtTime || h.TrxLineNo != detail.TrxLineNo)
            || histories.Select(h => h.StockPostingId).Distinct().Count() != histories.Length)
            return null;
        var originals = histories.Where(h => h.EntryRole == "ORIGINAL").ToArray();
        var reversals = histories.Where(h => h.EntryRole == "REVERSAL").ToArray();
        if (originals.Any(h => h.ReversesHistoryId is not null || h.FromBalLocId is not null
                || h.ToBalLocId != pileId || h.FrStdQty is not null || h.ToStdQty is not > 0m)
            || reversals.Any(r => r.ReversesHistoryId is not int id || originals.Count(o => o.Id == id) != 1)
            || reversals.GroupBy(r => r.ReversesHistoryId).Any(g => g.Count() != 1)) return null;
        var active = originals.Where(o => !reversals.Any(r => r.ReversesHistoryId == o.Id)).ToArray();
        if (active.Length != (removal ? 1 : 0)) return null;
        if (removal && (active[0].DocumentRevision != detail.DocumentRevision || active[0].ToStdQty != detail.ToStdQty
                || active[0].ToStdUom != detail.ToStdUom || active[0].ToWarehouse != detail.ToWarehouse
                || !string.Equals(active[0].ToLocation ?? string.Empty, detail.ToLocation ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(active[0].ToLotNo ?? string.Empty, detail.ToLotNo ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || active[0].IStatus != detail.IStatus)) return null;
        if (batch.TrxType == IvTrxTypes.GoodsReceive && histories.Any(h => h.PoNo != detail.PoNo
                || h.PoRelNo != detail.PoRelNo || h.PoLineNo != detail.PoLineNo)) return null;

        var historyIds = histories.Select(h => h.Id).ToArray();
        var ownerIds = histories.Select(h => h.StockPostingId!.Value).ToArray();
        var owners = await db.StockPostings.AsNoTracking().Where(p => ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var facts = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId.HasValue
                && historyIds.Contains(f.InventoryHistoryId.Value)).ToArrayAsync(ct);
        if (owners.Count != ownerIds.Length || facts.Length != histories.Length
            || facts.GroupBy(f => f.InventoryHistoryId).Any(g => g.Count() != 1)) return null;
        var originalFactIds = facts.Where(f => originals.Any(h => h.Id == f.InventoryHistoryId)).Select(f => f.Id).ToArray();
        var layers = await db.StockFifoLayers.AsNoTracking().Where(l => originalFactIds.Contains(l.OriginValuationFactId))
            .ToArrayAsync(ct);
        if (layers.Length != originals.Length) return null;
        foreach (var original in originals)
        {
            var owner = owners[original.StockPostingId!.Value];
            var origin = facts.SingleOrDefault(f => f.InventoryHistoryId == original.Id);
            var layer = origin is null ? null : layers.SingleOrDefault(l => l.OriginValuationFactId == origin.Id);
            if (owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
                || owner.PostingRole != "PRIMARY" || owner.SourceModule != "INVENTORY"
                || owner.SourceDocumentType != batch.TrxType
                || owner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || owner.SourceDocumentNo != batch.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || owner.DocumentRevision != original.DocumentRevision || owner.EffectiveAt != original.TrxDtTime
                || owner.LedgerEpochId != issue.LedgerEpochId || owner.PostingSequence <= 0
                || owner.PostingSequence > issueSequence && owner.PostingSequence <= reversalSequence
                || origin is null || layer is null || origin.CompanyCode != company || origin.BranchCode != branch
                || origin.StockPostingId != owner.Id || origin.LedgerEpochId != issue.LedgerEpochId
                || origin.ItemCode != issue.ICode || origin.CostMethod != StockCostMethods.Fifo
                || origin.Direction != 1 || origin.ValuationStatus != StockValuationStatuses.Valued
                || origin.OriginalValuationFactId is not null || origin.ReversesValuationFactId is not null
                || origin.EffectiveAt != original.TrxDtTime || origin.BaseQty != original.ToStdQty
                || origin.BaseUom != original.ToStdUom || origin.ToBalLocId != pileId
                || origin.CostAmount != origin.BaseCostAmount
                || layer.CompanyCode != company || layer.BranchCode != branch || layer.ItemCode != issue.ICode
                || layer.OriginStockPostingId != owner.Id || layer.OriginalQty != origin.BaseQty
                || layer.OriginalValue != origin.CostAmount || layer.AccumulatedAdjustment != 0m) return null;
            var reverse = reversals.SingleOrDefault(r => r.ReversesHistoryId == original.Id);
            if (reverse is null)
            {
                if (layer.RemainingQty != layer.OriginalQty || layer.RemainingValue != layer.OriginalValue)
                    return null;
                continue;
            }
            var reverseOwner = owners[reverse.StockPostingId!.Value];
            var reverseFact = facts.SingleOrDefault(f => f.InventoryHistoryId == reverse.Id);
            if (reverse.DocumentRevision != original.DocumentRevision || reverse.FromBalLocId != original.ToBalLocId
                || reverse.ToBalLocId != original.FromBalLocId || reverse.FrStdQty != original.ToStdQty
                || reverse.ToStdQty != original.FrStdQty || reverse.FrStdUom != original.ToStdUom
                || reverseOwner.CompanyCode != company || reverseOwner.BranchCode != branch || reverseOwner.SealedAtUtc is null
                || reverseOwner.PostingRole != "REVERSAL" || reverseOwner.ReversesPostingId != owner.Id
                || reverseOwner.SourceModule != owner.SourceModule || reverseOwner.SourceDocumentType != owner.SourceDocumentType
                || reverseOwner.SourceDocumentId != owner.SourceDocumentId || reverseOwner.SourceDocumentNo != owner.SourceDocumentNo
                || reverseOwner.DocumentRevision != owner.DocumentRevision || reverseOwner.EffectiveAt != reverse.TrxDtTime
                || reverseOwner.LedgerEpochId != owner.LedgerEpochId || reverseOwner.PostingSequence <= owner.PostingSequence
                || reverseFact is null || reverseFact.CompanyCode != company || reverseFact.BranchCode != branch
                || reverseFact.StockPostingId != reverseOwner.Id || reverseFact.LedgerEpochId != origin.LedgerEpochId
                || reverseFact.ItemCode != origin.ItemCode || reverseFact.CostMethod != StockCostMethods.Fifo
                || reverseFact.Direction != -1 || reverseFact.ValuationStatus != StockValuationStatuses.Reversed
                || reverseFact.OriginalValuationFactId != origin.Id || reverseFact.ReversesValuationFactId != origin.Id
                || reverseFact.BaseQty != origin.BaseQty || reverseFact.CostAmount != origin.CostAmount
                || reverseFact.BaseCostAmount != origin.BaseCostAmount || reverseFact.EffectiveAt != reverse.TrxDtTime
                || layer.Status != StockFifoLayerStatuses.Closed || layer.RemainingQty != 0m || layer.RemainingValue != 0m)
                return null;
        }
        var laterRoots = owners.Values.Where(p => p.PostingSequence > reversalSequence).Select(p => p.Id).Order().ToArray();
        var hash = StockPostingFingerprint.Create(new
        {
            histories = histories.OrderBy(h => h.Id).Select(h => new { h.Id, h.StockPostingId, h.EntryRole,
                h.ReversesHistoryId, h.DocumentRevision, h.TrxDtTime, h.FromBalLocId, h.ToBalLocId, h.FrStdQty,
                h.ToStdQty, h.FrStdUom, h.ToStdUom, h.PoNo, h.PoRelNo, h.PoLineNo }).ToArray(),
            owners = owners.Values.OrderBy(p => p.Id).Select(p => new { p.Id, p.PostingSequence, p.PostingRole,
                p.ReversesPostingId, p.SourceDocumentId, p.DocumentRevision, p.EffectiveAt, p.SealedAtUtc }).ToArray(),
            facts = facts.OrderBy(f => f.Id).Select(f => new { f.Id, f.StockPostingId, f.InventoryHistoryId,
                f.ReversesValuationFactId, f.Direction, f.BaseQty, f.CostAmount }).ToArray(),
            layers = layers.OrderBy(l => l.Id).Select(l => new { l.Id, l.OriginValuationFactId,
                l.OriginalQty, l.OriginalValue, l.RemainingQty, l.RemainingValue, l.Status, l.RowVersion }).ToArray()
        }, new { }).RequestFingerprint;
        return new(active.SingleOrDefault(), hash, laterRoots);
    }
}
