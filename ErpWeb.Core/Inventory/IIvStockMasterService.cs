namespace ErpWeb.Core.Inventory;

public interface IIvStockMasterService
{
    Task<IvMasterOperationResult<IvStockMasterListPage>> SearchAsync(
        IvStockMasterListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvStockMasterEditVm>> GetAsync(
        string iCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
        IvStockMasterEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteBulkAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvStockMasterListPage>> ExportRowsAsync(
        IvStockMasterListQuery query,
        CancellationToken cancellationToken = default);
}
