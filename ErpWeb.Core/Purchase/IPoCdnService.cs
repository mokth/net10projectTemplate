using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

// ─────────────────────────── Error / result ───────────────────────────

public enum PoCdnErrorKind
{
    None = 0,
    Validation,
    Concurrency,
    Confirmation,
    NotFound,
    Authorization,
    BusinessRule,
    Unexpected
}

public sealed class PoCdnOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public PoCdnErrorKind ErrorKind { get; init; }
    public string? DocNo { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public bool RequiresConfirmation { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public PoCdnDocument? Document { get; init; }
    public PoCdnVendorDefaults? VendorDefaults { get; init; }
    public PoCdnListPage? ListPage { get; init; }
    public IReadOnlyList<PoCdnPostingItemResult> Posting { get; init; } = [];
    public IReadOnlyList<PoCdnItemLookupRow> Items { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IReadOnlyList<PoCdnVendorLookupRow> Vendors { get; init; } = [];
    public IReadOnlyList<PoCdnTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> PayCodes { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> Departments { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> Projects { get; init; } = [];
    public IReadOnlyList<PoCdnReasonCodeOption> ReasonCodes { get; init; } = [];
    public IReadOnlyList<PoCdnInvoicePickerRow> InvoicePickerRows { get; init; } = [];
    public IReadOnlyList<PoCdnInvoiceLinePickerRow> InvoiceLinePickerRows { get; init; } = [];

    /// <summary>Reservation breakdown for a posted invoice (screen indicator / report).</summary>
    public PoCdnInvoiceReservationSummary? Reservations { get; init; }

    /// <summary>Report-only reservation page.</summary>
    public PoCdnReservationReportPage? ReservationReport { get; init; }

    /// <summary>True when a vendor is inactive or suspended at POST time (C15) — a warning, not a block.</summary>
    public bool VendorWarning { get; init; }
    public string? VendorWarningMessage { get; init; }

    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static PoCdnOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None };

    public static PoCdnOperationResult OkSaved(string docNo) =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None, DocNo = docNo };

    public static PoCdnOperationResult OkDocument(PoCdnDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = PoCdnErrorKind.None,
            Document = document,
            DocNo = document.DocNo
        };

    public static PoCdnOperationResult OkList(PoCdnListPage page) =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None, ListPage = page };

    public static PoCdnOperationResult OkLookups(
        IReadOnlyList<PoCdnItemLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses,
        IReadOnlyList<PoCdnVendorLookupRow> vendors,
        IReadOnlyList<PoCdnTaxGroupLookupRow> taxGroups,
        IReadOnlyList<IvCodeLookupRow> payCodes,
        IReadOnlyList<IvCodeLookupRow> departments,
        IReadOnlyList<IvCodeLookupRow> projects,
        IReadOnlyList<PoCdnReasonCodeOption> reasonCodes) =>
        new()
        {
            Succeeded = true,
            ErrorKind = PoCdnErrorKind.None,
            Items = items,
            Warehouses = warehouses,
            Vendors = vendors,
            TaxGroups = taxGroups,
            PayCodes = payCodes,
            Departments = departments,
            Projects = projects,
            ReasonCodes = reasonCodes
        };

    public static PoCdnOperationResult OkDefaults(PoCdnVendorDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None, VendorDefaults = defaults };

    public static PoCdnOperationResult OkRate(decimal rate, bool valid) =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None, CurrRate = rate, CurrRateValid = valid };

    /// <summary>
    /// C17: a currency that cannot be resolved must fail closed *and* carry the reason. Returning
    /// Succeeded = true while dropping the message let the caller proceed on a zero rate.
    /// </summary>
    public static PoCdnOperationResult FailRate(string message) =>
        new()
        {
            Succeeded = false,
            ErrorKind = PoCdnErrorKind.Validation,
            ErrorMessage = message,
            CurrRate = 0m,
            CurrRateValid = false
        };

    public static PoCdnOperationResult OkInvoicePicker(IReadOnlyList<PoCdnInvoicePickerRow> rows) =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None, InvoicePickerRows = rows };

    public static PoCdnOperationResult OkInvoiceLines(IReadOnlyList<PoCdnInvoiceLinePickerRow> rows) =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None, InvoiceLinePickerRows = rows };

    public static PoCdnOperationResult OkReservations(PoCdnInvoiceReservationSummary summary) =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None, Reservations = summary };

    public static PoCdnOperationResult OkReservationReport(PoCdnReservationReportPage page) =>
        new() { Succeeded = true, ErrorKind = PoCdnErrorKind.None, ReservationReport = page };

    public static PoCdnOperationResult OkPosting(
        IReadOnlyList<PoCdnPostingItemResult> posting,
        bool vendorWarning = false,
        string? vendorWarningMessage = null)
    {
        var ok = posting.Count(x => x.Succeeded);
        var fail = posting.Count - ok;
        var attempted = posting.Count(x =>
            !string.Equals(x.Outcome, "Not attempted", StringComparison.OrdinalIgnoreCase));
        var summary = fail == 0
            ? null
            : string.Join(" ", posting.Where(x => !x.Succeeded).Select(x => $"{x.DocNo}: {x.ErrorMessage}"));
        return new PoCdnOperationResult
        {
            Succeeded = fail == 0 && attempted > 0,
            ErrorKind = fail == 0 ? PoCdnErrorKind.None : PoCdnErrorKind.BusinessRule,
            ErrorMessage = summary,
            SucceededCount = ok,
            FailedCount = fail,
            Posting = posting,
            VendorWarning = vendorWarning,
            VendorWarningMessage = vendorWarningMessage
        };
    }

    public static PoCdnOperationResult Fail(
        string message,
        PoCdnErrorKind kind = PoCdnErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static PoCdnOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = PoCdnErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

// ─────────────────────────── Posting item result ───────────────────────────

public sealed class PoCdnPostingItemResult
{
    public string DocNo { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public string? ReasonCode { get; init; }
    public bool VendorWarning { get; init; }
    public string? VendorWarningMessage { get; init; }
    public int? VrBatchNo { get; init; }

    public static PoCdnPostingItemResult Posted(string docNo, int? vrBatchNo = null, string? vendorWarning = null) =>
        new()
        {
            DocNo = docNo,
            Succeeded = true,
            Outcome = "Posted",
            VrBatchNo = vrBatchNo,
            VendorWarning = vendorWarning is not null,
            VendorWarningMessage = vendorWarning
        };

    public static PoCdnPostingItemResult RolledBack(string docNo) =>
        new() { DocNo = docNo, Succeeded = true, Outcome = "Rolled back" };

    public static PoCdnPostingItemResult Failed(string docNo, string reason) =>
        new() { DocNo = docNo, Succeeded = false, Outcome = "Failed: " + reason, ErrorMessage = reason };

    public static PoCdnPostingItemResult Failed(string docNo, string reasonCode, string userMessage) =>
        new()
        {
            DocNo = docNo,
            Succeeded = false,
            Outcome = "Failed: " + userMessage,
            ErrorMessage = userMessage,
            ReasonCode = reasonCode
        };

    public static PoCdnPostingItemResult NotAttempted(string docNo) =>
        new() { DocNo = docNo, Succeeded = false, Outcome = "Not attempted", ErrorMessage = "Not attempted" };
}

// ─────────────────────────── Keyed request ───────────────────────────

/// <summary>A key + RowVersion pair used for batch Post / Rollback / Delete from a list view.</summary>
public sealed class PoCdnKeyedRequest
{
    public string DocNo { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
}

// ─────────────────────────── List / search ───────────────────────────

public sealed class PoCdnListQuery
{
    /// <summary>Required: "CN" or "DN".</summary>
    public string Type { get; set; } = string.Empty;
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class PoCdnListRow
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string? InvNo { get; init; }
    public string? SupplierDocNo { get; init; }
    public DateTime? SupplierDocDate { get; init; }
    public string? ReasonCode { get; init; }
    public bool ReturnStock { get; init; }
    public decimal TotAmnt { get; init; }
    public int LineCount { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }

    /// <summary>Required for optimistic concurrency in batch operations.</summary>
    public byte[] RowVersion { get; init; } = [];

    /// <summary>C1 wording: POSTED is operational finalization only.</summary>
    public string StatusDisplay =>
        string.Equals(Status, PoCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase)
            ? "Posted (operational)"
            : Status;
}

public sealed class PoCdnListPage
{
    public IReadOnlyList<PoCdnListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

// ─────────────────────────── Lookup rows ───────────────────────────

public sealed class PoCdnVendorLookupRow
{
    public string VendorCode { get; init; } = string.Empty;
    public string VendorName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public bool IsActive { get; init; }
    public bool Suspended { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(VendorName) ? VendorCode : $"{VendorCode} — {VendorName}";
}

public sealed class PoCdnItemLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? IType { get; init; }
    public string? StdUom { get; init; }
    public string? PurUom { get; init; }
    public decimal? StdPackSize { get; init; }
    public decimal? PurStdPackSize { get; init; }
    public decimal? PurchasePrice { get; init; }
    public string? PurchaseGlCode { get; init; }
    public string? TaxGroup { get; init; }
    public string? PurchaseTaxGroup { get; init; }
    public bool StockControl { get; init; }
    public bool LotControl { get; init; }
    public string? DefWarehouse { get; init; }
    public string? DefLocation { get; init; }
    public string? Classification { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} — {IDesc}";

    public bool IsService => string.Equals((IType ?? string.Empty).Trim(), "SERVICE", StringComparison.OrdinalIgnoreCase);

    /// <summary>C42 fallback conversion factor — item master is only used when no historical document exists.</summary>
    public decimal ItemPackSize => PoOrderCalc.EffectivePackSize(PurStdPackSize ?? StdPackSize ?? 1m);
}

public sealed class PoCdnTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string? TaxGrDesc { get; init; }
    public decimal Percentage { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(TaxGrDesc) ? TaxGrCode : $"{TaxGrCode} — {TaxGrDesc}";
}

/// <summary>A selectable reason code for the document type, with the metadata the UI must honour.</summary>
public sealed class PoCdnReasonCodeOption
{
    public string Code { get; init; } = string.Empty;
    public bool InventoryCapable { get; init; }
    public bool RequiresPo { get; init; }
    public bool RequiresInvoice { get; init; }
    public bool InternalAdjustment { get; init; }

    /// <summary>True when the current user may select this code; only internal adjustment is gated (C44).</summary>
    public bool AllowedForUser { get; init; } = true;

    public bool AllowsBlankSupplierDocNo => InternalAdjustment;
}

// ─────────────────────────── Vendor defaults ───────────────────────────

public sealed class PoCdnVendorDefaults
{
    public string VendorCode { get; init; } = string.Empty;
    public string VendorName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public bool IsActive { get; init; }
    public bool Suspended { get; init; }

    /// <summary>C13/C15: set when the vendor cannot be used for a new document.</summary>
    public bool BlockedForSave { get; init; }
    public string? BlockedMessage { get; init; }

    public string? InvAddress1 { get; init; }
    public string? InvAddress2 { get; init; }
    public string? InvAddress3 { get; init; }
    public string? InvAddress4 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
    public string? Country { get; init; }
    public string? Tel { get; init; }
    public string? Fax { get; init; }
}

// ─────────────────────────── Line DTO ───────────────────────────

public sealed class PoCdnLineDto
{
    public short Line { get; init; }

    /// <summary>Source invoice line (C24). Null = header-level adjustment.</summary>
    public short? InvLineNo { get; init; }

    /// <summary>Declared physical-return intent (C43).</summary>
    public bool IsStockReturn { get; init; }

    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal StdQty { get; init; }
    public decimal StdCustPSize { get; init; }
    public string? SellingUom { get; init; }
    public string? StdUom { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public decimal ItemDiscount { get; init; }
    public decimal ItemDiscount1 { get; init; }
    public string? IDiscountType { get; init; }
    public string? IDiscountType1 { get; init; }
    public bool IsInclusive { get; init; }
    public string? TaxGroup { get; init; }
    public decimal TaxAmt { get; init; }
    public decimal NetAmount { get; init; }
    public bool IsTaxOnly { get; init; }
    public bool StockControl { get; init; }
    public string? ItemGlCode { get; init; }
    public string? Classification { get; init; }
    public string? Remarks { get; init; }
    public decimal CostPrice { get; init; }

    // PO link
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public short? PoLineNo { get; init; }

    // Stock-return fields (populated only when IsStockReturn)
    public string? FrWarehouse { get; init; }
    public string? LocCode { get; init; }
    public string? IStatus { get; init; }
    public string? LotNo { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public int? FromBalLocId { get; init; }
}

// ─────────────────────────── Document ───────────────────────────

public sealed class PoCdnDocument
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string Status { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? InvNo { get; init; }
    public bool ReturnStock { get; init; }
    public int? VrBatchNo { get; init; }
    public string VendorCode { get; set; } = string.Empty;
    public string? VendorName { get; init; }
    public string? Prefix { get; init; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public string? ReasonCode { get; init; }
    public string? SupplierDocNo { get; init; }
    public DateTime? SupplierDocDate { get; init; }
    public string? BuyerCode { get; init; }
    public string? Dept { get; init; }
    public string? ProjId { get; init; }
    public string? Remarks { get; init; }
    public string? RefNo { get; init; }
    public string? ExternalDocNo { get; init; }
    public string? InvAddress1 { get; init; }
    public string? InvAddress2 { get; init; }
    public string? InvAddress3 { get; init; }
    public string? InvAddress4 { get; init; }
    public string? InvCity { get; init; }
    public string? InvState { get; init; }
    public string? InvPostalCode { get; init; }
    public string? InvCountry { get; init; }
    public string? InvTel { get; init; }
    public string? InvFax { get; init; }
    public decimal GrossAmnt { get; init; }
    public decimal Taxes { get; init; }
    public decimal TotAmnt { get; init; }
    public DateTime? PostedDate { get; init; }
    public string? PostedBy { get; init; }
    public DateTime? RollbackDate { get; init; }
    public string? RollbackBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<PoCdnLineDto> Lines { get; init; } = [];

    public bool IsNew => string.Equals(Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase);
    public bool IsPosted => string.Equals(Status, PoCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase);

    /// <summary>C1 wording for the footer / status chip.</summary>
    public string StatusDisplay => IsPosted ? "Posted (operational)" : Status;
}

// ─────────────────────────── Save request ───────────────────────────

public sealed class PoCdnSaveRequest
{
    /// <summary>Required: "CN" or "DN".</summary>
    public string Type { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string VendorCode { get; set; } = string.Empty;

    /// <summary>Invoice reference. Required for a CN; optional for a DN.</summary>
    public string? InvNo { get; set; }

    /// <summary>Header gate for physical return (C43). Always false for a DN.</summary>
    public bool ReturnStock { get; set; }

    public string? Currency { get; set; }
    public decimal CurrRate { get; set; }
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }

    /// <summary>Required on save; must belong to the taxonomy for <see cref="Type"/> (C9).</summary>
    public string? ReasonCode { get; set; }

    /// <summary>The supplier's own document number (C2/C16/C44).</summary>
    public string? SupplierDocNo { get; set; }

    /// <summary>Required whenever <see cref="SupplierDocNo"/> is present.</summary>
    public DateTime? SupplierDocDate { get; set; }

    public string? BuyerCode { get; set; }
    public string? Dept { get; set; }
    public string? ProjId { get; set; }
    public string? Remarks { get; set; }
    public string? RefNo { get; set; }

    /// <summary>External/integration reference — must never equal the supplier document number (C14).</summary>
    public string? ExternalDocNo { get; set; }

    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvAddress4 { get; set; }
    public string? InvCity { get; set; }
    public string? InvState { get; set; }
    public string? InvPostalCode { get; set; }
    public string? InvCountry { get; set; }
    public string? InvTel { get; set; }
    public string? InvFax { get; set; }

    public byte[]? RowVersion { get; set; }
    public IReadOnlyList<PoCdnLineRequest>? Lines { get; set; }
}

public sealed class PoCdnLineRequest
{
    /// <summary>Source invoice line. Required on a stock-return line (C24).</summary>
    public short? InvLineNo { get; set; }

    /// <summary>Declared physical-return intent (C43).</summary>
    public bool IsStockReturn { get; set; }

    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal StdCustPSize { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public string? IDiscountType { get; set; }
    public string? IDiscountType1 { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGroup { get; set; }
    public string? ItemGlCode { get; set; }
    public string? Classification { get; set; }
    public string? Remarks { get; set; }
    public decimal CostPrice { get; set; }

    // PO link
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }

    // Stock-return fields
    public string? FrWarehouse { get; set; }
    public string? LocCode { get; set; }
    public string? IStatus { get; set; }
    public string? LotNo { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public int? FromBalLocId { get; set; }
}

// ─────────────────────────── Invoice picker ───────────────────────────

public sealed class PoCdnInvoicePickerRow
{
    public string InvNo { get; init; } = string.Empty;
    public DateTime InvDate { get; init; }
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public decimal TotAmnt { get; init; }
    public string DisplayText => $"{InvNo} ({InvDate:yyyy-MM-dd}) — {TotAmnt:#,##0.00}";
}

/// <summary>A selectable source invoice line, with the remaining ceilings already computed.</summary>
public sealed class PoCdnInvoiceLinePickerRow
{
    public short Line { get; init; }
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal StdQty { get; init; }
    public decimal StdCustPSize { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public decimal NetAmount { get; init; }
    public string? TaxGroup { get; init; }
    public bool IsInclusive { get; init; }
    public string? ItemGlCode { get; init; }
    public string? Classification { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public short? PoLineNo { get; init; }

    /// <summary>Invoice line StdQty minus what other NEW/POSTED CNs already consumed (C34).</summary>
    public decimal RemainingStdQty { get; init; }

    /// <summary>Invoice line Amount minus what other NEW/POSTED CNs already credited (C34).</summary>
    public decimal RemainingAmount { get; init; }

    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? $"{Line}: {ICode}" : $"{Line}: {ICode} — {IDesc}";
}

// ─────────────────────────── Reservation reporting ───────────────────────────

public sealed class PoCdnInvoiceReservationSummary
{
    public string InvNo { get; init; } = string.Empty;
    public DateTime InvDate { get; init; }
    public decimal InvoiceTotal { get; init; }
    public decimal PostedCnTotal { get; init; }
    public decimal DraftCnTotal { get; init; }
    public decimal Remaining { get; init; }
    public bool OverReserved { get; init; }
    public IReadOnlyList<PoCdnReservationRowDto> Rows { get; init; } = [];
}

public sealed class PoCdnReservationRowDto
{
    public string DocNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal TotAmnt { get; init; }
    public DateTime DocDate { get; init; }
    public string? SupplierDocNo { get; init; }
    public bool IsDraft => string.Equals(Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase);
    public string StatusDisplay => IsDraft ? "Drafts only" : Status;
}

public sealed class PoCdnReservationReportQuery
{
    public string? InvNo { get; set; }
    public string? VendorCode { get; set; }
    public bool OverReservedOnly { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

public sealed class PoCdnReservationReportRow
{
    public string InvNo { get; init; } = string.Empty;
    public DateTime InvDate { get; init; }
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public decimal InvoiceTotal { get; init; }
    public decimal PostedCnTotal { get; init; }
    public decimal DraftCnTotal { get; init; }
    public decimal Remaining { get; init; }
    public int DraftCount { get; init; }
    public bool OverReserved { get; init; }
}

public sealed class PoCdnReservationReportPage
{
    public IReadOnlyList<PoCdnReservationReportRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

// ─────────────────────────── Interface ───────────────────────────

public interface IPoCdnService
{
    /// <summary>Lookups (items, warehouses, vendors, tax groups, pay codes, reason codes for the type).</summary>
    Task<PoCdnOperationResult> GetLookupsAsync(
        string type,
        CancellationToken cancellationToken = default);

    Task<PoCdnOperationResult> GetVendorDefaultsAsync(
        string vendorCode,
        DateTime docDate,
        CancellationToken cancellationToken = default);

    Task<PoCdnOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime docDate,
        CancellationToken cancellationToken = default);

    /// <summary>List/search. <c>query.Type</c> is required ("CN" or "DN").</summary>
    Task<PoCdnOperationResult> SearchAsync(
        PoCdnListQuery query,
        CancellationToken cancellationToken = default);

    Task<PoCdnOperationResult> GetAsync(
        string docNo,
        CancellationToken cancellationToken = default);

    Task<PoCdnOperationResult> SaveNewAsync(
        PoCdnSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<PoCdnOperationResult> UpdateAsync(
        string docNo,
        PoCdnSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Delete NEW documents only. RowVersion required per item.</summary>
    Task<PoCdnOperationResult> DeleteAsync(
        IReadOnlyList<PoCdnKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>Post NEW → POSTED, creating/reusing the owned VR batch when ReturnStock.</summary>
    Task<PoCdnOperationResult> PostAsync(
        IReadOnlyList<PoCdnKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>Rollback POSTED → NEW, reversing the owned VR batch.</summary>
    Task<PoCdnOperationResult> RollbackAsync(
        IReadOnlyList<PoCdnKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>Posted INV invoices for the vendor, for the invoice picker.</summary>
    Task<PoCdnOperationResult> SearchPostedInvoicesAsync(
        string vendorCode,
        string? searchText,
        CancellationToken cancellationToken = default);

    /// <summary>Selectable lines of a posted invoice, with remaining source-line ceilings (C34).</summary>
    Task<PoCdnOperationResult> GetInvoiceLinesAsync(
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);

    /// <summary>Map a posted invoice to a draft document (never a quantity correction — C5).</summary>
    Task<PoCdnOperationResult> CopyFromInvoiceAsync(
        string invNo,
        CancellationToken cancellationToken = default);

    Task<PoCdnOperationResult> GetInvoiceReservationsAsync(
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);

    Task<PoCdnOperationResult> GetReservationReportAsync(
        PoCdnReservationReportQuery query,
        CancellationToken cancellationToken = default);

    Task<bool> CanAsync(
        string type,
        string permissionCode,
        CancellationToken cancellationToken = default);
}
