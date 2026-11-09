using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Quality;

public sealed class DisabledQaNonGoodAuthorization : IQaNonGoodAuthorization
{
    public static DisabledQaNonGoodAuthorization Instance { get; } = new();

    public Task<string?> ClaimDailyProductionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        ProductionOutput output,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> RestoreDailyProductionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long productionOutputId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
