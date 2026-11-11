using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Reads an inclusive physical as-of quantity from the active V2 timeline.
/// A count-owned ADJ is positioned by its generation's end-of-day key, not
/// by the legacy history timestamp stored at midnight.
/// </summary>
public static class StockReplayPhysicalAsOfReader
{
    public static Task<IReadOnlyDictionary<int, InventoryAsOfStock>> ReadAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<int> balanceIds,
        DateTime asOf,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(db, companyCode, branchCode, balanceIds, asOf,
            excludePendingCountId: null, cutoffOverride: null, cancellationToken);

    internal static Task<IReadOnlyDictionary<int, InventoryAsOfStock>> ReadAtKeyAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyCollection<int> balanceIds, StockReplayEventKey cutoff,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(db, companyCode, branchCode, balanceIds, cutoff.EffectiveAt,
            excludePendingCountId: null, cutoffOverride: cutoff, cancellationToken);

    internal static async Task<IReadOnlyDictionary<int, InventoryAsOfStock>> ReadBeforePendingCountAsync(
        StockPostingContext context, int batchNo, IReadOnlyCollection<int> balanceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        var db = context.Db;
        if (db.Database.CurrentTransaction is null || batchNo <= 0
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.PostingRole != "PRIMARY"
            || !int.TryParse(context.Posting.SourceDocumentId,
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var countId) || countId <= 0)
            throw Invalid("The pending count has no valid posting authority.");
        var header = await db.IvStockCountHdrs.AsNoTracking().SingleOrDefaultAsync(h =>
            h.Id == countId && h.CompanyCode == context.CompanyCode && h.BranchCode == context.BranchCode,
            cancellationToken);
        if (header is null || header.Status != "POSTED" || header.PostedBatchNo != batchNo
            || header.CountNo != context.Posting.SourceDocumentNo
            || context.Posting.EffectiveAt != StockReplayEventKey.CountEndOfDay(header.CountDate))
            throw Invalid("The pending count does not match its posting anchor.");
        var lineIds = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countId).Select(l => l.Id).ToArrayAsync(cancellationToken);
        var priorRows = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => lineIds.Contains(g.StockCountLineId)).ToArrayAsync(cancellationToken);
        if (priorRows.Length > 0)
        {
            var prior = StockCountGenerationResolver.ResolveActive(priorRows);
            var ownerIds = priorRows.SelectMany(g => new[] { g.AnchorPostingId, g.WritePostingId })
                .Distinct().ToArray();
            var owners = await db.StockPostings.AsNoTracking().Where(p => ownerIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, cancellationToken);
            if (prior.Values.Any(g => !g.IsRemoval)
                || priorRows.Any(g => g.CompanyCode != context.CompanyCode
                    || g.BranchCode != context.BranchCode
                    || g.EffectiveAt != context.Posting.EffectiveAt
                    || !owners.TryGetValue(g.AnchorPostingId, out var anchor)
                    || !owners.TryGetValue(g.WritePostingId, out var writer)
                    || anchor.CompanyCode != context.CompanyCode || anchor.BranchCode != context.BranchCode
                    || writer.CompanyCode != context.CompanyCode || writer.BranchCode != context.BranchCode
                    || anchor.SealedAtUtc is null || writer.SealedAtUtc is null
                    || anchor.LedgerEpochId != context.Epoch.Id || writer.LedgerEpochId != context.Epoch.Id
                    || anchor.PostingSequence != g.AnchorPostingSequence))
                throw Invalid("The pending count already has an active generation.");
        }
        return await ReadCoreAsync(db, context.CompanyCode, context.BranchCode,
            balanceIds, context.Posting.EffectiveAt, countId, cutoffOverride: null, cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<int, InventoryAsOfStock>> ReadCoreAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyCollection<int> balanceIds, DateTime asOf, int? excludePendingCountId,
        StockReplayEventKey? cutoffOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(balanceIds);
        if (string.IsNullOrWhiteSpace(companyCode)
            || string.IsNullOrWhiteSpace(branchCode))
            throw new ArgumentException("An Inventory tenant is required.");
        if (balanceIds.Count == 0)
            return new Dictionary<int, InventoryAsOfStock>();
        var ids = balanceIds.Distinct().ToArray();
        if (ids.Any(id => id <= 0))
            throw Invalid("An as-of pile ID is not positive.");
        var epochCount = await db.StockLedgerEpochs.AsNoTracking()
            .CountAsync(e => e.CompanyCode == companyCode
                && e.BranchCode == branchCode
                && e.Status == StockLedgerEpochStatuses.Active
                && e.Version == 2 && e.EffectiveFrom <= asOf,
                cancellationToken);
        if (epochCount != 1)
            throw Invalid("Strict as-of stock requires one active V2 epoch at the requested time.");

        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && ids.Contains(b.Id))
            .Select(b => new { b.Id, b.StdQty })
            .ToArrayAsync(cancellationToken);
        if (balances.Length != ids.Length)
            throw Invalid("An as-of pile does not belong to the requested tenant.");

        // Normal as-of observations include every event at the instant. The
        // rollback check can instead observe one exact active event key.
        var cutoff = cutoffOverride ?? new StockReplayEventKey(asOf,
            StockReplayEventKey.CountClass, long.MaxValue, "\uffff", int.MaxValue);
        // The active replay tail intentionally removes an original/reversal
        // pair. For an observation between their business dates, however, the
        // later reversal must be rewound while the earlier original remains.
        var crossing = await ReadCrossingReversalsAsync(db, companyCode,
            branchCode, ids, cutoff, cancellationToken);
        var result = new Dictionary<int, InventoryAsOfStock>(ids.Length);
        foreach (var balance in balances)
        {
            if (balance.StdQty < 0m || IvQty.Round(balance.StdQty) != balance.StdQty)
                throw Invalid($"Pile {balance.Id} has invalid live quantity.");
            var activeLater = await StockReplayPhysicalTimelineReader.ReadLaterWithoutCrossingAsync(
                db, companyCode, branchCode, balance.Id, cutoff,
                cancellationToken, excludePendingCountId);
            var later = activeLater.Concat(crossing.GetValueOrDefault(balance.Id) ?? [])
                .OrderBy(e => e.Key).ThenBy(e => e.EventId).ToArray();
            if (later.Select(e => e.EventId).Distinct().Count() != later.Length
                || later.Zip(later.Skip(1), (a, b) => a.Key == b.Key).Any(equal => equal))
                throw Invalid($"Pile {balance.Id} has duplicate as-of movement evidence.");
            var quantity = balance.StdQty;
            for (var i = later.Length - 1; i >= 0; i--)
            {
                quantity = IvQty.Round(quantity - later[i].SignedDelta);
                if (quantity < 0m)
                    throw Invalid($"Pile {balance.Id} has a negative physical slice before event {later[i].EventId}.");
            }
            result.Add(balance.Id, new InventoryAsOfStock(balance.Id,
                balance.StdQty, quantity, Math.Min(balance.StdQty, quantity)));
        }
        return result;
    }

    internal static async Task<Dictionary<int, List<StockReplayPhysicalEvent>>> ReadCrossingReversalsAsync(
        AppDbContext db, string company, string branch, int[] pileIds,
        StockReplayEventKey after, CancellationToken ct)
    {
        var result = new Dictionary<int, List<StockReplayPhysicalEvent>>();
        var candidates = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.CompanyCode == company && h.BranchCode == branch && h.TrxDtTime >= after.EffectiveAt
            && (h.EntryRole == "REVERSAL" || h.ReversesHistoryId != null)
            && (h.FromBalLocId.HasValue && pileIds.Contains(h.FromBalLocId.Value)
                || h.ToBalLocId.HasValue && pileIds.Contains(h.ToBalLocId.Value)))
            .ToArrayAsync(ct);
        if (candidates.Length == 0)
            return result;

        var candidateIds = candidates.Select(h => h.Id).ToArray();
        var originalIds = candidates.Where(h => h.ReversesHistoryId.HasValue)
            .Select(h => h.ReversesHistoryId!.Value).Distinct().ToArray();
        var allIds = candidateIds.Concat(originalIds).Distinct().ToArray();
        var countLinks = await db.StockCountReplayGenerations.AsNoTracking().Where(g =>
            g.AdjustmentHistoryId.HasValue && allIds.Contains(g.AdjustmentHistoryId.Value)
            || g.ReversalHistoryId.HasValue && allIds.Contains(g.ReversalHistoryId.Value))
            .Select(g => new { g.CompanyCode, g.BranchCode, g.AdjustmentHistoryId, g.ReversalHistoryId })
            .ToArrayAsync(ct);
        if (countLinks.Any(g => g.CompanyCode != company || g.BranchCode != branch))
            throw Invalid("An as-of count movement has a foreign generation owner.");
        var countReversals = countLinks.Where(g => g.ReversalHistoryId.HasValue)
            .Select(g => g.ReversalHistoryId!.Value).ToHashSet();
        var countAdjustments = countLinks.Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).ToHashSet();
        if (candidates.Any(h => countAdjustments.Contains(h.Id)))
            throw Invalid("An as-of count adjustment has an invalid reversal role.");
        var ordinary = candidates.Where(h => !countReversals.Contains(h.Id)).ToArray();
        if (ordinary.Any(h => h.ReversesHistoryId is not > 0))
            throw Invalid("An as-of reversal has no original movement.");
        originalIds = ordinary.Select(h => h.ReversesHistoryId!.Value).Distinct().ToArray();
        if (originalIds.Length == 0)
            return result;
        var originals = await db.IvTrxHistories.AsNoTracking()
            .Where(h => originalIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        if (originals.Count != originalIds.Length)
            throw Invalid("An as-of reversal has a missing original movement.");
        var crossing = ordinary.Where(r => originals[r.ReversesHistoryId!.Value].TrxDtTime <= after.EffectiveAt).ToArray();
        if (crossing.Length == 0)
            return result;
        if (crossing.Any(r => countAdjustments.Contains(r.ReversesHistoryId!.Value)))
            throw Invalid("A count adjustment has an unlinked reversal in the as-of timeline.");

        var crossingOriginalIds = crossing.Select(h => h.ReversesHistoryId!.Value).Distinct().ToArray();
        var allReversals = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.ReversesHistoryId.HasValue && crossingOriginalIds.Contains(h.ReversesHistoryId.Value))
            .ToArrayAsync(ct);
        if (crossing.Any(r => allReversals.Count(other => other.ReversesHistoryId == r.ReversesHistoryId) != 1))
            throw Invalid("An as-of movement has ambiguous reversal lineage.");
        var ownerIds = crossing.Select(h => h.StockPostingId)
            .Concat(crossing.Select(h => originals[h.ReversesHistoryId!.Value].StockPostingId))
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking().Where(p => ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        foreach (var reversal in crossing)
        {
            var original = originals[reversal.ReversesHistoryId!.Value];
            if (original.StockPostingId is not long originalOwnerId
                || reversal.StockPostingId is not long reversalOwnerId
                || !owners.TryGetValue(originalOwnerId, out var owner)
                || !owners.TryGetValue(reversalOwnerId, out var reverseOwner)
                || !ExactBusinessReversal(original, reversal, owner, reverseOwner, company, branch))
                throw Invalid($"Inventory history {original.Id} has no exact sealed as-of reversal.");

            var key = new StockReplayEventKey(reverseOwner.EffectiveAt,
                StockReplayEventKey.OrdinaryClass, reverseOwner.PostingSequence,
                $"H{reversal.Id:D10}", 0);
            var originalKey = new StockReplayEventKey(owner.EffectiveAt,
                StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
                $"H{original.Id:D10}", 0);
            if (originalKey.CompareTo(after) > 0 || key.CompareTo(after) <= 0)
                continue;
            foreach (var pileId in new[] { reversal.FromBalLocId, reversal.ToBalLocId }
                .Where(id => id.HasValue && pileIds.Contains(id.Value)).Select(id => id!.Value).Distinct())
            {
                var delta = IvQty.Round((reversal.ToBalLocId == pileId ? reversal.ToStdQty ?? 0m : 0m)
                    - (reversal.FromBalLocId == pileId ? reversal.FrStdQty ?? 0m : 0m));
                if (delta == 0m)
                    throw Invalid($"Inventory reversal {reversal.Id} has no physical as-of leg.");
                if (!result.TryGetValue(pileId, out var events))
                    result.Add(pileId, events = []);
                events.Add(new StockReplayPhysicalEvent(reversal.Id, key, delta));
            }
        }
        return result;
    }

    private static bool ExactBusinessReversal(
        IvTrxHistory original, IvTrxHistory reversal, StockPosting owner,
        StockPosting reverseOwner, string company, string branch) =>
        original.CompanyCode == company && original.BranchCode == branch
        && reversal.CompanyCode == company && reversal.BranchCode == branch
        && original.LedgerVersion == 2 && reversal.LedgerVersion == 2
        && original.EntryRole == "ORIGINAL" && original.ReversesHistoryId is null
        && reversal.EntryRole == "REVERSAL" && reversal.ReversesHistoryId == original.Id
        && original.BatchStatus == IvBatchStatuses.Posted && reversal.BatchStatus == IvBatchStatuses.Posted
        && original.TrxType == reversal.TrxType && original.BatchNo == reversal.BatchNo
        && original.TrxLineNo == reversal.TrxLineNo && original.DocumentRevision == reversal.DocumentRevision
        && original.ICode == reversal.ICode && original.IStatus == reversal.IStatus
        && original.LedgerEpochId is > 0 && original.LedgerEpochId == reversal.LedgerEpochId
        && original.PostingLineNo is > 0 && reversal.PostingLineNo is > 0
        && original.FromBalLocId == reversal.ToBalLocId && original.ToBalLocId == reversal.FromBalLocId
        && original.FromLotId == reversal.ToLotId && original.ToLotId == reversal.FromLotId
        && original.FrStdQty == reversal.ToStdQty && original.ToStdQty == reversal.FrStdQty
        && original.FrStdUom == reversal.ToStdUom && original.ToStdUom == reversal.FrStdUom
        && original.FrWarehouse == reversal.ToWarehouse && original.ToWarehouse == reversal.FrWarehouse
        && original.FrLocation == reversal.ToLocation && original.ToLocation == reversal.FrLocation
        && original.FrLotNo == reversal.ToLotNo && original.ToLotNo == reversal.FrLotNo
        && owner.CompanyCode == company && owner.BranchCode == branch && owner.SealedAtUtc is not null
        && owner.Id == original.StockPostingId && owner.LedgerEpochId == original.LedgerEpochId
        && owner.PostingRole == "PRIMARY" && owner.PostingSequence > 0
        && owner.DocumentRevision == original.DocumentRevision && owner.EffectiveAt == original.TrxDtTime
        && reverseOwner.CompanyCode == company && reverseOwner.BranchCode == branch
        && reverseOwner.SealedAtUtc is not null && reverseOwner.Id == reversal.StockPostingId
        && reverseOwner.LedgerEpochId == owner.LedgerEpochId && reverseOwner.PostingRole == "REVERSAL"
        && reverseOwner.ReversesPostingId == owner.Id && reverseOwner.PostingSequence > owner.PostingSequence
        && reverseOwner.SourceModule == owner.SourceModule && reverseOwner.SourceDocumentType == owner.SourceDocumentType
        && reverseOwner.SourceDocumentId == owner.SourceDocumentId && reverseOwner.SourceDocumentNo == owner.SourceDocumentNo
        && reverseOwner.DocumentRevision == owner.DocumentRevision && reverseOwner.EffectiveAt == reversal.TrxDtTime;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
