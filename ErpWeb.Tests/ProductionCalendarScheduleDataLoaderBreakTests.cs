using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionCalendarScheduleDataLoaderBreakTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public ProductionCalendarScheduleDataLoaderBreakTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Loader_uses_child_breaks_when_version_promoted()
    {
        await SeedShiftAsync(version: 1, includeChildBreak: true, includeStaleWideBreak: true);

        var data = await CreateLoader().LoadAsync("DEMO", "M1", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1));

        var shift = Assert.Single(data.ShiftGroups["G1"].Shifts);
        var br = Assert.Single(shift.Breaks);
        Assert.Equal(new TimeOnly(10, 0), br.Start);
        Assert.Equal(new TimeOnly(10, 30), br.End);
    }

    [Fact]
    public async Task Loader_authoritative_empty_child_version_ignores_stale_wide()
    {
        await SeedShiftAsync(version: 1, includeChildBreak: false, includeStaleWideBreak: true);

        var data = await CreateLoader().LoadAsync("DEMO", "M1", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1));

        var shift = Assert.Single(data.ShiftGroups["G1"].Shifts);
        Assert.Empty(shift.Breaks);
        Assert.Single(data.GlobalUsableIntervals);
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0), data.GlobalUsableIntervals[0].Start);
        Assert.Equal(new DateTime(2026, 6, 1, 17, 0, 0), data.GlobalUsableIntervals[0].End);
    }

    [Fact]
    public async Task Loader_legacy_version_falls_back_to_wide_breaks()
    {
        await SeedShiftAsync(version: 0, includeChildBreak: false, includeStaleWideBreak: true);

        var data = await CreateLoader().LoadAsync("DEMO", "M1", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1));

        var shift = Assert.Single(data.ShiftGroups["G1"].Shifts);
        var br = Assert.Single(shift.Breaks);
        Assert.Equal(new TimeOnly(12, 0), br.Start);
        Assert.Equal(new TimeOnly(12, 30), br.End);
    }

    [Fact]
    public async Task Loader_break_change_changes_schedule_source_hash()
    {
        await SeedShiftAsync(version: 1, includeChildBreak: true, includeStaleWideBreak: false);
        var loader = CreateLoader();
        var start = new DateOnly(2026, 6, 1);
        var end = new DateOnly(2026, 6, 1);

        var h1 = WorkOrderScheduleSourceHasher.Compute(
            await loader.LoadAsync("DEMO", "M1", start, end), start, end);

        await using (var db = _factory.CreateDbContext())
        {
            var child = await db.PrShiftBreaks.SingleAsync(x => x.ShiftCd == "DAY");
            child.BreakTo = DateTime.Today.AddHours(11);
            await db.SaveChangesAsync();
        }

        var h2 = WorkOrderScheduleSourceHasher.Compute(
            await loader.LoadAsync("DEMO", "M1", start, end), start, end);

        Assert.NotEqual(h1, h2);
    }

    public void Dispose() => _connection.Dispose();

    private ProductionCalendarScheduleDataLoader CreateLoader() => new(_factory);

    private async Task SeedShiftAsync(byte version, bool includeChildBreak, bool includeStaleWideBreak)
    {
        await using var db = _factory.CreateDbContext();
        db.PrShiftCalendars.Add(new PrShiftCalendar
        {
            Dt = new DateTime(2026, 6, 1),
            DateCd = "W",
            ShfGrpCd = "G1",
            MachineCode = "M1",
            CompCode = "DEMO"
        });
        db.PrShiftGroups.Add(new PrShiftGroup
        {
            ShfGrpCd = "G1",
            ShfGrpDes = "Group 1",
            ShiftCd = "DAY",
            CompCode = "DEMO"
        });
        db.PrShifts.Add(new PrShift
        {
            ShiftCd = "DAY",
            ShiftDes = "Day",
            StartTm = DateTime.Today.AddHours(8),
            EndTm = DateTime.Today.AddHours(17),
            BreakTm1From = includeStaleWideBreak ? DateTime.Today.AddHours(12) : DateTime.Today,
            BreakTm1To = includeStaleWideBreak ? DateTime.Today.AddHours(12).AddMinutes(30) : DateTime.Today,
            BreakStorageVersion = version,
            CompCode = "DEMO"
        });
        if (includeChildBreak)
        {
            db.PrShiftBreaks.Add(new PrShiftBreak
            {
                CompCode = "DEMO",
                ShiftCd = "DAY",
                BreakSeq = 1,
                BreakFrom = DateTime.Today.AddHours(10),
                BreakTo = DateTime.Today.AddHours(10).AddMinutes(30)
            });
        }

        await db.SaveChangesAsync();
    }
}
