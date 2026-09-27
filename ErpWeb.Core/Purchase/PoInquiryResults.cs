namespace ErpWeb.Core.Purchase;

/// <summary>
/// Shared filter for Purchase Inquiry Phase 1. Company always comes from the authenticated context.
/// Dates follow the house half-open pattern: <c>&gt;= DateFrom.Date</c>, <c>&lt; DateTo.Date.AddDays(1)</c>.
/// </summary>
public sealed class PoInquiryQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    /// <summary>Supplier / vendor code filter.</summary>
    public string? SuppCode { get; set; }

    /// <summary>Buyer code filter (PO screens).</summary>
    public string? BuyerCode { get; set; }

    public string? Status { get; set; }
    public string? Type { get; set; }
    public string? SearchText { get; set; }
    public string? BranchCode { get; set; }

    /// <summary>Item code filter (Phase 2 screens). Empty = ALL.</summary>
    public string? ItemCode { get; set; }

    /// <summary>Currency filter (Price History). Empty = ALL.</summary>
    public string? Currency { get; set; }

    /// <summary>Warehouse filter — Matching/Delivery use <c>PoOrderDetail.ToWarehouse</c>. Empty = ALL.</summary>
    public string? Warehouse { get; set; }

    /// <summary>
    /// Workbench exception preset (<see cref="PoInquiryWorkbenchPresets"/>). Applied in the
    /// canonical service pipeline after base filters / ACCESS / revision rules.
    /// </summary>
    public string? WorkbenchPreset { get; set; }

    /// <summary>Company-local "today" for overdue ETA. UI stamps from <c>ICurrentDateService</c>.</summary>
    public DateTime? AsOfDate { get; set; }

    public int Skip { get; set; }
    public int Take { get; set; }
}

/// <summary>Workbench preset keys — applied only inside <see cref="PoPurchaseInquiryService"/>.</summary>
public static class PoInquiryWorkbenchPresets
{
    public const string AllOpen = "ALL_OPEN";
    public const string Overdue = "OVERDUE";
    public const string Partial = "PARTIAL";
    public const string Unbilled = "UNBILLED";
    public const string Unconverted = "UNCONVERTED";
    public const string SbNotSubmitted = "SB_NOT_SUBMITTED";
    public const string SbInvalid = "SB_INVALID";

    // Phase 2 Matching
    public const string ReceivedNotInvoiced = "RECEIVED_NOT_INVOICED";
    public const string PoOutstanding = "PO_OUTSTANDING";
    public const string PartiallyMatched = "PARTIALLY_MATCHED";
    public const string FullyMatched = "FULLY_MATCHED";
    public const string PriceMismatch = "PRICE_MISMATCH";
    public const string InvoicedNotReceived = "INVOICED_NOT_RECEIVED";
    public const string All = "ALL";
}

/// <summary>Matching status values — exclusive priority order in the service.</summary>
public static class PoMatchingStatuses
{
    public const string InvoicedNotReceived = "INVOICED_NOT_RECEIVED";
    public const string PriceMismatch = "PRICE_MISMATCH";
    public const string ReceivedNotInvoiced = "RECEIVED_NOT_INVOICED";
    public const string PoOutstanding = "PO_OUTSTANDING";
    public const string PartiallyMatched = "PARTIALLY_MATCHED";
    public const string FullyMatched = "FULLY_MATCHED";
    public const string Exception = "EXCEPTION";
}

public sealed class PoInquiryPage<T>
{
    public IReadOnlyList<T> Rows { get; set; } = [];
    public int TotalCount { get; set; }
}

/// <summary>Latest-revision PO line with persisted quantity rollups (Step 0.5B/C).</summary>
public sealed class PoOrderOutstandingRow
{
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public DateTime? PoDate { get; set; }
    public string? Status { get; set; }
    public string? VendCode { get; set; }
    public string? VendName { get; set; }
    public string? Buyer { get; set; }
    public short Line { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal PoPurQty { get; set; }
    public decimal RecvQty { get; set; }
    public decimal ReturnQty { get; set; }
    /// <summary>Persisted outstanding delivery qty — shown as-is, never recomputed.</summary>
    public decimal BalanceQty { get; set; }
    public decimal InvoicedQty { get; set; }
    /// <summary>Derived — <see cref="PoOrderCalc.ComputeInvoiceable"/>.</summary>
    public decimal InvoiceableQty { get; set; }
    /// <summary>
    /// Outstanding commercial net allocated from persisted <c>NetAmount</c> by BalanceQty/PoPurQty.
    /// Evidence: line NetAmount is post-discount commercial net (PoOrderCalc.ApplyTwoLevelDiscount);
    /// TotAmnt on PO uses Sum(NetAmount+TaxAmount). Do not use BalanceQty*PoUnitPrice.
    /// </summary>
    public decimal OutstandingValue { get; set; }
    public DateTime? EtaDate { get; set; }
    /// <summary>Days past ETA when overdue; otherwise null.</summary>
    public int? DaysOverdue { get; set; }
    public string? ToWarehouse { get; set; }
}

/// <summary>
/// PO outstanding chips — computed from base filters + latest-revision rules (not WorkbenchPreset).
/// Preset narrows the grid; chips remain the exception dashboard for the applied inquiry filters.
/// </summary>
public sealed class PoOrderOutstandingSummary
{
    public int OpenLineCount { get; set; }
    public int OverdueLineCount { get; set; }
    public int PartialLineCount { get; set; }
    public int UnbilledLineCount { get; set; }
    public decimal OutstandingValue { get; set; }
}

/// <summary>PR line with derived consumption (Step 0.5A — LivePoConsumedForPrAsync semantics).</summary>
public sealed class PoPrStatusRow
{
    public string PrNo { get; set; } = string.Empty;
    public DateTime CreateDt { get; set; }
    public string? HeaderStatus { get; set; }
    public string? Requester { get; set; }
    public string? DeptCode { get; set; }
    public short Line { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal PurchaseQty { get; set; }
    /// <summary>Derived — sum of live PO PoPurQty for this PR line.</summary>
    public decimal ConsumedQty { get; set; }
    /// <summary>Derived — PurchaseQty − ConsumedQty.</summary>
    public decimal RemainingQty { get; set; }
    public string? VendorCd { get; set; }
    public string? ToWarehouse { get; set; }
    public DateTime? EtaDt { get; set; }
    public string? LineStatus { get; set; }
    /// <summary>Distinct live (non-cancelled) PO numbers linked to this PR line, comma-separated.</summary>
    public string? LinkedPoNos { get; set; }
}

public sealed class PoPrStatusSummary
{
    public int UnconvertedLineCount { get; set; }
    public decimal TotalRemainingQty { get; set; }
}

public sealed class PoSupplierPurchaseHistoryTotals
{
    public int InvoiceCount { get; set; }
    public decimal InvoiceTotal { get; set; }
    public int CreditNoteCount { get; set; }
    public decimal CreditNoteTotal { get; set; }
    public int DebitNoteCount { get; set; }
    public decimal DebitNoteTotal { get; set; }
    public decimal NetPurchase { get; set; }
}

public sealed class PoSupplierTransactionRow
{
    public string DocType { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string? SuppCode { get; set; }
    public string? SuppName { get; set; }
    public string? Buyer { get; set; }
    public string? Status { get; set; }
    public decimal TotAmnt { get; set; }
    public short? PoRelNo { get; set; }
    public string? Extra { get; set; }
}

public sealed class PoSupplierPurchaseHistoryRow
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int InvoiceCount { get; set; }
    public decimal InvoiceTotal { get; set; }
    public int CreditNoteCount { get; set; }
    public decimal CreditNoteTotal { get; set; }
    public int DebitNoteCount { get; set; }
    public decimal DebitNoteTotal { get; set; }
    public decimal NetPurchase { get; set; }
    public string Period => $"{Year:0000}-{Month:00}";
}

public sealed class PoInvoiceInquiryRow
{
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? VendorCode { get; set; }
    public string? VendorName { get; set; }
    public decimal TotAmnt { get; set; }
    public decimal Taxes { get; set; }
    public string? Currency { get; set; }
    public string? ExternalDocNo { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? PostedDate { get; set; }
}

public sealed class PoCdnInquiryRow
{
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? VendorCode { get; set; }
    public string? VendorName { get; set; }
    public string? InvNo { get; set; }
    public decimal TotAmnt { get; set; }
    public bool ReturnStock { get; set; }
    public string? VrBatchNo { get; set; }
    public string? TaxGrCode { get; set; }
}

public sealed class PoDocumentRelationshipRow
{
    public string Relation { get; set; } = string.Empty;
    public string? SourceDocNo { get; set; }
    public string? TargetDocNo { get; set; }
    public decimal Qty { get; set; }
    public string? RelatedPoNo { get; set; }
    public string? RelatedPrNo { get; set; }
    public DateTime? DocDate { get; set; }
    public string? VendCode { get; set; }
    public string? VendName { get; set; }
}

public sealed class PoSbEInvoiceStatusRow
{
    public string DocType { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string? VendorCode { get; set; }
    public string? VendorName { get; set; }
    public string? IrbmStatus { get; set; }
    public string NormalizedStatus { get; set; } = string.Empty;
    public string? IrbmUuid { get; set; }
    public string? IrbmSubmitId { get; set; }
    public string? LatestSubmissionStatus { get; set; }
    public DateTime? LatestSubmittedOn { get; set; }
    public bool IsSubmitted { get; set; }
    public bool IsNotSubmitted { get; set; }
    public bool IsUnknownStatus { get; set; }
    public string StatusLabel { get; set; } = string.Empty;
}

public sealed class PoSbEInvoiceSummary
{
    public int TotalCount { get; set; }
    public int NotSubmittedCount { get; set; }
    public int InvalidCount { get; set; }
}

public sealed class PoSbEInvoiceSubmissionRow
{
    public int Id { get; set; }
    public DateTime? SubmittedOn { get; set; }
    public string? Status { get; set; }
    public string? SubmissionUuid { get; set; }
    public string? Uuid { get; set; }
    public string? LongId { get; set; }
}

public sealed class PoSbEInvoiceReconciliationRow
{
    public string DocType { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public string? ErpStatus { get; set; }
    public string? ErpUuid { get; set; }
    public string? RegistryStatus { get; set; }
    public string? RegistryUuid { get; set; }
    public string Finding { get; set; } = string.Empty;
}

// ============================ Phase 2 rows ============================

/// <summary>Posted purchase invoice line price history (mirrors Sales price history).</summary>
public sealed class PoPurchasePriceHistoryRow
{
    public DateTime DocDate { get; set; }
    public string DocNo { get; set; } = string.Empty;
    public short Line { get; set; }
    public string? VendorCode { get; set; }
    public string? VendorName { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public decimal Qty { get; set; }
    public string? Uom { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ItemDiscAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal? NetUnitPrice { get; set; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; }
    public decimal? LocalAmount { get; set; }
}

public sealed class PoPurchasePriceHistorySummary
{
    public decimal? LatestNetUnitPrice { get; set; }
    public decimal? PreviousNetUnitPrice { get; set; }
    public decimal? MinNetUnitPrice { get; set; }
    public decimal? MaxNetUnitPrice { get; set; }
    public decimal? AvgNetUnitPrice { get; set; }
    public int SampleCount { get; set; }
}

/// <summary>PO-line matching workbench row (one row per latest-revision PO line).</summary>
public sealed class PoMatchingRow
{
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public DateTime? PoDate { get; set; }
    public string? VendCode { get; set; }
    public string? VendName { get; set; }
    public string? Buyer { get; set; }
    public string? CurCode { get; set; }
    public short Line { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? PurchaseUom { get; set; }
    public decimal PoUnitPrice { get; set; }
    public decimal PoPurQty { get; set; }
    public decimal RecvQty { get; set; }
    public decimal ReturnQty { get; set; }
    public decimal NetReceivedQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public decimal InvoiceableQty { get; set; }
    public decimal OverInvoicedQty { get; set; }
    public decimal BalanceQty { get; set; }
    public decimal PoAmount { get; set; }
    /// <summary>Inquiry-only proportional allocation — not GR stock valuation.</summary>
    public decimal ReceivedAmount { get; set; }
    public decimal InvoiceAmount { get; set; }
    /// <summary>Diagnostic only — sum of posted INV line qty; does not drive status.</summary>
    public decimal InvoiceQtyPosted { get; set; }
    public decimal? EffectiveInvoiceUnitPrice { get; set; }
    public decimal QtyVariance { get; set; }
    public decimal? PriceVariance { get; set; }
    public decimal? PriceVariancePct { get; set; }
    public string MatchingStatus { get; set; } = string.Empty;
    public bool PriceMismatch { get; set; }
    public bool OverInvoiced { get; set; }
    public bool HasMultipleInvoices { get; set; }
    public bool HasMultipleInvoicePrices { get; set; }
    public bool HasMultipleGRs { get; set; }
    public string? LatestInvoiceDocNo { get; set; }
    public int? SingleGrBatchNo { get; set; }
    public DateTime? EtaDate { get; set; }
    public string? ToWarehouse { get; set; }
}

public sealed class PoMatchingSummary
{
    public int ReceivedNotInvoicedCount { get; set; }
    public int PoOutstandingCount { get; set; }
    public int PartiallyMatchedCount { get; set; }
    public int FullyMatchedCount { get; set; }
    public int PriceMismatchCount { get; set; }
    public int InvoicedNotReceivedCount { get; set; }
}

/// <summary>Supplier-level ETA delivery performance (latest RecvDate vs EtaDate).</summary>
public sealed class PoDeliveryPerformanceRow
{
    public string? SuppCode { get; set; }
    public string? SuppName { get; set; }
    public int PurchaseOrderCount { get; set; }
    public int PoLinesEvaluated { get; set; }
    public decimal OrderedQty { get; set; }
    public decimal ReceivedQty { get; set; }
    public int OnTimeLines { get; set; }
    public int LateLines { get; set; }
    public decimal? OnTimePct { get; set; }
    public decimal? AvgDaysLate { get; set; }
    public int? MaxDaysLate { get; set; }
}
