using ErpWeb.Model.Data;

namespace ErpWeb.Core.Numbering;

public interface IRunningNumberService
{
    Task<int> PeekNextAsync(
        AppDbContext db,
        string companyCode,
        string docKey,
        CancellationToken cancellationToken = default);

    Task<int> GetNextAsync(
        AppDbContext db,
        string companyCode,
        string docKey,
        CancellationToken cancellationToken = default);
}
