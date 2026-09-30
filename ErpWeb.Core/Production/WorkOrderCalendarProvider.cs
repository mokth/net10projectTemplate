using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Machine calendars come from the shift calendar. Non-machine steps use the company plant
/// calendar (<c>PrCalendar</c>) as one 08:00–17:00 shift on working days.
/// </summary>
public sealed class WorkOrderCalendarProvider : IWorkOrderCalendarProvider
{
    private const string PlantShiftGroup = "PLANT";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IProductionCalendarScheduleDataLoader _machines;

    public WorkOrderCalendarProvider(
        IDbContextFactory<AppDbContext> dbFactory,
        IProductionCalendarScheduleDataLoader machines)
    {
        _dbFactory = dbFactory;
        _machines = machines;
    }

    public async Task<WorkOrderCalendarSlice> LoadMachineAsync(
        string companyCode,
        string machineCode,
        DateOnly horizonStart,
        DateOnly horizonEnd,
        CancellationToken cancellationToken = default)
    {
        var data = await _machines.LoadAsync(companyCode, machineCode, horizonStart, horizonEnd, cancellationToken);
        return new WorkOrderCalendarSlice { Data = data };
    }

    public async Task<WorkOrderCalendarSlice> LoadPlantAsync(
        string companyCode,
        string? branchCode,
        DateOnly horizonStart,
        DateOnly horizonEnd,
        CancellationToken cancellationToken = default)
    {
        var company = (companyCode ?? string.Empty).Trim().ToUpperInvariant();
        var start = horizonStart.ToDateTime(TimeOnly.MinValue);
        var end = horizonEnd.ToDateTime(TimeOnly.MaxValue);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.PrCalendars.AsNoTracking()
            .Where(x => x.CompCode == company && x.Dt >= start && x.Dt <= end)
            .ToListAsync(cancellationToken);

        var calendar = rows
            .GroupBy(x => DateOnly.FromDateTime(x.Dt))
            .ToDictionary(
                g => g.Key,
                g => new CalendarDayRow
                {
                    OperationalDate = g.Key,
                    DateCd = CalendarYearFingerprint.NormalizeDateCd(g.First().DateCd),
                    ShfGrpCd = string.Equals(CalendarYearFingerprint.NormalizeDateCd(g.First().DateCd), "W", StringComparison.Ordinal)
                        ? PlantShiftGroup
                        : null
                });

        var groups = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase)
        {
            [PlantShiftGroup] = new ShiftGroupSource
            {
                ShiftGroupCode = PlantShiftGroup,
                Shifts =
                [
                    new ShiftSourceDefinition
                    {
                        ShiftCd = "DAY",
                        Start = new TimeOnly(8, 0),
                        End = new TimeOnly(17, 0),
                        Breaks = []
                    }
                ]
            }
        };

        var data = ProductionCalendarScheduler.BuildScheduleData("PLANT", calendar, groups, []);
        DateTime? lastModified = rows.Count == 0 ? null : rows.Max(x => x.Updated ?? x.Created);
        return new WorkOrderCalendarSlice { Data = data, LastModified = lastModified };
    }
}
