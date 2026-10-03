using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// Single production-domain authority for whether an operation may begin execution.
/// Both Daily Production and Issue-to-Production consume the frozen Work Order route graph
/// through this contract; product-definition master data is deliberately never consulted.
/// </summary>
public interface IProductionOperationEligibilityService
{
    ProductionOperationEligibilityResult Evaluate(
        ProductionWorkOrderOperation selectedOperation,
        IReadOnlyCollection<ProductionWorkOrderRouteStep> routeSteps,
        IReadOnlyCollection<ProductionWorkOrderOperation> operations);
}

public sealed class ProductionOperationEligibilityResult
{
    public bool IsEligible { get; init; }
    public string? BlockingReason { get; init; }
    public IReadOnlyList<long> BlockingOperationIds { get; init; } = [];
}
