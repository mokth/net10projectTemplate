using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Quality;

public interface IQaFgAuthorization
{
    Task<string?> ClaimFinishedGoodAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        ProductionFinishedGoodReceipt receipt,
        IReadOnlyDictionary<long, ProductionBalLot> pools,
        CancellationToken cancellationToken = default);

    Task<string?> RestoreFinishedGoodAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchId,
        CancellationToken cancellationToken = default);

    Task<string?> AssertWipAdjustmentAllowedAsync(
        AppDbContext db,
        string companyCode,
        IEnumerable<ProductionBalLot> pools,
        CancellationToken cancellationToken = default);
}
