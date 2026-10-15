using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Resolves the only active leaf in each append-only count adjustment chain.</summary>
public static class StockCountGenerationResolver
{
    public static async Task<IReadOnlyDictionary<int, StockCountReplayGeneration>>
        ResolveActiveAsync(
            AppDbContext db,
            string companyCode,
            string branchCode,
            IReadOnlyCollection<int> countLineIds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(countLineIds);
        if (countLineIds.Count == 0)
            return new Dictionary<int, StockCountReplayGeneration>();

        var ids = countLineIds.Distinct().ToArray();
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => ids.Contains(l.Id)
                        && l.Header.CompanyCode == companyCode
                        && l.Header.BranchCode == branchCode)
            .Select(l => new
            {
                l.Id, l.StockCountId, l.BalLocId, l.ICode, l.PhysicalQty,
                l.Header.CountNo, l.Header.CountDate, l.Header.Status,
                l.Header.PostedBatchNo
            })
            .ToArrayAsync(cancellationToken);
        if (lines.Length != ids.Length)
            throw Corrupt("Count line identity does not match the requested tenant.");

        var rows = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.CompanyCode == companyCode && g.BranchCode == branchCode
                        && ids.Contains(g.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        var active = ResolveActive(rows);
        if (active.Count != ids.Length)
            throw Corrupt("A requested posted count line has no adjustment generation.");

        var postingIds = rows.SelectMany(x => new[] { x.AnchorPostingId, x.WritePostingId })
            .Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(x => postingIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var historyIds = rows.SelectMany(x => new[] { x.AdjustmentHistoryId, x.ReversalHistoryId })
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(x => historyIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var legacyBatchNos = lines.Where(x => x.PostedBatchNo is not null)
            .Select(x => x.PostedBatchNo!.Value).Distinct().ToArray();
        var legacyBatches = await db.IvTrxBatches.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                        && legacyBatchNos.Contains(x.BatchNo))
            .ToDictionaryAsync(x => x.BatchNo, cancellationToken);
        var byLine = lines.ToDictionary(x => x.Id);
        var byGeneration = rows.ToDictionary(x => x.Id);

        foreach (var line in lines)
        {
            var leaf = active[line.Id];
            if (line.PhysicalQty is null
                || line.Status == "POSTED" && leaf.IsRemoval
                || line.Status == "ROLLED_BACK" && !leaf.IsRemoval
                || line.Status is not ("POSTED" or "ROLLED_BACK"))
                throw Corrupt($"Count line {line.Id} status disagrees with its active generation.");
        }

        foreach (var row in rows)
        {
            var line = byLine[row.StockCountLineId];
            if (!postings.TryGetValue(row.AnchorPostingId, out var anchor)
                || !postings.TryGetValue(row.WritePostingId, out var writer)
                || anchor.CompanyCode != companyCode || anchor.BranchCode != branchCode
                || writer.CompanyCode != companyCode || writer.BranchCode != branchCode
                || anchor.SealedAtUtc is null || writer.SealedAtUtc is null
                || anchor.PostingSequence != row.AnchorPostingSequence
                || row.EffectiveAt != StockReplayEventKey.CountEndOfDay(line.CountDate))
                throw Corrupt($"Count generation {row.Id} has an invalid sealed posting identity.");

            var newAnchor = anchor.SourceModule == "INVENTORY"
                && anchor.SourceDocumentType == "IV_STOCK_COUNT"
                && anchor.SourceDocumentId == line.StockCountId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && anchor.SourceDocumentNo == line.CountNo
                && anchor.EffectiveAt == row.EffectiveAt;
            var legacyAnchor = legacyBatches.TryGetValue(line.PostedBatchNo ?? 0, out var legacyBatch)
                && anchor.SourceModule == "INVENTORY"
                && anchor.SourceDocumentType == "ADJ"
                && anchor.SourceDocumentId == legacyBatch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && anchor.BusinessDate.Date == line.CountDate.Date
                && legacyBatch.RefNo == line.CountNo;
            if (!newAnchor && !legacyAnchor)
                throw Corrupt($"Count generation {row.Id} is not owned by its count anchor.");

            if (row.AdjustmentHistoryId is int adjustmentId
                && (!histories.TryGetValue(adjustmentId, out var adjustment)
                    || adjustment.CompanyCode != companyCode || adjustment.BranchCode != branchCode
                    || adjustment.StockPostingId != row.WritePostingId
                    || adjustment.BatchNo != row.AdjustmentBatchNo
                    || adjustment.TrxDtTime != line.CountDate.Date
                    || adjustment.LedgerVersion != 2
                    || adjustment.TrxType != "ADJ"
                    || adjustment.BatchStatus != "POSTED"
                    || adjustment.ICode != line.ICode
                    || adjustment.EntryRole != "ORIGINAL"
                    || adjustment.ReversesHistoryId is not null
                    || row.SignedAdjustmentQty > 0m
                        && (adjustment.ToBalLocId != line.BalLocId
                            || adjustment.FromBalLocId is not null
                            || adjustment.ToStdQty != row.SignedAdjustmentQty
                            || adjustment.FrStdQty is not null)
                    || row.SignedAdjustmentQty < 0m
                        && (adjustment.FromBalLocId != line.BalLocId
                            || adjustment.ToBalLocId is not null
                            || adjustment.FrStdQty != -row.SignedAdjustmentQty
                            || adjustment.ToStdQty is not null)))
                throw Corrupt($"Count generation {row.Id} adjustment history disagrees with its counted pile.");

            if (row.ReversalHistoryId is int reversalId
                && (!histories.TryGetValue(reversalId, out var reversal)
                    || reversal.CompanyCode != companyCode || reversal.BranchCode != branchCode
                    || reversal.StockPostingId != row.WritePostingId
                    || reversal.BatchNo != row.ReversalBatchNo
                    || reversal.TrxDtTime != line.CountDate.Date
                    || reversal.LedgerVersion != 2
                    || reversal.TrxType != "ADJ"
                    || reversal.BatchStatus != "POSTED"
                    || reversal.ICode != line.ICode
                    || reversal.EntryRole != "REVERSAL"
                    || row.PreviousGenerationId is not long parentId
                    || reversal.ReversesHistoryId != byGeneration[parentId].AdjustmentHistoryId
                    || !histories.TryGetValue(reversal.ReversesHistoryId!.Value, out var reversed)
                    || reversal.FromBalLocId != reversed.ToBalLocId
                    || reversal.ToBalLocId != reversed.FromBalLocId
                    || reversal.FrStdQty != reversed.ToStdQty
                    || reversal.ToStdQty != reversed.FrStdQty
                    || reversal.StockCountCostBasisId != reversed.StockCountCostBasisId))
                throw Corrupt($"Count generation {row.Id} reversal history is incomplete.");
        }

        return active;
    }

    public static IReadOnlyDictionary<int, StockCountReplayGeneration> ResolveActive(
        IReadOnlyCollection<StockCountReplayGeneration> generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        var result = new Dictionary<int, StockCountReplayGeneration>();
        foreach (var group in generations.GroupBy(g => g.StockCountLineId))
        {
            var rows = group.ToArray();
            var ids = rows.Select(g => g.Id).ToHashSet();
            if (group.Key <= 0 || ids.Count != rows.Length || ids.Contains(0)
                || rows.Count(g => g.PreviousGenerationId is null) != 1)
                throw Corrupt($"Count line {group.Key} has duplicate or ambiguous generation roots.");
            var byId = rows.ToDictionary(g => g.Id);
            var root = rows.Single(g => g.PreviousGenerationId is null);
            var adjustmentIds = new HashSet<int>();
            var reversalIds = new HashSet<int>();

            foreach (var row in rows)
            {
                if (row.AnchorPostingId <= 0 || row.WritePostingId <= 0
                    || row.AnchorPostingSequence <= 0
                    || row.CompanyCode != rows[0].CompanyCode
                    || row.BranchCode != rows[0].BranchCode
                    || row.EffectiveAt != root.EffectiveAt
                    || row.EffectiveAt != StockReplayEventKey.CountEndOfDay(row.EffectiveAt)
                    || IvQty.Round(row.SignedAdjustmentQty) != row.SignedAdjustmentQty
                    || row.IsRemoval && (row.SignedAdjustmentQty != 0m || row.AdjustmentHistoryId is not null)
                    || (row.SignedAdjustmentQty == 0m) != (row.AdjustmentHistoryId is null)
                    || (row.AdjustmentBatchNo is null) != (row.AdjustmentHistoryId is null)
                    || (row.ReversalBatchNo is null) != (row.ReversalHistoryId is null)
                    || row.PreviousGenerationId is long priorId && !ids.Contains(priorId))
                    throw Corrupt($"Count line {group.Key} has invalid generation {row.Id}.");
                if (row.AdjustmentHistoryId is int adjustmentId && !adjustmentIds.Add(adjustmentId)
                    || row.ReversalHistoryId is int reversalId && !reversalIds.Add(reversalId))
                    throw Corrupt($"Count line {group.Key} reuses a history identity.");
                if (row.PreviousGenerationId is long parent
                    && ((byId[parent].AdjustmentHistoryId is not null)
                        != (row.ReversalHistoryId is not null)))
                    throw Corrupt($"Count line {group.Key} has an incomplete reversal at generation {row.Id}.");
                if (row.PreviousGenerationId is long parentGenerationId)
                {
                    var previous = byId[parentGenerationId];
                    var validAnchor = previous.IsRemoval
                        ? row.AnchorPostingId != previous.AnchorPostingId
                            && row.AnchorPostingSequence > previous.AnchorPostingSequence
                            && !row.IsRemoval
                        : row.AnchorPostingId == previous.AnchorPostingId
                            && row.AnchorPostingSequence == previous.AnchorPostingSequence;
                    if (!validAnchor)
                        throw Corrupt($"Count line {group.Key} changes its anchor outside a repost at generation {row.Id}.");
                }
                if (row.PreviousGenerationId is null && (row.ReversalHistoryId is not null || row.IsRemoval))
                    throw Corrupt($"Count line {group.Key} has a reversal on its initial generation.");
            }

            var children = rows.Where(g => g.PreviousGenerationId is not null)
                .GroupBy(g => g.PreviousGenerationId!.Value).ToArray();
            if (children.Any(g => g.Count() != 1))
                throw Corrupt($"Count line {group.Key} has a forked generation chain.");

            var parentIds = children.Select(g => g.Key).ToHashSet();
            var leaves = rows.Where(g => !parentIds.Contains(g.Id)).ToArray();
            if (leaves.Length != 1)
                throw Corrupt($"Count line {group.Key} has no unique active generation.");

            var leaf = leaves[0];
            var visited = new HashSet<long>();
            for (var cursor = leaf; ;)
            {
                if (!visited.Add(cursor.Id))
                    throw Corrupt($"Count line {group.Key} has a generation cycle.");
                if (cursor.PreviousGenerationId is not long parentId)
                    break;
                cursor = byId[parentId];
            }
            if (visited.Count != rows.Length)
                throw Corrupt($"Count line {group.Key} has disconnected generations.");

            result.Add(group.Key, leaf);
        }
        return result;
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
