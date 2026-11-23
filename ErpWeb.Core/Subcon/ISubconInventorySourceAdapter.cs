using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Subcon;

public interface ISubconInventorySourceAdapter
{
    bool SupportsLiveCustodyPosting => false;

    Task<SubconResult> ValidateBeforePostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);

    Task<SubconResult> PostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);

    Task<SubconResult> PostAsync(
        SubconKeyCommand command,
        SubconDispatchDto loaded,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default) =>
        PostAsync(loaded, cancellationToken);

    Task<SubconResult> RollbackAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default);

    Task<SubconResult> RollbackAsync(
        SubconKeyCommand command,
        SubconDispatchDto loaded,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default) =>
        RollbackAsync(loaded, cancellationToken);

    Task<SubconResult> ValidateReceiptBeforePostAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default);

    Task<SubconResult> PostReceiptAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default);

    Task<SubconResult> PostReceiptAsync(
        SubconKeyCommand command,
        SubconReceiptDto loaded,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default) =>
        PostReceiptAsync(loaded, cancellationToken);

    Task<SubconResult> RollbackReceiptAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default);

    Task<SubconResult> RollbackReceiptAsync(
        SubconKeyCommand command,
        SubconReceiptDto loaded,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default) =>
        RollbackReceiptAsync(loaded, cancellationToken);
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

    public Task<SubconResult> ValidateReceiptBeforePostAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconBlockedReasons.NoAtomicInventorySeam));

    public Task<SubconResult> PostReceiptAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconBlockedReasons.NoAtomicInventorySeam));

    public Task<SubconResult> RollbackReceiptAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default) =>
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
