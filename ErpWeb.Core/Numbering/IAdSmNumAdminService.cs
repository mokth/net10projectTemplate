using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Numbering;

public interface IAdSmNumAdminService
{
    // Continuous AdSmNum
    Task<IvMasterOperationResult<IReadOnlyList<AdSmNumListRow>>> ListContinuousAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<AdSmNumEditVm>> GetContinuousAsync(
        string numCd,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<AdSmNumEditVm>> SaveContinuousAsync(
        AdSmNumEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteContinuousAsync(
        IReadOnlyList<string> numCds,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteContinuousAsync(
        IReadOnlyList<string> numCds,
        CancellationToken cancellationToken = default);

    // Period AdSmNumDate
    Task<IvMasterOperationResult<IReadOnlyList<AdSmNumDateListRow>>> ListPeriodAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<AdSmNumDateEditVm>> GetPeriodAsync(
        int uid,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<AdSmNumDateEditVm>> SavePeriodAsync(
        AdSmNumDateEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeletePeriodAsync(
        IReadOnlyList<AdSmNumDateKey> keys,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeletePeriodAsync(
        IReadOnlyList<AdSmNumDateKey> keys,
        CancellationToken cancellationToken = default);
}
