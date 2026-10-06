using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger.Costing;

public interface IInventoryCostingStrategy
{
    string CostMethod { get; }

    Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> pendingHistory,
        CancellationToken cancellationToken = default);
}
