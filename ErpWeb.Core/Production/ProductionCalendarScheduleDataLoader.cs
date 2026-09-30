using ErpWeb.Core.Inventory;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public interface IProductionCalendarScheduleDataLoader
{
    Task<ScheduleMachineData> LoadAsync(
        string companyCode,
        string machineCode,
        DateOnly horizonStart,
        DateOnly horizonEnd,
        CancellationToken ct = default);
}

public sealed class ProductionCalendarScheduleDataLoader : IProductionCalendarScheduleDataLoader
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ProductionCalendarScheduleDataLoader(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ScheduleMachineData> LoadAsync(
        string companyCode,
        string machineCode,
        DateOnly horizonStart,
        DateOnly horizonEnd,
        CancellationToken ct = default)
    {
        var comp = PlanningCodeNormalizer.NormalizeCode(companyCode);
        var mac = PlanningCodeNormalizer.NormalizeCode(machineCode);
        var start = horizonStart.ToDateTime(TimeOnly.MinValue);
        var end = horizonEnd.ToDateTime(new TimeOnly(23, 59, 59));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var calRows = await db.PrShiftCalendars.AsNoTracking()
            .Where(x => x.CompCode == comp && x.MachineCode == mac && x.Dt >= start && x.Dt <= end)
            .ToListAsync(ct);

        var calendar = calRows.ToDictionary(
            x => DateOnly.FromDateTime(x.Dt),
            x => new CalendarDayRow
            {
                OperationalDate = DateOnly.FromDateTime(x.Dt),
                DateCd = CalendarYearFingerprint.NormalizeDateCd(x.DateCd),
                ShfGrpCd = x.ShfGrpCd
            });

        var groupCodes = calendar.Values
            .Where(x => x.DateCd == "W" && !string.IsNullOrWhiteSpace(x.ShfGrpCd))
            .Select(x => x.ShfGrpCd!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shiftGroups = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase);
        if (groupCodes.Count > 0)
        {
            var groupRows = await db.PrShiftGroups.AsNoTracking()
                .Where(x => x.CompCode == comp && groupCodes.Contains(x.ShfGrpCd))
                .ToListAsync(ct);
            var shiftCds = groupRows.Select(x => x.ShiftCd).Distinct().ToList();
            var shifts = await db.PrShifts.AsNoTracking()
                .Where(x => x.CompCode == comp && shiftCds.Contains(x.ShiftCd))
                .ToListAsync(ct);
            var shiftByCd = shifts.ToDictionary(x => x.ShiftCd, StringComparer.OrdinalIgnoreCase);

            foreach (var g in groupRows.GroupBy(x => x.ShfGrpCd, StringComparer.OrdinalIgnoreCase))
            {
                var defs = new List<ShiftSourceDefinition>();
                foreach (var member in g)
                {
                    if (!shiftByCd.TryGetValue(member.ShiftCd, out var s))
                        continue;
                    defs.Add(new ShiftSourceDefinition
                    {
                        ShiftCd = s.ShiftCd,
                        Start = ShiftTimeCalculator.FromLegacyDateTime(s.StartTm) ?? default,
                        End = ShiftTimeCalculator.FromLegacyDateTime(s.EndTm) ?? default,
                        Breaks =
                        [
                            ShiftTimeCalculator.FromLegacyPair(s.BreakTm1From, s.BreakTm1To, true),
                            ShiftTimeCalculator.FromLegacyPair(s.BreakTm2From, s.BreakTm2To, true),
                            ShiftTimeCalculator.FromLegacyPair(s.BreakTm3From, s.BreakTm3To, true),
                            ShiftTimeCalculator.FromLegacyPair(s.BreakTm4From, s.BreakTm4To, true),
                            ShiftTimeCalculator.FromLegacyPair(s.BreakTm5From, s.BreakTm5To, true)
                        ]
                    });
                }
                shiftGroups[g.Key] = new ShiftGroupSource
                {
                    ShiftGroupCode = g.Key,
                    Shifts = defs
                };
            }
        }

        // Preventive range: extend by 1 day past horizon for overnight spill
        var prevStart = horizonStart.ToDateTime(TimeOnly.MinValue).AddDays(-1);
        var prevEnd = horizonEnd.ToDateTime(new TimeOnly(23, 59, 59)).AddDays(1);
        var preventives = await db.PrPreventives.AsNoTracking()
            .Where(x => x.MachineCd == mac && x.DownDt >= prevStart && x.DownDt <= prevEnd)
            .ToListAsync(ct);

        var windows = preventives.Select(p =>
        {
            var day = p.DownDt.Date;
            var s = day.Add(p.StartTm.TimeOfDay);
            var e = day.Add(p.EndTm.TimeOfDay);
            if (e <= s) e = e.AddDays(1);
            return new PreventiveWindow { Start = DateTime.SpecifyKind(s, DateTimeKind.Unspecified), End = DateTime.SpecifyKind(e, DateTimeKind.Unspecified) };
        }).ToList();

        return ProductionCalendarScheduler.BuildScheduleData(mac, calendar, shiftGroups, windows);
    }
}
