using ErpWeb.Core.Production;

namespace ErpWeb.Tests;

/// <summary>In-memory loader used by WO unit tests — unlimited Mon–Fri 08:00–17:00 capacity.</summary>
public sealed class FakeProductionCalendarScheduleDataLoader : IProductionCalendarScheduleDataLoader
{
    public Task<ScheduleMachineData> LoadAsync(
        string companyCode,
        string machineCode,
        DateOnly horizonStart,
        DateOnly horizonEnd,
        CancellationToken ct = default)
    {
        var calendar = new Dictionary<DateOnly, CalendarDayRow>();
        for (var d = horizonStart; d <= horizonEnd; d = d.AddDays(1))
        {
            var isOff = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            calendar[d] = new CalendarDayRow
            {
                OperationalDate = d,
                DateCd = isOff ? "O" : "W",
                ShfGrpCd = isOff ? null : "DEFAULT"
            };
        }

        var groups = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEFAULT"] = new ShiftGroupSource
            {
                ShiftGroupCode = "DEFAULT",
                Shifts =
                [
                    new ShiftSourceDefinition
                    {
                        ShiftCd = "S1",
                        Start = new TimeOnly(8, 0),
                        End = new TimeOnly(17, 0),
                        Breaks = [new ErpWeb.Core.Planning.ShiftTimeCalculator.BreakPair(new TimeOnly(12, 0), new TimeOnly(13, 0))]
                    }
                ]
            }
        };

        var data = ProductionCalendarScheduler.BuildScheduleData(machineCode, calendar, groups, []);
        return Task.FromResult(data);
    }
}

/// <summary>Plant and machine calendars share the same unlimited weekday capacity in unit tests.</summary>
public sealed class AlwaysOpenWorkOrderCalendarProvider : IWorkOrderCalendarProvider
{
    private readonly FakeProductionCalendarScheduleDataLoader _inner = new();

    public async Task<WorkOrderCalendarSlice> LoadMachineAsync(
        string companyCode, string machineCode, DateOnly horizonStart, DateOnly horizonEnd, CancellationToken cancellationToken = default)
        => new()
        {
            Data = await _inner.LoadAsync(companyCode, machineCode, horizonStart, horizonEnd, cancellationToken),
            LastModified = new DateTime(2026, 1, 1)
        };

    public Task<WorkOrderCalendarSlice> LoadPlantAsync(
        string companyCode, string? branchCode, DateOnly horizonStart, DateOnly horizonEnd, CancellationToken cancellationToken = default)
        => LoadMachineAsync(companyCode, "PLANT", horizonStart, horizonEnd, cancellationToken);
}
