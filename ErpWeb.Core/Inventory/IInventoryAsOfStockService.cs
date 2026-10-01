using ErpWeb.Model.Data;

namespace ErpWeb.Core.Inventory;

public interface IInventoryAsOfStockService
{
    Task<IReadOnlyDictionary<int, InventoryAsOfStock>> GetAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<int> balanceIds,
        DateTime asOf,
        CancellationToken cancellationToken = default);
}

public sealed record InventoryAsOfStock(
    int BalanceId,
    decimal CurrentBaseQty,
    decimal AsOfBaseQty,
    decimal UsableBaseQty);
