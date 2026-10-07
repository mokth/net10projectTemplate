using ErpWeb.Core.Lookups;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Unified purchasing item row spanning active stock masters and indirect <c>PoPurItem</c> rows.
/// </summary>
public sealed record PoPurchasingItemLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public bool IsIndirect { get; init; }
    public string? IType { get; init; }
    public string? PurchaseUom { get; init; }
    public string? StdUom { get; init; }
    public decimal PackSz { get; init; } = 1m;
    public decimal? StdPackSize { get; init; }
    public decimal? PurStdPackSize { get; init; }
    public decimal? UnitPrice { get; init; }
    public string? TaxGroup { get; init; }
    public string? PurchaseTaxGroup { get; init; }
    public string? DefWarehouse { get; init; }
    public string? DefLocation { get; init; }
    public string? Category { get; init; }
    public string? VendorCd { get; init; }
    public string? VendNm { get; init; }
    public decimal Moq { get; init; }
    public bool StockControl { get; init; }
    public bool LotControl { get; init; }
    public string? Classification { get; init; }
    public string? PurchaseGlCode { get; init; }
    public string SourceLabel => IsIndirect ? "Indirect" : "Stock";
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} — {IDesc}";
}

public interface IPoPurchasingItemLookupService
{
    /// <summary>
    /// Exact code resolve across stock + indirect. Ambiguous when the same code exists in both.
    /// Unit price is null when the caller lacks VIEW_COST on the given menu (or PR/PO menus).
    /// </summary>
    Task<LargeLookupResolveResult<PoPurchasingItemLookupRow>> ResolveAsync(
        string iCode,
        string? menuCode = null,
        bool includeIndirect = true,
        CancellationToken cancellationToken = default);

    /// <summary>Server-paged search across stock and optional indirect items.</summary>
    Task<LargeLookupPage<PoPurchasingItemLookupRow>> SearchPagedAsync(
        LargeLookupSearchRequest request,
        string? menuCode = null,
        bool includeIndirect = true,
        CancellationToken cancellationToken = default);
}
