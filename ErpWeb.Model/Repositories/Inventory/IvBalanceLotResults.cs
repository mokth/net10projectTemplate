using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Filter/paging criteria for the Balance-by-Lot inquiry over <c>dbo.IvBalLoc</c>.
///
/// <para>
/// Every filter is optional and, where it is a toggle, an <em>inclusion</em> switch:
/// <c>true</c> (the default) means "do not restrict on this category", <c>false</c> means
/// "apply the predicate". This is the opposite of a "hide" flag and is load-bearing — see
/// <see cref="IncludeZeroQty"/> / <see cref="IncludeInactive"/> / <see cref="IncludeNonStockControl"/>.
/// </para>
/// </summary>
public sealed class IvBalanceLotQuery
{
    public string? ICode { get; set; }
    public string? WhCode { get; set; }
    public string? LocCode { get; set; }
    public string? LotNo { get; set; }

    /// <summary>Explicit item-status list. Empty means ALL statuses, including SCRAPS (never "none").</summary>
    public IReadOnlyList<string> IStatuses { get; set; } = [];

    public string? SearchText { get; set; }
    public decimal? MinQty { get; set; }
    public decimal? MaxQty { get; set; }

    /// <summary>Only piles whose lot expires strictly before this date (date part only).</summary>
    public DateTime? ExpiryBefore { get; set; }

    /// <summary>"Last movement" date range, inclusive by day. <see cref="IvBalLoc.TransDate"/> is the
    /// last posted movement's date and is mutable, not a history of movements.</summary>
    public DateTime? TransDateFrom { get; set; }
    public DateTime? TransDateTo { get; set; }

    public bool IncludeZeroQty { get; set; } = true;
    public bool IncludeInactive { get; set; } = true;
    public bool IncludeNonStockControl { get; set; } = true;

    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

/// <summary>
/// One pile (item × warehouse × bin × lot × status) for the Balance-by-Lot inquiry grid and export.
/// <c>Value</c> is deliberately NOT projected by the repository — the service computes it after
/// materialisation and clears it for callers without <c>CanViewPrice</c>.
/// </summary>
public sealed class IvBalanceLotRow
{
    public int Id { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string WhCode { get; init; } = string.Empty;
    public string? WhDesc { get; init; }
    public string LocCode { get; init; } = string.Empty;
    public string? LocDesc { get; init; }
    public string LotNo { get; init; } = string.Empty;
    public string IStatus { get; init; } = string.Empty;
    public string? IStatusDesc { get; init; }
    public decimal StdQty { get; init; }
    public string? StdUom { get; init; }
    public int? LotId { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public DateTime? TransDate { get; init; }
    public string? PoNo { get; init; }
    public string? RefNo { get; init; }
    public string? Remarks { get; init; }

    /// <summary>Effective per-<see cref="StdUom"/> price: <c>IvBalLoc.UnitPrice ?? IvStockMaster.PurchasePrice</c>.</summary>
    public decimal? UnitPrice { get; init; }

    /// <summary><c>IvQty.Round(StdQty * (UnitPrice ?? 0m))</c>, or <c>null</c> when the caller may not view price.</summary>
    public decimal? Value { get; set; }

    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }

    /// <summary>
    /// Always <c>null</c>: <c>IvBalLoc</c> has <c>ModifiedDate</c> but no <c>ModifiedBy</c> column
    /// (legacy table had <c>Updated</c>, not <c>UpdatedUID</c>). The property exists only because the
    /// grid resolves <c>DxGridDataColumn.FieldName</c> by property name; no column was invented.
    /// </summary>
    public string? ModifiedBy { get; init; }
}

/// <summary>Aggregate over the SAME predicate as the grid. <see cref="TotalValue"/> is set by the
/// service: rounded when price may be viewed, <c>null</c> otherwise.</summary>
public sealed class IvBalanceLotSummary
{
    public int TotalRows { get; init; }
    public decimal TotalQty { get; init; }
    public decimal? TotalValue { get; set; }
    public int ZeroQtyRowCount { get; init; }
    public int ExpiredRowCount { get; init; }
}

/// <summary>Server-side sort whitelist. An unknown field falls back to the default order.</summary>
public static class IvBalanceLotSortFields
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IvBalanceLotRow.ICode),
        nameof(IvBalanceLotRow.IDesc),
        nameof(IvBalanceLotRow.WhCode),
        nameof(IvBalanceLotRow.LocCode),
        nameof(IvBalanceLotRow.LotNo),
        nameof(IvBalanceLotRow.IStatus),
        nameof(IvBalanceLotRow.StdQty),
        nameof(IvBalanceLotRow.ExpiryDate),
        nameof(IvBalanceLotRow.TransDate),
        nameof(IvBalanceLotRow.StdUom)
    };
}

/// <summary>Internal query shape produced by the single shared composition.</summary>
public sealed class IvBalanceLotSlice
{
    public IvBalLoc Bal { get; set; } = null!;
    public IvStockMaster? Sm { get; set; }
    public IvLot? Lot { get; set; }
    public IvStatus? Status { get; set; }
    public IvWarehouse? Wh { get; set; }
    public IvLocation? Loc { get; set; }
}
