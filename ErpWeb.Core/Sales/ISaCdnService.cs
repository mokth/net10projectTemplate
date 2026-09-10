using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

// ─────────────────────────── Error / Result ───────────────────────────

public enum SaCdnErrorKind
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

public sealed class SaCdnOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public SaCdnErrorKind ErrorKind { get; init; }
    public string? DocNo { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public bool RequiresConfirmation { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public SaCdnDocument? Document { get; init; }
    public SaCdnCustomerDefaults? CustomerDefaults { get; init; }
    public SaCdnListPage? ListPage { get; init; }
    public IReadOnlyList<SaCdnPostingItemResult> Posting { get; init; } = [];
    public IReadOnlyList<SaCdnItemLookupRow> Items { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IReadOnlyList<SaCdnCustomerLookupRow> Customers { get; init; } = [];
    public IReadOnlyList<SaCdnTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> PayCodes { get; init; } = [];
    public IReadOnlyList<SaCdnInvoicePickerRow> InvoicePickerRows { get; init; } = [];
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static SaCdnOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = SaCdnErrorKind.None };

    public static SaCdnOperationResult OkSaved(string docNo) =>
        new() { Succeeded = true, ErrorKind = SaCdnErrorKind.None, DocNo = docNo };

    public static SaCdnOperationResult OkDocument(SaCdnDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaCdnErrorKind.None,
            Document = document,
            DocNo = document.DocNo
        };

    public static SaCdnOperationResult OkList(SaCdnListPage page) =>
        new() { Succeeded = true, ErrorKind = SaCdnErrorKind.None, ListPage = page };

    public static SaCdnOperationResult OkLookups(
        IReadOnlyList<SaCdnItemLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses,
        IReadOnlyList<SaCdnCustomerLookupRow> customers,
        IReadOnlyList<SaCdnTaxGroupLookupRow> taxGroups,
        IReadOnlyList<IvCodeLookupRow> payCodes) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaCdnErrorKind.None,
            Items = items,
            Warehouses = warehouses,
            Customers = customers,
            TaxGroups = taxGroups,
            PayCodes = payCodes
        };

    public static SaCdnOperationResult OkDefaults(SaCdnCustomerDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = SaCdnErrorKind.None, CustomerDefaults = defaults };

    public static SaCdnOperationResult OkRate(decimal rate, bool valid) =>
        new() { Succeeded = true, ErrorKind = SaCdnErrorKind.None, CurrRate = rate, CurrRateValid = valid };

    public static SaCdnOperationResult OkInvoicePicker(IReadOnlyList<SaCdnInvoicePickerRow> rows) =>
        new() { Succeeded = true, ErrorKind = SaCdnErrorKind.None, InvoicePickerRows = rows };

    public static SaCdnOperationResult OkPosting(IReadOnlyList<SaCdnPostingItemResult> posting)
    {
        var ok = posting.Count(x => x.Succeeded);
        var fail = posting.Count - ok;
        var attempted = posting.Count(x => !string.Equals(x.Outcome, "Not attempted", StringComparison.OrdinalIgnoreCase));
        var summary = fail == 0
            ? null
            : string.Join(" ", posting.Where(x => !x.Succeeded).Select(x => $"{x.DocNo}: {x.ErrorMessage}"));
        return new SaCdnOperationResult
        {
            Succeeded = fail == 0 && attempted > 0,
            ErrorKind = fail == 0 ? SaCdnErrorKind.None : SaCdnErrorKind.BusinessRule,
            ErrorMessage = summary,
            SucceededCount = ok,
            FailedCount = fail,
            Posting = posting
        };
    }

    public static SaCdnOperationResult Fail(
        string message,
        SaCdnErrorKind kind = SaCdnErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static SaCdnOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = SaCdnErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

// ─────────────────────────── Posting item result ───────────────────────────

public sealed class SaCdnPostingItemResult
{
    public string DocNo { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public string? ReasonCode { get; init; }

    public static SaCdnPostingItemResult Posted(string docNo) =>
        new() { DocNo = docNo, Succeeded = true, Outcome = "Posted" };

    public static SaCdnPostingItemResult RolledBack(string docNo) =>
        new() { DocNo = docNo, Succeeded = true, Outcome = "Rolled back" };

    public static SaCdnPostingItemResult Failed(string docNo, string reason) =>
        new() { DocNo = docNo, Succeeded = false, Outcome = "Failed: " + reason, ErrorMessage = reason };

    public static SaCdnPostingItemResult Failed(string docNo, string reasonCode, string userMessage) =>
        new()
        {
            DocNo = docNo,
            Succeeded = false,
            Outcome = "Failed: " + userMessage,
            ErrorMessage = userMessage,
            ReasonCode = reasonCode
        };

    public static SaCdnPostingItemResult NotAttempted(string docNo) =>
        new() { DocNo = docNo, Succeeded = false, Outcome = "Not attempted", ErrorMessage = "Not attempted" };
}

// ─────────────────────────── Keyed request ───────────────────────────

/// <summary>A key + RowVersion pair used for batch Post/Rollback/Delete from a list view.</summary>
public sealed class SaCdnKeyedRequest
{
    public string DocNo { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
}

// ─────────────────────────── List / search ───────────────────────────

public sealed class SaCdnListQuery
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

public sealed class SaCdnListRow
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string CustCode { get; init; } = string.Empty;
    public string? CustName { get; init; }
    public string? InvNo { get; init; }
    public decimal TotAmnt { get; init; }
    public int LineCount { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    /// <summary>Required for optimistic concurrency in batch operations.</summary>
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaCdnListPage
{
    public IReadOnlyList<SaCdnListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

// ─────────────────────────── Lookup rows ───────────────────────────

public sealed class SaCdnCustomerLookupRow
{
    public string CustCode { get; init; } = string.Empty;
    public string CustName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string? DiscountMethod { get; init; }
    public bool? DecPoint { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(CustName) ? CustCode : $"{CustCode} — {CustName}";
}

public sealed class SaCdnItemLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? StdUom { get; init; }
    public decimal? StdPackSize { get; init; }
    public decimal? SellingPrice { get; init; }
    public string? SellingGlCode { get; init; }
    public string? TaxGroup { get; init; }
    public bool StockControl { get; init; }
    public bool LotControl { get; init; }
    public string? DefWarehouse { get; init; }
    public string? DefLocation { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} — {IDesc}";
}

public sealed class SaCdnTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string? TaxGrDesc { get; init; }
    public decimal Percentage { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(TaxGrDesc) ? TaxGrCode : $"{TaxGrCode} — {TaxGrDesc}";
}

// ─────────────────────────── Customer defaults ───────────────────────────

public sealed class SaCdnCustomerDefaults
{
    public string CustCode { get; init; } = string.Empty;
    public string CustName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public bool? Taxable { get; init; }
    public string? SalesmanCode { get; init; }
    public string? DiscountMethod { get; init; }
    public bool? DecPoint { get; init; }
    public string? InvName { get; init; }
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
    public IReadOnlyList<SaCustAddressVm> ShipToAddresses { get; init; } = [];
}

// ─────────────────────────── Line DTO ───────────────────────────

public sealed class SaCdnLineDto
{
    public int Line { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal StdQty { get; init; }
    public decimal StdCustPsize { get; init; }
    public string? StdUom { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public decimal ItemDiscount { get; init; }
    public decimal ItemDiscount2 { get; init; }
    public decimal ItemDiscount3 { get; init; }
    public decimal ItemDiscount4 { get; init; }
    public decimal ItemDiscount5 { get; init; }
    public decimal ItemDiscount6 { get; init; }
    public decimal ItemDiscAmount { get; init; }
    public bool IsInclusive { get; init; }
    public string? TaxGrCode { get; init; }
    public decimal TaxAmt { get; init; }
    public decimal NetAmount { get; init; }
    public decimal LocalAmount { get; init; }
    public string? OrderType { get; init; }
    public bool StockControl { get; init; }
    public string? ItemGlCode { get; init; }
    public string? Classification { get; init; }
    public string? Remarks { get; init; }
    public decimal CostPrice { get; init; }
    // Stock-return fields (populated only when ReturnStock=true)
    public string? FrWarehouse { get; init; }
    public string? LocCode { get; init; }
    public string? IStatus { get; init; }
    public string? LotNo { get; init; }
    public DateTime? ExpiryDate { get; init; }
}

// ─────────────────────────── Document ───────────────────────────

public sealed class SaCdnDocument
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string Status { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? InvNo { get; init; }
    public string? DoNo { get; init; }
    public bool ReturnStock { get; init; }
    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; init; }
    public string? Prefix { get; init; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public string? SalesmanCode { get; init; }
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
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<SaCdnLineDto> Lines { get; init; } = [];
}

// ─────────────────────────── Save request ───────────────────────────

public sealed class SaCdnSaveRequest
{
    /// <summary>Required: "CN" or "DN".</summary>
    public string Type { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string CustCode { get; set; } = string.Empty;
    /// <summary>Invoice reference (CN only). DN must leave empty.</summary>
    public string? InvNo { get; set; }
    public string? DoNo { get; set; }
    /// <summary>Return stock to inventory (CN only). DN always false.</summary>
    public bool ReturnStock { get; set; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }
    public string? SalesmanCode { get; set; }
    public string? Dept { get; set; }
    public string? ProjId { get; set; }
    public string? Remarks { get; set; }
    public string? RefNo { get; set; }
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
    public IReadOnlyList<SaCdnLineRequest>? Lines { get; set; }
}

public sealed class SaCdnLineRequest
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount2 { get; set; }
    public decimal ItemDiscount3 { get; set; }
    public decimal ItemDiscount4 { get; set; }
    public decimal ItemDiscount5 { get; set; }
    public decimal ItemDiscount6 { get; set; }
    public decimal ItemDiscAmount { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGrCode { get; set; }
    public string? OrderType { get; set; }
    public string? Classification { get; set; }
    public string? Remarks { get; set; }
    public decimal CostPrice { get; set; }
    public string? ItemGlCode { get; set; }
    // Stock-return fields
    public string? FrWarehouse { get; set; }
    public string? LocCode { get; set; }
    public string? IStatus { get; set; }
    public string? LotNo { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

// ─────────────────────────── Invoice picker (for CopyFromInvoice) ───────────────────────────

public sealed class SaCdnInvoicePickerRow
{
    public string InvNo { get; init; } = string.Empty;
    public DateTime InvDate { get; init; }
    public string CustCode { get; init; } = string.Empty;
    public string? CustName { get; init; }
    public decimal TotAmnt { get; init; }
    public string DisplayText => $"{InvNo} ({InvDate:yyyy-MM-dd}) — {TotAmnt:#,##0.00}";
}

// ─────────────────────────── Interface ───────────────────────────

public interface ISaCdnService
{
    /// <summary>Lookups (items, warehouses, customers, tax groups, pay codes). Type ignored for now.</summary>
    Task<SaCdnOperationResult> GetLookupsAsync(
        string type,
        CancellationToken cancellationToken = default);

    Task<SaCdnOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime docDate,
        CancellationToken cancellationToken = default);

    Task<SaCdnOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime docDate,
        CancellationToken cancellationToken = default);

    /// <summary>List/search. query.Type is required ("CN" or "DN").</summary>
    Task<SaCdnOperationResult> SearchAsync(
        SaCdnListQuery query,
        CancellationToken cancellationToken = default);

    Task<SaCdnOperationResult> GetAsync(
        string docNo,
        CancellationToken cancellationToken = default);

    Task<SaCdnOperationResult> SaveNewAsync(
        SaCdnSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaCdnOperationResult> UpdateAsync(
        string docNo,
        SaCdnSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Delete CNs/DNs (NEW status only). RowVersion required per item.</summary>
    Task<SaCdnOperationResult> DeleteAsync(
        IReadOnlyList<SaCdnKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>Post CNs/DNs (NEW → POSTED). RowVersion required per item.</summary>
    Task<SaCdnOperationResult> PostAsync(
        IReadOnlyList<SaCdnKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>Rollback CNs/DNs (POSTED → NEW). RowVersion required per item.</summary>
    Task<SaCdnOperationResult> RollbackAsync(
        IReadOnlyList<SaCdnKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>Search POSTED invoices for the given customer (for CN copy picker).</summary>
    Task<SaCdnOperationResult> SearchPostedInvoicesAsync(
        string? custCode,
        string? searchText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load a POSTED invoice and map it to an in-memory CDN draft. Does NOT save.
    /// ReturnStock=false, warehouse/lot empty.
    /// </summary>
    Task<SaCdnOperationResult> CopyFromInvoiceAsync(
        string invNo,
        CancellationToken cancellationToken = default);
}
