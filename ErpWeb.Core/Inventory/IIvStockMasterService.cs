namespace ErpWeb.Core.Inventory;

using ErpWeb.Core.Inventory.Lifecycle;

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

    Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
        IvStockMasterEditVm model,
        bool isNew,
        IvStockMasterImageChange? imageChange,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
        IvStockMasterEditVm model,
        bool isNew,
        IvStockMasterImageGalleryChangeSet? galleryChanges,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only retirement preview. UI explanation only; the transition re-evaluates in its own
    /// transaction and this result is never used as final approval.
    /// </summary>
    Task<IvMasterOperationResult<ItemRetirementCheckResult>> PreviewRetirementAsync(
        string iCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicit, audited lifecycle command. All-or-none across the selected items.
    /// </summary>
    Task<IvMasterOperationResult<IvStockLifecycleBulkResult>> ChangeLifecycleAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        string targetStatus,
        string reason,
        string sourceAction,
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
