using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public enum PoInvoiceErrorKind
{
    None = 0,
    Validation,
    Concurrency,
    NotFound,
    Authorization,
    BusinessRule,
    Unexpected
}

public sealed class PoInvoiceOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public PoInvoiceErrorKind ErrorKind { get; init; }
    public string? DocNo { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public PoInvoiceDocument? Document { get; init; }
    public PoInvoiceListPage? ListPage { get; init; }
    public PoInvoiceLookups? Lookups { get; init; }
    public PoInvoiceVendorDefaults? VendorDefaults { get; init; }
    public IReadOnlyList<PoInvoiceInvoicePickerRow> InvoicePickerRows { get; init; } = [];
    public IReadOnlyList<PoInvoicePoLinePickerRow> PoLinePickerRows { get; init; } = [];
    public IReadOnlyList<PoInvoicePostingItemResult> Posting { get; init; } = [];
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static PoInvoiceOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = PoInvoiceErrorKind.None };

    public static PoInvoiceOperationResult OkSaved(string docNo) =>
        new() { Succeeded = true, ErrorKind = PoInvoiceErrorKind.None, DocNo = docNo };

    public static PoInvoiceOperationResult OkDocument(PoInvoiceDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = PoInvoiceErrorKind.None,
            Document = document,
            DocNo = document.DocNo
        };

    public static PoInvoiceOperationResult OkList(PoInvoiceListPage page) =>
        new() { Succeeded = true, ErrorKind = PoInvoiceErrorKind.None, ListPage = page };

    public static PoInvoiceOperationResult OkLookups(PoInvoiceLookups lookups) =>
        new() { Succeeded = true, ErrorKind = PoInvoiceErrorKind.None, Lookups = lookups };

    public static PoInvoiceOperationResult OkVendorDefaults(PoInvoiceVendorDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = PoInvoiceErrorKind.None, VendorDefaults = defaults };

    public static PoInvoiceOperationResult OkInvoicePicker(IReadOnlyList<PoInvoiceInvoicePickerRow> rows) =>
        new() { Succeeded = true, ErrorKind = PoInvoiceErrorKind.None, InvoicePickerRows = rows };

    public static PoInvoiceOperationResult OkPoLines(IReadOnlyList<PoInvoicePoLinePickerRow> rows) =>
        new() { Succeeded = true, ErrorKind = PoInvoiceErrorKind.None, PoLinePickerRows = rows };

    public static PoInvoiceOperationResult OkPosting(IReadOnlyList<PoInvoicePostingItemResult> posting)
    {
        var ok = posting.Count(x => x.Succeeded);
        var fail = posting.Count - ok;
        var attempted = posting.Count(x =>
            !string.Equals(x.Outcome, "Not attempted", StringComparison.OrdinalIgnoreCase));
        var summary = fail == 0
            ? null
            : string.Join(" ", posting.Where(x => !x.Succeeded).Select(x => $"{x.DocNo}: {x.ErrorMessage}"));
        return new PoInvoiceOperationResult
        {
            Succeeded = fail == 0 && attempted > 0,
            ErrorKind = fail == 0 ? PoInvoiceErrorKind.None : PoInvoiceErrorKind.BusinessRule,
            ErrorMessage = summary,
            SucceededCount = ok,
            FailedCount = fail,
            Posting = posting
        };
    }

    public static PoInvoiceOperationResult Fail(
        string message,
        PoInvoiceErrorKind kind = PoInvoiceErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static PoInvoiceOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = PoInvoiceErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class PoInvoicePostingItemResult
{
    public string DocNo { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }

    public static PoInvoicePostingItemResult Posted(string docNo) =>
        new() { DocNo = docNo, Succeeded = true, Outcome = "Posted" };

    public static PoInvoicePostingItemResult RolledBack(string docNo) =>
        new() { DocNo = docNo, Succeeded = true, Outcome = "Rolled back" };

    public static PoInvoicePostingItemResult Failed(string docNo, string reason) =>
        new() { DocNo = docNo, Succeeded = false, Outcome = "Failed: " + reason, ErrorMessage = reason };

    public static PoInvoicePostingItemResult NotAttempted(string docNo) =>
        new() { DocNo = docNo, Succeeded = false, Outcome = "Not attempted", ErrorMessage = "Not attempted" };
}

public sealed class PoInvoiceKeyedRequest
{
    public string DocNo { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
}

public sealed class PoInvoiceListQuery
{
    public string? Type { get; set; }
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class PoInvoiceListRow
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string? InvNo { get; init; }
    public decimal TotAmnt { get; init; }
    public int LineCount { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public bool CanPost { get; init; }
    public bool CanRollback { get; init; }
}

public sealed class PoInvoiceListPage
{
    public IReadOnlyList<PoInvoiceListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class PoInvoiceVendorLookupRow
{
    public string SuppCode { get; init; } = string.Empty;
    public string SuppName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(SuppName) ? SuppCode : $"{SuppCode} - {SuppName}";
}

public sealed class PoInvoiceTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string? TaxGrDesc { get; init; }
    public decimal Percentage { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(TaxGrDesc) ? TaxGrCode : $"{TaxGrCode} - {TaxGrDesc}";
}

public sealed class PoInvoiceLookups
{
    public IReadOnlyList<PoInvoiceVendorLookupRow> Vendors { get; init; } = [];
    public IReadOnlyList<PoInvoiceTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> PayCodes { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> Currencies { get; init; } = [];
}

public sealed class PoInvoiceVendorDefaults
{
    public string VendorCode { get; init; } = string.Empty;
    public string VendorName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public decimal CurrRate { get; init; } = 1m;
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
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

public sealed class PoInvoiceLineDto
{
    public short Line { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal UnitPrice { get; init; }
    public string? SellingUom { get; init; }
    public string? StdUom { get; init; }
    public decimal StdQty { get; init; }
    public decimal Amount { get; init; }
    public decimal ItemDiscount { get; init; }
    public decimal ItemDiscount1 { get; init; }
    public string? IDiscountType { get; init; }
    public string? IDiscountType1 { get; init; }
    public bool IsInclusive { get; init; }
    public string? TaxGroup { get; init; }
    public decimal TaxAmt { get; init; }
    public decimal NetAmount { get; init; }
    public string? ItemGlCode { get; init; }
    public string? Remarks { get; init; }
    public bool? OneTime { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public short? PoLineNo { get; init; }
}

public sealed class PoInvoiceDocument
{
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? InvNo { get; set; }
    public string VendorCode { get; set; } = string.Empty;
    public string? VendorName { get; set; }
    public string? Prefix { get; set; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }
    public string? LocationCode { get; set; }
    public string? ProjId { get; set; }
    public string? Dept { get; set; }
    public string? Remarks { get; set; }
    public string? RefNo { get; set; }
    public string? ExternalDocNo { get; set; }
    public decimal? PriceTolerance { get; set; }
    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvAddress4 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Fax { get; set; }
    public decimal GrossAmnt { get; set; }
    public decimal Taxes { get; set; }
    public decimal TotAmnt { get; set; }
    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public bool CanPost { get; set; }
    public bool CanRollback { get; set; }
    public IReadOnlyList<PoInvoiceLineDto> Lines { get; set; } = [];
}

public sealed class PoInvoiceSaveRequest
{
    public string Type { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string VendorCode { get; set; } = string.Empty;
    /// <summary>Supplier invoice no (required on INV); referenced purchase INV DocNo (required on CN).</summary>
    public string? InvNo { get; set; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }
    public string? LocationCode { get; set; }
    public string? ProjId { get; set; }
    public string? Dept { get; set; }
    public string? Remarks { get; set; }
    public string? RefNo { get; set; }
    public string? ExternalDocNo { get; set; }
    public decimal? PriceTolerance { get; set; }
    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvAddress4 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Fax { get; set; }
    public byte[]? RowVersion { get; set; }
    public IReadOnlyList<PoInvoiceLineRequest>? Lines { get; set; }
}

public sealed class PoInvoiceLineRequest
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public string? SellingUom { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public string? IDiscountType { get; set; }
    public string? IDiscountType1 { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGroup { get; set; }
    public string? ItemGlCode { get; set; }
    public string? Remarks { get; set; }
    public bool? OneTime { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }
}

public sealed class PoInvoiceInvoicePickerRow
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public decimal TotAmnt { get; init; }
    public string DisplayText => $"{DocNo} ({DocDate:yyyy-MM-dd}) — {TotAmnt:#,##0.00}";
}

public sealed class PoInvoicePoLinePickerRow
{
    public string PoNo { get; init; } = string.Empty;
    public short PoRelNo { get; init; }
    public short Line { get; init; }
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public string? PurchaseUom { get; init; }
    public decimal PoUnitPrice { get; init; }
    public decimal OrderedQty { get; init; }
    public decimal RecvQty { get; init; }
    public decimal ReturnQty { get; init; }
    public decimal InvoicedQty { get; init; }
    public decimal InvoiceableQty { get; init; }
    public decimal NetReceivedQty { get; init; }
    public bool? OneTime { get; init; }
    public string? TaxGroup { get; init; }
    public bool IsInclusive { get; init; }
    public string DisplayText =>
        $"{PoNo}/{PoRelNo} L{Line} {ICode} — invoiceable {InvoiceableQty:n4}";
}

public interface IPoInvoiceService
{
    Task<PoInvoiceOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> GetVendorDefaultsAsync(
        string vendorCode,
        DateTime docDate,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> SearchAsync(
        PoInvoiceListQuery query,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> GetAsync(
        string docNo,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> SaveNewAsync(
        PoInvoiceSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> UpdateAsync(
        string docNo,
        PoInvoiceSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> DeleteAsync(
        IReadOnlyList<PoInvoiceKeyedRequest> items,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> PostAsync(
        IReadOnlyList<PoInvoiceKeyedRequest> items,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> RollbackAsync(
        IReadOnlyList<PoInvoiceKeyedRequest> items,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> SearchPostedInvoicesAsync(
        string? vendorCode,
        string? searchText,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> SearchInvoiceablePoLinesAsync(
        string vendorCode,
        string? searchText,
        CancellationToken cancellationToken = default);

    Task<PoInvoiceOperationResult> CopyFromInvoiceAsync(
        string invDocNo,
        CancellationToken cancellationToken = default);
}
