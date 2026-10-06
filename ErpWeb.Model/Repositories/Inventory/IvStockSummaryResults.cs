namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// The stock-summary grouping modes. One page answers "totals by …" for four keys rather than shipping
/// four near-identical grids (D1).
///
/// <para>
/// <b>D16 — why the mode changes the quantity column.</b> A row's quantity is in that item's
/// <em>standard</em> UOM, so a total is only a quantity when the group holds exactly one UOM:
/// <list type="bullet">
///   <item><description><c>ITEM</c> and <c>ITEM_WAREHOUSE</c> — one item per group, so one UOM. The
///   quantity total is meaningful.</description></item>
///   <item><description><c>WAREHOUSE</c> and <c>CLASS</c> — a grand total there adds PCS + KG + BOX,
///   which is not a quantity. The quantity column is hidden unless the user opts in, and then it is
///   captioned "Total qty (mixed Std UOM)".</description></item>
/// </list>
/// </para>
/// </summary>
public static class IvStockSummaryGroupBys
{
    public const string Item = "ITEM";
    public const string Warehouse = "WAREHOUSE";
    public const string ItemWarehouse = "ITEM_WAREHOUSE";

    /// <summary>
    /// "Class" is <c>IvStockMaster.IClassCode</c> (column <c>IClass</c>) resolved against
    /// <c>IvClass</c> — the codebase's own meaning of the word, exactly as the item-master list and the
    /// stock-count scope use it. It is NOT <c>IvClassification</c>, <c>Classification</c>, <c>IType</c>
    /// or <c>ISubClassCode</c>.
    /// </summary>
    public const string Class = "CLASS";

    public const string Default = Item;

    public static readonly IReadOnlyList<string> All = [Item, Warehouse, ItemWarehouse, Class];

    public static bool IsKnown(string? groupBy) =>
        groupBy is not null && All.Contains(groupBy.Trim().ToUpperInvariant());

    public static string Normalize(string? groupBy) =>
        IsKnown(groupBy) ? groupBy!.Trim().ToUpperInvariant() : Default;

    public static string Describe(string? groupBy) => Normalize(groupBy) switch
    {
        Item => "Item",
        Warehouse => "Warehouse",
        ItemWarehouse => "Item × Warehouse",
        Class => "Class",
        _ => "Item"
    };

    /// <summary>
    /// False for <c>WAREHOUSE</c>/<c>CLASS</c>, where a quantity total mixes units. The page uses this
    /// to decide the default visibility and the caption of the quantity column (D16).
    /// </summary>
    public static bool HasSingleUomPerGroup(string? groupBy) =>
        Normalize(groupBy) is Item or ItemWarehouse;
}

/// <summary>
/// Filter criteria for the stock summary. Like <see cref="IvBalanceLotQuery"/> every toggle is an
/// <em>inclusion</em> switch: <c>true</c> (the default) means "do not restrict on this category".
/// </summary>
public sealed class IvStockSummaryQuery
{
    /// <summary>One of <see cref="IvStockSummaryGroupBys"/>; unknown falls back to ITEM.</summary>
    public string GroupBy { get; set; } = IvStockSummaryGroupBys.Default;

    /// <summary>Optional as-of date used by the authoritative valuation screen.</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>Optional financial method filter used by the authoritative valuation screen.</summary>
    public string? CostMethod { get; set; }

    public string? ICode { get; set; }
    public string? WhCode { get; set; }

    /// <summary>Item class filter — <c>IvStockMaster.IClassCode</c>.</summary>
    public string? IClassCode { get; set; }

    public IReadOnlyList<string> IStatuses { get; set; } = [];

    /// <summary>Free text over item code, item description and warehouse code.</summary>
    public string? SearchText { get; set; }

    public bool IncludeZeroQty { get; set; } = true;
    public bool IncludeInactive { get; set; } = true;
    public bool IncludeNonStockControl { get; set; } = true;

    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

/// <summary>
/// One group row. The group-key columns are all present for every mode and hold <c>null</c> where the
/// mode does not group on them, so ONE grid definition serves all four modes by toggling visibility.
///
/// <para>
/// <see cref="EstValue"/> and <see cref="UomDisplay"/> are settable and filled by the service:
/// <c>EstValue</c> is masked when the caller may not see price (D11 Option B) and <c>UomDisplay</c>
/// turns a null <see cref="StdUom"/> into the literal "(mixed)" so a mixed-unit group is never rendered
/// as a blank cell.
/// </para>
/// </summary>
public sealed class IvStockSummaryRow
{
    /// <summary>
    /// Stable, unique row identity for the grid. It cannot be derived from a single column because the
    /// group key differs per mode (and a lot-per-warehouse alert row repeats the item code), so the
    /// service composes it.
    /// </summary>
    public string RowKey { get; set; } = string.Empty;

    // ── Group keys ────────────────────────────────────────────────────────────────────────────────
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public string? WhCode { get; init; }
    public string? WhDesc { get; init; }
    public string? IClassCode { get; init; }
    public string? IClassDesc { get; init; }

    /// <summary>The item's standard UOM; <c>null</c> when the group holds more than one.</summary>
    public string? StdUom { get; init; }

    /// <summary>Service-filled display value for the UOM column — the UOM, or "(mixed)".</summary>
    public string? UomDisplay { get; set; }

    // ── Measures ─────────────────────────────────────────────────────────────────────────────────
    /// <summary><c>SUM(StdQty)</c>. Meaningful as a quantity only when <see cref="StdUom"/> is set (D16).</summary>
    public decimal TotalQty { get; init; }

    public int ItemCount { get; init; }

    /// <summary>Number of <c>IvBalLoc</c> piles behind the group.</summary>
    public int PileCount { get; init; }

    /// <summary>Piles whose quantity is exactly zero — carried stock, reported not hidden.</summary>
    public int ZeroQtyPileCount { get; init; }

    /// <summary>
    /// <c>IvQty.Round(SUM(StdQty × (bal.UnitPrice ?? item.PurchasePrice ?? 0)))</c>, or <c>null</c> when
    /// the caller may not see price. An estimate, not a general-ledger valuation (D5/D13).
    /// </summary>
    public decimal? EstValue { get; set; }

    /// <summary>Authoritative value from the sealed stock-valuation ledger.</summary>
    public decimal? InventoryValue { get; set; }

    /// <summary>Informational unit cost derived from the authoritative row value and quantity.</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>The financial method that produced the authoritative value.</summary>
    public string? CostMethod { get; set; }

    /// <summary>RESOLVED, QTY_MISMATCH, UNRESOLVED, or another explicit report status.</summary>
    public string? ValuationStatus { get; set; }

    /// <summary>True when a warehouse row is an allocation of a branch/item financial pool.</summary>
    public bool IsAllocatedWarehouseValue { get; set; }
}

/// <summary>
/// Aggregate over the SAME predicate as the grid. Because grouping only partitions the piles,
/// <see cref="TotalQty"/>/<see cref="PileCount"/>/<see cref="ZeroQtyPileCount"/>/<see cref="TotalValue"/>
/// are computed over the FLAT slice in SQL and are therefore identical for every grouping mode; only
/// <see cref="GroupCount"/> depends on the mode.
/// </summary>
public sealed class IvStockSummarySummary
{
    public int GroupCount { get; init; }
    public int ItemCount { get; init; }
    public int PileCount { get; init; }
    public int ZeroQtyPileCount { get; init; }
    public decimal TotalQty { get; init; }
    public decimal? TotalValue { get; set; }
}

/// <summary>Paged group rows plus the unbounded group count for the same predicate.</summary>
public sealed class IvStockSummaryPage
{
    public IReadOnlyList<IvStockSummaryRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

// NOTE: there is deliberately no shared sort whitelist here. A grouped query can only be ordered by its
// OWN grouping keys and aggregates, so the legal sort fields are derived per grouping mode inside
// IvStockInquiryRepository — one whitelist for all four modes would permit an ungrouped column to reach
// the expression tree, which is not valid SQL.
