namespace ErpWeb.Model.Entities.Costing;

public sealed class CostingRepairAuditEvent
{
    public long Id { get; set; }
    public long RepairCaseId { get; set; }
    public CostingRepairCase? RepairCase { get; set; }
    public int Sequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int StepNo { get; set; }
    public string StableStepId { get; set; } = string.Empty;
    public int AttemptNo { get; set; } = 1;
    public string Module { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNo { get; set; } = string.Empty;
    public string? DesiredAction { get; set; }
    public Guid? ModuleRequestId { get; set; }
    public string? BeforeStatus { get; set; }
    public string? AfterStatus { get; set; }
    public long? ResultStockPostingId { get; set; }
    public string? DependencyFingerprint { get; set; }
    public long? PostingWatermark { get; set; }
    public string EvidenceJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
