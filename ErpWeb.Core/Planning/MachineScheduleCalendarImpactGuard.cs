using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

/// <summary>
/// Prevents calendar-source writes that overlap committed active machine schedules. The guard
/// reads persisted Work Order schedule dates as a safe fallback while allocation backfill is
/// incomplete; planner write readiness remains independently fail-closed until backfill passes.
/// </summary>
public static class MachineScheduleCalendarImpactGuard
{
    public static Task<List<string>> FindMachinesUsingShiftGroupsAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyCollection<string> shiftGroupCodes,
        DateTime fromDate,
        CancellationToken cancellationToken = default)
    {
        var groups = shiftGroupCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (groups.Count == 0)
            return Task.FromResult(new List<string>());

        return db.PrShiftCalendars.AsNoTracking()
            .Where(x => x.CompCode == companyCode
                && x.Dt >= fromDate
                && x.ShfGrpCd != null
                && groups.Contains(x.ShfGrpCd))
            .Select(x => x.MachineCode)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public static async Task<List<string>> FindMachinesUsingShiftAsync(
        AppDbContext db,
        string companyCode,
        string shiftCode,
        DateTime fromDate,
        CancellationToken cancellationToken = default)
    {
        var groups = await db.PrShiftGroups.AsNoTracking()
            .Where(x => x.CompCode == companyCode && x.ShiftCd == shiftCode)
            .Select(x => x.ShfGrpCd)
            .Distinct()
            .ToListAsync(cancellationToken);
        return await FindMachinesUsingShiftGroupsAsync(db, companyCode, groups, fromDate, cancellationToken);
    }

    public static async Task<string?> FindBlockingImpactAsync(
        AppDbContext db,
        string companyCode,
        DateTime affectedStart,
        DateTime affectedEnd,
        IReadOnlyCollection<string>? machineCodes = null,
        CancellationToken cancellationToken = default)
    {
        if (affectedEnd <= affectedStart || machineCodes is { Count: 0 })
            return null;

        var scheduled = db.ProductionWorkOrderMachines.AsNoTracking()
            .Where(machine => machine.IsSelected
                && machine.PlannedStartDateTime != null
                && machine.PlannedCompletionDateTime != null
                && machine.PlannedStartDateTime < affectedEnd
                && machine.PlannedCompletionDateTime > affectedStart
                && machine.Operation != null
                && machine.Operation.WorkOrder != null
                && machine.Operation.WorkOrder.CompanyCode == companyCode
                && (machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress));

        if (machineCodes is not null)
        {
            var normalized = machineCodes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (normalized.Count == 0)
                return null;
            scheduled = scheduled.Where(machine => normalized.Contains(machine.MachineCode));
        }

        var count = await scheduled.Select(machine => machine.OperationId)
            .Distinct()
            .CountAsync(cancellationToken);
        if (count == 0)
            return null;

        return $"Calendar change blocked: {count:N0} active machine schedule(s) overlap the affected period. "
            + "Resolve them in Machine Schedule Planner before saving.";
    }
}
