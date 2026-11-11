namespace ErpWeb.Core.Quality;

public sealed class QaResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public string? InfoMessage { get; init; }
    public QaPolicyDto? Policy { get; init; }
    public QaCaseDto? Case { get; init; }
    public QaListPage? ListPage { get; init; }
    public QaSourceListPage? SourcePage { get; init; }
    public QaInquiryPage? InquiryPage { get; init; }
    public byte[]? FileBytes { get; init; }
    public string? FileName { get; init; }

    public static QaResult Fail(string message) => new() { Succeeded = false, ErrorMessage = message };

    public static QaResult OkPolicy(QaPolicyDto policy, string? infoMessage = null) =>
        new() { Succeeded = true, Policy = policy, InfoMessage = infoMessage };

    public static QaResult OkCase(QaCaseDto value) => new() { Succeeded = true, Case = value };

    public static QaResult OkList(QaListPage page) => new() { Succeeded = true, ListPage = page };

    public static QaResult OkSources(QaSourceListPage page) => new() { Succeeded = true, SourcePage = page };

    public static QaResult OkInquiry(QaInquiryPage page) => new() { Succeeded = true, InquiryPage = page };

    public static QaResult OkFile(byte[] bytes, string fileName) =>
        new() { Succeeded = true, FileBytes = bytes, FileName = fileName };
}

public sealed class QaPolicyDto
{
    public int Version { get; init; }
    public bool Enabled { get; init; }
    public bool IncomingInspectionEnabled { get; init; }
    public bool FinalFgInspectionEnabled { get; init; }
    public bool LogicalHoldEnabled { get; init; }
    public bool PhysicalHoldEnabled { get; init; }
    public bool NonGoodEnabled { get; init; }
    public bool InspectorApproverSeparation { get; init; } = true;
    public bool IsPublished { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class QaPolicySaveCommand
{
    public bool InspectorApproverSeparation { get; init; } = true;
    public bool Enabled { get; init; }
    public bool IncomingInspectionEnabled { get; init; }
    public bool FinalFgInspectionEnabled { get; init; }
    public bool LogicalHoldEnabled { get; init; }
    public bool PhysicalHoldEnabled { get; init; }
    public bool NonGoodEnabled { get; init; }
    public byte[]? RowVersion { get; init; }
}

public sealed class QaListQuery
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public string? SourceType { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class QaListPage
{
    public IReadOnlyList<QaCaseListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class QaCaseListRow
{
    public string CaseNo { get; init; } = string.Empty;
    public DateTime CaseDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string? SourceDocNo { get; init; }
    public string? SupplierName { get; init; }
    public string? WorkOrderNo { get; init; }
    public int LineCount { get; init; }
    public string EligibilityNote { get; init; } = string.Empty;
    public DateTime CreatedDate { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }

    public string StatusLabel => QaLabels.Status(Status);
    public string SourceTypeLabel => QaLabels.Source(SourceType);
    public string NextAction => QaLabels.NextActionShort(Status);
    public bool IsPendingReview =>
        string.Equals(Status, QaCaseStatuses.PendingReview, StringComparison.OrdinalIgnoreCase);
}

public sealed class QaCaseDto
{
    public string CaseNo { get; init; } = string.Empty;
    public DateTime CaseDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string? SourceDocType { get; init; }
    public string? SourceDocNo { get; init; }
    public long? SourceHeaderId { get; init; }
    public int? SourceDocumentRevision { get; init; }
    public string? SupplierCode { get; init; }
    public string? SupplierName { get; init; }
    public string? DeliveryNote { get; init; }
    public Guid? DeliveryInstanceId { get; init; }
    public decimal DeliveredQty { get; init; }
    public decimal AcceptedQty { get; init; }
    public decimal RejectedQty { get; init; }
    public decimal PostedQty { get; init; }
    public decimal RemainingQty { get; init; }
    public decimal HeldQty { get; init; }
    public decimal PhysicalHeldQty { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public string? WorkOrderNo { get; init; }
    public long? ProductionBalLotId { get; init; }
    public string? SourceHref { get; init; }
    public string EligibilityNote { get; init; } = string.Empty;
    public string? Remarks { get; init; }
    public int PolicyVersion { get; init; }
    public string? SubmittedBy { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public string? InspectedBy { get; init; }
    public DateTime? InspectedAtUtc { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTime? ReviewedAtUtc { get; init; }
    public string? ReviewDecision { get; init; }
    public string? ReviewRemarks { get; init; }
    public DateTime CreatedDate { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<QaCaseLineDto> Lines { get; init; } = [];
    public IReadOnlyList<QaAuditEventDto> Audit { get; init; } = [];
    public IReadOnlyList<QaDestinationDto> Destinations { get; init; } = [];
    public IReadOnlyList<QaActionDto> Actions { get; init; } = [];
    public IReadOnlyList<QaAttemptDto> Attempts { get; init; } = [];
    public string? SourceCheckNote { get; init; }
    public bool FollowUpReady { get; init; }
    public int OpenActionCount { get; init; }
    public string AttachmentsNote { get; init; } = QaCaseMessages.AttachmentsDeferred;
}

public sealed class QaCaseLineDto
{
    public long Uid { get; init; }
    public int LineNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemName { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public decimal InspectedQty { get; init; }
    public string? LotNo { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
    public string? ItemStatus { get; init; }
    public long? SourceLineId { get; init; }
    public short? PoLineNo { get; init; }
    public string? SourceIdentity { get; init; }
    public string? LineEligibilityNote { get; init; }
    public string? Remarks { get; init; }
    public decimal AcceptedQty { get; init; }
    public decimal RejectedQty { get; init; }
    public decimal PostedQty { get; init; }
    public decimal RemainingQty { get; init; }
    public QaInspectionDto? Inspection { get; init; }
    public IReadOnlyList<QaDispositionDto> Dispositions { get; init; } = [];
}

public sealed class QaInspectionDto
{
    public string Result { get; init; } = string.Empty;
    public string Inspector { get; init; } = string.Empty;
    public DateTime InspectedAtUtc { get; init; }
    public string? Notes { get; init; }
    public string? MeasuredValues { get; init; }
}

public sealed class QaDispositionDto
{
    public string Kind { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public string? ReasonCode { get; init; }
    public string? Remarks { get; init; }
}

public sealed class QaAuditEventDto
{
    public string EventType { get; init; } = string.Empty;
    public DateTime AtUtc { get; init; }
    public string ByUser { get; init; } = string.Empty;
    public string? Payload { get; init; }
}

public sealed class QaCaseSaveCommand
{
    public string? CaseNo { get; init; }
    public DateTime CaseDate { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string? SourceDocType { get; init; }
    public string? SourceDocNo { get; init; }
    public long? SourceHeaderId { get; init; }
    public int? SourceDocumentRevision { get; init; }
    public string? SupplierCode { get; init; }
    public string? SupplierName { get; init; }
    public string? DeliveryNote { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public string? WorkOrderNo { get; init; }
    public long? ProductionBalLotId { get; init; }
    public string? SourceHref { get; init; }
    public string? Remarks { get; init; }
    public byte[]? RowVersion { get; init; }
    public IReadOnlyList<QaCaseLineSaveCommand> Lines { get; init; } = [];
}

public sealed class QaCaseLineSaveCommand
{
    public int LineNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemName { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public string? LotNo { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
    public string? ItemStatus { get; init; }
    public long? SourceLineId { get; init; }
    public short? PoLineNo { get; init; }
    public string? SourceIdentity { get; init; }
    public string? Remarks { get; init; }
}

public sealed class QaInspectionSaveCommand
{
    public string CaseNo { get; init; } = string.Empty;
    public byte[]? RowVersion { get; init; }
    public string? ReinspectionReason { get; init; }
    public IReadOnlyList<QaInspectionLineCommand> Lines { get; init; } = [];
}

public sealed class QaActionDto
{
    public int ActionNo { get; init; }
    public int LineNo { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? AssignedTo { get; init; }
    public string? AssignedDepartment { get; init; }
    public string? Reason { get; init; }
    public DateTime? DueDate { get; init; }
    public string? Remarks { get; init; }
    public string? CompletionEvidence { get; init; }
    public Guid? RequestId { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public string StatusLabel => QaLabels.ActionStatus(Status);
    public string TypeLabel => QaLabels.ActionType(ActionType);
    public bool IsOpen => QaActionStatuses.IsOpen(Status);
}

public sealed class QaAttemptDto
{
    public int LineNo { get; init; }
    public int AttemptNo { get; init; }
    public string Result { get; init; } = string.Empty;
    public string Inspector { get; init; } = string.Empty;
    public DateTime InspectedAtUtc { get; init; }
    public decimal InspectedQty { get; init; }
    public string? Notes { get; init; }
    public string? DispositionSnapshot { get; init; }
    public string? Reason { get; init; }
    public string ResultLabel => QaLabels.Result(Result);
}

public sealed class QaActionSaveCommand
{
    public string CaseNo { get; init; } = string.Empty;
    public byte[]? RowVersion { get; init; }
    public int LineNo { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public string? AssignedTo { get; init; }
    public string? AssignedDepartment { get; init; }
    public string? Reason { get; init; }
    public DateTime? DueDate { get; init; }
    public string? Remarks { get; init; }
    public Guid? RequestId { get; init; }
}

public sealed class QaActionUpdateCommand
{
    public string CaseNo { get; init; } = string.Empty;
    public byte[]? RowVersion { get; init; }
    public int ActionNo { get; init; }
    public string? AssignedTo { get; init; }
    public string? AssignedDepartment { get; init; }
    public string? Reason { get; init; }
    public string? Remarks { get; init; }
    public string? CompletionEvidence { get; init; }
}

public sealed class QaInspectionLineCommand
{
    public int LineNo { get; init; }
    public string Result { get; init; } = string.Empty;
    public decimal InspectedQty { get; init; }
    public string? Notes { get; init; }
    public string? MeasuredValues { get; init; }
    public IReadOnlyList<QaDispositionDto> Dispositions { get; init; } = [];
}

public sealed class QaReviewCommand
{
    public string CaseNo { get; init; } = string.Empty;
    public byte[]? RowVersion { get; init; }
    public string? Remarks { get; init; }
}

public sealed class QaSourceQuery
{
    public string SourceType { get; set; } = string.Empty;
    public string? SearchText { get; set; }
    public long? ExactSourceHeaderId { get; set; }
    public long? ExactLotId { get; set; }
    public long? ExactOutputId { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class QaSourceListPage
{
    public IReadOnlyList<QaSourceRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class QaSourceRow
{
    public string SourceType { get; init; } = string.Empty;
    public string DisplayText { get; init; } = string.Empty;
    public string? SourceDocType { get; init; }
    public string? SourceDocNo { get; init; }
    public long? SourceHeaderId { get; init; }
    public int? SourceDocumentRevision { get; init; }
    public long? SourceLineId { get; init; }
    public string? ItemCode { get; init; }
    public string? ItemName { get; init; }
    public string? Uom { get; init; }
    public decimal Qty { get; init; }
    public string? LotNo { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
    public string? ItemStatus { get; init; }
    public string? SupplierCode { get; init; }
    public string? SupplierName { get; init; }
    public string? DeliveryNote { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public short? PoLineNo { get; init; }
    public string? WorkOrderNo { get; init; }
    public long? ProductionBalLotId { get; init; }
    public string? SourceHref { get; init; }
    public string EligibilityNote { get; init; } = string.Empty;
}

public sealed class QaDestinationDto
{
    public string Kind { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string Guidance { get; init; } = string.Empty;
    public string? Href { get; init; }
    public string? DocumentNo { get; init; }
    public decimal Qty { get; init; }
    public bool CanNavigate { get; init; }
}

public sealed class QaInquiryQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Status { get; set; }
    public string? SourceType { get; set; }
    public string? SearchText { get; set; }
    public string? ItemCode { get; set; }
    public string? LotNo { get; set; }
    public string? PoNo { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? Supplier { get; set; }
    public string? Inspector { get; set; }
    public string? Reviewer { get; set; }
    public string? ActionOwner { get; set; }
    public bool OverdueOnly { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

public sealed class QaInquiryPage
{
    public int CaseCount { get; init; }
    public int OpenCaseCount { get; init; }
    public int PendingInspectionCount { get; init; }
    public int PendingReviewCount { get; init; }
    public int OpenDestinationCount { get; init; }
    public decimal RejectedDecisionQty { get; init; }
    public int OverdueActionCount { get; init; }
    public int CompletedActionCount { get; init; }
    public decimal? AverageCloseDays { get; init; }
    public decimal? MedianCloseDays { get; init; }
    public int InspectedCount { get; init; }
    public int FirstPassCount { get; init; }
    public int ApprovedWithoutRejectCount { get; init; }
    public decimal FirstPassYield { get; init; }
    public string FirstPassYieldNote { get; init; } = string.Empty;
    public string RecordOnlyNote { get; init; } = QaCaseMessages.InquiryRecordOnly;
    public bool FollowUpReady { get; init; }
    public IReadOnlyList<QaInquiryRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class QaInquiryRow
{
    public string CaseNo { get; init; } = string.Empty;
    public DateTime CaseDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string? SourceDocNo { get; init; }
    public string? SupplierName { get; init; }
    public string? WorkOrderNo { get; init; }
    public string? Inspector { get; init; }
    public string? Reviewer { get; init; }
    public decimal AcceptedQty { get; init; }
    public decimal RejectedQty { get; init; }
    public decimal PostedQty { get; init; }
    public int AgingDays { get; init; }
    public int OpenActionCount { get; init; }
    public string DestinationSummary { get; init; } = string.Empty;
    public IReadOnlyList<QaDestinationDto> Destinations { get; init; } = [];
}
