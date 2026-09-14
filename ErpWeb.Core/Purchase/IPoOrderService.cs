using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public enum PoOrderErrorKind
{
    None = 0,
    Validation,
    Concurrency,
    NotFound,
    Authorization,
    BusinessRule,
    Unexpected
}

public sealed class PoOrderOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public PoOrderErrorKind ErrorKind { get; init; }
    public string? PoNo { get; init; }
    public short PoRelNo { get; init; }
    public string? TempDocId { get; init; }
    public PoOrderDocument? Document { get; init; }
    public PoOrderListPage? ListPage { get; init; }
    public PoOrderLookups? Lookups { get; init; }
    public PoOrderSupplierDefaults? SupplierDefaults { get; init; }
    public IReadOnlyList<PoPrForPoRow>? PrRows { get; init; }
    public IReadOnlyList<PoPrRemainingLineDto>? PrRemainingLines { get; init; }
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static PoOrderOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = PoOrderErrorKind.None };

    public static PoOrderOperationResult OkDocument(PoOrderDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = PoOrderErrorKind.None,
            Document = document,
            PoNo = document.PoNo,
            PoRelNo = document.PoRelNo
        };

    public static PoOrderOperationResult OkList(PoOrderListPage page) =>
        new() { Succeeded = true, ErrorKind = PoOrderErrorKind.None, ListPage = page };

    public static PoOrderOperationResult OkLookups(PoOrderLookups lookups) =>
        new() { Succeeded = true, ErrorKind = PoOrderErrorKind.None, Lookups = lookups };

    public static PoOrderOperationResult OkSupplierDefaults(PoOrderSupplierDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = PoOrderErrorKind.None, SupplierDefaults = defaults };

    public static PoOrderOperationResult OkTempDocId(string tempDocId) =>
        new() { Succeeded = true, ErrorKind = PoOrderErrorKind.None, TempDocId = tempDocId };

    public static PoOrderOperationResult OkPrRows(IReadOnlyList<PoPrForPoRow> rows) =>
        new() { Succeeded = true, ErrorKind = PoOrderErrorKind.None, PrRows = rows };

    public static PoOrderOperationResult OkPrRemainingLines(IReadOnlyList<PoPrRemainingLineDto> lines) =>
        new() { Succeeded = true, ErrorKind = PoOrderErrorKind.None, PrRemainingLines = lines };

    public static PoOrderOperationResult Fail(
        string message,
        PoOrderErrorKind kind = PoOrderErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static PoOrderOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = PoOrderErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class PoOrderListQuery
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

public sealed class PoOrderListRow
{
    public string PoNo { get; init; } = string.Empty;
    public short PoRelNo { get; init; }
    public DateTime? PoDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? VendCode { get; init; }
    public string? VendName { get; init; }
    public string? Buyer { get; init; }
    public string? CurCode { get; init; }
    public decimal Gross { get; init; }
    public decimal Taxes { get; init; }
    public decimal Total { get; init; }
    public DateTime? EtaDate { get; init; }
    public int LineCount { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public bool CanCancel { get; init; }
    public bool CanRevise { get; init; }
    public bool CanForceClose { get; init; }
    public bool CanReopen { get; init; }
    public bool IsForceClosed { get; init; }
}

public sealed class PoOrderListPage
{
    public IReadOnlyList<PoOrderListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class PoOrderRevisionRow
{
    public short PoRelNo { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? RevisionReason { get; init; }
}

public sealed class PoOrderItemLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public bool IsIndirect { get; init; }
    public string? IType { get; init; }
    public string? PurchaseUom { get; init; }
    public string? StdUom { get; init; }
    public decimal PackSz { get; init; }
    public decimal? UnitPrice { get; init; }
    public string? TaxGroup { get; init; }
    public string? Category { get; init; }
    public string? DefWarehouse { get; init; }
    public decimal Moq { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} - {IDesc}";
}

public sealed class PoOrderVendorLookupRow
{
    public string SuppCode { get; init; } = string.Empty;
    public string SuppName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string? PoPrefix { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(SuppName) ? SuppCode : $"{SuppCode} - {SuppName}";
}

public sealed class PoOrderCodeLookupRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
}

public sealed class PoOrderTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string? TaxGrDesc { get; init; }
    public decimal Percentage { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(TaxGrDesc) ? TaxGrCode : $"{TaxGrCode} - {TaxGrDesc}";
}

public sealed class PoOrderShipToLookupRow
{
    public int Line { get; init; }
    public string? Name { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? Address3 { get; init; }
    public string? Address4 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Postal { get; init; }
    public string? CountryCode { get; init; }
    public string? Tel { get; init; }
    public string? Fax { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? $"Address {Line}" : Name;
}

public sealed class PoOrderLookups
{
    public IReadOnlyList<PoOrderItemLookupRow> DirectItems { get; init; } = [];
    public IReadOnlyList<PoOrderItemLookupRow> IndirectItems { get; init; } = [];
    public IReadOnlyList<PoOrderVendorLookupRow> Vendors { get; init; } = [];
    public IReadOnlyList<PoOrderTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<PoOrderCodeLookupRow> Currencies { get; init; } = [];
    public IReadOnlyList<PoOrderCodeLookupRow> BuyingTerms { get; init; } = [];
    public IReadOnlyList<PoOrderCodeLookupRow> PaymentTerms { get; init; } = [];
    public IReadOnlyList<PoOrderCodeLookupRow> Buyers { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IReadOnlyList<PoOrderCodeLookupRow> Departments { get; init; } = [];
    public IReadOnlyList<PoOrderCodeLookupRow> Projects { get; init; } = [];
    public bool DefaultInclusive { get; init; }
    public bool UseWeight { get; init; }
    public bool CanViewCost { get; init; }
}

public sealed class PoOrderSupplierDefaults
{
    public string VendCode { get; init; } = string.Empty;
    public string VendName { get; init; } = string.Empty;
    public string? CurCode { get; init; }
    public string? TermCode { get; init; }
    public string? BuyingTerm { get; init; }
    public string? TaxGrpCode { get; init; }
    public string? PoPrefix { get; init; }
    public string? ContactPerson { get; init; }
    public string? Email { get; init; }
    public string? Website { get; init; }
    public string? RegNo { get; init; }
    public string? VendAddress1 { get; init; }
    public string? VendAddress2 { get; init; }
    public string? VendAddress3 { get; init; }
    public string? VendAddress4 { get; init; }
    public string? VendCity { get; init; }
    public string? VendState { get; init; }
    public string? VendPostal { get; init; }
    public string? VendCountryCode { get; init; }
    public string? VendTel { get; init; }
    public string? VendFax { get; init; }
    public IReadOnlyList<PoOrderShipToLookupRow> ShipToAddresses { get; init; } = [];
}

public sealed class PoOrderLineDto
{
    public short Line { get; set; }
    public string? PrNo { get; set; }
    public short? PrLineNo { get; set; }
    public bool? OneTime { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? IType { get; set; }
    public decimal PoUnitPrice { get; set; }
    public decimal PoQty { get; set; }
    public decimal PoPurQty { get; set; }
    public decimal WtQty { get; set; }
    public decimal Amount { get; set; }
    public decimal RecvQty { get; set; }
    public decimal ReturnQty { get; set; }
    public decimal BalanceQty { get; set; }
    public decimal OverRecvQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public decimal PackSz { get; set; }
    public string? StdUom { get; set; }
    public string? WtUom { get; set; }
    public string? PurchaseUom { get; set; }
    public DateTime? EtaDate { get; set; }
    public string? CurCode { get; set; }
    public string? Remarks { get; set; }
    public string? PoDesc { get; set; }
    public DateTime? RecvDate { get; set; }
    public string? RepairType { get; set; }
    public decimal Discount { get; set; }
    public decimal ItemDiscount { get; set; }
    public string? DiscountType { get; set; }
    public decimal NetAmount { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public string? DiscountType1 { get; set; }
    public string? CjNo { get; set; }
    public int? CjRelNo { get; set; }
    public int? CjLine { get; set; }
    public string? ProjId { get; set; }
    public string? VendorPartNo { get; set; }
    public string? TaxGroup { get; set; }
    public decimal TaxAmount { get; set; }
    public bool IsInclusive { get; set; }
    public string? ToWarehouse { get; set; }
    public string? Requester { get; set; }
    public bool IsReceived => RecvQty > 0m;
}

public sealed class PoOrderDocument
{
    public string PoNo { get; init; } = string.Empty;
    public short PoRelNo { get; init; }
    public bool IsLatest { get; init; }
    public DateTime? PoDate { get; set; }
    public string Status { get; init; } = string.Empty;
    public string? Buyer { get; set; }
    public bool? OneTime { get; set; }
    public string? VendCode { get; set; }
    public string? VendName { get; set; }
    public string? VendAddress1 { get; set; }
    public string? VendAddress2 { get; set; }
    public string? VendAddress3 { get; set; }
    public string? VendAddress4 { get; set; }
    public string? VendCity { get; set; }
    public string? VendState { get; set; }
    public string? VendPostal { get; set; }
    public string? VendCountryCode { get; set; }
    public string? VendTel { get; set; }
    public string? VendFax { get; set; }
    public string? CurCode { get; set; }
    public string? TermCode { get; set; }
    public string? ContactPerson { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipAddress4 { get; set; }
    public string? ShipCity { get; set; }
    public string? ShipState { get; set; }
    public string? ShipPostal { get; set; }
    public string? ShipCountryCode { get; set; }
    public string? ShipTel { get; set; }
    public string? ShipFax { get; set; }
    public string? TaxGrpCode { get; set; }
    public decimal TaxAmount { get; init; }
    public decimal Discount { get; set; }
    public string? SiRemark { get; set; }
    public string? RegNo { get; set; }
    public string? DeptCode { get; set; }
    public string? BuyingTerm { get; set; }
    public string? LocationCode { get; init; }
    public string? ProjId { get; set; }
    public string? Prefix { get; init; }
    public string? CheckBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? AuthorisedBy { get; set; }
    public string? QuatationNo { get; set; }
    public string? Ref1 { get; set; }
    public string? Ref2 { get; set; }
    public string? Ref3 { get; set; }
    public string? Ref4 { get; set; }
    public string? RevisionReason { get; set; }
    public decimal Gross { get; init; }
    public decimal Taxes { get; init; }
    public decimal Total { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? ModifiedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public bool CanCancel { get; init; }
    public bool CanRevise { get; init; }
    public bool CanForceClose { get; init; }
    public bool CanReopen { get; init; }
    public bool IsForceClosed { get; init; }
    public string? CloseReason { get; init; }
    public string? ClosedBy { get; init; }
    public DateTime? ClosedOn { get; init; }
    public bool HasReceivedLines { get; init; }
    public IReadOnlyList<PoOrderLineDto> Lines { get; init; } = [];
    public IReadOnlyList<PoOrderRevisionRow> Revisions { get; init; } = [];
}

public sealed class PoOrderSaveRequest
{
    public DateTime? PoDate { get; set; }
    public string? Buyer { get; set; }
    public bool? OneTime { get; set; }
    public string? VendCode { get; set; }
    public string? VendName { get; set; }
    public string? VendAddress1 { get; set; }
    public string? VendAddress2 { get; set; }
    public string? VendAddress3 { get; set; }
    public string? VendAddress4 { get; set; }
    public string? VendCity { get; set; }
    public string? VendState { get; set; }
    public string? VendPostal { get; set; }
    public string? VendCountryCode { get; set; }
    public string? VendTel { get; set; }
    public string? VendFax { get; set; }
    public string? CurCode { get; set; }
    public string? TermCode { get; set; }
    public string? ContactPerson { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipAddress4 { get; set; }
    public string? ShipCity { get; set; }
    public string? ShipState { get; set; }
    public string? ShipPostal { get; set; }
    public string? ShipCountryCode { get; set; }
    public string? ShipTel { get; set; }
    public string? ShipFax { get; set; }
    public string? TaxGrpCode { get; set; }
    public decimal Discount { get; set; }
    public string? SiRemark { get; set; }
    public string? RegNo { get; set; }
    public string? DeptCode { get; set; }
    public string? BuyingTerm { get; set; }
    public string? ProjId { get; set; }
    public string? CheckBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? AuthorisedBy { get; set; }
    public string? QuatationNo { get; set; }
    public string? Ref1 { get; set; }
    public string? Ref2 { get; set; }
    public string? Ref3 { get; set; }
    public string? Ref4 { get; set; }
    public string? RevisionReason { get; set; }
    public bool ClearLinesOnSupplierChange { get; set; }
    public string? TempDocId { get; set; }
    public short? PoRelNo { get; set; }
    public byte[]? RowVersion { get; set; }
    public IReadOnlyList<PoOrderLineDto> Lines { get; set; } = [];
}

public sealed class PoOrderKeyedRequest
{
    public string PoNo { get; init; } = string.Empty;
    public short? PoRelNo { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public string? CloseReason { get; init; }
}

public sealed class PoPrForPoRow
{
    public string PrNo { get; init; } = string.Empty;
    public DateTime CreateDt { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Requester { get; init; }
    public string? ProjId { get; init; }
    public string? Remarks { get; init; }
}

public sealed class PoPrRemainingLineDto
{
    public string PrNo { get; init; } = string.Empty;
    public short Line { get; init; }
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public decimal PurchaseQty { get; init; }
    public decimal RemainingQty { get; init; }
    public string? PurchaseUom { get; init; }
    public decimal PackSz { get; init; }
    public string? StdUom { get; init; }
    public decimal UnitPrice { get; init; }
    public string? Currency { get; init; }
    public string? VendorCd { get; init; }
    public string? VendNm { get; init; }
    public string? TaxGroup { get; init; }
    public bool IsInclusive { get; init; }
    public string? ToWarehouse { get; init; }
    public DateTime? EtaDt { get; init; }
    public string? Purpose { get; init; }
    public bool? OneTimeItemYn { get; init; }
}

public interface IPoOrderService
{
    Task<PoOrderOperationResult> CreateTempDocIdAsync(CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> GetSupplierDefaultsAsync(
        string vendCode,
        CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> SearchAsync(PoOrderListQuery query, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> GetAsync(string poNo, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> GetAsync(string poNo, short poRelNo, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> GetReviseDraftAsync(string poNo, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> SearchPrForPoAsync(
        string? vendorCd,
        string? searchText,
        CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> GetPrRemainingLinesAsync(
        string prNo,
        string? vendorCd = null,
        CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> SaveNewAsync(PoOrderSaveRequest? request, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> UpdateAsync(
        string poNo,
        PoOrderSaveRequest? request,
        CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> CopyAsync(string sourcePoNo, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> ReviseAsync(
        string poNo,
        PoOrderSaveRequest? request,
        CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> CancelAsync(PoOrderKeyedRequest request, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> DeleteAsync(PoOrderKeyedRequest request, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> ForceCloseAsync(PoOrderKeyedRequest request, CancellationToken cancellationToken = default);

    Task<PoOrderOperationResult> ReopenAsync(PoOrderKeyedRequest request, CancellationToken cancellationToken = default);
}
