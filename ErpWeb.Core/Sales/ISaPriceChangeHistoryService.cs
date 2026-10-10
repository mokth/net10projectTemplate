using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public interface ISaPriceChangeHistoryService
{
    Task<IvMasterOperationResult<SaPriceChangeHistoryPage>> SearchAsync(
        SaPriceChangeHistoryQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaPriceChangeHistoryBatch>> GetBatchAsync(
        long priceChangeBatchId,
        int skip = 0,
        int take = SaPriceChangeHistoryLimits.DefaultPageSize,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyList<SaPriceChangeHistoryRow>>> ExportAsync(
        SaPriceChangeHistoryQuery query,
        CancellationToken cancellationToken = default);
}
