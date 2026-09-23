namespace ErpWeb.Core.Purchase;

/// <summary>Failure classification for the self-billed services, mirroring the shipped purchase services.</summary>
public enum PoSbErrorKind
{
    None,
    Validation,
    Concurrency,
    NotFound,
    Authorization,
    BusinessRule,
    Unexpected
}

/// <summary>One document addressed by number + the row version the caller loaded.</summary>
public sealed class PoSbKeyedRequest
{
    public string DocNo { get; init; } = string.Empty;
    public byte[]? RowVersion { get; init; }
}

/// <summary>
/// List filter shared by both self-billed lists. Resolution of the branch/company is always the
/// caller's scope — a query never carries a tenant.
/// </summary>
/// <remarks>
/// Deliberately MUTABLE (like <c>PoCdnListQuery</c> / <c>SaCdnListQuery</c>): a grid data source clones
/// the caller's filter and then overwrites <see cref="Skip"/>/<see cref="Take"/>/<see cref="SortField"/> per
/// request, so <c>init</c> accessors would make the object unusable as a paging carrier.
/// </remarks>
public sealed class PoSbQuery
{
    public string? SearchText { get; set; }

    /// <summary>ERP document status (<c>NEW</c>; <c>POSTED</c> survives only on legacy rows).</summary>
    public string? Status { get; set; }

    /// <summary>
    /// e-Invoice status filter (e.g. <c>VALID</c> for the note origin picker). Deliberately a SEPARATE
    /// property from <see cref="Status"/>: they read different columns and must never be swapped.
    /// </summary>
    public string? IrbmStatus { get; set; }

    public string? VendorCode { get; set; }

    /// <summary>CN / DN filter for the note list; ignored by the invoice list.</summary>
    public string? Type { get; set; }

    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string SortField { get; set; } = "DocNo";
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = PoSbLimits.DefaultPageSize;

    /// <summary>Normalises paging so a caller cannot ask for an unbounded page.</summary>
    public (int Skip, int Take) NormalizedPaging()
    {
        var skip = Math.Max(0, Skip);
        var take = Take <= 0 ? PoSbLimits.DefaultPageSize : Math.Min(Take, PoSbLimits.MaxPageSize);
        return (skip, take);
    }
}

/// <summary>A paged list result.</summary>
public sealed class PoSbPage<TRow>
{
    public IReadOnlyList<TRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>A code + display pair for a lookup combo.</summary>
public sealed class PoSbCodeLookupRow
{
    public string Code { get; init; } = string.Empty;
    public string DisplayText { get; init; } = string.Empty;
}

/// <summary>Vendor picker row (<c>PoSupplier</c>).</summary>
public sealed class PoSbVendorLookupRow
{
    public string SuppCode { get; init; } = string.Empty;
    public string DisplayText { get; init; } = string.Empty;
}

public sealed class PoSbTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string DisplayText { get; init; } = string.Empty;

    /// <summary>Used by the entry popup to PREVIEW the line tax; the server recomputes it on save.</summary>
    public decimal Percentage { get; init; }
}

/// <summary>Everything the self-billed entry screens load once.</summary>
public sealed class PoSbLookups
{
    public IReadOnlyList<PoSbVendorLookupRow> Vendors { get; init; } = [];
    public IReadOnlyList<PoSbTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<PoSbCodeLookupRow> Currencies { get; init; } = [];
    public IReadOnlyList<PoSbCodeLookupRow> Uoms { get; init; } = [];
}

/// <summary>Defaults applied when a vendor is chosen on a new document.</summary>
public sealed class PoSbVendorDefaults
{
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string? Currency { get; init; }
    public string? TaxGrCode { get; init; }
}

/// <summary>
/// A stored line, as returned to the editor. Shared by both self-billed families: the line shape is
/// identical and the parent only differs.
/// </summary>
public sealed class PoSbLineDto
{
    public short Line { get; init; }
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal UnitPrice { get; init; }
    public string? SellingUom { get; init; }
    public string? StdUom { get; init; }
    public decimal Amount { get; init; }
    public decimal TaxAmt { get; init; }
    public decimal NetAmount { get; init; }
    public string? TaxGroup { get; init; }
    public bool IsInclusive { get; init; }
    public decimal Discount { get; init; }
    public decimal ItemDiscount { get; init; }
    public decimal ItemDiscount1 { get; init; }
    public string? IDiscountType { get; init; }
    public string? IDiscountType1 { get; init; }

    /// <summary>LHDN item classification code — required on every submissible line.</summary>
    public string? Classification { get; init; }

    public string? Remarks { get; init; }
}

/// <summary>
/// A line as sent by the editor. Amounts are NOT accepted from the caller: the server recomputes
/// amount / discount / tax from qty, unit price and the discount slots.
/// </summary>
public sealed class PoSbLineRequest
{
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal UnitPrice { get; init; }
    public string? SellingUom { get; init; }
    public string? StdUom { get; init; }
    public string? TaxGroup { get; init; }
    public bool IsInclusive { get; init; }
    public decimal ItemDiscount { get; init; }
    public string? IDiscountType { get; init; }
    public decimal ItemDiscount1 { get; init; }
    public string? IDiscountType1 { get; init; }
    public string? Classification { get; init; }
    public string? Remarks { get; init; }
}
