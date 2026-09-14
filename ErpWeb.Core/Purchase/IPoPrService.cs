using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public enum PoPrErrorKind
{
    None = 0,
    Validation,
    Concurrency,
    NotFound,
    Authorization,
    BusinessRule,
    Unexpected
}

public sealed class PoPrOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public PoPrErrorKind ErrorKind { get; init; }
    public string? PrNo { get; init; }
    public string? TempDocId { get; init; }
    public PoPrDocument? Document { get; init; }
    public PoPrListPage? ListPage { get; init; }
    public PoPrLookups? Lookups { get; init; }
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static PoPrOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = PoPrErrorKind.None };

    public static PoPrOperationResult OkDocument(PoPrDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = PoPrErrorKind.None,
            Document = document,
            PrNo = document.PrNo
        };

    public static PoPrOperationResult OkList(PoPrListPage page) =>
        new() { Succeeded = true, ErrorKind = PoPrErrorKind.None, ListPage = page };

    public static PoPrOperationResult OkLookups(PoPrLookups lookups) =>
        new() { Succeeded = true, ErrorKind = PoPrErrorKind.None, Lookups = lookups };

    public static PoPrOperationResult OkTempDocId(string tempDocId) =>
        new() { Succeeded = true, ErrorKind = PoPrErrorKind.None, TempDocId = tempDocId };

    public static PoPrOperationResult Fail(
        string message,
        PoPrErrorKind kind = PoPrErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static PoPrOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = PoPrErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class PoPrListQuery
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

public sealed class PoPrListRow
{
    public string PrNo { get; init; } = string.Empty;
    public DateTime CreateDt { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Requester { get; init; }
    public string? DeptCode { get; init; }
    public string? PrType { get; init; }
    public string? Remarks { get; init; }
    public string? PoNo { get; init; }
    public int LineCount { get; init; }
    public decimal RemainingQty { get; init; }
    public bool HasConsumedLines { get; init; }
    public decimal Gross { get; init; }
    public decimal Taxes { get; init; }
    public decimal Total { get; init; }
    public string? Currency { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public bool CanCancel { get; init; }
}

public sealed class PoPrListPage
{
    public IReadOnlyList<PoPrListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class PoPrItemLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public bool IsIndirect { get; init; }
    public string? PurchaseUom { get; init; }
    public string? StdUom { get; init; }
    public decimal PackSz { get; init; }
    public decimal? UnitPrice { get; init; }
    public string? TaxGroup { get; init; }
    public string? Category { get; init; }
    public string? VendorCd { get; init; }
    public string? VendNm { get; init; }
    public decimal Moq { get; init; }
    public string? DefWarehouse { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} - {IDesc}";
}

public sealed class PoPrVendorLookupRow
{
    public string SuppCode { get; init; } = string.Empty;
    public string SuppName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(SuppName) ? SuppCode : $"{SuppCode} - {SuppName}";
}

public sealed class PoPrCodeLookupRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
}

public sealed class PoPrTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string? TaxGrDesc { get; init; }
    public decimal Percentage { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(TaxGrDesc) ? TaxGrCode : $"{TaxGrCode} - {TaxGrDesc}";
}

public sealed class PoPrLookups
{
    public IReadOnlyList<PoPrItemLookupRow> DirectItems { get; init; } = [];
    public IReadOnlyList<PoPrItemLookupRow> IndirectItems { get; init; } = [];
    public IReadOnlyList<PoPrVendorLookupRow> Vendors { get; init; } = [];
    public IReadOnlyList<PoPrTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<PoPrCodeLookupRow> Currencies { get; init; } = [];
    public IReadOnlyList<PoPrCodeLookupRow> BuyingTerms { get; init; } = [];
    public IReadOnlyList<PoPrCodeLookupRow> PaymentTerms { get; init; } = [];
    public IReadOnlyList<PoPrCodeLookupRow> AuthorisedPersons { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IReadOnlyList<PoPrCodeLookupRow> Categories { get; init; } = [];
    public IReadOnlyList<PoPrCodeLookupRow> Departments { get; init; } = [];
    public IReadOnlyList<PoPrCodeLookupRow> Projects { get; init; } = [];
    public bool DefaultInclusive { get; init; }
    public bool UseWeight { get; init; }
    public bool CanViewCost { get; init; }
}

public sealed class PoPrLineDto
{
    public short Line { get; set; }
    public DateTime? EtaDt { get; set; }
    public bool? OneTimeItemYn { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? Category { get; set; }
    public decimal Qty { get; set; }
    public decimal PackSz { get; set; }
    public string? StdUom { get; set; }
    public decimal PurchaseQty { get; set; }
    public string? PurchaseUom { get; set; }
    public string? Currency { get; set; }
    public decimal UnitPrice { get; set; }
    public bool? OneTimeVendor { get; set; }
    public string? VendorCd { get; set; }
    public string? VendNm { get; set; }
    public string? Purpose { get; set; }
    public string? Status { get; set; }
    public decimal StdQty { get; set; }
    public decimal WtQty { get; set; }
    public string? WtUom { get; set; }
    public string? PaymentTerm { get; set; }
    public string? BuyingTerm { get; set; }
    public string? RepairType { get; set; }
    public decimal Amount { get; set; }
    public string? PoNo { get; set; }
    public string? TaxGroup { get; set; }
    public decimal TaxAmount { get; set; }
    public bool IsInclusive { get; set; }
    public string? ToWarehouse { get; set; }
    public string? SoNo { get; set; }
    public int? SoLine { get; set; }
    public decimal NetAmount { get; set; }
    public bool IsConsumed => !string.IsNullOrWhiteSpace(PoNo);
}

public sealed class PoPrDocument
{
    public string PrNo { get; init; } = string.Empty;
    public DateTime CreateDt { get; set; }
    public string Status { get; init; } = string.Empty;
    public string? Requester { get; set; }
    public string? DeptCode { get; set; }
    public string? CheckedBy { get; set; }
    public string? AuthorisedBy { get; set; }
    public string? AuthorisedBy2nd { get; set; }
    public string? PrType { get; set; }
    public string? LocationCode { get; init; }
    public string? PoNo { get; init; }
    public string? ApprReason { get; init; }
    public string? ProjId { get; set; }
    public string? Remarks { get; set; }
    public string? Currency { get; init; }
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
    public bool HasConsumedLines { get; init; }
    public IReadOnlyList<PoPrLineDto> Lines { get; init; } = [];
}

public sealed class PoPrSaveRequest
{
    public DateTime CreateDt { get; set; }
    public string? Requester { get; set; }
    public string? DeptCode { get; set; }
    public string? CheckedBy { get; set; }
    public string? AuthorisedBy { get; set; }
    public string? AuthorisedBy2nd { get; set; }
    public string? PrType { get; set; }
    public string? ProjId { get; set; }
    public string? Remarks { get; set; }
    public string? TempDocId { get; set; }
    public byte[]? RowVersion { get; set; }
    public IReadOnlyList<PoPrLineDto> Lines { get; set; } = [];
}

public sealed class PoPrKeyedRequest
{
    public string PrNo { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
}

public sealed class PoPrCancelRequest
{
    public string PrNo { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
    public string ApprReason { get; init; } = string.Empty;
}

public interface IPoPrService
{
    Task<PoPrOperationResult> CreateTempDocIdAsync(CancellationToken cancellationToken = default);

    Task<PoPrOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<PoPrOperationResult> SearchAsync(PoPrListQuery query, CancellationToken cancellationToken = default);

    Task<PoPrOperationResult> GetAsync(string prNo, CancellationToken cancellationToken = default);

    Task<PoPrOperationResult> SaveNewAsync(PoPrSaveRequest? request, CancellationToken cancellationToken = default);

    Task<PoPrOperationResult> UpdateAsync(string prNo, PoPrSaveRequest? request, CancellationToken cancellationToken = default);

    Task<PoPrOperationResult> CopyAsync(string sourcePrNo, CancellationToken cancellationToken = default);

    Task<PoPrOperationResult> CancelAsync(PoPrCancelRequest request, CancellationToken cancellationToken = default);

    Task<PoPrOperationResult> DeleteAsync(PoPrKeyedRequest request, CancellationToken cancellationToken = default);
}
