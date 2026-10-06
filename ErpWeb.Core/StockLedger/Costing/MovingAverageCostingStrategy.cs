using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger.Costing;

/// <summary>
/// Strategy seam for the existing proven moving-average engine. The delegate keeps the current
/// valuation implementation intact while the ledger is introduced to multiple explicit methods;
/// subsequent method strategies can be added without creating a second posting envelope.
/// </summary>
public sealed class MovingAverageCostingStrategy : IInventoryCostingStrategy
{
    private readonly Func<StockPostingContext, IReadOnlyList<IvTrxHistory>, CancellationToken,
        Task<IReadOnlyList<StockValuationFact>>> _valuePending;

    public MovingAverageCostingStrategy(
        Func<StockPostingContext, IReadOnlyList<IvTrxHistory>, CancellationToken,
            Task<IReadOnlyList<StockValuationFact>>> valuePending)
    {
        _valuePending = valuePending ?? throw new ArgumentNullException(nameof(valuePending));
    }

    public string CostMethod => StockCostMethods.MovingAverage;

    public Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> pendingHistory,
        CancellationToken cancellationToken = default) =>
        _valuePending(context, pendingHistory, cancellationToken);
}
