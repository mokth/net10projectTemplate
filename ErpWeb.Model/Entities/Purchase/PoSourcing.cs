namespace ErpWeb.Model.Entities.Purchase;

public static class PoSourcingSourceTypes
{
    public const string Direct = "DIRECT";
    public const string Pr = "PR";
}

public static class PoSourcingStatuses
{
    public const string Draft = "DRAFT";
    public const string CollectingQuotes = "COLLECTING_QUOTES";
    public const string ReadyToCompare = "READY_TO_COMPARE";
    public const string AwaitingAward = "AWAITING_AWARD";
    public const string Awarded = "AWARDED";
    public const string PartiallyConverted = "PARTIALLY_CONVERTED";
    public const string Converted = "CONVERTED";
    public const string Cancelled = "CANCELLED";
}

public static class PoSourcingSupplierResponseStatuses
{
    public const string Invited = "INVITED";
    public const string Quoted = "QUOTED";
    public const string Declined = "DECLINED";
    public const string Withdrawn = "WITHDRAWN";
}

public static class PoSourcingQuoteStatuses
{
    public const string Draft = "DRAFT";
    public const string Received = "RECEIVED";
    public const string Superseded = "SUPERSEDED";
    public const string Withdrawn = "WITHDRAWN";
    public const string Declined = "DECLINED";
    public const string Expired = "EXPIRED";
}

public static class PoSourcingQuoteLineStatuses
{
    public const string Quoted = "QUOTED";
    public const string NoQuote = "NO_QUOTE";
}

public static class PoSourcingFreightAllocationModes
{
    public const string ValueProRata = "VALUE_PRO_RATA";
    public const string QtyProRata = "QTY_PRO_RATA";
    public const string HeaderOnly = "HEADER_ONLY";
}

public static class PoSourcingAwardStatuses
{
    public const string Proposed = "PROPOSED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Superseded = "SUPERSEDED";
}

public static class PoSourcingConversionTargetTypes
{
    public const string Pr = "PR";
    public const string Po = "PO";
}

public static class PoSourcingConversionStatuses
{
    public const string Active = "ACTIVE";
    public const string Released = "RELEASED";
    public const string DetachedQuarantined = "DETACHED_QUARANTINED";
}

public static class PoSourcingAttachmentScopes
{
    public const string Rfq = "RFQ";
    public const string Quote = "QUOTE";
}

public static class PoSourcingEventTypes
{
    public const string Created = "CREATED";
    public const string Updated = "UPDATED";
    public const string Cancelled = "CANCELLED";
    public const string SupplierAdded = "SUPPLIER_ADDED";
    public const string QuoteRecorded = "QUOTE_RECORDED";
    public const string QuoteRevised = "QUOTE_REVISED";
    public const string QuoteWithdrawn = "QUOTE_WITHDRAWN";
    public const string QuoteDeclined = "QUOTE_DECLINED";
    public const string AwardProposed = "AWARD_PROPOSED";
    public const string AwardApproved = "AWARD_APPROVED";
    public const string AwardRejected = "AWARD_REJECTED";
    public const string AwardSuperseded = "AWARD_SUPERSEDED";
    public const string ConvertedToPr = "CONVERTED_TO_PR";
    public const string ConvertedToPo = "CONVERTED_TO_PO";
    public const string TargetUpdated = "TARGET_UPDATED";
    public const string TargetRevised = "TARGET_REVISED";
    public const string TargetReleased = "TARGET_RELEASED";
    public const string TargetDetachedWhileDisabled = "TARGET_DETACHED_WHILE_DISABLED";
    public const string AttachmentUploaded = "ATTACHMENT_UPLOADED";
    public const string AttachmentDeleted = "ATTACHMENT_DELETED";
}

public class PoSourcingHdr
{
    public long SourcingId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string SourcingNo { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceType { get; set; } = PoSourcingSourceTypes.Direct;
    public string? SourcePrNo { get; set; }
    public string? Requester { get; set; }
    public string? BuyerCode { get; set; }
    public string? DeptCode { get; set; }
    public string? ProjectCode { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? DeliveryWhse { get; set; }
    public DateTime RequestDate { get; set; }
    public DateTime? ReplyDeadline { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string BaseCurrency { get; set; } = string.Empty;
    public string Status { get; set; } = PoSourcingStatuses.Draft;
    public string? Remarks { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoSourcingLine> Lines { get; set; } = new List<PoSourcingLine>();
    public ICollection<PoSourcingSupplier> Suppliers { get; set; } = new List<PoSourcingSupplier>();
    public ICollection<PoSourcingQuote> Quotes { get; set; } = new List<PoSourcingQuote>();
    public ICollection<PoSourcingAward> Awards { get; set; } = new List<PoSourcingAward>();
    public ICollection<PoSourcingConversion> Conversions { get; set; } = new List<PoSourcingConversion>();
    public ICollection<PoSourcingAttachment> Attachments { get; set; } = new List<PoSourcingAttachment>();
    public ICollection<PoSourcingEvent> Events { get; set; } = new List<PoSourcingEvent>();
}

public class PoSourcingLine
{
    public long SourcingLineId { get; set; }
    public long SourcingId { get; set; }
    public PoSourcingHdr? Sourcing { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public short LineNo { get; set; }
    public string? ICode { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public bool OneTimeItemYn { get; set; }
    public string RequestedUom { get; set; } = string.Empty;
    public string StdUom { get; set; } = string.Empty;
    public decimal PackSz { get; set; } = 1m;
    public decimal RequestedQty { get; set; }
    public decimal RequestedStdQty { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string? DeliveryWhse { get; set; }
    public string? PrNo { get; set; }
    public short? PrLineNo { get; set; }
    public string? PrOriginalVendorCd { get; set; }
    public string? Notes { get; set; }
}

public class PoSourcingSupplier
{
    public long SourcingSupplierId { get; set; }
    public long SourcingId { get; set; }
    public PoSourcingHdr? Sourcing { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string? SuppCode { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? DefaultCurrency { get; set; }
    public string ResponseStatus { get; set; } = PoSourcingSupplierResponseStatuses.Invited;
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public class PoSourcingQuote
{
    public long QuoteId { get; set; }
    public long SourcingId { get; set; }
    public PoSourcingHdr? Sourcing { get; set; }
    public long SourcingSupplierId { get; set; }
    public PoSourcingSupplier? Supplier { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string SupplierQuoteRef { get; set; } = string.Empty;
    public short QuoteRevNo { get; set; } = 1;
    public DateTime QuoteDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal? ExchangeRateToBase { get; set; }
    public DateTime? ExchangeRateDate { get; set; }
    public string? PayTermCode { get; set; }
    public string? BuyingTermCode { get; set; }
    public string? QuoteChannel { get; set; }
    public decimal HeaderFreightAmount { get; set; }
    public decimal HeaderOtherCharges { get; set; }
    public string FreightAllocationMode { get; set; } = PoSourcingFreightAllocationModes.ValueProRata;
    public bool IsInclusive { get; set; }
    public string Status { get; set; } = PoSourcingQuoteStatuses.Received;
    public string? BuyerNotes { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoSourcingQuoteLine> Lines { get; set; } = new List<PoSourcingQuoteLine>();
}

public class PoSourcingQuoteLine
{
    public long QuoteLineId { get; set; }
    public long QuoteId { get; set; }
    public PoSourcingQuote? Quote { get; set; }
    public long SourcingId { get; set; }
    public long SourcingLineId { get; set; }
    public PoSourcingLine? SourcingLine { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string LineStatus { get; set; } = PoSourcingQuoteLineStatuses.Quoted;
    public string? VendorPartNo { get; set; }
    public string? OfferedSpec { get; set; }
    public string QuotedUom { get; set; } = string.Empty;
    public decimal QuotedPackSz { get; set; } = 1m;
    public decimal QuotedQty { get; set; }
    public decimal QuotedStdQty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscPer { get; set; }
    public decimal DiscAmt { get; set; }
    public string? TaxGroupCode { get; set; }
    public decimal TaxPer { get; set; }
    public decimal NonRecoverableTaxPer { get; set; }
    public decimal LineCharges { get; set; }
    public int? LeadTimeDays { get; set; }
    public DateTime? EstimatedDeliveryDate { get; set; }
    public decimal? Moq { get; set; }
    public string? WarrantyTerms { get; set; }
    public string? ExceptionsNote { get; set; }
    public bool IsComparable { get; set; }
    public string? NonComparableReason { get; set; }
    public decimal? NormalizedBaseUnitCost { get; set; }
    public decimal? NormalizedComparableLandedTotal { get; set; }
    public decimal? NormalizedTaxInclusivePayableTotal { get; set; }
}

public class PoSourcingAward
{
    public long AwardId { get; set; }
    public long SourcingId { get; set; }
    public PoSourcingHdr? Sourcing { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public int AwardVersion { get; set; } = 1;
    public string Status { get; set; } = PoSourcingAwardStatuses.Proposed;
    public string? OverallReason { get; set; }
    public string? ExceptionReason { get; set; }
    public string? ComparisonSnapshotJson { get; set; }
    public string ProposedBy { get; set; } = string.Empty;
    public DateTime ProposedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionReason { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoSourcingAwardLine> Lines { get; set; } = new List<PoSourcingAwardLine>();
}

public class PoSourcingAwardLine
{
    public long AwardLineId { get; set; }
    public long AwardId { get; set; }
    public PoSourcingAward? Award { get; set; }
    public long SourcingId { get; set; }
    public long SourcingLineId { get; set; }
    public PoSourcingLine? SourcingLine { get; set; }
    public long QuoteId { get; set; }
    public long QuoteLineId { get; set; }
    public long SourcingSupplierId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string? SuppCodeSnapshot { get; set; }
    public string SupplierNameSnapshot { get; set; } = string.Empty;
    public string? ICodeSnapshot { get; set; }
    public bool OneTimeItemYnSnapshot { get; set; }
    public string CurrencySnapshot { get; set; } = string.Empty;
    public decimal ExchangeRateSnapshot { get; set; }
    public bool IsInclusiveSnapshot { get; set; }
    public string AwardedUom { get; set; } = string.Empty;
    public string StdUomSnapshot { get; set; } = string.Empty;
    public decimal PackSzSnapshot { get; set; } = 1m;
    public decimal AwardedQty { get; set; }
    public decimal AwardedStdQty { get; set; }
    public decimal AwardedUnitPrice { get; set; }
    public decimal DiscPerSnapshot { get; set; }
    public string? TaxGroupSnapshot { get; set; }
    public decimal TaxPerSnapshot { get; set; }
    public decimal ComparableBaseUnitCostSnapshot { get; set; }
    public decimal ComparableTotalSnapshot { get; set; }
    public bool IsLowestComparableOffer { get; set; }
    public string? SelectionReason { get; set; }
    public bool IsQuarantined { get; set; }
    public string? QuarantineReason { get; set; }
}

public class PoSourcingConversion
{
    public long ConversionId { get; set; }
    public long SourcingId { get; set; }
    public PoSourcingHdr? Sourcing { get; set; }
    public long AwardId { get; set; }
    public PoSourcingAward? Award { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public string TargetDocType { get; set; } = string.Empty;
    public string TargetDocNo { get; set; } = string.Empty;
    public short TargetDocRelNo { get; set; }
    public string? TargetSupplierCode { get; set; }
    public string TargetCurrency { get; set; } = string.Empty;
    public bool TargetIsInclusive { get; set; }
    public bool TargetOneTimeItemYn { get; set; }
    public string Status { get; set; } = PoSourcingConversionStatuses.Active;
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? LastReconciledAtUtc { get; set; }
    public string? ReconciliationNote { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoSourcingConversionLine> Lines { get; set; } = new List<PoSourcingConversionLine>();
}

public class PoSourcingConversionLine
{
    public long ConversionLineId { get; set; }
    public long ConversionId { get; set; }
    public PoSourcingConversion? Conversion { get; set; }
    public long SourcingId { get; set; }
    public long SourcingLineId { get; set; }
    public long AwardId { get; set; }
    public long AwardLineId { get; set; }
    public PoSourcingAwardLine? AwardLine { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string TargetDocType { get; set; } = string.Empty;
    public string TargetDocNo { get; set; } = string.Empty;
    public short TargetDocRelNo { get; set; }
    public short TargetLineNo { get; set; }
    public short OriginalTargetLineNo { get; set; }
    public string TargetUom { get; set; } = string.Empty;
    public decimal TargetPackSz { get; set; } = 1m;
    public decimal ConvertedPurQty { get; set; }
    public decimal ConvertedStdQty { get; set; }
    public decimal EffectiveActiveStdQty { get; set; }
    public decimal TargetUnitPrice { get; set; }
    public string? SourcePrNo { get; set; }
    public short? SourcePrLineNo { get; set; }
    public string LineLinkStatus { get; set; } = PoSourcingConversionStatuses.Active;
}

public class PoSourcingAttachment
{
    public long AttachmentId { get; set; }
    public long SourcingId { get; set; }
    public PoSourcingHdr? Sourcing { get; set; }
    public long? QuoteId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ScopeKind { get; set; } = PoSourcingAttachmentScopes.Rfq;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredRelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public bool ContainsSensitivePricing { get; set; } = true;
    public DateTime UploadedAtUtc { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
}

public class PoSourcingEvent
{
    public long EventId { get; set; }
    public long SourcingId { get; set; }
    public PoSourcingHdr? Sourcing { get; set; }
    public long? QuoteId { get; set; }
    public long? AwardId { get; set; }
    public long? ConversionId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? PriorStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? Reason { get; set; }
    public string? SnapshotHash { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}
