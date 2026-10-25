using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Read-only decision at the Inventory posting seam. A later pile date is a
/// conservative dependency signal, never proof of historical availability.
/// The method-specific replay planner still proves quantity and cost.
/// </summary>
internal static class StockReplayInventoryRouteSelector
{
    internal sealed record Decision(
        bool HasLaterPhysical, bool HasLaterValuation, bool HasLaterPileDate)
    {
        internal bool RequiresReplay =>
            HasLaterPhysical || HasLaterValuation || HasLaterPileDate;
    }

    internal static async Task<Decision> ReadAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, string expectedTrxType,
        CancellationToken cancellationToken)
    {
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo && b.TrxType == expectedTrxType
            && b.BatchStatus == IvBatchStatuses.New, cancellationToken);
        if (batch is null)
            return new(false, false, false);

        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id)
            .ToArrayAsync(cancellationToken);
        var pileIds = details.SelectMany(d => new[] { d.FromBalLocId, d.ToBalLocId })
            .Where(id => id is > 0).Select(id => id!.Value)
            .ToHashSet();
        foreach (var detail in details.Where(d => d.ToBalLocId is null
            && d.ToStdQty is > 0m && !string.IsNullOrWhiteSpace(d.ToWarehouse)))
        {
            var existing = await StockReplayInventoryReceiptPileLocator.FindUniqueAsync(
                db, companyCode, branchCode, detail, cancellationToken);
            if (existing is > 0)
                pileIds.Add(existing.Value);
        }
        var itemCodes = details.Select(d => d.ICode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!).Distinct().ToArray();
        var laterValuation = itemCodes.Length > 0
            && await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == companyCode && f.BranchCode == branchCode
                && itemCodes.Contains(f.ItemCode)
                && f.EffectiveAt > batch.TrxDtTime
                && f.StockPosting!.SealedAtUtc != null
                && f.ReversesValuationFactId == null
                && !db.StockValuationFacts.Any(r =>
                    r.ReversesValuationFactId == f.Id), cancellationToken);

        var laterPileDate = false;
        var laterPhysical = false;
        var after = new StockReplayEventKey(batch.TrxDtTime,
            StockReplayEventKey.OrdinaryClass, long.MaxValue,
            "H9999999999", 0);
        foreach (var pileId in pileIds)
        {
            var balance = await db.IvBalLocs.AsNoTracking()
                .SingleOrDefaultAsync(b => b.Id == pileId
                    && b.CompanyCode == companyCode
                    && b.BranchCode == branchCode, cancellationToken);
            if (balance is null)
                continue;
            laterPileDate |= balance.TransDate?.Date > batch.TrxDtTime.Date;
            laterPhysical |= (await StockReplayPhysicalTimelineReader.ReadLaterAsync(
                db, companyCode, branchCode, pileId, after,
                cancellationToken)).Count > 0;
        }
        return new(laterPhysical, laterValuation, laterPileDate);
    }

    internal static async Task<Decision> ReadRollbackAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, string expectedTrxType,
        CancellationToken cancellationToken)
    {
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo && b.TrxType == expectedTrxType
            && b.BatchStatus == IvBatchStatuses.Posted, cancellationToken);
        if (batch is null)
            return new(false, false, false);

        var originals = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == companyCode && h.BranchCode == branchCode
                && h.BatchNo == batchNo && h.EntryRole == "ORIGINAL"
                && h.LedgerVersion == 2 && h.StockPostingId != null
                && !db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id))
            .ToArrayAsync(cancellationToken);
        if (originals.Length == 0)
            return new(false, false, false);
        var ownerIds = originals.Select(h => h.StockPostingId!.Value)
            .Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id) && p.CompanyCode == companyCode
                && p.BranchCode == branchCode && p.SealedAtUtc != null)
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (owners.Count != ownerIds.Length)
            return new(true, false, false);

        var lastKeyByPile = new Dictionary<int, StockReplayEventKey>();
        foreach (var history in originals)
        {
            var owner = owners[history.StockPostingId!.Value];
            var key = new StockReplayEventKey(owner.EffectiveAt,
                StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
                $"H{history.Id:D10}", 0);
            foreach (var pileId in new[] { history.FromBalLocId, history.ToBalLocId })
            {
                if (pileId is not > 0)
                    continue;
                if (!lastKeyByPile.TryGetValue(pileId.Value, out var previous)
                    || key.CompareTo(previous) > 0)
                    lastKeyByPile[pileId.Value] = key;
            }
        }
        foreach (var (pileId, lastKey) in lastKeyByPile)
        {
            if ((await StockReplayPhysicalTimelineReader.ReadLaterAsync(db,
                    companyCode, branchCode, pileId, lastKey,
                    cancellationToken)).Count > 0)
                return new(true, false, false);
        }

        foreach (var group in originals.Where(h => !string.IsNullOrWhiteSpace(h.ICode))
                     .GroupBy(h => h.ICode!))
        {
            var firstOwner = group.Select(h => owners[h.StockPostingId!.Value])
                .OrderBy(p => p.EffectiveAt).ThenBy(p => p.PostingSequence)
                .First();
            if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == companyCode && f.BranchCode == branchCode
                && f.ItemCode == group.Key
                && (f.EffectiveAt > firstOwner.EffectiveAt
                    || f.EffectiveAt == firstOwner.EffectiveAt
                    && f.StockPosting!.PostingSequence > firstOwner.PostingSequence)
                && f.StockPosting!.SealedAtUtc != null
                && f.ReversesValuationFactId == null
                && !db.StockValuationFacts.Any(r =>
                    r.ReversesValuationFactId == f.Id), cancellationToken))
                return new(false, true, false);
        }
        return new(false, false, false);
    }
}
