using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    /// <summary>INV-03: locked BalLoc must have TransDate on or before the document date.</summary>
    private static string? ValidateBalanceStockDate(
        int balLocId,
        DateTime? stockDate,
        DateTime documentDate,
        string? lotNo)
    {
        if (!stockDate.HasValue)
            return IvStockDateRules.NullStockDateMessage(balLocId);

        if (!IvStockDateRules.IsAvailableOn(stockDate, documentDate))
            return IvStockDateRules.FutureStockMessage(lotNo, stockDate.Value, documentDate);

        return null;
    }

    /// <summary>INV-04: reject when posted history exists on a later calendar day.</summary>
    private async Task<string?> ValidateNoLaterDayMovementsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<int> balLocIds,
        DateTime documentDate,
        int? excludeBatchNo,
        CancellationToken cancellationToken)
    {
        if (balLocIds.Count == 0)
            return null;

        var hits = await _posting.FindLaterDayMovementsAsync(
            db, companyCode, branchCode, balLocIds, documentDate, excludeBatchNo, cancellationToken);
        if (hits.Count == 0)
            return null;

        var hit = hits[0];
        return IvStockMovementRules.LaterDayMovementMessage(
            hit.ICode, hit.Warehouse, hit.LotNo, hit.TrxDtTime, documentDate);
    }

    /// <summary>INV-05: reject rollback when a later persisted movement remains on any BalLoc.</summary>
    private async Task<string?> ValidateRollbackChronologyAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        IReadOnlyList<IvTrxHistory> history,
        CancellationToken cancellationToken)
    {
        var targets = history
            .SelectMany(h => new[]
            {
                h.FromBalLocId is int fromId ? (BalLocId: fromId, HistoryId: h.Id, Date: h.TrxDtTime) : default,
                h.ToBalLocId is int toId ? (BalLocId: toId, HistoryId: h.Id, Date: h.TrxDtTime) : default
            })
            .Where(x => x.BalLocId > 0)
            .GroupBy(x => x.BalLocId)
            .Select(g => (
                BalLocId: g.Key,
                TargetDate: g.Max(x => x.Date),
                TargetMaxHistoryId: g.Max(x => x.HistoryId)))
            .ToList();

        if (targets.Count == 0)
            return null;

        var hits = await _posting.FindLaterRollbackMovementsAsync(
            db, companyCode, branchCode, targets, batchNo, cancellationToken);
        if (hits.Count == 0)
            return null;

        var hit = hits[0];
        return IvStockMovementRules.RollbackLaterMovementMessage(batchNo, hit.BalLocId, hit.TrxDtTime);
    }

    /// <summary>
    /// After inverse qty, repair TransDate from remaining history (including zero-net BalLocs).
    /// </summary>
    private async Task RepairTransDatesAfterRollbackAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int excludeBatchNo,
        IEnumerable<int> balLocIds,
        CancellationToken cancellationToken)
    {
        foreach (var balLocId in balLocIds.Distinct().OrderBy(x => x))
        {
            var latest = await _posting.GetLatestRemainingMovementAsync(
                db, companyCode, branchCode, balLocId, excludeBatchNo, cancellationToken);
            await _posting.SetBalLocTransDateAsync(
                db, balLocId, companyCode, branchCode, latest, cancellationToken);
        }
    }
}
