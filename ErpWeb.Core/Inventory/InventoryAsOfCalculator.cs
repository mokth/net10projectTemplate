using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class AsOfBalanceDto
{
    public long BranchId { get; init; }
    public long WarehouseId { get; init; }
    public long ItemVariantId { get; init; }
    public decimal Qty { get; init; }
    public decimal UnitCost { get; init; }
    public decimal Value { get; init; }
}

public sealed class InventoryValuationDto
{
    public DateTime AsOfDate { get; init; }
    public decimal TotalQty { get; init; }
    public decimal TotalValue { get; init; }
    public IReadOnlyList<AsOfBalanceDto> Lines { get; init; } = [];
}

/// <summary>
/// Computes historical qty/value from immutable StockLedger (never live StockBalance).
/// Residual value = Σ inbound Amount − Σ outbound Amount up to as-of date.
/// </summary>
internal static class InventoryAsOfCalculator
{
    public static async Task<IReadOnlyList<AsOfBalanceDto>> ComputeAsync(
        AppDbContext db,
        int companyId,
        DateTime asOfDate,
        long? branchId = null,
        long? warehouseId = null,
        CancellationToken ct = default)
    {
        var asOf = asOfDate.Date;
        var query = db.StockLedgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.TransactionDate <= asOf);

        if (branchId is long b) query = query.Where(l => l.BranchId == b);
        if (warehouseId is long w) query = query.Where(l => l.WarehouseId == w);

        var rows = await query
            .GroupBy(l => new { l.BranchId, l.WarehouseId, l.ItemVariantId })
            .Select(g => new
            {
                g.Key.BranchId,
                g.Key.WarehouseId,
                g.Key.ItemVariantId,
                Qty = g.Sum(x => x.QtyInBase - x.QtyOutBase),
                Value = g.Sum(x => x.QtyInBase > 0 ? x.Amount : -x.Amount)
            })
            .ToListAsync(ct);

        return rows
            .Where(r => r.Qty != 0 || r.Value != 0)
            .Select(r =>
            {
                var qty = Math.Round(r.Qty, 6, MidpointRounding.AwayFromZero);
                var value = Math.Round(r.Value, 4, MidpointRounding.AwayFromZero);
                var cost = qty == 0
                    ? 0m
                    : Math.Round(value / qty, 6, MidpointRounding.AwayFromZero);
                return new AsOfBalanceDto
                {
                    BranchId = r.BranchId,
                    WarehouseId = r.WarehouseId,
                    ItemVariantId = r.ItemVariantId,
                    Qty = qty,
                    UnitCost = cost,
                    Value = value
                };
            })
            .OrderBy(r => r.BranchId)
            .ThenBy(r => r.WarehouseId)
            .ThenBy(r => r.ItemVariantId)
            .ToList();
    }
}
