using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Quality;

namespace ErpWeb.Core.Quality;

public sealed class QaStockOutNeed
{
    public int BalLocId { get; init; }
    public string ItemStatus { get; init; } = string.Empty;
    public decimal OnHand { get; init; }
    public decimal Qty { get; init; }
    public int BatchDetailId { get; init; }
}

public interface IQaPhysicalHoldAuthorization
{
    Task<string?> PlaceFromApprovedCaseAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaCase entity,
        QaPolicy? policy,
        CancellationToken cancellationToken = default);

    Task<string?> ReleaseCaseAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaCase entity,
        CancellationToken cancellationToken = default);

    Task<string?> AssertAndClaimStockOutAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string trxType,
        int batchNo,
        IReadOnlyList<QaStockOutNeed> needs,
        CancellationToken cancellationToken = default);

    // The caller holds the affected inventory balance lock in its transaction.
    Task<string?> AssertRemainingCoversHoldsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        decimal remainingBaseQty,
        CancellationToken cancellationToken = default);

    Task<string?> RestoreStockOutAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default);
}
