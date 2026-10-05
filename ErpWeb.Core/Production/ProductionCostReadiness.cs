using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Canonical inventory-cost and production-pool readiness checks for IP, Daily, and FG.</summary>
public static class ProductionCostReadiness
{
    public static bool HasVerifiedInventoryCost(decimal? unitPrice, string? priceEvidence) =>
        unitPrice is >= 0m && !string.IsNullOrWhiteSpace(priceEvidence);

    public static string? InventoryBalanceError(IvBalLocLockResult balance)
    {
        ArgumentNullException.ThrowIfNull(balance);
        if (HasVerifiedInventoryCost(balance.UnitPrice, balance.PriceEvidence))
            return null;

        var location = string.IsNullOrWhiteSpace(balance.LocCode) ? balance.LocationCode : balance.LocCode;
        var lot = string.IsNullOrWhiteSpace(balance.LotNo) ? "(no lot)" : balance.LotNo;
        return $"Cost evidence is missing for {balance.ICode}, {balance.WhCode}/{location}, lot {lot} (IvBalLoc {balance.Id}). Repair inventory valuation before Issue to Production.";
    }

    public static async Task<string?> PoolValuationError(
        AppDbContext db,
        ProductionBalLot lot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lot);

        if (string.IsNullOrWhiteSpace(lot.BaseUom) || lot.ConversionFactorToBase <= 0m)
            return $"Production pool {lot.Uid} ({lot.ItemCode} / lot {lot.LotNo}) is missing base UOM evidence.";
        if (lot.Qty < 0m || lot.BaseQty < 0m || lot.TotalCost < 0m || lot.AverageUnitCost < 0m)
            return $"Production pool {lot.Uid} ({lot.ItemCode} / lot {lot.LotNo}) has a negative quantity or value.";
        if (lot.BaseQty == 0m && lot.TotalCost != 0m)
            return $"Production pool {lot.Uid} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.";

        var pool = await db.ProductionPoolValuationRows
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == lot.Uid, ct);
        if (pool is null)
            return $"Production pool {lot.Uid} ({lot.ItemCode} / lot {lot.LotNo}) is UNVALUED. Reverse and repost its upstream transaction after cost evidence is repaired.";
        if (!string.Equals(pool.Status, ProductionPoolValuationService.Verified, StringComparison.Ordinal))
            return $"Production pool {lot.Uid} ({lot.ItemCode} / lot {lot.LotNo}) is UNVALUED. Reverse and repost its upstream transaction after cost evidence is repaired.";
        if (pool.TrackedBaseQty < 0m || pool.TrackedValue < 0m)
            return $"Production pool {lot.Uid} ({lot.ItemCode} / lot {lot.LotNo}) has a negative quantity or value.";
        if (pool.TrackedBaseQty == 0m && pool.TrackedValue != 0m)
            return $"Production pool {lot.Uid} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.";
        if (pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost)
            return $"Production pool {lot.Uid} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.";
        return null;
    }

    public static async Task<string?> FinishedGoodSourceError(
        AppDbContext db,
        ProductionBalLot lot,
        CancellationToken ct)
    {
        if (await PoolValuationError(db, lot, ct) is { } poolError)
            return poolError;
        if (lot.ProductionLocationId is null)
            return $"Production pool {lot.Uid} ({lot.ItemCode} / lot {lot.LotNo}) is missing a production location required for Finished Good Receipt.";
        return null;
    }
}
