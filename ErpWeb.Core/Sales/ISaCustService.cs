using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public interface ISaCustService
{
    Task<IvMasterOperationResult<SaCustListPage>> SearchAsync(
        SaCustListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaCustEditVm>> GetAsync(
        string custCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaCustEditVm>> SaveAsync(
        SaCustEditVm model,
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

    Task<IvMasterOperationResult<SaCustListPage>> ExportRowsAsync(
        SaCustListQuery query,
        CancellationToken cancellationToken = default);
}
