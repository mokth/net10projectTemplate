using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

internal static class PurchaseReplayChronologyGuard
{
    // A canceled sale is absent from the active tail, but its issue and reversal
    // can still cross the invoice date and require historical recosting.
    internal static Task<bool> HasLaterCanceledSaleAsync(
        AppDbContext db, string company, string branch, long epoch, DateTime effectiveAt,
        long sequence, string[] affectedItems, CancellationToken cancellationToken) =>
        db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.CompanyCode == company && h.BranchCode == branch
            && h.LedgerVersion == 2 && (h.LedgerEpochId == epoch || h.LedgerEpochId == null)
            && h.TrxType == IvTrxTypes.SalesOut && h.ICode != null && affectedItems.Contains(h.ICode)
            && db.IvTrxHistories.Any(r => r.CompanyCode == company && r.BranchCode == branch
                && r.ReversesHistoryId == h.Id
                && (h.TrxDtTime > effectiveAt || h.TrxDtTime == effectiveAt
                        && !db.StockPostings.Any(p => p.Id == h.StockPostingId && p.CompanyCode == company
                            && p.BranchCode == branch && p.LedgerEpochId == epoch && p.SealedAtUtc != null
                            && p.PostingSequence <= sequence)
                    || r.TrxDtTime > effectiveAt || r.TrxDtTime == effectiveAt
                        && !db.StockPostings.Any(p => p.Id == r.StockPostingId && p.CompanyCode == company
                            && p.BranchCode == branch && p.LedgerEpochId == epoch && p.SealedAtUtc != null
                            && p.PostingSequence <= sequence))),
            cancellationToken);

    internal static async Task<bool> HasLaterActiveWacCostAsync(
        AppDbContext db, string company, string branch, long epoch, DateTime effectiveAt,
        long sequence, string[] affectedItems, CancellationToken cancellationToken, long[]? ownReplayFacts = null)
    {
        ownReplayFacts ??= [];
        // Check the active physical timeline independently of valuation rows.
        // A missing or damaged valuation must never hide a later stock movement.
        var piles = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && affectedItems.Contains(b.ICode))
            .Select(b => b.Id).ToArrayAsync(cancellationToken);
        var key = new StockReplayEventKey(effectiveAt, StockReplayEventKey.OrdinaryClass,
            sequence, "\uffff", int.MaxValue);
        foreach (var pile in piles)
            if ((await StockReplayPhysicalTimelineReader.ReadLaterAsync(
                    db, company, branch, pile, key, cancellationToken)).Count != 0)
                return true;
        return await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
            f.CompanyCode == company && f.BranchCode == branch && f.LedgerEpochId == epoch
            && affectedItems.Contains(f.ItemCode) && !ownReplayFacts.Contains(f.Id)
            && f.StockPosting!.SealedAtUtc != null
            && f.ReversesValuationFactId == null
            && (f.EffectiveAt > effectiveAt || f.EffectiveAt == effectiveAt
                && (f.InventoryHistoryId == null
                    ? f.StockPosting.PostingSequence > sequence
                    : !db.IvTrxHistories.Any(h => h.Id == f.InventoryHistoryId
                        && h.CompanyCode == company && h.BranchCode == branch && h.StockPostingId != null
                        && db.StockPostings.Any(p => p.Id == h.StockPostingId && p.CompanyCode == company
                            && p.BranchCode == branch && p.SealedAtUtc != null && p.LedgerEpochId == epoch
                            && p.PostingSequence <= sequence))))
            && !db.StockValuationFacts.Any(r => r.ReversesValuationFactId == f.Id
                && r.StockPosting!.SealedAtUtc != null), cancellationToken);
    }

}
