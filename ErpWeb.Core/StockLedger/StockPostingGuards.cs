using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;

namespace ErpWeb.Core.StockLedger;

public sealed record StockFreezeScope(long? ProductionLocationId, string? WarehouseCode, string? LocationCode);

public interface IStockPeriodGuard
{
    Task<StockLedgerError?> ValidateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        DateTime effectiveAt,
        CancellationToken cancellationToken = default);
}

public interface IStockFreezeGuard
{
    Task<StockLedgerError?> ValidateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<StockFreezeScope> scopes,
        CancellationToken cancellationToken = default);
}

public sealed class StockPeriodGuard : IStockPeriodGuard
{
    public async Task<StockLedgerError?> ValidateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        DateTime effectiveAt,
        CancellationToken cancellationToken = default)
    {
        var refusal = await IvPeriodCloseGuard.EnsureOpenAsync(
            db, companyCode, branchCode, effectiveAt, cancellationToken);
        return refusal is null ? null : new(StockLedgerErrorCodes.ClosedPeriod, refusal);
    }
}

/// <summary>P1 extension point. P5 replaces this with the persistent production-count freeze guard.</summary>
public sealed class NoActiveStockFreezeGuard : IStockFreezeGuard
{
    public Task<StockLedgerError?> ValidateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<StockFreezeScope> scopes,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<StockLedgerError?>(null);
}
