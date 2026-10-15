using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Reads a pile's sealed, active physical tail. Ordinary histories and count
/// generations are separate sets: a count history's stored midnight timestamp
/// is never used as the count's logical position.
/// </summary>
public static class StockReplayPhysicalTimelineReader
{
    public static async Task<IReadOnlyList<StockReplayPhysicalEvent>> ReadLaterAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        StockReplayEventKey after,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode)
            || balLocId <= 0)
            throw new ArgumentException("A tenant and a positive pile ID are required.");

        // Read all candidate history legs, including reversals and count-owned ADJ.
        // The latter are removed by generation linkage before ordinary events are built.
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == companyCode && h.BranchCode == branchCode
                // Count-owned ADJ history remains at midnight. Include the whole
                // candidate day, then compare canonical logical keys below.
                && h.TrxDtTime >= after.EffectiveAt.Date
                && (h.FromBalLocId == balLocId || h.ToBalLocId == balLocId))
            .ToArrayAsync(cancellationToken);
        var historyIds = histories.Select(h => h.Id).ToArray();
        var countOwnedIds = historyIds.Length == 0
            ? new HashSet<int>()
            : (await db.StockCountReplayGenerations.AsNoTracking()
                    .Where(g => g.CompanyCode == companyCode && g.BranchCode == branchCode
                        && (g.AdjustmentHistoryId.HasValue
                            && historyIds.Contains(g.AdjustmentHistoryId.Value)
                            || g.ReversalHistoryId.HasValue
                            && historyIds.Contains(g.ReversalHistoryId.Value)))
                    .Select(g => new { g.AdjustmentHistoryId, g.ReversalHistoryId })
                    .ToArrayAsync(cancellationToken))
                .SelectMany(g => new[] { g.AdjustmentHistoryId, g.ReversalHistoryId })
                .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();

        var ordinary = histories.Where(h => !countOwnedIds.Contains(h.Id)).ToArray();
        if (ordinary.Any(h => h.LedgerVersion != 2
            || h.EntryRole is not ("ORIGINAL" or "REVERSAL")
            || h.StockPostingId is null))
            throw Corrupt("The physical tail contains an unproven legacy or malformed history.");

        var originalIds = ordinary.Where(h => h.EntryRole == "ORIGINAL")
            .Select(h => h.Id).ToArray();
        var reversing = originalIds.Length == 0
            ? []
            : await db.IvTrxHistories.AsNoTracking()
                .Where(h => h.CompanyCode == companyCode && h.BranchCode == branchCode
                    && h.ReversesHistoryId.HasValue
                    && originalIds.Contains(h.ReversesHistoryId.Value))
                .ToArrayAsync(cancellationToken);
        if (reversing.Any(h => h.EntryRole != "REVERSAL" || h.StockPostingId is null)
            || reversing.GroupBy(h => h.ReversesHistoryId).Any(g => g.Count() != 1))
            throw Corrupt("An ordinary history has invalid reversal lineage.");
        var originalsById = ordinary.Where(h => h.EntryRole == "ORIGINAL")
            .ToDictionary(h => h.Id);
        foreach (var reversal in reversing)
        {
            var original = originalsById[reversal.ReversesHistoryId!.Value];
            if (countOwnedIds.Contains(reversal.Id)
                || reversal.LedgerVersion != 2 || reversal.BatchStatus != "POSTED"
                || reversal.ICode != original.ICode
                || reversal.FromBalLocId != original.ToBalLocId
                || reversal.ToBalLocId != original.FromBalLocId
                || reversal.FrStdQty != original.ToStdQty
                || reversal.ToStdQty != original.FrStdQty)
                throw Corrupt($"Inventory history {original.Id} has a non-inverse reversal.");
        }
        var reversedIds = reversing.Select(h => h.ReversesHistoryId!.Value).ToHashSet();
        var activeOrdinary = ordinary.Where(h => h.EntryRole == "ORIGINAL"
            && !reversedIds.Contains(h.Id)).ToArray();

        var activeIds = activeOrdinary.Select(h => (long)h.Id).ToArray();
        var effects = activeIds.Length == 0
            ? []
            : await db.StockReplayEffects.AsNoTracking()
                .Where(e => e.CompanyCode == companyCode && e.BranchCode == branchCode
                    && e.EffectKind == StockReplayEffectTypes.PhysicalReplacement
                    && e.AffectedEntityType == StockReplayEffectTypes.InventoryHistory
                    && activeIds.Contains(e.AffectedEntityId))
                .ToArrayAsync(cancellationToken);
        if (effects.GroupBy(e => e.AffectedEntityId).Any(g => g.Count() != 1))
            throw Corrupt("An Inventory correction has ambiguous business-owner effects.");
        var effectByHistory = effects.ToDictionary(e => checked((int)e.AffectedEntityId));
        if (effects.Any(e => e.OriginalEntityId is not long id
                || id <= 0 || id > int.MaxValue))
            throw Corrupt("An Inventory correction has no valid original history identity.");
        var originalEffectIds = effects.Select(e => (int)e.OriginalEntityId!.Value)
            .Distinct().ToArray();
        var originals = originalEffectIds.Length == 0
            ? new Dictionary<int, IvTrxHistory>()
            : await db.IvTrxHistories.AsNoTracking()
                .Where(h => originalEffectIds.Contains(h.Id))
                .ToDictionaryAsync(h => h.Id, cancellationToken);
        var runIds = effects.Select(e => e.ReplayRunId).Distinct().ToArray();
        var runs = runIds.Length == 0
            ? []
            : await db.StockReplayRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id))
                .ToArrayAsync(cancellationToken);
        var runsById = runs.ToDictionary(r => r.Id);

        // Include a count on the candidate date even if its stored ADJ history is
        // at midnight. The key comparison below handles the final-tick tie.
        var countDateBound = after.EffectiveAt.Date;
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.BalLocId == balLocId
                && l.Header.CompanyCode == companyCode
                && l.Header.BranchCode == branchCode
                && l.Header.CountDate >= countDateBound
                && (l.Header.Status == "POSTED" || l.Header.Status == "ROLLED_BACK"))
            .Select(l => new { l.Id, l.BalLocId, l.PhysicalQty })
            .ToArrayAsync(cancellationToken);
        IReadOnlyDictionary<int, StockCountReplayGeneration> activeCounts = lines.Length == 0
            ? new Dictionary<int, StockCountReplayGeneration>()
            : await StockCountGenerationResolver.ResolveActiveAsync(
                db, companyCode, branchCode, lines.Select(l => l.Id).ToArray(), cancellationToken);

        var postingIds = activeOrdinary.Select(h => h.StockPostingId!.Value)
            .Concat(reversing.Select(h => h.StockPostingId!.Value))
            .Concat(effects.Select(e => e.BusinessOwnerPostingId))
            .Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (reversing.Any(h => !postings.TryGetValue(h.StockPostingId!.Value, out var posting)
            || posting.CompanyCode != companyCode || posting.BranchCode != branchCode
            || posting.SealedAtUtc is null))
            throw Corrupt("An ordinary history reversal lacks a sealed posting.");

        var events = new List<StockReplayPhysicalEvent>(activeOrdinary.Length + lines.Length);
        foreach (var history in activeOrdinary)
        {
            if (history.BatchStatus != "POSTED")
                throw Corrupt($"Inventory history {history.Id} is not posted.");
            if (!postings.TryGetValue(history.StockPostingId!.Value, out var posting)
                || posting.CompanyCode != companyCode || posting.BranchCode != branchCode
                || posting.SealedAtUtc is null || posting.PostingSequence <= 0)
                throw Corrupt($"Inventory history {history.Id} lacks a sealed chronological owner.");
            var owner = posting;
            var lineIdentityId = history.Id;
            if (effectByHistory.TryGetValue(history.Id, out var effect))
            {
                if (effect.RootStockPostingId != posting.Id
                    || !runsById.TryGetValue(effect.ReplayRunId, out var run)
                    || run.CompanyCode != companyCode || run.BranchCode != branchCode
                    || run.RootStockPostingId != posting.Id
                    || run.RootRequestId != posting.RequestId
                    || effect.OriginalEntityId is not long originalId
                    || originalId <= 0 || originalId == history.Id
                    || !originals.TryGetValue((int)originalId, out var original)
                    || original.CompanyCode != companyCode || original.BranchCode != branchCode
                    || original.EntryRole != "ORIGINAL" || original.ICode != history.ICode
                    || original.FromBalLocId != balLocId && original.ToBalLocId != balLocId
                    || !postings.TryGetValue(effect.BusinessOwnerPostingId, out owner)
                    || owner.CompanyCode != companyCode || owner.BranchCode != branchCode
                    || owner.SealedAtUtc is null || owner.PostingSequence <= 0
                    || original.StockPostingId != owner.Id
                    || original.TrxDtTime != owner.EffectiveAt
                    || history.TrxDtTime != owner.EffectiveAt)
                    throw Corrupt($"Inventory correction {history.Id} has invalid business-owner lineage.");
                lineIdentityId = original.Id;
            }
            else if (posting.EffectiveAt != history.TrxDtTime)
                throw Corrupt($"Inventory history {history.Id} has a different effective time than its owner.");
            var delta = IvQty.Round((history.ToBalLocId == balLocId ? history.ToStdQty ?? 0m : 0m)
                - (history.FromBalLocId == balLocId ? history.FrStdQty ?? 0m : 0m));
            if (delta == 0m || history.ToBalLocId == balLocId && history.ToStdQty is not > 0m
                || history.FromBalLocId == balLocId && history.FrStdQty is not > 0m)
                throw Corrupt($"Inventory history {history.Id} has no valid physical leg for pile {balLocId}.");
            var key = new StockReplayEventKey(owner.EffectiveAt,
                StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
                $"H{lineIdentityId:D10}", 0);
            if (key.CompareTo(after) > 0)
                events.Add(new StockReplayPhysicalEvent(history.Id, key, delta));
        }

        foreach (var line in lines)
        {
            var generation = activeCounts[line.Id];
            if (generation.IsRemoval)
                continue;
            var key = new StockReplayEventKey(generation.EffectiveAt,
                StockReplayEventKey.CountClass, generation.AnchorPostingSequence,
                $"C{line.Id:D10}", 0);
            if (key.CompareTo(after) > 0)
                events.Add(new StockReplayPhysicalEvent(-generation.Id, key,
                    generation.SignedAdjustmentQty, line.PhysicalQty));
        }

        return events.OrderBy(e => e.Key).ThenBy(e => e.EventId).ToArray();
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
