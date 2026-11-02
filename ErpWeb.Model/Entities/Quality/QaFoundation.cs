namespace ErpWeb.Model.Entities.Quality;

/// <summary>Company-level QA policy. Posting gates stay off in Phase 1; this is record-only.</summary>
public class QaPolicy
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool Enabled { get; set; }
    public bool IncomingInspectionEnabled { get; set; }
    public bool FinalFgInspectionEnabled { get; set; }
    public bool LogicalHoldEnabled { get; set; }
    public bool PhysicalHoldEnabled { get; set; }
    public bool NonGoodEnabled { get; set; }
    public bool InspectorApproverSeparation { get; set; } = true;
    public bool IsPublished { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class QaCase
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string CaseNo { get; set; } = string.Empty;
    public DateTime CaseDate { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string SourceType { get; set; } = string.Empty;
    public string? SourceDocType { get; set; }
    public string? SourceDocNo { get; set; }
    public long? SourceHeaderId { get; set; }
    public int? SourceDocumentRevision { get; set; }
    public string? SupplierCode { get; set; }
    public string? SupplierName { get; set; }
    public string? DeliveryNote { get; set; }
    public Guid? DeliveryInstanceId { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public string? WorkOrderNo { get; set; }
    public long? ProductionBalLotId { get; set; }
    public string? SourceHref { get; set; }
    public string EligibilityNote { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public int PolicyVersion { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? InspectedBy { get; set; }
    public DateTime? InspectedAtUtc { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewDecision { get; set; }
    public string? ReviewRemarks { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<QaCaseLine> Lines { get; set; } = [];
    public ICollection<QaInspection> Inspections { get; set; } = [];
    public ICollection<QaDispositionLine> Dispositions { get; set; } = [];
    public ICollection<QaAuditEvent> AuditEvents { get; set; } = [];
    public ICollection<QaReceiptGrant> Grants { get; set; } = [];
    public ICollection<QaFgGrant> FgGrants { get; set; } = [];
    public ICollection<QaLogicalHold> LogicalHolds { get; set; } = [];
    public ICollection<QaPhysicalHold> PhysicalHolds { get; set; } = [];
    public ICollection<QaNonGoodGrant> NonGoodGrants { get; set; } = [];
}

public class QaCaseLine
{
    public long Uid { get; set; }
    public long CaseId { get; set; }
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string Uom { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal InspectedQty { get; set; }
    public string? LotNo { get; set; }
    public string? WarehouseCode { get; set; }
    public string? LocationCode { get; set; }
    public string? ItemStatus { get; set; }
    public long? SourceLineId { get; set; }
    public short? PoLineNo { get; set; }
    public string? SourceIdentity { get; set; }
    public string? LineEligibilityNote { get; set; }
    public string? Remarks { get; set; }

    public QaCase Case { get; set; } = null!;
    public ICollection<QaInspection> Inspections { get; set; } = [];
    public ICollection<QaDispositionLine> Dispositions { get; set; } = [];
    public ICollection<QaReceiptGrant> Grants { get; set; } = [];
    public ICollection<QaFgGrant> FgGrants { get; set; } = [];
}

public class QaInspection
{
    public long Uid { get; set; }
    public long CaseId { get; set; }
    public long CaseLineId { get; set; }
    public string Result { get; set; } = string.Empty;
    public string Inspector { get; set; } = string.Empty;
    public DateTime InspectedAtUtc { get; set; }
    public string? Notes { get; set; }
    public string? MeasuredValues { get; set; }

    public QaCase Case { get; set; } = null!;
    public QaCaseLine CaseLine { get; set; } = null!;
}

public class QaDispositionLine
{
    public long Uid { get; set; }
    public long CaseId { get; set; }
    public long CaseLineId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string? ReasonCode { get; set; }
    public string? Remarks { get; set; }

    public QaCase Case { get; set; } = null!;
    public QaCaseLine CaseLine { get; set; } = null!;
}

public class QaAuditEvent
{
    public long Uid { get; set; }
    public long CaseId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; }
    public string ByUser { get; set; } = string.Empty;
    public string? Payload { get; set; }

    public QaCase Case { get; set; } = null!;
}

public class QaReceiptGrant
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long CaseId { get; set; }
    public long CaseLineId { get; set; }
    public Guid DeliveryInstanceId { get; set; }
    public int LineKey { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }
    public string? SupplierCode { get; set; }
    public string? DeliveryNote { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string Uom { get; set; } = string.Empty;
    public string? LotNo { get; set; }
    public decimal AcceptedQty { get; set; }
    public decimal RejectedQty { get; set; }
    public int PolicyVersion { get; set; }
    public string Status { get; set; } = "OPEN";
    public DateTime ApprovedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaCase Case { get; set; } = null!;
    public QaCaseLine CaseLine { get; set; } = null!;
    public ICollection<QaReceiptAllocation> Allocations { get; set; } = [];
}

public class QaReceiptAllocation
{
    public long Uid { get; set; }
    public long GrantId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string Status { get; set; } = "POSTED";
    public int BatchNo { get; set; }
    public int BatchDetailId { get; set; }
    public int DocumentRevision { get; set; }
    public Guid? PostingOperationId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaReceiptGrant Grant { get; set; } = null!;
}

public class QaFgGrant
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long CaseId { get; set; }
    public long CaseLineId { get; set; }
    public long ProductionBalLotId { get; set; }
    public int LineKey { get; set; }
    public string? WorkOrderNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string Uom { get; set; } = string.Empty;
    public string? LotNo { get; set; }
    public decimal AcceptedQty { get; set; }
    public decimal RejectedQty { get; set; }
    public int PolicyVersion { get; set; }
    public string Status { get; set; } = "OPEN";
    public DateTime ApprovedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaCase Case { get; set; } = null!;
    public QaCaseLine CaseLine { get; set; } = null!;
    public ICollection<QaFgAllocation> Allocations { get; set; } = [];
}

public class QaFgAllocation
{
    public long Uid { get; set; }
    public long GrantId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string Status { get; set; } = "POSTED";
    public int BatchId { get; set; }
    public long SourceId { get; set; }
    public int DocumentRevision { get; set; }
    public Guid? PostingOperationId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaFgGrant Grant { get; set; } = null!;
}

public class QaLogicalHold
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long CaseId { get; set; }
    public long CaseLineId { get; set; }
    public long ProductionBalLotId { get; set; }
    public decimal ReservedBaseQty { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public string Cause { get; set; } = "KEEP_HOLD";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReleasedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaCase Case { get; set; } = null!;
    public QaCaseLine CaseLine { get; set; } = null!;
}

public class QaPhysicalHold
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long CaseId { get; set; }
    public long CaseLineId { get; set; }
    public int BalLocId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public string LocationCode { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public string ItemStatus { get; set; } = string.Empty;
    public decimal ReservedBaseQty { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public string Cause { get; set; } = "KEEP_HOLD";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReleasedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaCase Case { get; set; } = null!;
    public QaCaseLine CaseLine { get; set; } = null!;
    public ICollection<QaPhysicalAllocation> Allocations { get; set; } = [];
}

public class QaPhysicalAllocation
{
    public long Uid { get; set; }
    public long HoldId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string Status { get; set; } = "POSTED";
    public string TrxType { get; set; } = string.Empty;
    public int BatchNo { get; set; }
    public int BatchDetailId { get; set; }
    public Guid? PostingOperationId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaPhysicalHold Hold { get; set; } = null!;
}

public class QaNonGoodGrant
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long CaseId { get; set; }
    public long CaseLineId { get; set; }
    public long ProductionOutputId { get; set; }
    public int LineKey { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? DocumentNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string Uom { get; set; } = string.Empty;
    public string? LotNo { get; set; }
    public decimal AcceptedQty { get; set; }
    public decimal RejectedQty { get; set; }
    public int PolicyVersion { get; set; }
    public string Status { get; set; } = "OPEN";
    public DateTime ApprovedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaCase Case { get; set; } = null!;
    public QaCaseLine CaseLine { get; set; } = null!;
    public ICollection<QaNonGoodAllocation> Allocations { get; set; } = [];
}

public class QaNonGoodAllocation
{
    public long Uid { get; set; }
    public long GrantId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string Status { get; set; } = "POSTED";
    public long ProductionOutputId { get; set; }
    public int DocumentRevision { get; set; }
    public Guid? PostingOperationId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public QaNonGoodGrant Grant { get; set; } = null!;
}
