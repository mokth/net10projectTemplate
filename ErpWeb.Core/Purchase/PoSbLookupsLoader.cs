using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// The lookups and vendor defaults shared by both self-billed entry screens. Kept as one loader so the
/// invoice and the note cannot drift apart in what they offer or in the defaults they apply.
/// </summary>
internal static class PoSbLookupsLoader
{
    public static async Task<PoSbLookups> LoadAsync(
        AppDbContext db, string companyCode, CancellationToken cancellationToken)
    {
        var vendors = await db.PoSuppliers.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.IsActive)
            .OrderBy(x => x.SuppCode)
            .Select(x => new PoSbVendorLookupRow
            {
                SuppCode = x.SuppCode,
                DisplayText = x.SuppCode + " - " + x.SuppName
            })
            .ToListAsync(cancellationToken);

        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .OrderBy(x => x.TaxGrCode)
            .Select(x => new PoSbTaxGroupLookupRow
            {
                TaxGrCode = x.TaxGrCode,
                Percentage = x.Percentage,
                DisplayText = x.TaxGrDesc == null || x.TaxGrDesc == ""
                    ? x.TaxGrCode
                    : x.TaxGrCode + " - " + x.TaxGrDesc
            })
            .ToListAsync(cancellationToken);

        var currencies = await db.SaCurrencies.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .OrderBy(x => x.CurrCode)
            .Select(x => new PoSbCodeLookupRow
            {
                Code = x.CurrCode,
                DisplayText = x.CurrCode
            })
            .ToListAsync(cancellationToken);

        var uoms = await db.MsUoms.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .OrderBy(x => x.UomCode)
            .Select(x => new PoSbCodeLookupRow
            {
                Code = x.UomCode,
                DisplayText = x.UomCode
            })
            .ToListAsync(cancellationToken);

        return new PoSbLookups
        {
            Vendors = vendors,
            TaxGroups = taxGroups,
            Currencies = currencies,
            Uoms = uoms
        };
    }

    /// <summary>Vendor defaults for a new document, or null when the vendor is unknown to this company.</summary>
    public static async Task<PoSbVendorDefaults?> LoadVendorDefaultsAsync(
        AppDbContext db, string companyCode, string vendorCode, CancellationToken cancellationToken)
    {
        var code = PoSbCalc.NullIfBlank(vendorCode);
        if (code is null)
        {
            return null;
        }

        var vendor = await db.PoSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.SuppCode == code, cancellationToken);

        return vendor is null
            ? null
            : new PoSbVendorDefaults
            {
                VendorCode = vendor.SuppCode,
                VendorName = vendor.SuppName,
                Currency = vendor.Currency,
                TaxGrCode = vendor.TaxGroup ?? vendor.TaxGrCode
            };
    }

    /// <summary>
    /// Tax group code → percentage for the company. The LHDN tax <i>type</i> is resolved separately by
    /// the e-Invoice façade from <c>SaTaxGroup.TaxType</c>; this map only feeds the arithmetic.
    /// </summary>
    public static async Task<Dictionary<string, decimal>> LoadTaxPercentsAsync(
        AppDbContext db, string companyCode, CancellationToken cancellationToken)
    {
        var rows = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .Select(x => new { x.TaxGrCode, x.Percentage })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            map[row.TaxGrCode] = row.Percentage;
        }

        return map;
    }
}
