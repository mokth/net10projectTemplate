namespace ErpWeb.Core.Sales;

/// <summary>
/// Dimension a Sales Summary grid groups by. Each dimension except <see cref="Source"/> reads the
/// invoice's own POSTED snapshot, so it stays historically correct; <see cref="Source"/> joins the
/// live customer master and is therefore <b>current</b> attribution (see <c>CustSource</c>).
/// </summary>
public enum SaSalesSummaryDimension
{
    Salesman = 0,
    Customer = 1,
    Area = 2,
    Industry = 3,
    Channel = 4,
    CustType = 5,
    CustGroup = 6,
    Source = 7
}

/// <summary>
/// Shared filter for the Phase 1 analysis queries. Company always comes from the authenticated context
/// and is never part of this DTO (R7).
/// <para>
/// <see cref="DateFrom"/> and <see cref="DateTo"/> are required and treated as an inclusive calendar
/// range using the repository's half-open pattern (<c>&gt;= DateFrom.Date</c> and
/// <c>&lt; DateTo.Date.AddDays(1)</c>), so an invoice late on the DateTo day is never lost.
/// </para>
/// </summary>
public sealed class SaSalesAnalysisQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    /// <summary>Sales Summary grouping key. Ignored by the attainment and conversion queries.</summary>
    public SaSalesSummaryDimension Dimension { get; set; } = SaSalesSummaryDimension.Salesman;

    public string? SalesmanCode { get; set; }
    public string? CustCode { get; set; }
    public string? AreaCode { get; set; }
    public string? IndustryCode { get; set; }
    public string? ChannelCode { get; set; }
    public string? CustType { get; set; }
    public string? CustGroupCode { get; set; }
    public string? CustSource { get; set; }

    /// <summary>
    /// Optional branch restriction. Applies to Sales Summary (and its period chips) only — attainment
    /// is always company-wide because targets carry no branch (R2).
    /// </summary>
    public string? BranchCode { get; set; }

    /// <summary>QT conversion: also break the buckets down per sales rep.</summary>
    public bool GroupQtBySalesRep { get; set; }
}

public sealed class SaSalesSummaryRow
{
    /// <summary>The grouping key (code, or the QT/SO-free dimension value). Blank keys render as "(blank)".</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Master description when the dimension has one (customer / salesman / source name).</summary>
    public string? Description { get; init; }

    public int InvoiceCount { get; init; }

    /// <summary>Sum of <c>SaInvoice.TotAmnt</c> — the invoice total, not accounting revenue (R6).</summary>
    public decimal InvoiceTotal { get; init; }

    public decimal GrossAmount { get; init; }
    public decimal TaxAmount { get; init; }

    /// <summary>Share of the filtered <see cref="SaSalesSummaryResult.Totals"/> InvoiceTotal, 0..100.</summary>
    public decimal SharePercent { get; init; }
}

public sealed class SaSalesSummaryResult
{
    public IReadOnlyList<SaSalesSummaryRow> Rows { get; init; } = [];
    public SaSalesPeriodTotals Totals { get; init; } = new();
}

/// <summary>
/// Period KPI chips. CN/DN are POSITIVE stored totals that are netted here exactly once
/// (<c>Net = Invoice + DN − CN</c>) — do not negate them again (R3).
/// <para>
/// The chips honour the document-level filters the CN/DN tables actually carry (date, branch, customer,
/// salesman) but not the invoice-only dimensional filters, because <c>SaCdn</c> stores no
/// area/industry/channel/type/group snapshot. The dimensional grid is posted-invoice-only.
/// </para>
/// </summary>
public sealed class SaSalesPeriodTotals
{
    public int InvoiceCount { get; init; }
    public decimal InvoiceTotal { get; init; }
    public decimal GrossAmount { get; init; }
    public decimal TaxAmount { get; init; }

    public int CreditNoteCount { get; init; }
    public decimal CreditNoteTotal { get; init; }

    public int DebitNoteCount { get; init; }
    public decimal DebitNoteTotal { get; init; }

    public decimal NetSalesAmount { get; init; }
}

/// <summary>
/// One row of the target-vs-actual grid. Attainment is company-wide: <see cref="ActualAmount"/> sums
/// POSTED invoices by salesman and <see cref="TargetAmount"/> sums the full monthly targets of every
/// month the range touches — partial months are never prorated (R1/R2).
/// </summary>
public sealed class SaSalesRepAttainmentRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }

    public decimal ActualAmount { get; init; }
    public decimal TargetAmount { get; init; }

    /// <summary>
    /// <c>Actual / Target × 100</c>, or <c>null</c> (rendered N/A) when the target is zero — a missing
    /// or explicitly zero target must never produce Infinity, NaN or a misleading 0% (M2).
    /// </summary>
    public decimal? AttainmentPercent { get; init; }

    /// <summary>
    /// Display form of <see cref="AttainmentPercent"/>: "N/A" when there is no target to measure
    /// against (M2). Kept next to the value so every surface — grid and CSV — says the same thing.
    /// </summary>
    public string AttainmentDisplay =>
        AttainmentPercent is decimal value ? value.ToString("0.##") : "N/A";

    /// <summary>Number of intersecting months that actually carried a target row (informational).</summary>
    public int MonthsWithTarget { get; init; }
}

public sealed class SaQtConversionBucket
{
    public int Count { get; init; }
    public decimal Amount { get; init; }
}

public sealed class SaQtLostReasonRow
{
    /// <summary>Blank / null reasons are grouped under "(blank)" so nothing silently disappears.</summary>
    public string Reason { get; init; } = string.Empty;
    public int Count { get; init; }
    public decimal Amount { get; init; }
}

public sealed class SaQtConversionBySalesRepRow
{
    public string SalesRep { get; init; } = string.Empty;
    public SaQtConversionBucket Total { get; init; } = new();
    public SaQtConversionBucket Open { get; init; } = new();
    public SaQtConversionBucket Won { get; init; } = new();
    public SaQtConversionBucket Lost { get; init; } = new();
    public SaQtConversionBucket Expired { get; init; } = new();
    public SaQtConversionBucket Cancelled { get; init; } = new();

    /// <summary>Same rule as the overall Win Rate; null when Won + Lost is zero.</summary>
    public decimal? WinRatePercent { get; init; }
}

/// <summary>
/// Quotation conversion summary for CURRENT revisions only. Amounts are the document header
/// <c>TotAmnt</c>; lines are never re-totalled here (M5). Won is the state the converter writes —
/// <c>Status == CLOSED</c> and <c>ClosedReason == CONVERTED</c> — not <c>ConversionStatus</c> alone (M4).
/// </summary>
public sealed class SaQtConversionResult
{
    public SaQtConversionBucket Total { get; init; } = new();
    public SaQtConversionBucket Open { get; init; } = new();
    public SaQtConversionBucket Won { get; init; } = new();
    public SaQtConversionBucket Lost { get; init; } = new();
    public SaQtConversionBucket Expired { get; init; } = new();
    public SaQtConversionBucket Cancelled { get; init; } = new();

    /// <summary><c>Won / (Won + Lost) × 100</c>; null when the denominator is zero.</summary>
    public decimal? WinRatePercent { get; init; }

    public IReadOnlyList<SaQtLostReasonRow> LostReasons { get; init; } = [];
    public IReadOnlyList<SaQtConversionBySalesRepRow> BySalesRep { get; init; } = [];
}
