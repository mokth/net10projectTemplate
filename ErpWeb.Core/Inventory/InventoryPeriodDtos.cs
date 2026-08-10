using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Inventory;

public sealed class PeriodOpResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public InventoryPeriod? Period { get; init; }
    public IReadOnlyList<InventoryPeriod> Periods { get; init; } = [];
    public IReadOnlyList<StockSnapshot> Snapshots { get; init; } = [];
    public InventoryValuationDto? Valuation { get; init; }

    public static PeriodOpResult Ok(InventoryPeriod period, IReadOnlyList<StockSnapshot>? snapshots = null) =>
        new() { Succeeded = true, Period = period, Snapshots = snapshots ?? [] };

    public static PeriodOpResult Ok(IReadOnlyList<InventoryPeriod> periods) =>
        new() { Succeeded = true, Periods = periods };

    public static PeriodOpResult Ok(InventoryValuationDto valuation) =>
        new() { Succeeded = true, Valuation = valuation };

    public static PeriodOpResult Fail(string code, string? message = null) =>
        new() { Succeeded = false, ErrorCode = code, ErrorMessage = message ?? code };
}

public interface IInventoryPeriodService
{
    Task<PeriodOpResult> EnsurePeriodAsync(int fiscalYear, int fiscalMonth, CancellationToken ct = default);
    Task<PeriodOpResult> ListPeriodsAsync(CancellationToken ct = default);
    Task<PeriodOpResult> ClosePeriodAsync(long periodId, string closedBy, CancellationToken ct = default);
    Task<PeriodOpResult> GetSnapshotsAsync(long periodId, CancellationToken ct = default);
}

public interface IInventoryAsOfService
{
    Task<PeriodOpResult> GetAsOfValuationAsync(
        DateTime asOfDate,
        long? branchId = null,
        long? warehouseId = null,
        CancellationToken ct = default);
}
