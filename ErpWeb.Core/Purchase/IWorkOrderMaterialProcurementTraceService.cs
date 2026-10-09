namespace ErpWeb.Core.Purchase;

/// <summary>
/// Read-only procurement coverage for one frozen Work Order material.  The trace is derived from
/// the existing PR/PO documents; it does not duplicate procurement status on the Work Order or DR.
/// </summary>
public interface IWorkOrderMaterialProcurementTraceService
{
    Task<WorkOrderMaterialProcurementTraceResult> GetAsync(
        long workOrderMaterialId,
        DateTime? asOfDate = null,
        CancellationToken cancellationToken = default);
}

public sealed class WorkOrderMaterialProcurementTraceResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public WorkOrderMaterialProcurementTrace? Data { get; init; }

    public static WorkOrderMaterialProcurementTraceResult Ok(WorkOrderMaterialProcurementTrace data) =>
        new() { Succeeded = true, Data = data };

    public static WorkOrderMaterialProcurementTraceResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class WorkOrderMaterialProcurementTrace
{
    public long WorkOrderMaterialId { get; init; }
    public long WorkOrderId { get; init; }
    public string ComponentCode { get; init; } = string.Empty;
    public string? ComponentDescription { get; init; }
    public string? BaseUom { get; init; }
    public string SupplySource { get; init; } = string.Empty;
    public decimal RequiredBaseQty { get; init; }
    public decimal AvailableBaseQty { get; init; }
    public decimal PhysicalShortBaseQty { get; init; }
    public decimal OpenProcurementBaseQty { get; init; }
    public decimal NetProcurementRequiredBaseQty { get; init; }
    public bool IsConsistent { get; init; }
    public string? InconsistencyMessage { get; init; }
    public IReadOnlyList<WorkOrderMaterialProcurementTraceLine> Lines { get; init; } = [];
}

public sealed class WorkOrderMaterialProcurementTraceLine
{
    public long WorkOrderMaterialId { get; init; }
    public string? PrNo { get; init; }
    public short? PrLineNo { get; init; }
    public decimal PrStdQty { get; init; }
    public string? PrStdUom { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public decimal PoOrderedStdQty { get; init; }
    public decimal PoOpenStdQty { get; init; }
    public decimal PrUnorderedStdQty { get; init; }
    public DateTime? EtaDate { get; init; }
    public bool IsCurrentPoRevision { get; init; }
    public bool IsConsistent { get; init; }
    public string? InconsistencyMessage { get; init; }
}
