using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

/// <summary>Reconstructs past stock by reversing ledger movements later than the requested instant.</summary>
public sealed class InventoryAsOfStockService : IInventoryAsOfStockService
{
    public async Task<IReadOnlyDictionary<int, InventoryAsOfStock>> GetAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<int> balanceIds,
        DateTime asOf,
        CancellationToken cancellationToken = default)
    {
        if (balanceIds.Count == 0)
            return new Dictionary<int, InventoryAsOfStock>();

        var ids = balanceIds.Distinct().ToArray();
        var current = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && ids.Contains(x.Id))
            .Select(x => new { x.Id, x.StdQty })
            .ToListAsync(cancellationToken);
        var movements = await db.IvTrxHistories.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.TrxDtTime > asOf
                && ((x.FromBalLocId.HasValue && ids.Contains(x.FromBalLocId.Value))
                    || (x.ToBalLocId.HasValue && ids.Contains(x.ToBalLocId.Value))))
            .Select(x => new { x.FromBalLocId, x.ToBalLocId, x.FrStdQty, x.ToStdQty })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, InventoryAsOfStock>(current.Count);
        foreach (var balance in current)
        {
            var postInbound = movements.Where(x => x.ToBalLocId == balance.Id).Sum(x => x.ToStdQty ?? 0m);
            var postOutbound = movements.Where(x => x.FromBalLocId == balance.Id).Sum(x => x.FrStdQty ?? 0m);
            var currentQty = IvQty.Round(Math.Max(balance.StdQty, 0m));
            var asOfQty = IvQty.Round(Math.Max(balance.StdQty - postInbound + postOutbound, 0m));
            result[balance.Id] = new InventoryAsOfStock(
                balance.Id, currentQty, asOfQty, IvQty.Round(Math.Min(currentQty, asOfQty)));
        }
        return result;
    }
}
