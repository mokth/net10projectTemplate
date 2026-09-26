namespace ErpWeb.Core.Sales;

/// <summary>
/// Shared filter for the read-only Sales Inquiry screens (plan-salesReportsAndInquiries Phase 1).
/// Company always comes from the authenticated context and is never part of this DTO.
/// <para>
/// Dates are optional and follow the house half-open pattern: when <see cref="DateFrom"/> is set the
/// query filters <c>&gt;= DateFrom.Date</c>, and when <see cref="DateTo"/> is set it filters
/// <c>&lt; DateTo.Date.AddDays(1)</c>. When neither is set no date predicate is applied, so an
/// operational list can be run without a date window.
/// </para>
/// </summary>
public sealed class SaInquiryQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    /// <summary>Customer code filter. Applies to every inquiry.</summary>
    public string? CustCode { get; set; }

    /// <summary>Sales rep / salesman filter. Applies to every inquiry.</summary>
    public string? SalesmanCode { get; set; }

    /// <summary>Optional branch restriction. The service always scopes to the authenticated branch.</summary>
    public string? BranchCode { get; set; }

    /// <summary>Document status filter (the per-document vocabulary of the screen being queried).</summary>
    public string? Status { get; set; }

    /// <summary>CN/DN type filter (<see cref="SaCdnTypes.CreditNote"/> / <see cref="SaCdnTypes.DebitNote"/>).</summary>
    public string? Type { get; set; }

    /// <summary>
    /// Free-text search over the document number / reference / remark fields a screen exposes. Optional.
    /// </summary>
    public string? SearchText { get; set; }

    /// <summary>Server-side paging. The grid's data source supplies these; the export supplies 0 / cap.</summary>
    public int Skip { get; set; }

    /// <summary>Server-side page size. 0 means the service default.</summary>
    public int Take { get; set; }

    // ---- Sales Monitor filters (plan-salesDecisionSupport.prompt.md, Phase A) -------------------
    // Only the monitor screens read these; the Phase-1 inquiry methods ignore them, so an extra
    // property here cannot change an existing screen's predicate.

    /// <summary>
    /// The company-local "as of" date every derived age/expiry figure is measured against. The page
    /// supplies it from <c>ICurrentDateService</c> (never <c>DateTime.Today</c>); a monitor method
    /// falls back to <c>DateTime.Today</c> when it is null.
    /// </summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>A1: keep only lines whose expected delivery date is strictly before <see cref="AsOfDate"/>.</summary>
    public bool OverdueOnly { get; set; }

    /// <summary>A1/A3: restrict to one monitoring bucket label (<see cref="SaMonitorBuckets"/>).</summary>
    public string? Bucket { get; set; }

    /// <summary>A2: restrict to one <see cref="SaDoInvoiceStates"/> value.</summary>
    public string? InvoiceState { get; set; }

    /// <summary>A2: keep only lines that still owe billing (the default for that screen).</summary>
    public bool PendingOnly { get; set; }

    /// <summary>A3: keep only quotations the lazy-expiry sweep is about to expire.</summary>
    public bool ExpiringSoonOnly { get; set; }

    /// <summary>A3: keep only quotations already past their validity date.</summary>
    public bool ExpiredOnly { get; set; }
}

/// <summary>A paged, server-side materialised result.</summary>
public sealed class SaInquiryPage<T>
{
    public IReadOnlyList<T> Rows { get; set; } = [];
    public int TotalCount { get; set; }
}

/// <summary>
/// One movement of the customer's transaction log — the union of QT / SO / DO / INV / CN / DN for one
/// customer. Every status is included (it is a movement log, not a sales total) and the caller filters
/// by status if they want a subset.
/// </summary>
public sealed class SaCustomerTransactionRow
{
    /// <summary><c>QT</c> / <c>SO</c> / <c>DO</c> / <c>INV</c> / <c>CN</c> / <c>DN</c>.</summary>
    public string DocType { get; set; } = string.Empty;

    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }
    public string? SalesRep { get; set; }
    public string? Status { get; set; }

    /// <summary>Header total for the document (the stored <c>TotAmnt</c>, never re-totalled from lines).</summary>
    public decimal TotAmnt { get; set; }
}

/// <summary>
/// One month of the customer's POSTED sales history — invoice total plus CN/DN netting, exactly the
/// <c>Net = Invoice + DN − CN</c> convention (CN/DN are stored positive and netted once).
/// </summary>
public sealed class SaCustomerSalesHistoryRow
{
    public int Year { get; set; }
    public int Month { get; set; }

    public int InvoiceCount { get; set; }
    public decimal InvoiceTotal { get; set; }
    public int CreditNoteCount { get; set; }
    public decimal CreditNoteTotal { get; set; }
    public int DebitNoteCount { get; set; }
    public decimal DebitNoteTotal { get; set; }
    public decimal NetSales { get; set; }

    /// <summary>Display period <c>yyyy-MM</c>.</summary>
    public string Period => $"{Year:0000}-{Month:00}";
}

/// <summary>
/// One live (<c>IsCurrent</c>) quotation revision with its validity window. Expiry is
/// <c>Today &gt; ValidUntil</c>, computed server-side from the system date — never persisted.
/// </summary>
public sealed class SaQtStatusRow
{
    public string QtNo { get; set; } = string.Empty;
    public short Rev { get; set; }
    public DateTime QtDate { get; set; }
    public DateTime ValidUntil { get; set; }
    public string? Status { get; set; }
    public string? ConversionStatus { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }
    public string? SalesRep { get; set; }
    public decimal TotAmnt { get; set; }

    /// <summary>True when the system date is past the validity limit. Accepted quotations never auto-expire.</summary>
    public bool IsExpired { get; set; }
}

/// <summary>
/// One line of a current Sales Order, with the persisted quantity rollups. <see cref="BalanceQty"/> is
/// the stored outstanding quantity (maintained by <c>SaSoQty</c> — never recomputed here);
/// <see cref="WrittenOffQty"/> is shown but is never outstanding.
/// </summary>
public sealed class SaSoOutstandingRow
{
    public string SoNo { get; set; } = string.Empty;
    public short Rev { get; set; }
    public DateTime SoDate { get; set; }
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
    public decimal ShippedQty { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public decimal BalanceQty { get; set; }
    public decimal WrittenOffQty { get; set; }
    public DateTime? DeliveryDate { get; set; }
}

/// <summary>One line of a Delivery Order with its billing state and its SO / invoice references.</summary>
public sealed class SaDoStatusRow
{
    public string DoNo { get; set; } = string.Empty;
    public DateTime DoDate { get; set; }
    public string? Status { get; set; }
    public string? BillingStatus { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }
    public string? SalesRep { get; set; }
    public decimal TotAmnt { get; set; }

    public short Line { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal Qty { get; set; }
    public string? SoNo { get; set; }
    public string? InvNo { get; set; }
}

/// <summary>
/// One document-relationship edge: SO→DO and DO→INV from the allocation ledger
/// (<c>SaDocApplication</c>) plus INV→DO and INV→SO from the invoice detail's persisted
/// <c>DoNo</c> / <c>SoNo</c> links. The ledger is authoritative for the allocation quantity; the
/// invoice links identify the source documents.
/// </summary>
public sealed class SaDocumentRelationshipRow
{
    /// <summary><c>SO→DO</c> / <c>DO→INV</c> / <c>INV→DO</c> / <c>INV→SO</c>.</summary>
    public string Relation { get; set; } = string.Empty;

    public string? SourceDocNo { get; set; }
    public string? TargetDocNo { get; set; }
    public decimal Qty { get; set; }
    public string? RelatedSoNo { get; set; }
    public DateTime? DocDate { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }
}

/// <summary>One credit/debit note row, both types combined with a <c>Type</c> discriminator.</summary>
public sealed class SaCdnInquiryRow
{
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? InvNo { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }
    public string? SalesRep { get; set; }
    public string? RefNo { get; set; }
    public string? Remarks { get; set; }
    public decimal TotAmnt { get; set; }
}

/// <summary>
/// One ERP document's current e-Invoice status, resolved from its own <c>IRBM*</c> columns and — when a
/// submission exists — the latest authoritative <c>EInvDocSubmission</c> row (highest <c>ID</c>).
/// A document with no submission row and a blank <c>IRBMStatus</c> is shown as "not submitted".
/// </summary>
public sealed class SaEInvoiceStatusRow
{
    /// <summary><c>INV</c> / <c>CN</c> / <c>DN</c>.</summary>
    public string DocType { get; set; } = string.Empty;

    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string? CustCode { get; set; }
    public string? CustName { get; set; }

    /// <summary>The ERP document's own <c>IRBMStatus</c>, raw.</summary>
    public string? IrbmStatus { get; set; }

    /// <summary><see cref="IrbmStatus"/> normalised through <see cref="EInvoiceStatuses.Normalize"/>.</summary>
    public string NormalizedStatus { get; set; } = string.Empty;

    public string? IrbmUuid { get; set; }
    public string? IrbmSubmitId { get; set; }

    /// <summary>When MyInvois accepted the submission (persisted <c>IrbmSentOn</c>), null if never sent.</summary>
    public DateTime? IrbmSentOn { get; set; }

    /// <summary>The document's stored header total (never re-totalled from lines).</summary>
    public decimal TotAmnt { get; set; }

    /// <summary>The latest submission row's status, or null when no submission row exists.</summary>
    public string? LatestSubmissionStatus { get; set; }

    /// <summary>The latest submission row's issue timestamp, or null when no submission row exists.</summary>
    public DateTime? LatestSubmittedOn { get; set; }

    /// <summary>True when a submission row exists for this document (it has been submitted at least once).</summary>
    public bool IsSubmitted { get; set; }

    /// <summary>True when there is no submission row AND no IRBM value at all — never submitted.</summary>
    public bool IsNotSubmitted { get; set; }

    /// <summary>
    /// True when a submission row exists but its status is not a usable <see cref="EInvoiceStatuses"/>
    /// value (blank or unmapped). Distinct from "not submitted", which means no row at all.
    /// </summary>
    public bool IsUnknownStatus { get; set; }

    /// <summary>Precomputed display label the grid renders, so the screen and the CSV say the same thing.</summary>
    public string StatusLabel { get; set; } = string.Empty;
}

/// <summary>One attempt of a document's e-Invoice submission history, oldest first.</summary>
public sealed class SaEInvoiceSubmissionRow
{
    public int Id { get; set; }
    public DateTime? SubmittedOn { get; set; }
    public string? Status { get; set; }
    public string? SubmissionUuid { get; set; }
    public string? Uuid { get; set; }
    public string? LongId { get; set; }
}

/// <summary>
/// One reconciliation finding between an ERP document's <c>IRBM*</c> fields and its latest authoritative
/// submission row. "not submitted" is distinct from a status/uuid disagreement.
/// </summary>
public sealed class SaEInvoiceReconciliationRow
{
    public string DocType { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public string? ErpStatus { get; set; }
    public string? ErpUuid { get; set; }
    public string? RegistryStatus { get; set; }
    public string? RegistryUuid { get; set; }

    /// <summary>Human-readable finding, e.g. <c>match</c> / <c>status differs</c> / <c>not submitted</c>.</summary>
    public string Finding { get; set; } = string.Empty;
}
