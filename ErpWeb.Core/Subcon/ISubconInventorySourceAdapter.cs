namespace ErpWeb.Core.Subcon;

public interface ISubconInventorySourceAdapter
{
    Task<SubconResult> ValidateBeforePostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);

    Task<SubconResult> PostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);

    Task<SubconResult> RollbackAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);
}

public interface ISubconProductionSourceAdapter
{
    Task<SubconResult> ValidateBeforePostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);

    Task<SubconResult> PostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);

    Task<SubconResult> RollbackAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);
}

public sealed class SubconBlockedInventorySourceAdapter : ISubconInventorySourceAdapter
{
    public Task<SubconResult> ValidateBeforePostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconBlockedReasons.NoAtomicInventorySeam));

    public Task<SubconResult> PostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconBlockedReasons.NoAtomicInventorySeam));

    public Task<SubconResult> RollbackAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconBlockedReasons.NoAtomicInventorySeam));
}

public sealed class SubconBlockedProductionSourceAdapter : ISubconProductionSourceAdapter
{
    public Task<SubconResult> ValidateBeforePostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconBlockedReasons.NoProductionCustody));

    public Task<SubconResult> PostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconBlockedReasons.NoProductionCustody));

    public Task<SubconResult> RollbackAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconBlockedReasons.NoProductionCustody));
}
