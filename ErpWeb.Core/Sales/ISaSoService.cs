using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public enum SaSoErrorKind
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

public sealed class SaSoOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public SaSoErrorKind ErrorKind { get; init; }
    public string? SoNo { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public SaSoDocument? Document { get; init; }
    public SaSoCustomerDefaults? CustomerDefaults { get; init; }
    public SaSoListPage? ListPage { get; init; }
    public IReadOnlyList<SaSoLineDto> RemainingLines { get; init; } = [];
    public IReadOnlyList<SaSoItemLookupRow> Items { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IReadOnlyList<SaSoCustomerLookupRow> Customers { get; init; } = [];
    public IReadOnlyList<SaSoTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> PayCodes { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> Departments { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> Projects { get; init; } = [];
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static SaSoOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = SaSoErrorKind.None };

    public static SaSoOperationResult OkDocument(SaSoDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaSoErrorKind.None,
            Document = document,
            SoNo = document.SoNo
        };

    public static SaSoOperationResult OkList(SaSoListPage page) =>
        new() { Succeeded = true, ErrorKind = SaSoErrorKind.None, ListPage = page };

    public static SaSoOperationResult OkLookups(
        IReadOnlyList<SaSoItemLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses,
        IReadOnlyList<SaSoCustomerLookupRow> customers,
        IReadOnlyList<SaSoTaxGroupLookupRow> taxGroups,
        IReadOnlyList<IvCodeLookupRow> payCodes,
        IReadOnlyList<IvCodeLookupRow>? departments = null,
        IReadOnlyList<IvCodeLookupRow>? projects = null) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaSoErrorKind.None,
            Items = items,
            Warehouses = warehouses,
            Customers = customers,
            TaxGroups = taxGroups,
            PayCodes = payCodes,
            Departments = departments ?? [],
            Projects = projects ?? []
        };

    public static SaSoOperationResult OkDefaults(SaSoCustomerDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = SaSoErrorKind.None, CustomerDefaults = defaults };

    public static SaSoOperationResult OkRate(decimal rate, bool valid) =>
        new() { Succeeded = true, ErrorKind = SaSoErrorKind.None, CurrRate = rate, CurrRateValid = valid };

    public static SaSoOperationResult OkRemainingLines(string soNo, IReadOnlyList<SaSoLineDto> lines) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaSoErrorKind.None,
            SoNo = soNo,
            RemainingLines = lines
        };

    public static SaSoOperationResult Fail(
        string message,
        SaSoErrorKind kind = SaSoErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static SaSoOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = SaSoErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class SaSoKeyedRequest
{
    public string SoNo { get; init; } = string.Empty;
    /// <summary>Optional. When set, must match the locked current CustRel or the call fails as SUPERSEDED.</summary>
    public short? CustRel { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaSoListQuery
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

public sealed class SaSoListRow
{
    public string SoNo { get; init; } = string.Empty;
    public short CustRel { get; init; }
    public DateTime SoDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string FulfillmentStatus { get; init; } = SaDualStatuses.None;
    public string BillingStatus { get; init; } = SaDualStatuses.None;
    public decimal FulfillmentPct { get; init; }
    public decimal BillingPct { get; init; }
    public string CustCode { get; init; } = string.Empty;
    public string? CustName { get; init; }
    public string? CustPo { get; init; }
    public decimal TotAmnt { get; init; }
    public int LineCount { get; init; }
    public DateTime? ClosedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>UX hint only — service revalidates at mutate time.</summary>
    public bool CanRevise { get; init; }

    /// <summary>UX hint only — service revalidates at mutate time.</summary>
    public bool CanDelete { get; init; }

    /// <summary>
    /// Short usage reason when <see cref="CanDelete"/> / <see cref="CanRevise"/> are false due to revision use
    /// (not status). Null when unused or status-gated.
    /// </summary>
    public string? MutationBlockReason { get; init; }
}

public sealed class SaSoListPage
{
    public IReadOnlyList<SaSoListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SaSoCustomerLookupRow
{
    public string CustCode { get; init; } = string.Empty;
    public string CustName { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public string? DiscountMethod { get; init; }
    public bool? DecPoint { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(CustName) ? CustCode : $"{CustCode} - {CustName}";
}

public sealed class SaSoItemLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? SellingUom { get; init; }
    public string? StdUom { get; init; }
    public decimal? StdPackSize { get; init; }
    public decimal? SellingPrice { get; init; }
    public string? TaxGroup { get; init; }
    public bool StockControl { get; init; }
    public string? DefWarehouse { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} - {IDesc}";
}

public sealed class SaSoTaxGroupLookupRow
{
    public string TaxGrCode { get; init; } = string.Empty;
    public string? TaxGrDesc { get; init; }
    public decimal Percentage { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(TaxGrDesc) ? TaxGrCode : $"{TaxGrCode} - {TaxGrDesc}";
}

public sealed class SaSoCustomerDefaults
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
    public string? ShipAddress4 { get; init; }
    public string? ShipCity { get; init; }
    public string? ShipState { get; init; }
    public string? ShipPostalCode { get; init; }
    public string? ShipCountry { get; init; }
    public string? ShipTel { get; init; }
    public string? ShipFax { get; init; }
    public IReadOnlyList<SaCustAddressVm> ShipToAddresses { get; init; } = [];
}

public sealed class SaSoDocument
{
    public string SoNo { get; init; } = string.Empty;
    public short CustRel { get; init; } = 1;
    public bool IsCurrent { get; init; } = true;
    public short LastCustRel { get; init; } = 1;
    public string? RevisionReason { get; init; }
    public DateTime SoDate { get; set; }
    public string Status { get; init; } = string.Empty;
    public string FulfillmentStatus { get; init; } = SaDualStatuses.None;
    public string BillingStatus { get; init; } = SaDualStatuses.None;
    public string? ClosedReason { get; init; }
    public DateTime? ClosedDate { get; init; }
    public string? ClosedBy { get; init; }
    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; init; }
    public string? CustPo { get; init; }
    public string? Ref1 { get; init; }
    public string? ProjId { get; init; }
    public string? Prefix { get; init; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public string? SalesRep { get; init; }
    public string? Remarks { get; init; }
    public string? ShipName { get; init; }
    public string? ShipAddress1 { get; init; }
    public string? ShipAddress2 { get; init; }
    public string? ShipAddress3 { get; init; }
    public string? ShipAddress4 { get; init; }
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
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? ModifiedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<SaSoLineDto> Lines { get; init; } = [];
    public IReadOnlyList<SaSoRevisionHistoryRow> Revisions { get; init; } = [];
}

public sealed class SaSoRevisionHistoryRow
{
    public short CustRel { get; init; }
    public bool IsCurrent { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? RevisionReason { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
}

public sealed class SaSoLineDto
{
    public int Line { get; init; }
    public short CustRel { get; init; }
    public string? CustPo { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? CustICode { get; init; }
    public decimal OrderQty { get; init; }
    public decimal ShippedQty { get; init; }
    public decimal BalanceQty { get; init; }
    public decimal DeliveredQty { get; init; }
    public decimal InvoicedQty { get; init; }
    /// <summary>R3: quantity written off by a DO force-close. Monotonic; never revenue.</summary>
    public decimal WrittenOffQty { get; init; }
    public decimal RemainingBillableQty { get; init; }
    public decimal StdQty { get; init; }
    public decimal StdPsize { get; init; }
    public string? SellingUom { get; init; }
    public string? StdUom { get; init; }
    public string? Warehouse { get; init; }
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
    public string? Classification { get; init; }
    public string? Remarks { get; init; }
    public DateTime? DeliveryDate { get; init; }
    public DateTime? Eta { get; init; }
    public DateTime? Etd { get; init; }
}

public sealed class SaSoSaveRequest
{
    public DateTime SoDate { get; set; }
    public string CustCode { get; set; } = string.Empty;
    public string? CustPo { get; set; }
    public string? Ref1 { get; set; }
    public string? ProjId { get; set; }
    public string? Currency { get; set; }
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }
    public string? SalesRep { get; set; }
    public string? Remarks { get; set; }
    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipAddress4 { get; set; }
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
    public string? InvAddress4 { get; set; }
    public string? InvCity { get; set; }
    public string? InvState { get; set; }
    public string? InvPostalCode { get; set; }
    public string? InvCountry { get; set; }
    public string? InvTel { get; set; }
    public string? InvFax { get; set; }
    /// <summary>Optional. Why this revision was created (Revise only).</summary>
    public string? RevisionReason { get; set; }
    /// <summary>Optional. When set on mutate, must match locked current CustRel.</summary>
    public short? CustRel { get; set; }
    public byte[]? RowVersion { get; set; }
    public IReadOnlyList<SaSoLineRequest>? Lines { get; set; }
}

public sealed class SaSoLineRequest
{
    public int Line { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? CustICode { get; set; }
    public decimal OrderQty { get; set; }
    public string? Warehouse { get; set; }
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
    public string? Classification { get; set; }
    public string? Remarks { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public DateTime? Eta { get; set; }
    public DateTime? Etd { get; set; }
}

public interface ISaSoService
{
    Task<SaSoOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime soDate,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime soDate,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> SearchAsync(
        SaSoListQuery query,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> GetAsync(
        string soNo,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> GetAsync(
        string soNo,
        short custRel,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> GetReviseDraftAsync(
        string soNo,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> SaveNewAsync(
        SaSoSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> UpdateAsync(
        string soNo,
        SaSoSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> ReviseAsync(
        string soNo,
        SaSoSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> DeleteAsync(
        IReadOnlyList<SaSoKeyedRequest> items,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> ForceCloseAsync(
        IReadOnlyList<SaSoKeyedRequest> items,
        CancellationToken cancellationToken = default);

    /// <param name="excludeDoNo">
    /// When editing a DO, pass that DO number so its own lines are not soft-reserved against itself
    /// (scoped with the caller's company/branch).
    /// </param>
    Task<SaSoOperationResult> GetRemainingLinesAsync(
        string soNo,
        string? excludeDoNo = null,
        CancellationToken cancellationToken = default);

    Task<SaSoOperationResult> GetBillableLinesAsync(
        string soNo,
        CancellationToken cancellationToken = default);
}
