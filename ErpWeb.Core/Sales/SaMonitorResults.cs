namespace ErpWeb.Core.Sales;

/// <summary>
/// Result shapes for the Sales Monitor screens (plan-salesDecisionSupport.prompt.md, Phase A).
///
/// <para>
/// Every figure here is either (a) a persisted column rendered as-is, or (b) a clearly-labelled derived
/// ratio. <see cref="SaMonitorBuckets"/> is UI/monitoring vocabulary only — it is never persisted and
/// never modifies transactional processing.
/// </para>
/// </summary>
public static class SaMonitorLimits
{
    /// <summary>
    /// e-Invoice "pending too long" monitoring window. A <b>presentation threshold</b> only — it names
    /// what the action queue highlights and never gates a submission or changes a document (plan §3 1b).
    /// </summary>
    public const int EInvoicePendingDays = 3;

    /// <summary>Quotation "expiring soon" window in days (inclusive), a monitoring vocabulary.</summary>
    public const int QtExpiringSoonDays = 7;
}

/// <summary>
/// Ageing / expiry bucket vocabulary (monitoring only). The boundary labels are part of the UI
/// contract, so they are produced in one place and never re-spelled by a page or a CSV writer.
/// </summary>
public static class SaMonitorBuckets
{
    public const string Days0To30 = "0-30";
    public const string Days31To60 = "31-60";
    public const string Days61To90 = "61-90";
    public const string Over90 = ">90";

    /// <summary>Ageing buckets in display order.</summary>
    public static readonly IReadOnlyList<string> AgeBuckets = [Days0To30, Days31To60, Days61To90, Over90];

    /// <summary>SO age bucket. The age of the <b>order</b> — never delivery ageing.</summary>
    public static string AgeBucket(int ageDays) => ageDays switch
    {
        <= 30 => Days0To30,
        <= 60 => Days31To60,
        <= 90 => Days61To90,
        _ => Over90
    };

    public const string Expired = "EXPIRED";
    public const string Days0To7 = "0-7";
    public const string Days8To30 = "8-30";
    public const string Over30 = ">30";

    /// <summary>Expiry buckets in display order.</summary>
    public static readonly IReadOnlyList<string> ExpiryBuckets = [Expired, Days0To7, Days8To30, Over30];

    /// <summary>
    /// Quotation expiry bucket. <paramref name="expired"/> is authoritative (a quotation may be expired
    /// or expiring soon by status, not only by date).
    /// </summary>
    public static string ExpiryBucket(int daysToExpiry, bool expired)
    {
        if (expired)
        {
            return Expired;
        }

        return daysToExpiry switch
        {
            <= SaMonitorLimits.QtExpiringSoonDays => Days0To7,
            <= 30 => Days8To30,
            _ => Over30
        };
    }
}

// ============================ A1 · SO ageing ============================

/// <summary>
/// One line of a current Sales Order with its <b>order</b> age and delivery lateness.
///
/// <para>
/// <see cref="AgeDays"/> is <c>asOf − SoDate</c> — the age of the order, <b>never</b> delivery ageing.
/// <see cref="OverdueDays"/> is derived from the line's <see cref="DeliveryDate"/> only and is
/// <c>&gt; 0</c> only; a delivery due today is <b>not</b> overdue.
/// </para>
/// </summary>
public sealed class SaSoAgeingRow
{
    public string SoNo { get; set; } = string.Empty;
    public short Rev { get; set; }
    public DateTime SoDate { get; set; }

    /// <summary>Age of the order in days: <c>asOf − SoDate</c> (derived).</summary>
    public int AgeDays { get; set; }

    /// <summary>Monitoring bucket of <see cref="AgeDays"/> (derived).</summary>
    public string AgeBucket { get; set; } = string.Empty;

    public string? Status { get; set; }
    public string? FulfillmentStatus { get; set; }
    public string? BillingStatus { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }
    public string? SalesRep { get; set; }
    public decimal TotAmnt { get; set; }

    public short Line { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal OrderQty { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public decimal BalanceQty { get; set; }
    public decimal WrittenOffQty { get; set; }

    /// <summary>The line's <b>expected delivery</b> date (persisted planning column).</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>True when <see cref="DeliveryDate"/> is before <c>asOf</c> (due today is not overdue).</summary>
    public bool IsOverdueDelivery { get; set; }

    /// <summary><c>asOf − DeliveryDate</c> in days when overdue, else 0 (derived).</summary>
    public int OverdueDays { get; set; }
}

/// <summary>One ageing bucket of the <see cref="SaSoAgeingSummary"/> strip.</summary>
public sealed class SaAgeBucketRow
{
    public string Label { get; set; } = string.Empty;

    /// <summary>Distinct sales orders in the bucket.</summary>
    public int Count { get; set; }

    /// <summary>Sum of the distinct orders' stored header totals in the bucket.</summary>
    public decimal Value { get; set; }

    /// <summary>Sum of the persisted <c>BalanceQty</c> of the bucket's lines.</summary>
    public decimal Qty { get; set; }
}

/// <summary>
/// A1 header strip. <see cref="OriginalValue"/> counts each order <b>once</b> (a header rollup must never
/// be multiplied by its line count).
/// </summary>
public sealed class SaSoAgeingSummary
{
    /// <summary>Distinct current sales orders in scope.</summary>
    public int OpenCount { get; set; }

    /// <summary>Sum of the distinct orders' stored header totals.</summary>
    public decimal OriginalValue { get; set; }

    /// <summary>
    /// Sum of the persisted per-line <c>NetAmount</c>, pro-rated by the outstanding portion
    /// (<c>NetAmount × BalanceQty ÷ OrderQty</c>). A <b>derived</b> ratio of persisted columns, labelled
    /// "derived" wherever it is shown.
    /// </summary>
    public decimal OutstandingValue { get; set; }

    /// <summary>Sum of the persisted <c>BalanceQty</c> over every line in scope.</summary>
    public decimal PendingQty { get; set; }

    /// <summary>Sum of the persisted <c>BalanceQty</c> over lines whose delivery is overdue.</summary>
    public decimal OverdueQty { get; set; }

    public IReadOnlyList<SaAgeBucketRow> Buckets { get; set; } = [];
}

// ============================ A2 · Delivered not fully invoiced ============================

/// <summary>
/// Per-line invoice state on a delivery order — display vocabulary derived from
/// <c>SaDoDetail.InvNo</c> plus the header <c>BillingStatus</c> rollup (see plan §4).
/// </summary>
public static class SaDoInvoiceStates
{
    /// <summary>No invoice number on the line and the header still says <c>NONE</c>.</summary>
    public const string NoInvoice = "NO_INVOICE";

    /// <summary>No invoice number on the line but the header says <c>PARTIAL</c> — another line billed.</summary>
    public const string Partial = "PARTIAL";

    /// <summary>The line carries its invoice number.</summary>
    public const string Invoiced = "INVOICED";

    /// <summary>The header's terminal billing state — nothing further will be invoiced.</summary>
    public const string WrittenOff = "WRITTEN_OFF";

    /// <summary>The two states that still owe billing.</summary>
    public static readonly IReadOnlyList<string> PendingStates = [NoInvoice, Partial];
}

/// <summary>
/// One line of a <b>posted</b> delivery order whose billing is not complete. "Not fully invoiced" —
/// deliberately not "not invoiced": a <c>PARTIAL</c> DO legitimately carries invoice numbers on the
/// lines it has billed.
///
/// <para>
/// No invoiced-quantity or balance-to-invoice quantity is shown: <c>SaDoDetail</c> persists neither, and
/// the plan's Gate 1 rule forbids inventing a formula to manufacture one. The persisted delivered
/// <see cref="Qty"/> and the line's own <see cref="LineInvNo"/> are shown instead.
/// </para>
/// </summary>
public sealed class SaDoNotFullyInvoicedRow
{
    public string DoNo { get; set; } = string.Empty;
    public DateTime DoDate { get; set; }

    /// <summary>When the DO was posted; null while it is still a draft (such rows are never listed).</summary>
    public DateTime? PostedDate { get; set; }

    /// <summary><c>asOf − (PostedDate ?? DoDate)</c> in days (derived).</summary>
    public int DaysSinceDelivered { get; set; }

    public string? Status { get; set; }
    public string? BillingStatus { get; set; }

    /// <summary>See <see cref="SaDoInvoiceStates"/>.</summary>
    public string InvoiceState { get; set; } = string.Empty;

    public string? CustCode { get; set; }
    public string? CustName { get; set; }
    public string? SalesRep { get; set; }
    public decimal TotAmnt { get; set; }

    public short Line { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }

    /// <summary>Persisted delivered quantity on the DO line.</summary>
    public decimal Qty { get; set; }

    public string? SoNo { get; set; }

    /// <summary>The line's own persisted invoice number (<c>SaDoDetail.InvNo</c>) — blank when unbilled.</summary>
    public string? LineInvNo { get; set; }

    /// <summary>True for <see cref="SaDoInvoiceStates.NoInvoice"/> / <see cref="SaDoInvoiceStates.Partial"/>.</summary>
    public bool IsPendingInvoice { get; set; }
}

/// <summary>
/// A2 header strip. The quantity and value are summed over the <b>delivered lines that still owe
/// billing</b>, and are named exactly that — they are not a "balance to invoice" (which this screen
/// cannot derive).
/// </summary>
/// <summary>One <see cref="SaDoInvoiceStates"/> row of the A2 strip.</summary>
public sealed class SaInvoiceStateRow
{
    public string State { get; set; } = string.Empty;
    public int Count { get; set; }

    /// <summary>Sum of the persisted delivered quantity on the lines in this state.</summary>
    public decimal Qty { get; set; }

    /// <summary>Sum of the persisted per-line <c>NetAmount</c> on the lines in this state.</summary>
    public decimal Value { get; set; }

    /// <summary>True for the two states that still owe billing.</summary>
    public bool IsPending { get; set; }
}

public sealed class SaDoNotFullyInvoicedSummary
{
    /// <summary>Distinct delivery orders in scope.</summary>
    public int DoCount { get; set; }

    /// <summary>Lines in scope.</summary>
    public int LineCount { get; set; }

    /// <summary>Sum of the persisted delivered quantity on the lines that still owe billing.</summary>
    public decimal NotFullyInvoicedQty { get; set; }

    /// <summary>Sum of the persisted per-line <c>NetAmount</c> on the lines that still owe billing.</summary>
    public decimal NotFullyInvoicedValue { get; set; }

    /// <summary>The four-state split, in display order.</summary>
    public IReadOnlyList<SaInvoiceStateRow> States { get; set; } = [];
}

// ============================ A3 · Quotation expiry watch ============================

/// <summary>
/// One live (<c>IsCurrent</c>) quotation revision with its validity window. A watchlist, not an
/// exception list: <c>EXPIRED</c> is a normal state of the quotation lifecycle (the lazy sweep moves
/// <c>NEW</c>/<c>SENT</c> there, and <c>ACCEPTED</c> is never auto-expired).
/// </summary>
public sealed class SaQtExpiryRow
{
    public string QtNo { get; set; } = string.Empty;
    public short Rev { get; set; }
    public DateTime QtDate { get; set; }
    public DateTime ValidUntil { get; set; }

    /// <summary><c>ValidUntil − asOf</c> in days; negative once past validity (derived).</summary>
    public int DaysToExpiry { get; set; }

    /// <summary>Monitoring bucket of <see cref="DaysToExpiry"/> (derived).</summary>
    public string ExpiryBucket { get; set; } = string.Empty;

    public string? Status { get; set; }
    public string? ConversionStatus { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }
    public string? SalesRep { get; set; }
    public decimal TotAmnt { get; set; }

    /// <summary>Past validity. <c>ACCEPTED</c> quotations never auto-expire, so they are never flagged.</summary>
    public bool IsExpired { get; set; }

    /// <summary>
    /// The lazy-expiry sweep will expire this revision: its status is still one the sweep may move
    /// (<see cref="SaQtStatuses.Expirable"/>) and validity ends within the monitoring window.
    /// </summary>
    public bool IsExpiringSoon { get; set; }
}

/// <summary>One sales-rep row of the <see cref="SaQtExpirySummary"/> strip.</summary>
public sealed class SaSalesRepValueRow
{
    public string? SalesRep { get; set; }
    public int Count { get; set; }
    public decimal Value { get; set; }
}

/// <summary>A3 header strip. "Open" is the documented <see cref="SaQtStatuses.Revisable"/> set.</summary>
public sealed class SaQtExpirySummary
{
    public int OpenCount { get; set; }
    public decimal OpenValue { get; set; }
    public int ExpiringSoonCount { get; set; }
    public decimal ExpiringSoonValue { get; set; }
    public int ExpiredCount { get; set; }
    public decimal ExpiredValue { get; set; }

    public IReadOnlyList<SaSalesRepValueRow> BySalesRep { get; set; } = [];
}

// ============================ A4 · e-Invoice action queue ============================

/// <summary>
/// Why a document appears in the action queue. Each reason is enforceable from a single persisted field
/// comparison; a reason that would need a new interpretation of a status is a Gate 1 item and is
/// deliberately absent (the plan's "cancellation pending" was dropped on exactly that ground).
/// </summary>
public static class SaEInvoiceActionReasons
{
    public const string NotSubmitted = "Not submitted";

    /// <summary>
    /// Monitoring hint only: the submission is still pending after
    /// <see cref="SaMonitorLimits.EInvoicePendingDays"/> days. Never a business rule.
    /// </summary>
    public const string PendingTooLong = "Pending > 3 days (monitoring)";

    public const string Invalid = "INVALID";
    public const string Failed = "FAILED";
    public const string StatusMismatch = "Status mismatch";
}

/// <summary>
/// One document on the e-Invoice action queue. Read-only: the queue never calls the portal, never
/// re-submits and never writes to a document or to the submission registry.
/// </summary>
public sealed class SaEInvoiceActionRow
{
    /// <summary><c>INV</c> / <c>CN</c> / <c>DN</c>.</summary>
    public string DocType { get; set; } = string.Empty;

    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }

    /// <summary>The document's stored header total (never re-totalled from lines).</summary>
    public decimal TotAmnt { get; set; }

    /// <summary>The document's own <c>IrbmStatus</c>, raw.</summary>
    public string? IrbmStatus { get; set; }

    /// <summary><see cref="IrbmStatus"/> normalised through <see cref="EInvoiceStatuses.Normalize"/>.</summary>
    public string NormalizedStatus { get; set; } = string.Empty;

    /// <summary>The latest submission row's status, or null when no submission row exists.</summary>
    public string? LatestSubmissionStatus { get; set; }

    /// <summary>Display status — the submission registry when it exists, else the ERP column.</summary>
    public string StatusLabel { get; set; } = string.Empty;

    /// <summary>When MyInvois accepted the submission (persisted <c>IrbmSentOn</c>).</summary>
    public DateTime? IrbmSentOn { get; set; }

    /// <summary><c>asOf − IrbmSentOn</c> in days, null when never submitted (derived).</summary>
    public int? DaysSinceSubmitted { get; set; }

    /// <summary>See <see cref="SaEInvoiceActionReasons"/>. Null means nothing to do.</summary>
    public string? ActionReason { get; set; }
}

/// <summary>One status row of the <see cref="SaEInvoiceStatusBreakdown"/> strip.</summary>
public sealed class SaEInvoiceStatusCountRow
{
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Value { get; set; }
}

/// <summary>A4 header strip: how many documents sit in each state, and how many need attention.</summary>
public sealed class SaEInvoiceStatusBreakdown
{
    public int TotalCount { get; set; }
    public int NeedsActionCount { get; set; }
    public IReadOnlyList<SaEInvoiceStatusCountRow> Statuses { get; set; } = [];
}
