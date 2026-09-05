using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public enum SaInvoiceErrorKind
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

public sealed class SaInvoiceOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public SaInvoiceErrorKind ErrorKind { get; init; }
    public string? InvNo { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public bool RequiresConfirmation { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public SaInvoiceDocument? Document { get; init; }
    public SaInvoiceCustomerDefaults? CustomerDefaults { get; init; }
    public SaInvoiceListPage? ListPage { get; init; }
    public IReadOnlyList<SaInvoicePostingItemResult> Posting { get; init; } = [];
    public IReadOnlyList<SaInvoiceItemLookupRow> Items { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IReadOnlyList<SaInvoiceCustomerLookupRow> Customers { get; init; } = [];
    public IReadOnlyList<SaInvoiceTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> PayCodes { get; init; } = [];
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static SaInvoiceOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = SaInvoiceErrorKind.None };

    public static SaInvoiceOperationResult OkSaved(string invNo) =>
        new() { Succeeded = true, ErrorKind = SaInvoiceErrorKind.None, InvNo = invNo };

    public static SaInvoiceOperationResult OkDocument(SaInvoiceDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaInvoiceErrorKind.None,
            Document = document,
            InvNo = document.InvNo
        };

    public static SaInvoiceOperationResult OkList(SaInvoiceListPage page) =>
        new() { Succeeded = true, ErrorKind = SaInvoiceErrorKind.None, ListPage = page };

    public static SaInvoiceOperationResult OkLookups(
        IReadOnlyList<SaInvoiceItemLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses,
        IReadOnlyList<SaInvoiceCustomerLookupRow> customers,
        IReadOnlyList<SaInvoiceTaxGroupLookupRow> taxGroups,
        IReadOnlyList<IvCodeLookupRow> payCodes) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaInvoiceErrorKind.None,
            Items = items,
            Warehouses = warehouses,
            Customers = customers,
            TaxGroups = taxGroups,
            PayCodes = payCodes
        };

    public static SaInvoiceOperationResult OkDefaults(SaInvoiceCustomerDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = SaInvoiceErrorKind.None, CustomerDefaults = defaults };

    public static SaInvoiceOperationResult OkRate(decimal rate, bool valid) =>
        new() { Succeeded = true, ErrorKind = SaInvoiceErrorKind.None, CurrRate = rate, CurrRateValid = valid };

    public static SaInvoiceOperationResult OkConfirmation(SaInvoiceDocument document, string message) =>
        new()
        {
            Succeeded = false,
            RequiresConfirmation = true,
            ErrorKind = SaInvoiceErrorKind.Confirmation,
            ErrorMessage = message,
            Document = document,
            InvNo = document.InvNo
        };

    public static SaInvoiceOperationResult OkPosting(IReadOnlyList<SaInvoicePostingItemResult> posting)
    {
        var ok = posting.Count(x => x.Succeeded);
        var fail = posting.Count - ok;
        var attempted = posting.Count(x => !string.Equals(x.Outcome, "Not attempted", StringComparison.OrdinalIgnoreCase));
        var summary = fail == 0
            ? null
            : string.Join(" ", posting.Where(x => !x.Succeeded).Select(x => $"{x.InvNo}: {x.ErrorMessage}"));
        return new SaInvoiceOperationResult
        {
            Succeeded = fail == 0 && attempted > 0,
            ErrorKind = fail == 0 ? SaInvoiceErrorKind.None : SaInvoiceErrorKind.BusinessRule,
            ErrorMessage = summary,
            SucceededCount = ok,
            FailedCount = fail,
            Posting = posting
        };
    }

    public static SaInvoiceOperationResult Fail(
        string message,
        SaInvoiceErrorKind kind = SaInvoiceErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static SaInvoiceOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = SaInvoiceErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class SaInvoicePostingItemResult
{
    public string InvNo { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }

    public static SaInvoicePostingItemResult Posted(string invNo) =>
        new() { InvNo = invNo, Succeeded = true, Outcome = "Posted" };

    public static SaInvoicePostingItemResult RolledBack(string invNo) =>
        new() { InvNo = invNo, Succeeded = true, Outcome = "Rolled back" };

    public static SaInvoicePostingItemResult Failed(string invNo, string reason) =>
        new() { InvNo = invNo, Succeeded = false, Outcome = "Failed: " + reason, ErrorMessage = reason };

    public static SaInvoicePostingItemResult NotAttempted(string invNo) =>
        new() { InvNo = invNo, Succeeded = false, Outcome = "Not attempted", ErrorMessage = "Not attempted" };
}

public sealed class SaInvoiceListQuery
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class SaInvoiceListRow
{
    public string InvNo { get; init; } = string.Empty;
    public DateTime InvDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CustCode { get; init; } = string.Empty;
    public string? CustName { get; init; }
    public decimal TotAmnt { get; init; }
    public int LineCount { get; init; }
    public bool ShipmentComplete { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed class SaInvoiceListPage
{
    public IReadOnlyList<SaInvoiceListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SaInvoiceCustomerLookupRow
{
    public string CustCode { get; init; } = string.Empty;
    public string CustName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string? InvoicePrefix { get; init; }
    public string? DiscountMethod { get; init; }
    public bool? DecPoint { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(CustName) ? CustCode : $"{CustCode} — {CustName}";
}

public sealed class SaInvoiceItemLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? StdUom { get; init; }
    public decimal? StdPackSize { get; init; }
    public decimal? SellingPrice { get; init; }
    public string? SellingGlCode { get; init; }
    public string? TaxGroup { get; init; }
    public bool StockControl { get; init; }
    public string? DefWarehouse { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} — {IDesc}";
}

public sealed class SaInvoiceTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string? TaxGrDesc { get; init; }
    public decimal Percentage { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(TaxGrDesc) ? TaxGrCode : $"{TaxGrCode} — {TaxGrDesc}";
}

public sealed class SaInvoiceCustomerDefaults
{
    public string CustCode { get; init; } = string.Empty;
    public string CustName { get; init; } = string.Empty;
    public string? InvPrefix { get; init; }
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
    public string? ShipName { get; init; }
    public string? ShipAddress1 { get; init; }
    public string? ShipAddress2 { get; init; }
    public string? ShipAddress3 { get; init; }
    public string? ShipCity { get; init; }
    public string? ShipState { get; init; }
    public string? ShipPostalCode { get; init; }
    public string? ShipCountry { get; init; }
    public string? ShipTel { get; init; }
    public string? ShipFax { get; init; }
}

public sealed class SaInvoiceDocument
{
    public string InvNo { get; init; } = string.Empty;
    public DateTime InvDate { get; set; }
    public string Status { get; init; } = string.Empty;
    public string? DoNo { get; init; }
    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; init; }
    public string? InvPrefix { get; init; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public string? SalesmanCode { get; init; }
    public string? PoNo { get; init; }
    public string? Remark { get; init; }
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
    public string? ShipName { get; init; }
    public string? ShipAddress1 { get; init; }
    public string? ShipAddress2 { get; init; }
    public string? ShipAddress3 { get; init; }
    public string? ShipCity { get; init; }
    public string? ShipState { get; init; }
    public string? ShipPostalCode { get; init; }
    public string? ShipCountry { get; init; }
    public string? ShipTel { get; init; }
    public string? ShipFax { get; init; }
    public decimal GrossAmnt { get; init; }
    public decimal Taxes { get; init; }
    public decimal TotAmnt { get; init; }
    public bool ShipmentComplete { get; init; }
    public int? SpBatchNo { get; init; }
    public string? SpBatchStatus { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<SaInvoiceLineDto> Lines { get; init; } = [];
    public IReadOnlyList<SaInvoiceShipmentLineDto> Shipment { get; set; } = [];
}

public sealed class SaInvoiceLineDto
{
    public int Line { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal StdQty { get; init; }
    public decimal? StdPackSize { get; init; }
    public string? StdUom { get; init; }
    public string? FrWarehouse { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public decimal ItemDiscount { get; init; }
    public decimal ItemDiscount2 { get; init; }
    public decimal ItemDiscount3 { get; init; }
    public decimal ItemDiscount4 { get; init; }
    public decimal ItemDiscount5 { get; init; }
    public decimal ItemDiscount6 { get; init; }
    public decimal ItemDiscAmount { get; init; }
    public decimal ItemDiscAmount1 { get; init; }
    public bool IsInclusive { get; init; }
    public string? TaxGrCode { get; init; }
    public decimal TaxAmt { get; init; }
    public decimal NetAmount { get; init; }
    public decimal LocalAmount { get; init; }
    public string? OrderType { get; init; }
    public bool StockControl { get; init; }
    public string? SellingGlCode { get; init; }
    public string? Remarks { get; init; }
    public decimal ShipQty { get; init; }
    public bool ShipmentComplete { get; init; }
}

public sealed class SaInvoiceShipmentLineDto
{
    public int Line { get; init; }
    public string? ICode { get; init; }
    public int? FromBalLocId { get; init; }
    public string? FrWarehouse { get; init; }
    public string? FrLocation { get; init; }
    public string? FrLotNo { get; init; }
    public decimal FrStdQty { get; init; }
    public string? IStatus { get; init; }
    public decimal? CurrentAvailableQty { get; init; }
    public string? FailReason { get; init; }
}

public sealed class SaInvoiceShipmentLotRequest
{
    public int FromBalLocId { get; init; }
    public decimal IssueQty { get; init; }
}

public sealed class SaInvoiceSaveRequest
{
    public DateTime InvDate { get; set; }
    public string CustCode { get; set; } = string.Empty;
    public string? Currency { get; set; }
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }
    public string? SalesmanCode { get; set; }
    public string? PoNo { get; set; }
    public string? Remark { get; set; }
    public string? InvName { get; set; }
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
    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipCity { get; set; }
    public string? ShipState { get; set; }
    public string? ShipPostalCode { get; set; }
    public string? ShipCountry { get; set; }
    public string? ShipTel { get; set; }
    public string? ShipFax { get; set; }
    public byte[]? RowVersion { get; set; }
    public IReadOnlyList<SaInvoiceLineRequest>? Lines { get; set; }
}

public sealed class SaInvoiceLineRequest
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public decimal Qty { get; set; }
    public string? FrWarehouse { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount2 { get; set; }
    public decimal ItemDiscount3 { get; set; }
    public decimal ItemDiscount4 { get; set; }
    public decimal ItemDiscount5 { get; set; }
    public decimal ItemDiscount6 { get; set; }
    public decimal ItemDiscAmount { get; set; }
    public decimal ItemDiscAmount1 { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGrCode { get; set; }
    public string? OrderType { get; set; }
    public string? Remarks { get; set; }
}

public interface ISaInvoiceService
{
    Task<SaInvoiceOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime invDate,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime invDate,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> SearchAsync(
        SaInvoiceListQuery query,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> GetAsync(
        string invNo,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> SaveNewAsync(
        SaInvoiceSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> UpdateAsync(
        string invNo,
        SaInvoiceSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> DeleteAsync(
        IReadOnlyList<string> invNos,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> AddShipmentAsync(
        string invNo,
        bool overwriteExisting,
        byte[]? rowVersion,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> GetShipmentEditAsync(
        string invNo,
        int soLineNo,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> ReplaceShipmentLineAsync(
        string invNo,
        int soLineNo,
        IReadOnlyList<SaInvoiceShipmentLotRequest> lots,
        byte[]? rowVersion,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> PostAsync(
        IReadOnlyList<string> invNos,
        CancellationToken cancellationToken = default);

    Task<SaInvoiceOperationResult> RollbackAsync(
        IReadOnlyList<string> invNos,
        CancellationToken cancellationToken = default);
}
