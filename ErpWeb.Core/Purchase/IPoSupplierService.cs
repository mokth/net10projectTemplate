using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public interface IPoSupplierService
{
    Task<IvMasterOperationResult<PoSupplierListPage>> SearchAsync(
        PoSupplierListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoSupplierEditVm>> GetAsync(
        string suppCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoSupplierEditVm>> SaveAsync(
        PoSupplierEditVm model,
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

    Task<IvMasterOperationResult<PoSupplierListPage>> ExportRowsAsync(
        PoSupplierListQuery query,
        CancellationToken cancellationToken = default);
}
