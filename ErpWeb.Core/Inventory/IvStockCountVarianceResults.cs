namespace ErpWeb.Core.Inventory;

/// <summary>
/// The variance direction tokens, in the SAME sense as the count sheet's own preview
/// (<c>variance = SystemQty − PhysicalQty</c>):
/// <c>&gt; 0</c> means the system held more than the shelf did, i.e. a write-DOWN.
///
/// <para>
/// They live here so the variance report and the post preview cannot disagree about which direction
/// "positive" points; the preview's own literals are deliberately left untouched.
/// </para>
/// </summary>
public static class IvStockCountVarianceDirections
{
    /// <summary>Physical count exceeded the system quantity — stock was found.</summary>
    public const string Increase = "INCREASE";

    /// <summary>System quantity exceeded the physical count — stock is missing.</summary>
    public const string Decrease = "DECREASE";

    /// <summary>Counted, and the two agree.</summary>
    public const string None = "NONE";

    /// <summary>Not counted, so no variance exists — the line is excluded from the accuracy denominator.</summary>
    public const string NotCounted = "NOT COUNTED";
}

/// <summary>
/// Filter/paging criteria for the stock-count variance report over POSTED sheets.
///
/// <para>
/// All four filters the plan names are here — date range, warehouse, class and item status — plus an
/// item filter, which is the natural drill-down for "why is the accuracy low".
/// </para>
///
/// <para>
/// The report is deliberately limited to <c>POSTED</c> sheets. A DRAFT or COUNTED sheet has no
/// evidence yet, and a ROLLED_BACK sheet's adjustment is no longer in the ledger — including either
/// would put a variance figure next to stock that no longer exists.
/// </para>
/// </summary>
public sealed class IvStockCountVarianceQuery
{
    /// <summary>Sheet date range, inclusive by day (half-open: <c>&gt;= from</c> and <c>&lt; to+1</c>).</summary>
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    public string? ICode { get; set; }
    public string? WhCode { get; set; }

    /// <summary>Item class (<c>IvStockMaster.IClassCode</c>), matched on the line's denormalised value.</summary>
    public string? IClassCode { get; set; }

    /// <summary>Line (pile) item-status inclusion list. Empty means ALL statuses, never "none".</summary>
    public IReadOnlyList<string> IStatuses { get; set; } = [];

    /// <summary>Substring match over the sheet number, item code and description.</summary>
    public string? SearchText { get; set; }

    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

/// <summary>
/// One count line of a POSTED sheet.
///
/// <para>
/// <b>The staleness caveat is part of the row.</b> <see cref="SystemQty"/> is Generate-time evidence,
/// not the live quantity at post — the posting engine re-reads the locked balance and computes the
/// adjustment delta against it. So <see cref="Variance"/> is a <em>sheet</em> variance, and the
/// header's <see cref="PostedStaleLines"/> is carried onto every row of that sheet as the disclosure.
/// </para>
///
/// <para>
/// <see cref="Variance"/>, <see cref="VarianceValue"/>, <see cref="Direction"/> and
/// <see cref="IsCounted"/> are settable and filled by the service: the grid resolves
/// <c>DxGridDataColumn.FieldName</c> by property name.
/// </para>
/// </summary>
public sealed class IvStockCountVarianceRow
{
    public int HeaderId { get; init; }
    public string CountNo { get; init; } = string.Empty;
    public DateTime CountDate { get; init; }
    public int? PostedBatchNo { get; init; }

    /// <summary>How many lines of this sheet were stale when it was posted. 0 = none. Disclosed per row.</summary>
    public int? PostedStaleLines { get; init; }

    public int LineId { get; init; }
    public short LineNumber { get; init; }
    public int BalLocId { get; init; }

    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? WHCode { get; init; }
    public string? LocCode { get; init; }
    public string? LotNo { get; init; }
    public string IStatus { get; init; } = string.Empty;
    public string? IClassCode { get; init; }
    public string? StdUom { get; init; }

    /// <summary>Generate-time evidence, never the live quantity at post.</summary>
    public decimal SystemQty { get; init; }

    /// <summary><c>null</c> = not counted. Those lines are excluded from the accuracy denominator (D17).</summary>
    public decimal? PhysicalQty { get; init; }

    /// <summary><c>SystemQty − PhysicalQty</c>, rounded through <c>IvQty.Round</c>. Positive = write-down.</summary>
    public decimal? Variance { get; set; }

    public string? Direction { get; set; }

    public bool IsCounted { get; set; }

    /// <summary>Evidence only — the price the sheet saw. Posting resolves its own price.</summary>
    public decimal? SnapshotUnitPrice { get; init; }

    /// <summary><c>Variance × SnapshotUnitPrice</c>, rounded. <c>null</c> when the price is unknown.</summary>
    public decimal? VarianceValue { get; set; }

    public short RecountCount { get; init; }
    public string? CountedBy { get; init; }
    public DateTime? CountedOn { get; init; }

    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
}

/// <summary>
/// Aggregate over the SAME predicate as the grid, computed in SQL (D17).
///
/// <para>
/// <b>Accuracy.</b> <see cref="LineAccuracyPercent"/> is
/// <c>ExactMatchLines / CountedLines × 100</c>, and it is <b>null (rendered "—") when nothing was
/// counted</b> — never 0 and never 100. A <c>1 − ABS(variance)/ABS(systemQty)</c> ratio is forbidden:
/// <c>SystemQty = 0</c> is common in a count and that formula divides by zero.
/// </para>
/// </summary>
public sealed class IvStockCountVarianceSummary
{
    public int SheetCount { get; init; }
    public int LineCount { get; init; }

    /// <summary>Lines with a physical quantity — the accuracy denominator.</summary>
    public int CountedLines { get; init; }

    /// <summary>Counted lines where the physical quantity equalled the snapshot exactly.</summary>
    public int ExactMatchLines { get; init; }

    public int IncreaseLines { get; init; }
    public int DecreaseLines { get; init; }

    /// <summary>Lines of the matching sheets that were stale when posted.</summary>
    public int PostedStaleLines { get; init; }

    /// <summary><c>Σ(SystemQty − PhysicalQty)</c> over counted lines. Signed.</summary>
    public decimal NetVarianceQty { get; init; }

    /// <summary><c>Σ|SystemQty − PhysicalQty|</c> over counted lines — the "how wrong were we" figure.</summary>
    public decimal AbsVarianceQty { get; init; }

    /// <summary><c>Σ(Variance × SnapshotUnitPrice)</c>; <c>null</c> when no counted line carries a price.</summary>
    public decimal? VarianceValue { get; init; }

    /// <summary>
    /// <c>ExactMatchLines / CountedLines × 100</c>, or <b>null</b> when <see cref="CountedLines"/> is 0.
    /// </summary>
    public decimal? LineAccuracyPercent =>
        CountedLines == 0 ? null : Math.Round(ExactMatchLines * 100m / CountedLines, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Paged variance rows plus the unbounded match count for the same predicate.</summary>
public sealed class IvStockCountVariancePage
{
    public IReadOnlyList<IvStockCountVarianceRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>Server-side sort whitelist. An unknown field falls back to the default order.</summary>
public static class IvStockCountVarianceSortFields
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IvStockCountVarianceRow.CountDate),
        nameof(IvStockCountVarianceRow.CountNo),
        nameof(IvStockCountVarianceRow.ICode),
        nameof(IvStockCountVarianceRow.IDesc),
        nameof(IvStockCountVarianceRow.WHCode),
        nameof(IvStockCountVarianceRow.LocCode),
        nameof(IvStockCountVarianceRow.LotNo),
        nameof(IvStockCountVarianceRow.IStatus),
        nameof(IvStockCountVarianceRow.SystemQty),
        nameof(IvStockCountVarianceRow.PhysicalQty),
        nameof(IvStockCountVarianceRow.Variance),
        nameof(IvStockCountVarianceRow.LineNumber)
    };
}
