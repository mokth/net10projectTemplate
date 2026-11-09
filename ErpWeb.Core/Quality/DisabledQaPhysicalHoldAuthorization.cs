using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Quality;

namespace ErpWeb.Core.Quality;

public sealed class DisabledQaPhysicalHoldAuthorization : IQaPhysicalHoldAuthorization
{
    public static DisabledQaPhysicalHoldAuthorization Instance { get; } = new();

    public Task<string?> PlaceFromApprovedCaseAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaCase entity,
        QaPolicy? policy,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> ReleaseCaseAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaCase entity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> AssertAndClaimStockOutAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string trxType,
        int batchNo,
        IReadOnlyList<QaStockOutNeed> needs,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> RestoreStockOutAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> AssertRemainingCoversHoldsAsync(
        AppDbContext db, string companyCode, string branchCode, int balLocId,
        decimal remainingBaseQty, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
