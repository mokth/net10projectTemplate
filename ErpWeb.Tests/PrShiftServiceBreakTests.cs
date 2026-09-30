using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class PrShiftServiceBreakTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public PrShiftServiceBreakTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Save_zero_break_shift_promotes_version_and_packs_sentinel()
    {
        var service = CreateShiftService();

        var result = await service.SaveAsync(new PrShiftEditVm
        {
            IsNew = true,
            ShiftCd = "DAY",
            ShiftDes = "Day",
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(17, 0),
            Breaks = []
        });

        Assert.True(result.Succeeded, result.Message);
        await using var db = _factory.CreateDbContext();
        var shift = await db.PrShifts.SingleAsync(x => x.ShiftCd == "DAY");
        Assert.Equal(1, shift.BreakStorageVersion);
        Assert.Empty(await db.PrShiftBreaks.Where(x => x.ShiftCd == "DAY").ToListAsync());
        Assert.NotNull(shift.BreakTm1From);
        Assert.Equal(TimeSpan.Zero, shift.BreakTm1From!.Value.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, shift.BreakTm5To!.Value.TimeOfDay);
    }

    [Fact]
    public async Task Save_replace_five_breaks_with_two_replaces_children_and_packs_remaining_sentinel()
    {
        var service = CreateShiftService();
        var create = await service.SaveAsync(new PrShiftEditVm
        {
            IsNew = true,
            ShiftCd = "DAY",
            ShiftDes = "Day",
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(18, 0),
            Breaks =
            [
                Pair(9, 0, 9, 5),
                Pair(10, 0, 10, 5),
                Pair(11, 0, 11, 5),
                Pair(12, 0, 12, 5),
                Pair(13, 0, 13, 5)
            ]
        });
        Assert.True(create.Succeeded, create.Message);

        var update = await service.SaveAsync(new PrShiftEditVm
        {
            IsNew = false,
            ShiftCd = "DAY",
            ShiftDes = "Day",
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(18, 0),
            Breaks =
            [
                Pair(10, 0, 10, 15),
                Pair(12, 0, 12, 30)
            ]
        });

        Assert.True(update.Succeeded, update.Message);
        await using var db = _factory.CreateDbContext();
        var children = await db.PrShiftBreaks.Where(x => x.ShiftCd == "DAY").OrderBy(x => x.BreakSeq).ToListAsync();
        Assert.Equal(2, children.Count);
        Assert.Equal((byte)1, children[0].BreakSeq);
        Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(children[0].BreakFrom));

        var shift = await db.PrShifts.SingleAsync(x => x.ShiftCd == "DAY");
        Assert.Equal(new TimeOnly(12, 30), TimeOnly.FromDateTime(shift.BreakTm2To!.Value));
        Assert.Equal(TimeSpan.Zero, shift.BreakTm3From!.Value.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, shift.BreakTm5To!.Value.TimeOfDay);
    }

    [Fact]
    public async Task Get_child_version_empty_ignores_stale_wide_break()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.PrShifts.Add(new PrShift
            {
                ShiftCd = "DAY",
                ShiftDes = "Day",
                StartTm = DateTime.Today.AddHours(8),
                EndTm = DateTime.Today.AddHours(17),
                BreakTm1From = DateTime.Today.AddHours(12),
                BreakTm1To = DateTime.Today.AddHours(12).AddMinutes(30),
                BreakStorageVersion = 1,
                CompCode = "DEMO"
            });
            await db.SaveChangesAsync();
        }

        var service = CreateShiftService();
        var result = await service.GetAsync("DAY");

        Assert.True(result.Succeeded, result.Message);
        Assert.Empty(result.Value!.Breaks);
    }

    [Fact]
    public async Task Save_stale_concurrency_does_not_replace_children()
    {
        var service = CreateShiftService();
        Assert.True((await service.SaveAsync(new PrShiftEditVm
        {
            IsNew = true,
            ShiftCd = "DAY",
            ShiftDes = "Day",
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(17, 0),
            Breaks = [Pair(12, 0, 12, 30)]
        })).Succeeded);

        // Touch once so Updated is stamped (concurrency uses OriginalUpdated).
        var primed = await service.GetAsync("DAY");
        Assert.True(primed.Succeeded);
        primed.Value!.Breaks = [Pair(12, 0, 12, 30)];
        Assert.True((await service.SaveAsync(primed.Value)).Succeeded);

        var first = await service.GetAsync("DAY");
        Assert.True(first.Succeeded);
        Assert.NotNull(first.Value!.OriginalUpdated);
        var stale = first.Value;
        stale.Breaks = [Pair(10, 0, 10, 15)];

        var second = await service.GetAsync("DAY");
        Assert.True(second.Succeeded);
        var fresh = second.Value!;
        fresh.Breaks = [Pair(14, 0, 14, 20)];
        Assert.True((await service.SaveAsync(fresh)).Succeeded);

        var conflict = await service.SaveAsync(stale);
        Assert.False(conflict.Succeeded);
        Assert.Equal(PlanningErrorCode.ConcurrencyConflict, conflict.ErrorCode);

        await using var db = _factory.CreateDbContext();
        var child = Assert.Single(await db.PrShiftBreaks.Where(x => x.ShiftCd == "DAY").ToListAsync());
        Assert.Equal(new TimeOnly(14, 0), TimeOnly.FromDateTime(child.BreakFrom));
    }

    [Fact]
    public async Task Delete_removes_parent_and_children()
    {
        var service = CreateShiftService();
        Assert.True((await service.SaveAsync(new PrShiftEditVm
        {
            IsNew = true,
            ShiftCd = "DAY",
            ShiftDes = "Day",
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(17, 0),
            Breaks = [Pair(12, 0, 12, 30)]
        })).Succeeded);

        Assert.True((await service.DeleteAsync("DAY")).Succeeded);
        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.PrShifts.Where(x => x.ShiftCd == "DAY").ToListAsync());
        Assert.Empty(await db.PrShiftBreaks.Where(x => x.ShiftCd == "DAY").ToListAsync());
    }

    public void Dispose() => _connection.Dispose();

    private PrShiftService CreateShiftService() =>
        new(_factory, InventoryTenantTestHelper.CreateTenantContext(), CreateAccess());

    private static IPlanningMasterAccess CreateAccess()
    {
        var access = new Mock<IPlanningMasterAccess>();
        access.Setup(x => x.CanAccessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        access.Setup(x => x.CanAddAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        access.Setup(x => x.CanEditAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        access.Setup(x => x.CanDeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return access.Object;
    }

    private static ShiftTimeCalculator.BreakPair Pair(int fromHour, int fromMinute, int toHour, int toMinute) =>
        new(new TimeOnly(fromHour, fromMinute), new TimeOnly(toHour, toMinute));
}
