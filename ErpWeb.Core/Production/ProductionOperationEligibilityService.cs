using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>Adapter around the route-sequence evaluator used by all production entry flows.</summary>
public sealed class ProductionOperationEligibilityService : IProductionOperationEligibilityService
{
    public ProductionOperationEligibilityResult Evaluate(
        ProductionWorkOrderOperation selectedOperation,
        IReadOnlyCollection<ProductionWorkOrderRouteStep> routeSteps,
        IReadOnlyCollection<ProductionWorkOrderOperation> operations)
    {
        var gate = ProductionOperationSequenceGate.Evaluate(selectedOperation, routeSteps, operations);
        return new ProductionOperationEligibilityResult
        {
            IsEligible = gate.Allowed,
            BlockingReason = gate.Message,
            BlockingOperationIds = gate.BlockingOperationIds
        };
    }
}
