using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Shared vendor-item qty / price tolerance lookup for GR and purchase invoice.
/// Prefer branch-exact rows, then UOM match.
/// </summary>
public static class PoToleranceLookup
{
    public sealed record Tolerances(decimal QtyTolerance, decimal PriceTolerance);

    public static async Task<decimal> GetQtyToleranceAsync(
        AppDbContext db,
        PoOrder po,
        PoOrderDetail line,
        CancellationToken cancellationToken = default)
    {
        var row = await FindVendorItemAsync(db, po, line, cancellationToken);
        return Normalize(row?.Tolerance ?? 0m);
    }

    public static async Task<decimal> GetPriceToleranceAsync(
        AppDbContext db,
        PoOrder po,
        PoOrderDetail line,
        CancellationToken cancellationToken = default)
    {
        var row = await FindVendorItemAsync(db, po, line, cancellationToken);
        return Normalize(row?.PriceTolerance ?? 0m);
    }

    public static async Task<Tolerances> GetAsync(
        AppDbContext db,
        PoOrder po,
        PoOrderDetail line,
        CancellationToken cancellationToken = default)
    {
        var row = await FindVendorItemAsync(db, po, line, cancellationToken);
        return new Tolerances(
            Normalize(row?.Tolerance ?? 0m),
            Normalize(row?.PriceTolerance ?? 0m));
    }

    public static decimal EffectivePriceTolerance(decimal? invoiceOverride, decimal vendorItemTolerance) =>
        invoiceOverride ?? vendorItemTolerance;

    private static async Task<PoVendorByItem?> FindVendorItemAsync(
        AppDbContext db,
        PoOrder po,
        PoOrderDetail line,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(po);
        ArgumentNullException.ThrowIfNull(line);

        var vendor = (po.VendCode ?? string.Empty).Trim();
        var iCode = (line.ICode ?? string.Empty).Trim();
        if (vendor.Length == 0 || iCode.Length == 0)
        {
            return null;
        }

        var purUom = (line.PurchaseUom ?? string.Empty).Trim();
        return await db.PoVendorByItems.AsNoTracking()
            .Where(x => x.CompanyCode == po.CompanyCode
                && x.Vendor == vendor
                && x.ICode == iCode
                && (x.BranchCode == po.BranchCode || x.BranchCode == null || x.BranchCode == string.Empty))
            .OrderByDescending(x => x.BranchCode == po.BranchCode)
            .ThenByDescending(x => purUom.Length > 0 && x.PurUom == purUom)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static decimal Normalize(decimal value) =>
        value < 0m ? 0m : (value > 100m ? 100m : value);
}
