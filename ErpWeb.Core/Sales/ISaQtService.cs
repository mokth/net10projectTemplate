using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public enum SaQtErrorKind
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

public sealed class SaQtOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public SaQtErrorKind ErrorKind { get; init; }
    public string? QtNo { get; init; }
    public decimal CurrRate { get; init; }
    public bool CurrRateValid { get; init; }
    public SaQtDocument? Document { get; init; }
    public SaSoCustomerDefaults? CustomerDefaults { get; init; }
    public SaQtListPage? ListPage { get; init; }
    /// <summary>Sales Order number created by a successful conversion.</summary>
    public string? ConvertedSoNo { get; init; }
    public IReadOnlyList<SaSoItemLookupRow> Items { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IReadOnlyList<SaSoCustomerLookupRow> Customers { get; init; } = [];
    public IReadOnlyList<SaSoTaxGroupLookupRow> TaxGroups { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> PayCodes { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> Projects { get; init; } = [];
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static SaQtOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = SaQtErrorKind.None };

    public static SaQtOperationResult OkDocument(SaQtDocument document) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaQtErrorKind.None,
            Document = document,
            QtNo = document.QtNo
        };

    public static SaQtOperationResult OkConverted(string qtNo, string soNo) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaQtErrorKind.None,
            QtNo = qtNo,
            ConvertedSoNo = soNo
        };

    public static SaQtOperationResult OkList(SaQtListPage page) =>
        new() { Succeeded = true, ErrorKind = SaQtErrorKind.None, ListPage = page };

    public static SaQtOperationResult OkLookups(
        IReadOnlyList<SaSoItemLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses,
        IReadOnlyList<SaSoCustomerLookupRow> customers,
        IReadOnlyList<SaSoTaxGroupLookupRow> taxGroups,
        IReadOnlyList<IvCodeLookupRow> payCodes,
        IReadOnlyList<IvCodeLookupRow>? projects = null) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaQtErrorKind.None,
            Items = items,
            Warehouses = warehouses,
            Customers = customers,
            TaxGroups = taxGroups,
            PayCodes = payCodes,
            Projects = projects ?? []
        };

    public static SaQtOperationResult OkDefaults(SaSoCustomerDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = SaQtErrorKind.None, CustomerDefaults = defaults };

    public static SaQtOperationResult OkRate(decimal rate, bool valid) =>
        new() { Succeeded = true, ErrorKind = SaQtErrorKind.None, CurrRate = rate, CurrRateValid = valid };

    public static SaQtOperationResult Fail(
        string message,
        SaQtErrorKind kind = SaQtErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static SaQtOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = SaQtErrorKind.Validation,
            ErrorMessage = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class SaQtKeyedRequest
{
    public string QtNo { get; init; } = string.Empty;

    /// <summary>Optional. When set, must match the locked current CustRel or the call fails as superseded.</summary>
    public short? CustRel { get; init; }

    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaQtListQuery
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

public sealed class SaQtListRow
{
    public string QtNo { get; init; } = string.Empty;
    public short CustRel { get; init; }
    public DateTime QtDate { get; init; }
    public DateTime ValidUntil { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ConversionStatus { get; init; } = SaQtConversionStatuses.None;
    public string CustCode { get; init; } = string.Empty;
    public string? CustName { get; init; }
    public string? CustPo { get; init; }
    public decimal TotAmnt { get; init; }
    public int LineCount { get; init; }
    public string? SalesRep { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>The Sales Order created from this revision, when it has been converted.</summary>
    public string? ConvertedSoNo { get; init; }

    /// <summary>True when the offer has lapsed (date-only). UX hint only — the service revalidates.</summary>
    public bool IsExpired { get; init; }

    // ── Action hints. UX only: every mutation re-checks status/current/expiry server-side. ──────
    public bool CanEdit { get; init; }
    public bool CanSend { get; init; }
    public bool CanAccept { get; init; }
    public bool CanLose { get; init; }
    public bool CanCancel { get; init; }
    public bool CanRevise { get; init; }
    public bool CanConvert { get; init; }
    public bool CanDelete { get; init; }

    /// <summary>Short reason when <see cref="CanRevise"/> / <see cref="CanDelete"/> are false.</summary>
    public string? MutationBlockReason { get; init; }
}

public sealed class SaQtListPage
{
    public IReadOnlyList<SaQtListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SaQtDocument
{
    public string QtNo { get; init; } = string.Empty;
    public short CustRel { get; init; } = 1;
    public bool IsCurrent { get; init; } = true;
    public short LastCustRel { get; init; } = 1;
    public string? RevisionReason { get; init; }
    public DateTime QtDate { get; set; }
    public DateTime ValidUntil { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ConversionStatus { get; init; } = SaQtConversionStatuses.None;
    public string? ClosedReason { get; init; }
    public DateTime? ClosedDate { get; init; }
    public string? ClosedBy { get; init; }
    public DateTime? SentDate { get; init; }
    public string? SentBy { get; init; }
    public DateTime? AcceptedDate { get; init; }
    public string? AcceptedBy { get; init; }
    public DateTime? LostDate { get; init; }
    public string? LostBy { get; init; }
    public string? LostReason { get; init; }
    public DateTime? ExpiredDate { get; init; }

        public string CustCode { get; set; } = string.Empty;
        public string? CustName { get; init; }
        public string? CustPo { get; init; }
        public string? ContactPerson { get; init; }
    public string? Ref1 { get; init; }
    public string? ProjId { get; init; }
    public string? Prefix { get; init; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; init; }
    public string? TaxGrCode { get; init; }
    public string? SalesRep { get; init; }
    public string? Remarks { get; init; }
    public string? InternalRemarks { get; init; }
    public string? ShipVia { get; init; }
    public string? DeliveryTerms { get; init; }

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

    /// <summary>The Sales Order created from this revision, when it has been converted.</summary>
    public string? ConvertedSoNo { get; init; }

    /// <summary>True when the offer has lapsed. ACCEPTED quotations report the lapse but keep their status.</summary>
    public bool IsExpired { get; init; }

    public IReadOnlyList<SaQtLineDto> Lines { get; init; } = [];
    public IReadOnlyList<SaQtRevisionHistoryRow> Revisions { get; init; } = [];
}

public sealed class SaQtRevisionHistoryRow
{
    public short CustRel { get; init; }
    public bool IsCurrent { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? RevisionReason { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
}

public sealed class SaQtLineDto
{
    public int Line { get; init; }
    public short CustRel { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? CustICode { get; init; }
    public decimal OrderQty { get; init; }

    /// <summary>Quantity already turned into a Sales Order.</summary>
    public decimal ConvertedQty { get; init; }

    /// <summary>Computed remainder (OrderQty - ConvertedQty). Never stored.</summary>
    public decimal RemainingQty { get; init; }

    public decimal StdQty { get; init; }
    public decimal StdPsize { get; init; }
    public string? SellingUom { get; init; }
    public string? StdUom { get; init; }
    public string? Warehouse { get; init; }
    public decimal UnitPrice { get; init; }

    /// <summary>
    /// WHICH price source priced this line — the persisted <c>SaPriceSourceTokens</c> value. NULL on
    /// rows written before the column existed, which means "not recorded", NOT "no price source".
    /// Explanatory only: <see cref="UnitPrice"/> is frozen and never re-derived.
    /// </summary>
    public string? PricingSource { get; init; }

    /// <summary>Readable reference behind <see cref="PricingSource"/> (<c>PL1</c>, <c>MOQ=100</c>, <c>QTY 10-99</c>).</summary>
    public string? PricingRef { get; init; }

    /// <summary>Phase 4: the price the ENGINE resolved; NULL unless an operator overrode it.</summary>
    public decimal? OriginalUnitPrice { get; init; }

    /// <summary>Phase 4: why the resolved price was changed; present only on a real override.</summary>
    public string? OverrideReason { get; init; }

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

public sealed class SaQtSaveRequest
{
    public DateTime QtDate { get; set; }

    /// <summary>Offer validity limit. When unset, the service defaults it to QT date + 30 days.</summary>
    public DateTime? ValidUntil { get; set; }

    public string CustCode { get; set; } = string.Empty;

        /// <summary>Generic customer-side reference (Customer RFQ / Reference). No RFQ-vs-PO semantics in MVP.</summary>
        public string? CustPo { get; set; }

        public string? ContactPerson { get; set; }

    public string? Ref1 { get; set; }
    public string? ProjId { get; set; }
    public string? Currency { get; set; }
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }
    public string? SalesRep { get; set; }
    public string? Remarks { get; set; }
    public string? InternalRemarks { get; set; }
    public string? ShipVia { get; set; }
    public string? DeliveryTerms { get; set; }

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

    /// <summary>Optional. When set on mutate, must match the locked current CustRel.</summary>
    public short? CustRel { get; set; }

    public byte[]? RowVersion { get; set; }
    public IReadOnlyList<SaQtLineRequest>? Lines { get; set; }
}

public sealed class SaQtLineRequest
{
    public int Line { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? CustICode { get; set; }
    public decimal OrderQty { get; set; }
    public string? Warehouse { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>The pricing provenance the engine reported for this line; stored, never trusted for the price.</summary>
    public string? PricingSource { get; set; }

    public string? PricingRef { get; set; }

    /// <summary>
    /// Phase 4: the price the page resolved. A value that DIFFERS from <see cref="UnitPrice"/> declares an
    /// override, refused by the service without <c>PRICE_OVERRIDE</c> and a reason. Null declares nothing.
    /// </summary>
    public decimal? OriginalUnitPrice { get; set; }

    /// <summary>Phase 4: why the resolved price was changed. Required whenever an override is declared.</summary>
    public string? OverrideReason { get; set; }

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

public interface ISaQtService
{
    Task<SaQtOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime qtDate,
        CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime qtDate,
        CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> SearchAsync(
        SaQtListQuery query,
        CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> GetAsync(
        string qtNo,
        CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> GetAsync(
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default);

    /// <summary>Current revision cloned into an editable NEW draft shape without persisting it.</summary>
    Task<SaQtOperationResult> GetReviseDraftAsync(
        string qtNo,
        CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> SaveNewAsync(
        SaQtSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> UpdateAsync(
        string qtNo,
        SaQtSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> ReviseAsync(
        string qtNo,
        SaQtSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>NEW → SENT. Runs the lazy-expiry check first; a lapsed quotation becomes EXPIRED.</summary>
    Task<SaQtOperationResult> SendAsync(
        SaQtKeyedRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// SENT → ACCEPTED. Runs the lazy-expiry check first: a lapsed quotation becomes EXPIRED and the
    /// acceptance is refused, so an expired offer can never be accepted.
    /// </summary>
    Task<SaQtOperationResult> AcceptAsync(
        SaQtKeyedRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>NEW/SENT → LOST. A lost reason is required.</summary>
    Task<SaQtOperationResult> LoseAsync(
        SaQtKeyedRequest request,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>NEW/SENT → CANCELLED.</summary>
    Task<SaQtOperationResult> CancelAsync(
        SaQtKeyedRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ACCEPTED → CLOSED (CONVERTED), creating exactly one Sales Order from the quotation's frozen
    /// commercial snapshot. Atomic: either both documents move or neither does.
    /// </summary>
    Task<SaQtOperationResult> ConvertToSoAsync(
        SaQtKeyedRequest request,
        CancellationToken cancellationToken = default);

    Task<SaQtOperationResult> DeleteAsync(
        IReadOnlyList<SaQtKeyedRequest> items,
        CancellationToken cancellationToken = default);
}
