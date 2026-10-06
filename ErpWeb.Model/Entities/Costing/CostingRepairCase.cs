namespace ErpWeb.Model.Entities.Costing;

public sealed class CostingRepairCase
{
    public long Id { get; set; }
    public Guid RepairRequestId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string? RootFindingCode { get; set; }
    public long? RootStockPostingId { get; set; }
    public long? RootValuationFactId { get; set; }
    public string Strategy { get; set; } = string.Empty;
    public string PreviewHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];

    public ICollection<CostingRepairAuditEvent> Events { get; set; } = new List<CostingRepairAuditEvent>();
}
