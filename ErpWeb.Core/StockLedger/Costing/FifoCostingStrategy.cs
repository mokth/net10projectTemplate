using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger.Costing;

/// <summary>Strategy adapter for the immutable financial FIFO layer engine.</summary>
public sealed class FifoCostingStrategy : IInventoryCostingStrategy
{
    private readonly Func<StockPostingContext, IReadOnlyList<IvTrxHistory>, CancellationToken,
        Task<IReadOnlyList<StockValuationFact>>> _valuePending;

    public FifoCostingStrategy(
        Func<StockPostingContext, IReadOnlyList<IvTrxHistory>, CancellationToken,
            Task<IReadOnlyList<StockValuationFact>>> valuePending)
    {
        _valuePending = valuePending ?? throw new ArgumentNullException(nameof(valuePending));
    }

    public string CostMethod => StockCostMethods.Fifo;

    public Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> pendingHistory,
        CancellationToken cancellationToken = default) =>
        _valuePending(context, pendingHistory, cancellationToken);
}
