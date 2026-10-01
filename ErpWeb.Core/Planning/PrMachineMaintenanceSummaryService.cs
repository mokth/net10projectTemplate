using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

public sealed class MachineMaintenanceSummaryFilter
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string? MachineCd { get; set; }
    public string? MachineType { get; set; }
}

public sealed class MachineMaintenanceSummaryKpis
{
    public int ScheduledPm { get; set; }
    public int CompletedPm { get; set; }
    public int PendingPm { get; set; }
    public int OverduePm { get; set; }
    public int Breakdowns { get; set; }
    public decimal BreakdownHours { get; set; }
    public decimal MaintenanceHours { get; set; }
    public decimal MaintenanceCost { get; set; }
}

public sealed class MachineMaintenanceSummaryByMachine
{
    public string MachineCd { get; set; } = string.Empty;
    public int ScheduledPm { get; set; }
    public int CompletedPm { get; set; }
    public int PendingPm { get; set; }
    public int OverduePm { get; set; }
    public int Breakdowns { get; set; }
    public decimal MaintenanceHours { get; set; }
    public decimal BreakdownHours { get; set; }
    public decimal Cost { get; set; }
}

public sealed class MachineMaintenanceSummaryByReason
{
    public string ReasonCd { get; set; } = string.Empty;
    public string? ReasonType { get; set; }
    public int Count { get; set; }
    public decimal Hours { get; set; }
    public decimal Cost { get; set; }
}

public sealed class MachineMaintenanceSummaryResult
{
    public MachineMaintenanceSummaryKpis Kpis { get; set; } = new();
    public IReadOnlyList<MachineMaintenanceSummaryByMachine> ByMachine { get; set; } = [];
    public IReadOnlyList<MachineMaintenanceSummaryByReason> ByReason { get; set; } = [];
}

public interface IPrMachineMaintenanceSummaryService
{
    Task<PlanningServiceResult<MachineMaintenanceSummaryResult>> GetAsync(MachineMaintenanceSummaryFilter filter, CancellationToken ct = default);
}

public sealed class PrMachineMaintenanceSummaryService : IPrMachineMaintenanceSummaryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;

    public PrMachineMaintenanceSummaryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<PlanningServiceResult<MachineMaintenanceSummaryResult>> GetAsync(MachineMaintenanceSummaryFilter filter, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningMachineSummary, ct))
            return PlanningServiceResult<MachineMaintenanceSummaryResult>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<MachineMaintenanceSummaryResult>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        var from = filter.FromDate.Date;
        var to = filter.ToDate.Date;
        if (to < from)
            return PlanningServiceResult<MachineMaintenanceSummaryResult>.Fail(PlanningErrorCode.ValidationFailed, "To date before From date.");

        var now = DateTime.Now;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var machineFilter = string.IsNullOrWhiteSpace(filter.MachineCd)
            ? null
            : PlanningCodeNormalizer.NormalizeCode(filter.MachineCd);

        var machinesQ = db.PrMachines.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode);
        if (!string.IsNullOrWhiteSpace(filter.MachineType))
            machinesQ = machinesQ.Where(x => x.MachineType == filter.MachineType);
        if (machineFilter is not null)
            machinesQ = machinesQ.Where(x => x.MachineCd == machineFilter);
        var machineCds = await machinesQ.Select(x => x.MachineCd).Distinct().ToListAsync(ct);

        // PM cards: planned period basis (DownDt in range)
        var pmQ = db.PrPreventives.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode
                        && x.DownDt >= from
                        && x.DownDt <= to
                        && x.Status != PreventiveStatuses.Cancelled);
        if (machineFilter is not null)
            pmQ = pmQ.Where(x => x.MachineCd == machineFilter);
        else if (machineCds.Count > 0 && !string.IsNullOrWhiteSpace(filter.MachineType))
            pmQ = pmQ.Where(x => machineCds.Contains(x.MachineCd));

        var pmRows = await pmQ.ToListAsync(ct);

        // Actual maintenance period: StartDateTime (fallback TrxDate) in range
        var actualToExclusive = to.AddDays(1);
        var maintQ = db.PrMacMaintenances.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode
                        && x.Status != PrMaintenanceStatuses.Cancelled);
        if (machineFilter is not null)
            maintQ = maintQ.Where(x => x.MacCode == machineFilter);
        else if (!string.IsNullOrWhiteSpace(filter.MachineType) && machineCds.Count > 0)
            maintQ = maintQ.Where(x => machineCds.Contains(x.MacCode!));

        var maintAll = await maintQ.ToListAsync(ct);
        var maintInActualPeriod = maintAll.Where(x =>
        {
            var when = x.StartDateTime ?? x.TrxDate;
            return when is not null && when.Value >= from && when.Value < actualToExclusive;
        }).ToList();

        var completedMaint = maintInActualPeriod
            .Where(x => string.Equals(x.Status, PrMaintenanceStatuses.Completed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        static decimal HoursOf(DateTime? start, DateTime? end)
        {
            if (start is null || end is null || end <= start) return 0m;
            return (decimal)(end.Value - start.Value).TotalHours;
        }

        var kpis = new MachineMaintenanceSummaryKpis
        {
            ScheduledPm = pmRows.Count(x =>
                string.Equals(x.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Status, PreventiveStatuses.Completed, StringComparison.OrdinalIgnoreCase)),
            CompletedPm = pmRows.Count(x =>
                string.Equals(x.Status, PreventiveStatuses.Completed, StringComparison.OrdinalIgnoreCase)),
            PendingPm = pmRows.Count(x =>
            {
                if (!string.Equals(x.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase)) return false;
                return PreventiveWindowNormalizer.TryNormalize(x.DownDt, x.StartTm, x.EndTm, out var w, out _)
                       && w.WindowEnd >= now;
            }),
            OverduePm = pmRows.Count(x =>
            {
                if (!string.Equals(x.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase)) return false;
                return PreventiveWindowNormalizer.TryNormalize(x.DownDt, x.StartTm, x.EndTm, out var w, out _)
                       && w.WindowEnd < now;
            }),
            Breakdowns = maintInActualPeriod.Count(x =>
                string.Equals(x.MType, PrMaintenanceTypes.Breakdown, StringComparison.OrdinalIgnoreCase)),
            BreakdownHours = completedMaint
                .Where(x => string.Equals(x.MType, PrMaintenanceTypes.Breakdown, StringComparison.OrdinalIgnoreCase))
                .Sum(x => HoursOf(x.StartDateTime, x.EndDateTime)),
            MaintenanceHours = completedMaint.Sum(x => HoursOf(x.StartDateTime, x.EndDateTime)),
            MaintenanceCost = completedMaint.Sum(x => x.PartsCost + x.LabourCost + x.OtherCost)
        };

        var byMachineKeys = pmRows.Select(x => x.MachineCd)
            .Concat(maintInActualPeriod.Select(x => x.MacCode ?? string.Empty))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        var byMachine = byMachineKeys.Select(mac =>
        {
            var pm = pmRows.Where(x => string.Equals(x.MachineCd, mac, StringComparison.OrdinalIgnoreCase)).ToList();
            var mh = maintInActualPeriod.Where(x => string.Equals(x.MacCode, mac, StringComparison.OrdinalIgnoreCase)).ToList();
            var mhDone = mh.Where(x => string.Equals(x.Status, PrMaintenanceStatuses.Completed, StringComparison.OrdinalIgnoreCase)).ToList();
            return new MachineMaintenanceSummaryByMachine
            {
                MachineCd = mac,
                ScheduledPm = pm.Count(x =>
                    string.Equals(x.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Status, PreventiveStatuses.Completed, StringComparison.OrdinalIgnoreCase)),
                CompletedPm = pm.Count(x => string.Equals(x.Status, PreventiveStatuses.Completed, StringComparison.OrdinalIgnoreCase)),
                PendingPm = pm.Count(x =>
                    string.Equals(x.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase)
                    && PreventiveWindowNormalizer.TryNormalize(x.DownDt, x.StartTm, x.EndTm, out var w, out _)
                    && w.WindowEnd >= now),
                OverduePm = pm.Count(x =>
                    string.Equals(x.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase)
                    && PreventiveWindowNormalizer.TryNormalize(x.DownDt, x.StartTm, x.EndTm, out var w, out _)
                    && w.WindowEnd < now),
                Breakdowns = mh.Count(x => string.Equals(x.MType, PrMaintenanceTypes.Breakdown, StringComparison.OrdinalIgnoreCase)),
                MaintenanceHours = mhDone.Sum(x => HoursOf(x.StartDateTime, x.EndDateTime)),
                BreakdownHours = mhDone
                    .Where(x => string.Equals(x.MType, PrMaintenanceTypes.Breakdown, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => HoursOf(x.StartDateTime, x.EndDateTime)),
                Cost = mhDone.Sum(x => x.PartsCost + x.LabourCost + x.OtherCost)
            };
        }).ToList();

        var reasonMeta = await db.PrMaintenanceReasons.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode)
            .ToDictionaryAsync(x => x.ReasonCd, x => x.ReasonType, StringComparer.OrdinalIgnoreCase, ct);

        var byReason = completedMaint
            .GroupBy(x => x.ReasonCd ?? "(none)", StringComparer.OrdinalIgnoreCase)
            .Select(g => new MachineMaintenanceSummaryByReason
            {
                ReasonCd = g.Key,
                ReasonType = reasonMeta.TryGetValue(g.Key, out var rt) ? rt : null,
                Count = g.Count(),
                Hours = g.Sum(x => HoursOf(x.StartDateTime, x.EndDateTime)),
                Cost = g.Sum(x => x.PartsCost + x.LabourCost + x.OtherCost)
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        return PlanningServiceResult<MachineMaintenanceSummaryResult>.Ok(new MachineMaintenanceSummaryResult
        {
            Kpis = kpis,
            ByMachine = byMachine,
            ByReason = byReason
        });
    }
}
