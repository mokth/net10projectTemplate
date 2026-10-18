using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Checks the header's permanent pointer to its first ADJ batch.</summary>
internal static class StockCountFirstBatchPointer
{
    public static async Task<bool> MatchesAsync(AppDbContext db,
        string companyCode, string branchCode,
        IReadOnlyCollection<int> countLineIds, int? postedBatchNo,
        CancellationToken cancellationToken)
    {
        var roots = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.CompanyCode == companyCode
                && g.BranchCode == branchCode
                && countLineIds.Contains(g.StockCountLineId)
                && g.PreviousGenerationId == null)
            .Select(g => new { g.StockCountLineId,
                g.AdjustmentBatchNo })
            .ToArrayAsync(cancellationToken);
        if (roots.Length != countLineIds.Count
            || roots.Select(g => g.StockCountLineId).Distinct().Count()
                != countLineIds.Count)
            return false;
        var batches = roots.Where(g => g.AdjustmentBatchNo.HasValue)
            .Select(g => g.AdjustmentBatchNo!.Value).Distinct().ToArray();
        return batches.Length <= 1
            && postedBatchNo == (batches.Length == 0
                ? null : batches[0]);
    }
}
