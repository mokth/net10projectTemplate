using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public enum PoSourcingErrorKind
{
    None = 0,
    Validation = 1,
    NotFound = 2,
    Authorization = 3,
    Concurrency = 4,
    BusinessRule = 5,
    FeatureDisabled = 6,
    IdempotencyConflict = 7,
    Unexpected = 8
}

public sealed class PoSourcingOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public PoSourcingErrorKind ErrorKind { get; init; }
    public IReadOnlyDictionary<string, string> FieldErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public PoSourcingLookupsDto? Lookups { get; init; }
    public PoSourcingSearchPageDto? Page { get; init; }
    public PoSourcingDocumentDto? Document { get; init; }
    public PoSourcingComparisonDto? Comparison { get; init; }
    public IReadOnlyList<PoSourcingEligiblePrLineDto>? EligiblePrLines { get; init; }
    public PoSourcingConversionPreviewDto? ConversionPreview { get; init; }
    public PoSourcingConversionBatchResultDto? ConversionResult { get; init; }
    public PoSourcingAttachmentDownloadDto? AttachmentDownload { get; init; }

    public static PoSourcingOperationResult Ok() => new() { Succeeded = true };

    public static PoSourcingOperationResult OkLookups(PoSourcingLookupsDto lookups) =>
        new() { Succeeded = true, Lookups = lookups };

    public static PoSourcingOperationResult OkPage(PoSourcingSearchPageDto page) =>
        new() { Succeeded = true, Page = page };

    public static PoSourcingOperationResult OkDocument(PoSourcingDocumentDto document) =>
        new() { Succeeded = true, Document = document };

    public static PoSourcingOperationResult OkComparison(PoSourcingComparisonDto comparison) =>
        new() { Succeeded = true, Comparison = comparison };

    public static PoSourcingOperationResult OkEligiblePrLines(IReadOnlyList<PoSourcingEligiblePrLineDto> lines) =>
        new() { Succeeded = true, EligiblePrLines = lines };

    public static PoSourcingOperationResult OkConversionPreview(PoSourcingConversionPreviewDto preview) =>
        new() { Succeeded = true, ConversionPreview = preview };

    public static PoSourcingOperationResult OkConversion(PoSourcingConversionBatchResultDto result, PoSourcingDocumentDto? document = null) =>
        new() { Succeeded = true, ConversionResult = result, Document = document };

    public static PoSourcingOperationResult OkAttachment(PoSourcingAttachmentDownloadDto download) =>
        new() { Succeeded = true, AttachmentDownload = download };

    public static PoSourcingOperationResult Fail(string message, PoSourcingErrorKind kind = PoSourcingErrorKind.BusinessRule) =>
        new() { Succeeded = false, ErrorMessage = message, ErrorKind = kind };

    public static PoSourcingOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? fieldErrors = null) =>
        new()
        {
            Succeeded = false,
            ErrorMessage = message,
            ErrorKind = PoSourcingErrorKind.Validation,
            FieldErrors = fieldErrors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class PoSourcingLookupsDto
{
    public string BaseCurrency { get; init; } = "MYR";
    public IReadOnlyList<PoOrderCodeLookupRow> Currencies { get; init; } = Array.Empty<PoOrderCodeLookupRow>();
    public IReadOnlyList<PoOrderTaxGroupLookupRow> TaxGroups { get; init; } = Array.Empty<PoOrderTaxGroupLookupRow>();
    public IReadOnlyList<PoOrderCodeLookupRow> BuyingTerms { get; init; } = Array.Empty<PoOrderCodeLookupRow>();
    public IReadOnlyList<PoOrderCodeLookupRow> PaymentTerms { get; init; } = Array.Empty<PoOrderCodeLookupRow>();
    public IReadOnlyList<PoOrderCodeLookupRow> Buyers { get; init; } = Array.Empty<PoOrderCodeLookupRow>();
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = Array.Empty<IvWarehouseLookupRow>();
    public IReadOnlyList<PoOrderCodeLookupRow> Departments { get; init; } = Array.Empty<PoOrderCodeLookupRow>();
    public IReadOnlyList<PoOrderCodeLookupRow> Projects { get; init; } = Array.Empty<PoOrderCodeLookupRow>();
    public bool CanAdd { get; init; }
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public bool CanCancel { get; init; }
    public bool CanApprove { get; init; }
    public bool CanReject { get; init; }
    public bool CanViewCost { get; init; }
    public bool CanCreatePr { get; init; }
    public bool CanCreatePo { get; init; }
}

public sealed class PoSourcingSearchFilter
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public string? SourceType { get; set; }
    public string? BuyerCode { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int PageIndex { get; set; }
    public int PageSize { get; set; } = 50;
}

public sealed class PoSourcingSearchPageDto
{
    public IReadOnlyList<PoSourcingListRowDto> Rows { get; init; } = Array.Empty<PoSourcingListRowDto>();
    public int TotalCount { get; init; }
}

public sealed class PoSourcingListRowDto
{
    public long SourcingId { get; init; }
    public string SourcingNo { get; init; } = string.Empty;
    public DateTime RequestDate { get; init; }
    public DateTime? ResponseDueDate { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? BuyerCode { get; init; }
    public string? DepartmentCode { get; init; }
    public string? ProjectId { get; init; }
    public string BaseCurrency { get; init; } = string.Empty;
    public int LineCount { get; init; }
    public int SupplierCount { get; init; }
    public int ActiveQuoteCount { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedDate { get; init; }
    public byte[] RowVersion { get; init; } = Array.Empty<byte>();
    public bool CanEdit { get; init; }
    public bool CanCancel { get; init; }
    public bool CanDelete { get; init; }
}

public sealed class PoSourcingEligiblePrLineDto
{
    public string PrNo { get; init; } = string.Empty;
    public short PrLine { get; init; }
    public DateTime PrDate { get; init; }
    public string? Requester { get; init; }
    public string? DeptCode { get; init; }
    public string? ProjId { get; init; }
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public string? Category { get; init; }
    public bool? OneTimeItemYn { get; init; }
    public decimal PrPurchaseQty { get; init; }
    public decimal RemainingPurchaseQty { get; init; }
    public string PurchaseUom { get; init; } = string.Empty;
    public decimal PackSz { get; init; }
    public string StdUom { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public decimal UnitPrice { get; init; }
    public string? VendorCd { get; init; }
    public string? VendNm { get; init; }
    public string? TaxGroup { get; init; }
    public bool IsInclusive { get; init; }
    public string? ToWarehouse { get; init; }
    public DateTime? EtaDt { get; init; }
    public long? WorkOrderMaterialId { get; init; }
}

public sealed class PoSourcingDocumentDto
{
    public long SourcingId { get; init; }
    public string SourcingNo { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime RequestDate { get; init; }
    public DateTime? ResponseDueDate { get; init; }
    public DateTime? RequiredEtaDate { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Requester { get; init; }
    public string? BuyerCode { get; init; }
    public string? DepartmentCode { get; init; }
    public string? ProjectId { get; init; }
    public string BaseCurrency { get; init; } = string.Empty;
    public string? DefaultToWarehouse { get; init; }
    public string? Remarks { get; init; }
    public string? InternalNotes { get; init; }
    public string? SelectionSummaryNotes { get; init; }
    public string? CancelReason { get; init; }
    public bool PricingRedacted { get; init; }
    public byte[] RowVersion { get; init; } = Array.Empty<byte>();

    public IReadOnlyList<PoSourcingLineDto> Lines { get; init; } = Array.Empty<PoSourcingLineDto>();
    public IReadOnlyList<PoSourcingSupplierDto> Suppliers { get; init; } = Array.Empty<PoSourcingSupplierDto>();
    public IReadOnlyList<PoSourcingQuoteDto> Quotes { get; init; } = Array.Empty<PoSourcingQuoteDto>();
    public IReadOnlyList<PoSourcingAwardDto> Awards { get; init; } = Array.Empty<PoSourcingAwardDto>();
    public IReadOnlyList<PoSourcingConversionDto> Conversions { get; init; } = Array.Empty<PoSourcingConversionDto>();
    public IReadOnlyList<PoSourcingAttachmentDto> Attachments { get; init; } = Array.Empty<PoSourcingAttachmentDto>();
    public IReadOnlyList<PoSourcingEventDto> Events { get; init; } = Array.Empty<PoSourcingEventDto>();

    public bool CanEdit { get; init; }
    public bool CanCancel { get; init; }
    public bool CanDelete { get; init; }
    public bool CanApprove { get; init; }
    public bool CanReject { get; init; }
    public bool CanViewCost { get; init; }
    public bool CanCreatePr { get; init; }
    public bool CanCreatePo { get; init; }
}

public sealed class PoSourcingLineDto
{
    public long SourcingLineId { get; set; }
    public short LineNo { get; set; }
    public string? SourcePrNo { get; set; }
    public short? SourcePrLine { get; set; }
    public long? WorkOrderMaterialId { get; set; }
    public bool OneTimeItemYn { get; set; }
    public string? ItemCode { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? ItemSpec { get; set; }
    public string? Category { get; set; }
    public decimal RequestedPurchaseQty { get; set; }
    public string RequestedPurchaseUom { get; set; } = string.Empty;
    public decimal PackSize { get; set; } = 1m;
    public string StdUom { get; set; } = string.Empty;
    public decimal RequestedStdQty { get; set; }
    public decimal AwardedPurchaseQty { get; set; }
    public decimal ConvertedPurchaseQty { get; set; }
    public DateTime? RequiredEtaDate { get; set; }
    public string? ToWarehouse { get; set; }
    public string? PreferredSupplierCode { get; set; }
    public string? TargetCurrency { get; set; }
    public decimal? TargetUnitPrice { get; set; }
    public string? TaxGroup { get; set; }
    public bool IsInclusive { get; set; }
    public string? Remarks { get; set; }
}

public sealed class PoSourcingSupplierDto
{
    public long SourcingSupplierId { get; set; }
    public string? SupplierCode { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public bool IsAdHocSupplier { get; set; }
    public string? ContactPerson { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? DefaultCurrency { get; set; }
    public string? PaymentTerm { get; set; }
    public string? BuyingTerm { get; set; }
    public string ResponseStatus { get; set; } = "INVITED";
    public DateTime?InvitedDate { get; set; }
    public DateTime? RespondedDate { get; set; }
    public string? Remarks { get; set; }
}

public sealed class PoSourcingQuoteDto
{
    public long QuoteId { get; set; }
    public long SourcingSupplierId { get; set; }
    public string? SupplierCode { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string SupplierQuoteNo { get; set; } = string.Empty;
    public short QuoteRevNo { get; set; } = 1;
    public bool IsLatestRevision { get; set; } = true;
    public DateTime QuoteDate { get; set; }
    public DateTime? ValidUntilDate { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public string Currency { get; set; } = string.Empty;
    public decimal ExchangeRate { get; set; } = 1m;
    public string? PaymentTerm { get; set; }
    public string? BuyingTerm { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGroup { get; set; }
    public int? LeadTimeDays { get; set; }
    public DateTime? PromisedEtaDate { get; set; }
    public decimal? FreightAmount { get; set; }
    public decimal? OtherChargesAmount { get; set; }
    public string FreightAllocationMode { get; set; } = "VALUE_PRO_RATA";
    public decimal? GrossAmount { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? NetAmount { get; set; }
    public decimal? TotalQuoteAmount { get; set; }
    public decimal? TotalComparableBaseAmount { get; set; }
    public string? Remarks { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public List<PoSourcingQuoteLineDto> Lines { get; set; } = new();
}

public sealed class PoSourcingQuoteLineDto
{
    public long QuoteLineId { get; set; }
    public long SourcingLineId { get; set; }
    public short SourcingLineNo { get; set; }
    public string? ItemCode { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string LineStatus { get; set; } = "QUOTED";
    public string? VendorPartNo { get; set; }
    public string? QuotedBrand { get; set; }
    public string? AlternateSpec { get; set; }
    public decimal OfferedQty { get; set; }
    public string QuoteUom { get; set; } = string.Empty;
    public decimal PackSize { get; set; } = 1m;
    public string StdUom { get; set; } = string.Empty;
    public decimal OfferedStdQty { get; set; }
    public decimal? QuotedUnitPrice { get; set; }
    public string? DiscountType1 { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public string? DiscountType2 { get; set; }
    public decimal ItemDiscount2 { get; set; }
    public string? TaxGroup { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal NonRecoverableTaxPercent { get; set; }
    public bool IsInclusive { get; set; }
    public decimal? LineAmount { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? NetAmount { get; set; }
    public decimal? AllocatedFreightAmount { get; set; }
    public decimal? AllocatedOtherCharges { get; set; }
    public decimal? ComparableUnitCostBase { get; set; }
    public decimal? ComparableLandedCostBase { get; set; }
    public decimal? Moq { get; set; }
    public int? LeadTimeDays { get; set; }
    public DateTime? PromisedEtaDate { get; set; }
    public string? Remarks { get; set; }
}

public sealed class PoSourcingAwardDto
{
    public long AwardId { get; init; }
    public short AwardNo { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime AwardDate { get; init; }
    public string? ProposedBy { get; init; }
    public DateTime ProposedDate { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTime? ApprovedDate { get; init; }
    public string? RejectedBy { get; init; }
    public DateTime? RejectedDate { get; init; }
    public string? SummaryRemarks { get; init; }
    public string? RejectionReason { get; init; }
    public byte[] RowVersion { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<PoSourcingAwardLineDto> Lines { get; init; } = Array.Empty<PoSourcingAwardLineDto>();
}

public sealed class PoSourcingAwardLineDto
{
    public long AwardLineId { get; init; }
    public long SourcingLineId { get; init; }
    public short SourcingLineNo { get; init; }
    public long QuoteId { get; init; }
    public long QuoteLineId { get; init; }
    public decimal AwardedPurchaseQty { get; init; }
    public decimal AwardedStdQty { get; init; }
    public decimal ConvertedPurchaseQty { get; init; }
    public decimal ConvertedStdQty { get; init; }
    public decimal RemainingPurchaseQty => Math.Max(0m, AwardedPurchaseQty - ConvertedPurchaseQty);
    public string? SnapshotSupplierCode { get; init; }
    public string SnapshotSupplierName { get; init; } = string.Empty;
    public string? SnapshotItemCode { get; init; }
    public string SnapshotItemDesc { get; init; } = string.Empty;
    public string SnapshotPurchaseUom { get; init; } = string.Empty;
    public decimal SnapshotPackSize { get; init; }
    public string SnapshotStdUom { get; init; } = string.Empty;
    public string SnapshotCurrency { get; init; } = string.Empty;
    public decimal SnapshotExchangeRate { get; init; }
    public decimal? SnapshotUnitPrice { get; init; }
    public string? SnapshotTaxGroup { get; init; }
    public decimal SnapshotTaxPercent { get; init; }
    public bool SnapshotIsInclusive { get; init; }
    public string? SnapshotPaymentTerm { get; init; }
    public string? SnapshotBuyingTerm { get; init; }
    public int? SnapshotLeadTimeDays { get; init; }
    public decimal? SnapshotMoq { get; init; }
    public decimal? SnapshotComparableUnitCostBase { get; init; }
    public decimal? SnapshotComparableLandedCostBase { get; init; }
    public bool IsLowestCostSelection { get; init; }
    public string? SelectionReasonCode { get; init; }
    public string? SelectionRemarks { get; init; }
    public string? ExceptionReason { get; init; }
}

public sealed class PoSourcingConversionDto
{
    public long ConversionId { get; init; }
    public long AwardId { get; init; }
    public string TargetType { get; init; } = string.Empty;
    public string TargetDocNo { get; init; } = string.Empty;
    public short TargetRelNo { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? StatusReason { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTime CreatedDate { get; init; }
    public IReadOnlyList<PoSourcingConversionLineDto> Lines { get; init; } = Array.Empty<PoSourcingConversionLineDto>();
}

public sealed class PoSourcingConversionLineDto
{
    public long ConversionLineId { get; init; }
    public long AwardLineId { get; init; }
    public long SourcingLineId { get; init; }
    public short TargetLineNo { get; init; }
    public decimal ConvertedPurchaseQty { get; init; }
    public decimal ConvertedStdQty { get; init; }
    public decimal? SnapshotUnitPrice { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? StatusReason { get; init; }
}

public sealed class PoSourcingAttachmentDto
{
    public long AttachmentId { get; init; }
    public string ScopeType { get; init; } = string.Empty;
    public long? QuoteId { get; init; }
    public long? AwardId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string? ContentType { get; init; }
    public long FileSizeBytes { get; init; }
    public bool ContainsSensitivePricing { get; init; }
    public string? Remarks { get; init; }
    public string UploadedBy { get; init; } = string.Empty;
    public DateTime UploadedDate { get; init; }
}

public sealed class PoSourcingAttachmentDownloadDto
{
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = "application/octet-stream";
    public byte[] Content { get; init; } = Array.Empty<byte>();
}

public sealed class PoSourcingEventDto
{
    public long EventId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string EventSummary { get; init; } = string.Empty;
    public string ActorUserId { get; init; } = string.Empty;
    public DateTime EventDateUtc { get; init; }
}

public sealed class PoSourcingComparisonDto
{
    public long SourcingId { get; init; }
    public string BaseCurrency { get; init; } = string.Empty;
    public bool PricingRedacted { get; init; }
    public PoSourcingComparisonMatrix Matrix { get; init; } = new();
    public IReadOnlyDictionary<long, PoSourcingHistoricalPriceRefDto> HistoricalPricesByLine { get; init; } =
        new Dictionary<long, PoSourcingHistoricalPriceRefDto>();
}

public sealed class PoSourcingHistoricalPriceRefDto
{
    public long SourcingLineId { get; init; }
    public string? LastDocNo { get; init; }
    public DateTime? LastDocDate { get; init; }
    public string? LastSupplierCode { get; init; }
    public string? LastCurrency { get; init; }
    public decimal? LastUnitPrice { get; init; }
}

public sealed class PoSourcingSaveRequest
{
    public long? SourcingId { get; set; }
    public string SourceType { get; set; } = "DIRECT_RFQ";
    public DateTime RequestDate { get; set; }
    public DateTime? ResponseDueDate { get; set; }
    public DateTime? RequiredEtaDate { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Requester { get; set; }
    public string? BuyerCode { get; set; }
    public string? DepartmentCode { get; set; }
    public string? ProjectId { get; set; }
    public string? BaseCurrency { get; set; }
    public string? DefaultToWarehouse { get; set; }
    public string? Remarks { get; set; }
    public string? InternalNotes { get; set; }
    public bool MarkAsOpen { get; set; }
    public byte[]? RowVersion { get; set; }
    public List<PoSourcingLineDto> Lines { get; set; } = new();
    public List<PoSourcingSupplierDto> Suppliers { get; set; } = new();
}

public sealed class PoSourcingCancelRequest
{
    public long SourcingId { get; set; }
    public string CancelReason { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public sealed class PoSourcingQuoteSaveRequest
{
    public long SourcingId { get; set; }
    public long? QuoteId { get; set; }
    public bool CreateNewRevision { get; set; }
    public long SourcingSupplierId { get; set; }
    public string SupplierQuoteNo { get; set; } = string.Empty;
    public DateTime QuoteDate { get; set; }
    public DateTime? ValidUntilDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal ExchangeRate { get; set; } = 1m;
    public string? PaymentTerm { get; set; }
    public string? BuyingTerm { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGroup { get; set; }
    public int? LeadTimeDays { get; set; }
    public DateTime? PromisedEtaDate { get; set; }
    public decimal FreightAmount { get; set; }
    public decimal OtherChargesAmount { get; set; }
    public string FreightAllocationMode { get; set; } = "VALUE_PRO_RATA";
    public string? Remarks { get; set; }
    public byte[]? RowVersion { get; set; }
    public List<PoSourcingQuoteLineDto> Lines { get; set; } = new();
}

public sealed class PoSourcingAwardRequest
{
    public long SourcingId { get; set; }
    public string? SummaryRemarks { get; set; }
    public bool ApproveImmediately { get; set; } = true;
    public byte[] SourcingRowVersion { get; set; } = Array.Empty<byte>();
    public List<PoSourcingAwardLineInputDto> Lines { get; set; } = new();
}

public sealed class PoSourcingAwardLineInputDto
{
    public long SourcingLineId { get; set; }
    public long QuoteLineId { get; set; }
    public decimal AwardedPurchaseQty { get; set; }
    public string? SelectionReasonCode { get; set; }
    public string? SelectionRemarks { get; set; }
    public string? ExceptionReason { get; set; }
}

public sealed class PoSourcingAwardDecisionRequest
{
    public long SourcingId { get; set; }
    public long AwardId { get; set; }
    public string? Remarks { get; set; }
    public byte[] AwardRowVersion { get; set; } = Array.Empty<byte>();
}

public sealed class PoSourcingConversionPreviewRequest
{
    public long SourcingId { get; set; }
    public long AwardId { get; set; }
    public string TargetType { get; set; } = "PO";
    public string? PrType { get; set; } = "P";
    public List<PoSourcingConversionLineSelectionDto> SelectedLines { get; set; } = new();
}

public sealed class PoSourcingConversionLineSelectionDto
{
    public long AwardLineId { get; set; }
    public decimal ConvertPurchaseQty { get; set; }
}

public sealed class PoSourcingConversionPreviewDto
{
    public string TargetType { get; init; } = string.Empty;
    public bool CanProceed { get; init; }
    public IReadOnlyList<string> BlockingErrors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PoSourcingConversionPreviewGroupDto> Groups { get; init; } = Array.Empty<PoSourcingConversionPreviewGroupDto>();
}

public sealed class PoSourcingConversionPreviewGroupDto
{
    public int GroupIndex { get; init; }
    public string GroupKeyDescription { get; init; } = string.Empty;
    public string? SupplierCode { get; init; }
    public string? SupplierName { get; init; }
    public string Currency { get; init; } = string.Empty;
    public bool IsInclusive { get; init; }
    public bool OneTimeItemYn { get; init; }
    public string? PrType { get; init; }
    public int LineCount { get; init; }
    public decimal TotalNetAmount { get; init; }
}

public sealed class PoSourcingConvertToPrRequest
{
    public long SourcingId { get; set; }
    public long AwardId { get; set; }
    public Guid RequestId { get; set; }
    public string PrType { get; set; } = "P";
    public DateTime? CreateDt { get; set; }
    public string? Remarks { get; set; }
    public List<PoSourcingConversionLineSelectionDto> SelectedLines { get; set; } = new();
}

public sealed class PoSourcingConvertToPoRequest
{
    public long SourcingId { get; set; }
    public long AwardId { get; set; }
    public Guid RequestId { get; set; }
    public DateTime? PoDate { get; set; }
    public string? SiRemark { get; set; }
    public List<PoSourcingConversionLineSelectionDto> SelectedLines { get; set; } = new();
}

public sealed class PoSourcingConversionBatchResultDto
{
    public string TargetType { get; init; } = string.Empty;
    public IReadOnlyList<string> CreatedDocumentNumbers { get; init; } = Array.Empty<string>();
    public bool WasIdempotentReplay { get; init; }
}

public sealed class PoSourcingAttachmentUploadRequest
{
    public long SourcingId { get; set; }
    public string ScopeType { get; set; } = "RFQ";
    public long? QuoteId { get; set; }
    public long? AwardId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public bool? ContainsSensitivePricing { get; set; }
    public string? Remarks { get; set; }
}

public interface IPoSourcingService
{
    Task<PoSourcingOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> SearchAsync(PoSourcingSearchFilter filter, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> GetAsync(long sourcingId, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> GetEligiblePrLinesAsync(string? prNo, string? search, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> SaveDraftAsync(PoSourcingSaveRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> CancelAsync(PoSourcingCancelRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> DeleteDraftAsync(long sourcingId, byte[] rowVersion, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> SaveQuoteAsync(PoSourcingQuoteSaveRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> WithdrawQuoteAsync(long sourcingId, long quoteId, byte[] rowVersion, string? reason, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> GetComparisonAsync(long sourcingId, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> SaveAwardAsync(PoSourcingAwardRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> ApproveAwardAsync(PoSourcingAwardDecisionRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> RejectAwardAsync(PoSourcingAwardDecisionRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> PreviewConversionAsync(PoSourcingConversionPreviewRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> ConvertToPrAsync(PoSourcingConvertToPrRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> ConvertToPoAsync(PoSourcingConvertToPoRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> UploadAttachmentAsync(PoSourcingAttachmentUploadRequest request, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> DownloadAttachmentAsync(long sourcingId, long attachmentId, CancellationToken cancellationToken = default);
    Task<PoSourcingOperationResult> DeleteAttachmentAsync(long sourcingId, long attachmentId, CancellationToken cancellationToken = default);
}
