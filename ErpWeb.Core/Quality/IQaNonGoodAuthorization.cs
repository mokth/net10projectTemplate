using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Quality;

public interface IQaNonGoodAuthorization
{
    Task<string?> ClaimDailyProductionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        ProductionOutput output,
        CancellationToken cancellationToken = default);

    Task<string?> RestoreDailyProductionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long productionOutputId,
        CancellationToken cancellationToken = default);
}
