using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public enum SaDoErrorKind
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

public sealed class SaDoOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public SaDoErrorKind ErrorKind { get; init; }
    public string? DoNo { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public bool RequiresConfirmation { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public SaDoDocument? Document { get; init; }
    public SaDoCustomerDefaults? CustomerDefaults { get; init; }
    public SaDoListPage? ListPage { get; init; }
    public IReadOnlyList<SaDoPostingItemResult> Posting { get; init; } = [];
    public IReadOnlyList<SaDoItemLookupRow> Items { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IReadOnlyList<SaDoCustomerLookupRow> Customers { get; init; } = [];
    public IReadOnlyList<SaDoTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> PayCodes { get; init; } = [];
    public IReadOnlyList<SaDoBillableLineDto> BillableLines { get; init; } = [];
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static SaDoOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = SaDoErrorKind.None };

    public static SaDoOperationResult OkSaved(string doNo) =>
        new() { Succeeded = true, ErrorKind = SaDoErrorKind.None, DoNo = doNo };

    public static SaDoOperationResult OkDocument(SaDoDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaDoErrorKind.None,
            Document = document,
            DoNo = document.DoNo
        };

    public static SaDoOperationResult OkList(SaDoListPage page) =>
        new() { Succeeded = true, ErrorKind = SaDoErrorKind.None, ListPage = page };

    public static SaDoOperationResult OkLookups(
        IReadOnlyList<SaDoItemLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses,
        IReadOnlyList<SaDoCustomerLookupRow> customers,
        IReadOnlyList<SaDoTaxGroupLookupRow> taxGroups,
        IReadOnlyList<IvCodeLookupRow> payCodes) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaDoErrorKind.None,
            Items = items,
            Warehouses = warehouses,
            Customers = customers,
            TaxGroups = taxGroups,
            PayCodes = payCodes
        };

    public static SaDoOperationResult OkDefaults(SaDoCustomerDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = SaDoErrorKind.None, CustomerDefaults = defaults };

    public static SaDoOperationResult OkRate(decimal rate, bool valid) =>
        new() { Succeeded = true, ErrorKind = SaDoErrorKind.None, CurrRate = rate, CurrRateValid = valid };

    public static SaDoOperationResult OkBillableLines(IReadOnlyList<SaDoBillableLineDto> lines) =>
        new() { Succeeded = true, ErrorKind = SaDoErrorKind.None, BillableLines = lines };

    public static SaDoOperationResult OkConfirmation(SaDoDocument document, string message) =>
        new()
        {
            Succeeded = false,
            RequiresConfirmation = true,
            ErrorKind = SaDoErrorKind.Confirmation,
            ErrorMessage = message,
            Document = document,
            DoNo = document.DoNo
        };

    public static SaDoOperationResult OkPosting(IReadOnlyList<SaDoPostingItemResult> posting)
    {
        var ok = posting.Count(x => x.Succeeded);
        var fail = posting.Count - ok;
        var attempted = posting.Count(x => !string.Equals(x.Outcome, "Not attempted", StringComparison.OrdinalIgnoreCase));
        var summary = fail == 0
            ? null
            : string.Join(" ", posting.Where(x => !x.Succeeded).Select(x => $"{x.DoNo}: {x.ErrorMessage}"));
        return new SaDoOperationResult
        {
            Succeeded = fail == 0 && attempted > 0,
            ErrorKind = fail == 0 ? SaDoErrorKind.None : SaDoErrorKind.BusinessRule,
            ErrorMessage = summary,
            SucceededCount = ok,
            FailedCount = fail,
            Posting = posting
        };
    }

    public static SaDoOperationResult Fail(
        string message,
        SaDoErrorKind kind = SaDoErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static SaDoOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = SaDoErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class SaDoPostingItemResult
{
    public string DoNo { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public string? ReasonCode { get; init; }

    public static SaDoPostingItemResult Posted(string doNo) =>
        new() { DoNo = doNo, Succeeded = true, Outcome = "Posted" };

    public static SaDoPostingItemResult RolledBack(string doNo) =>
        new() { DoNo = doNo, Succeeded = true, Outcome = "Rolled back" };

    public static SaDoPostingItemResult ForceClosed(string doNo) =>
        new() { DoNo = doNo, Succeeded = true, Outcome = "Force closed" };

    public static SaDoPostingItemResult Failed(string doNo, string reason) =>
        new() { DoNo = doNo, Succeeded = false, Outcome = "Failed: " + reason, ErrorMessage = reason };

    public static SaDoPostingItemResult Failed(string doNo, string reasonCode, string userMessage) =>
        new()
        {
            DoNo = doNo,
            Succeeded = false,
            Outcome = "Failed: " + userMessage,
            ErrorMessage = userMessage,
            ReasonCode = reasonCode
        };

    public static SaDoPostingItemResult NotAttempted(string doNo) =>
        new() { DoNo = doNo, Succeeded = false, Outcome = "Not attempted", ErrorMessage = "Not attempted" };
}

/// <summary>A key+RowVersion pair used for batch Post/Rollback/Delete/ForceClose from a list view.</summary>
public sealed class SaDoKeyedRequest
{
    public string DoNo { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaDoListQuery
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

public sealed class SaDoListRow
{
    public string DoNo { get; init; } = string.Empty;
    public DateTime DoDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CustCode { get; init; } = string.Empty;
    public string? CustName { get; init; }
    /// <summary>Tax-inclusive total (legacy <c>TotAmnt</c>). See <see cref="Totals"/> for the split.</summary>
    public decimal TotAmnt { get; init; }
    /// <summary>R7: ex-tax gross.</summary>
    public decimal GrossExTax { get; init; }
    /// <summary>R7: tax portion.</summary>
    public decimal Tax { get; init; }
    /// <summary>R7: the full money projection (ex-tax gross / tax / tax-inclusive total).</summary>
    public SalesDocTotals Totals { get; init; }
    public int LineCount { get; init; }
    public bool ShipmentComplete { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    /// <summary>Required for optimistic concurrency in batch operations from the list view.</summary>
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaDoListPage
{
    public IReadOnlyList<SaDoListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SaDoCustomerLookupRow
{
    public string CustCode { get; init; } = string.Empty;
    public string CustName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string? DiscountMethod { get; init; }
    public bool? DecPoint { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(CustName) ? CustCode : $"{CustCode} — {CustName}";
}

public sealed class SaDoItemLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? StdUom { get; init; }
    public decimal? StdPackSize { get; init; }
    public decimal? SellingPrice { get; init; }
    public string? TaxGroup { get; init; }
    public bool StockControl { get; init; }
    public string? DefWarehouse { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} — {IDesc}";
}

public sealed class SaDoTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string? TaxGrDesc { get; init; }
    public decimal Percentage { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(TaxGrDesc) ? TaxGrCode : $"{TaxGrCode} — {TaxGrDesc}";
}

public sealed class SaDoCustomerDefaults
{
    public string CustCode { get; init; } = string.Empty;
    public string CustName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public string? TaxGrCode { get; init; }
    public bool? Taxable { get; init; }
    public string? PayCode { get; init; }
    public string? SalesRep { get; init; }
    public string? DiscountMethod { get; init; }
    public bool? DecPoint { get; init; }
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
    public string? InvName { get; init; }
    public string? InvAddress1 { get; init; }
    public string? InvAddress2 { get; init; }
    public string? InvAddress3 { get; init; }
    public string? InvCity { get; init; }
    public string? InvState { get; init; }
    public string? InvPostalCode { get; init; }
    public string? InvCountry { get; init; }
    public string? InvTel { get; init; }
    public string? InvFax { get; init; }
    public IReadOnlyList<SaCustAddressVm> ShipToAddresses { get; init; } = [];
}

public sealed class SaDoDocument
{
    public string DoNo { get; init; } = string.Empty;
    public DateTime DoDate { get; set; }
    public string Status { get; init; } = string.Empty;
    public string BillingStatus { get; init; } = SaDualStatuses.None;
    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; init; }
    public string? Prefix { get; init; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public string? SalesRep { get; init; }
    public string? Ref1 { get; init; }
    public string? ProjId { get; init; }
    public string? Remarks { get; init; }
    public string? ShipVia { get; init; }
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
    public string? InvName { get; init; }
    public string? InvAddress1 { get; init; }
    public string? InvAddress2 { get; init; }
    public string? InvAddress3 { get; init; }
    public string? InvCity { get; init; }
    public string? InvState { get; init; }
    public string? InvPostalCode { get; init; }
    public string? InvCountry { get; init; }
    public string? InvTel { get; init; }
    public string? InvFax { get; init; }
    public decimal GrossAmnt { get; init; }
    public decimal Taxes { get; init; }
    public decimal TotAmnt { get; init; }
    public bool ShipmentComplete { get; init; }
    public int? SpBatchNo { get; init; }
    public string? SpBatchStatus { get; init; }
    /// <summary>
    /// NEW DO with stock lines that require shipment and no SP batch yet.
    /// </summary>
    public bool NeedsShipment { get; init; }
    /// <summary>Required for optimistic concurrency in all mutating operations.</summary>
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<SaDoLineDto> Lines { get; init; } = [];
    public IReadOnlyList<SaDoShipmentLineDto> Shipment { get; set; } = [];
}

public sealed class SaDoLineDto
{
    public int Line { get; init; }
    public string SoNo { get; init; } = string.Empty;
    public short? SoLine { get; init; }
    public short? CustRel { get; init; }
    public string? CustPo { get; init; }
    public decimal SoConsumedQty { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal StdQty { get; init; }
    public decimal StdPsize { get; init; }
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
    public string? TaxGroup { get; init; }
    public decimal TaxAmt { get; init; }
    public decimal NetAmount { get; init; }
    public decimal LocalAmount { get; init; }
    public string? OrderType { get; init; }
    public bool StockControl { get; init; }
    public string? Remarks { get; init; }
    public decimal ShipQty { get; init; }
    public bool ShipmentComplete { get; init; }
}

public sealed class SaDoShipmentLineDto
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

public sealed class SaDoShipmentLotRequest
{
    public int FromBalLocId { get; init; }
    public decimal IssueQty { get; init; }
}

public sealed class SaDoSaveRequest
{
    public DateTime DoDate { get; set; }
    public string CustCode { get; set; } = string.Empty;
    public string? Currency { get; set; }
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }
    public string? SalesRep { get; set; }
    public string? Ref1 { get; set; }
    public string? ProjId { get; set; }
    public string? Remarks { get; set; }
    public string? ShipVia { get; set; }
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
    public string? InvName { get; set; }
    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvCity { get; set; }
    public string? InvState { get; set; }
    public string? InvPostalCode { get; set; }
    public string? InvCountry { get; set; }
    public string? InvTel { get; set; }
    public string? InvFax { get; set; }
    public byte[]? RowVersion { get; set; }
    public IReadOnlyList<SaDoLineRequest>? Lines { get; set; }
}

public sealed class SaDoLineRequest
{
    public int Line { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? SoNo { get; set; }
    public short? SoLine { get; set; }
    public short? CustRel { get; set; }
    public bool LinkDo { get; set; }
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

public sealed class SaDoBillableLineDto
{
    public string DoNo { get; init; } = string.Empty;
    public short Line { get; init; }
    public string SoNo { get; init; } = string.Empty;
    public short SoLine { get; init; }
    public short CustRel { get; init; } = 1;
    public string? CustPo { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public decimal Qty { get; init; }
    public decimal RemainingBillableQty { get; init; }
    public decimal UnitPrice { get; init; }
    public string? SellingUom { get; init; }
    public string? FrWarehouse { get; init; }
    public bool StockControl { get; init; }
}

public interface ISaDoService
{
    Task<SaDoOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime doDate,
        CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime doDate,
        CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> SearchAsync(
        SaDoListQuery query,
        CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> GetAsync(
        string doNo,
        CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> SaveNewAsync(
        SaDoSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> UpdateAsync(
        string doNo,
        SaDoSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Delete DOs (NEW status only). RowVersion required per item.</summary>
    Task<SaDoOperationResult> DeleteAsync(
        IReadOnlyList<SaDoKeyedRequest> items,
        CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> AddShipmentAsync(
        string doNo,
        bool overwriteExisting,
        byte[]? rowVersion,
        CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> GetShipmentEditAsync(
        string doNo,
        int soLineNo,
        CancellationToken cancellationToken = default);

    Task<SaDoOperationResult> ReplaceShipmentLineAsync(
        string doNo,
        int soLineNo,
        IReadOnlyList<SaDoShipmentLotRequest> lots,
        byte[]? rowVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Post DOs (NEW → POSTED). RowVersion required per item.</summary>
    Task<SaDoOperationResult> PostAsync(
        IReadOnlyList<SaDoKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>Rollback DOs (POSTED → NEW). Keeps SP batch. RowVersion required per item.</summary>
    Task<SaDoOperationResult> RollbackAsync(
        IReadOnlyList<SaDoKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Force-close DOs (POSTED → CLOSED). Deletes SP batch and details without reversing stock.
    /// RowVersion required per item.
    /// </summary>
    Task<SaDoOperationResult> ForceCloseAsync(
        IReadOnlyList<SaDoKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <summary>Posted delivery order lines (SO-bound or standalone) with remaining billable qty for invoice picker.</summary>
    Task<SaDoOperationResult> GetBillableLinesAsync(
        string custCode,
        string? currency,
        CancellationToken cancellationToken = default);
}
