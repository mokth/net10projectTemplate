using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger.Costing;

/// <summary>Strategy adapter for the effective-dated Standard Cost implementation.</summary>
public sealed class StandardCostingStrategy : IInventoryCostingStrategy
{
    private readonly Func<StockPostingContext, IReadOnlyList<IvTrxHistory>, CancellationToken,
        Task<IReadOnlyList<StockValuationFact>>> _valuePending;

    public StandardCostingStrategy(
        Func<StockPostingContext, IReadOnlyList<IvTrxHistory>, CancellationToken,
            Task<IReadOnlyList<StockValuationFact>>> valuePending)
    {
        _valuePending = valuePending ?? throw new ArgumentNullException(nameof(valuePending));
    }

    public string CostMethod => StockCostMethods.Standard;

    public Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> pendingHistory,
        CancellationToken cancellationToken = default) =>
        _valuePending(context, pendingHistory, cancellationToken);
}
