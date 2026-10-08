namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Immutable, append-only absorbed conversion-cost evidence for one posted production output.
/// The fact stores the frozen work-order source line and the exact production movement it values;
/// rollback appends a linked fact rather than changing this row.
/// </summary>
public sealed class ProductionConversionCostFact
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long StockPostingId { get; set; }
    public long ProductionOutputId { get; set; }
    public long ProductionMovementId { get; set; }
    public long WorkOrderId { get; set; }
    public long RouteStepId { get; set; }
    public long WorkOrderOperationId { get; set; }
    public string CostType { get; set; } = string.Empty;
    public string SourceLineKey { get; set; } = string.Empty;
    public long? WorkOrderLabourId { get; set; }
    public long? WorkOrderMachineId { get; set; }
    public decimal BasisQty { get; set; }
    public string BasisUom { get; set; } = string.Empty;
    public decimal RatePerOutputUnit { get; set; }
    public decimal CostAmount { get; set; }
    public long? ReversesFactId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
