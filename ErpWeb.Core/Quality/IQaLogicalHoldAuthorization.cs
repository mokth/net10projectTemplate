using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Quality;

namespace ErpWeb.Core.Quality;

public interface IQaLogicalHoldAuthorization
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

    Task<string?> AssertRemainingCoversHoldsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long productionBalLotId,
        decimal remainingBaseQty,
        CancellationToken cancellationToken = default);
}
