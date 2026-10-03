using ErpWeb.Model.Data;

namespace ErpWeb.Core.Production;

public interface IProductionMaterialReconciliationService
{
    Task<ProductionMaterialReconciliationResult> ReconcileAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderId,
        CancellationToken cancellationToken = default);
}

public sealed class ProductionMaterialReconciliationResult
{
    public bool IsReconciled => Findings.Count == 0;
    public IReadOnlyList<ProductionMaterialReconciliationFinding> Findings { get; init; } = [];
}

public sealed class ProductionMaterialReconciliationFinding
{
    public string Code { get; init; } = string.Empty;
    public string? ItemCode { get; init; }
    public int? InventoryBatchNo { get; init; }
    public decimal RemainingBaseQty { get; init; }
    public string Message { get; init; } = string.Empty;
}
