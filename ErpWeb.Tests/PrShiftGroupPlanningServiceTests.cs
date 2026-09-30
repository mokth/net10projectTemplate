using ErpWeb.Core.Planning;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public class PrShiftGroupPlanningServiceTests
{
    [Fact]
    public void BuildInfo_daytime_with_breaks_produces_ordered_work_slots()
    {
        var def = new ShiftGroupDefinition
        {
            ShiftGroupCode = "G1",
            IsDefault = true,
            Shifts =
            [
                new ShiftDefinition
                {
                    ShiftCd = "DAY",
                    Start = new TimeOnly(8, 0),
                    End = new TimeOnly(17, 0),
                    GrossMinutes = 540,
                    BreakMinutes = 60,
                    NetMinutes = 480,
                    Breaks = [new ShiftTimeCalculator.BreakPair(new TimeOnly(12, 0), new TimeOnly(13, 0))]
                }
            ]
        };

        var info = PrShiftGroupPlanningService.BuildInfo(def, new DateOnly(2026, 9, 29));
        Assert.Equal(new DateOnly(2026, 9, 29), info.Date);
        Assert.Equal(480, info.TotalWorkingMinPerDay);
        Assert.Equal(60, info.TotalBreakMinPerDay);
        Assert.Equal(1, info.ShiftCount);
        Assert.Equal(2, info.Shifts[0].WorkSlots.Count);
        Assert.Equal(new DateTime(2026, 9, 29, 8, 0, 0), info.Shifts[0].WorkSlots[0].Start);
        Assert.Equal(new DateTime(2026, 9, 29, 12, 0, 0), info.Shifts[0].WorkSlots[0].End);
        Assert.Equal(new DateTime(2026, 9, 29, 13, 0, 0), info.Shifts[0].WorkSlots[1].Start);
        Assert.Equal(new DateTime(2026, 9, 29, 17, 0, 0), info.Shifts[0].WorkSlots[1].End);
        // NominalDaySegmentMinutes is NOT capacity
        Assert.Equal(1440, info.NominalDaySegmentMinutes);
    }

    [Fact]
    public void BuildInfo_overnight_post_midnight_break_anchors_to_date_plus_one()
    {
        var def = new ShiftGroupDefinition
        {
            ShiftGroupCode = "NIGHT",
            Shifts =
            [
                new ShiftDefinition
                {
                    ShiftCd = "N1",
                    Start = new TimeOnly(20, 0),
                    End = new TimeOnly(8, 0),
                    GrossMinutes = 720,
                    BreakMinutes = 60,
                    NetMinutes = 660,
                    Breaks = [new ShiftTimeCalculator.BreakPair(new TimeOnly(0, 0), new TimeOnly(1, 0))]
                }
            ]
        };

        var info = PrShiftGroupPlanningService.BuildInfo(def, new DateOnly(2026, 9, 29));
        var shift = info.Shifts[0];
        Assert.Equal(new DateTime(2026, 9, 29, 20, 0, 0), shift.StartDateTime);
        Assert.Equal(new DateTime(2026, 9, 30, 8, 0, 0), shift.EndDateTime);
        Assert.Equal(2, shift.WorkSlots.Count);
        Assert.Equal(new DateTime(2026, 9, 29, 20, 0, 0), shift.WorkSlots[0].Start);
        Assert.Equal(new DateTime(2026, 9, 30, 0, 0, 0), shift.WorkSlots[0].End);
        Assert.Equal(new DateTime(2026, 9, 30, 1, 0, 0), shift.WorkSlots[1].Start);
        Assert.Equal(new DateTime(2026, 9, 30, 8, 0, 0), shift.WorkSlots[1].End);
    }

    [Fact]
    public void BuildInfo_two_unequal_shifts_totals()
    {
        var def = new ShiftGroupDefinition
        {
            ShiftGroupCode = "G2",
            Shifts =
            [
                new ShiftDefinition
                {
                    ShiftCd = "A",
                    Start = new TimeOnly(8, 0),
                    End = new TimeOnly(16, 0),
                    GrossMinutes = 480,
                    BreakMinutes = 0,
                    NetMinutes = 480,
                    Breaks = []
                },
                new ShiftDefinition
                {
                    ShiftCd = "B",
                    Start = new TimeOnly(16, 0),
                    End = new TimeOnly(22, 0),
                    GrossMinutes = 360,
                    BreakMinutes = 0,
                    NetMinutes = 360,
                    Breaks = []
                }
            ]
        };

        var info = PrShiftGroupPlanningService.BuildInfo(def, new DateOnly(2026, 1, 1));
        Assert.Equal(840, info.TotalWorkingMinPerDay);
        Assert.Equal(2, info.ShiftCount);
        Assert.Equal(720, info.NominalDaySegmentMinutes); // 1440/2 — not capacity
        Assert.Equal(480, info.Shifts[0].NetMinutes);
        Assert.Equal(360, info.Shifts[1].NetMinutes);
    }

    [Fact]
    public void MachineGenerate_work_off_conflict_rejected()
    {
        var r = MachineCalendarGenerator.ValidateRequest(new MachineCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            OffWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Monday },
            WorkWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Monday },
            SelectedShiftGroupCd = "G1"
        });
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void MachineGenerate_work_days_require_selected_group()
    {
        var r = MachineCalendarGenerator.ValidateRequest(new MachineCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            OffWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday },
            WorkWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Monday },
            SelectedShiftGroupCd = null,
            DefaultShiftGroupCd = "DEF"
        });
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void MachineGenerate_fully_explicit_no_default_succeeds()
    {
        var off = new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday };
        var work = new HashSet<DayOfWeek>
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday
        };
        Assert.False(MachineCalendarGenerator.RequiresDefaultGroup(off, work));

        var gen = MachineCalendarGenerator.Generate(new MachineCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            OffWeekdays = off,
            WorkWeekdays = work,
            SelectedShiftGroupCd = "G1",
            DefaultShiftGroupCd = null,
            HolidayDates = new HashSet<DateOnly> { new(2026, 1, 1) }
        });
        Assert.True(gen.Succeeded);
        Assert.All(gen.Value!.Where(d => d.DateCd == "O"), d => Assert.Null(d.ShfGrpCd));
        Assert.Contains(gen.Value!, d => d.Date == new DateTime(2026, 1, 1) && d.DateCd == "O" && d.ShfGrpCd is null);
        Assert.Contains(gen.Value!, d => d.Date.DayOfWeek == DayOfWeek.Monday && d.DateCd == "W" && d.ShfGrpCd == "G1");
    }

    [Fact]
    public void MachineGenerate_fallback_without_default_fails()
    {
        var off = new HashSet<DayOfWeek> { DayOfWeek.Sunday };
        var work = new HashSet<DayOfWeek> { DayOfWeek.Monday };
        Assert.True(MachineCalendarGenerator.RequiresDefaultGroup(off, work));

        var gen = MachineCalendarGenerator.Generate(new MachineCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            OffWeekdays = off,
            WorkWeekdays = work,
            SelectedShiftGroupCd = "G1",
            DefaultShiftGroupCd = null
        });
        Assert.False(gen.Succeeded);
    }

    [Fact]
    public void MachineGenerate_holiday_clears_shift_group()
    {
        var allWork = Enum.GetValues<DayOfWeek>().ToHashSet();
        var gen = MachineCalendarGenerator.Generate(new MachineCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            OffWeekdays = new HashSet<DayOfWeek>(),
            WorkWeekdays = allWork,
            SelectedShiftGroupCd = "G1",
            HolidayDates = new HashSet<DateOnly> { new(2026, 5, 1) }
        });
        Assert.True(gen.Succeeded);
        var may1 = gen.Value!.Single(d => d.Date == new DateTime(2026, 5, 1));
        Assert.Equal("O", may1.DateCd);
        Assert.Null(may1.ShfGrpCd);
    }

    [Fact]
    public void Break_outside_shift_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ShiftTimeCalculator.ComputeNetMinutes(
                new TimeOnly(8, 0),
                new TimeOnly(17, 0),
                [new ShiftTimeCalculator.BreakPair(new TimeOnly(7, 0), new TimeOnly(7, 30))]));
    }

    [Fact]
    public void Break_at_shift_start_allowed()
    {
        var net = ShiftTimeCalculator.ComputeNetMinutes(
            new TimeOnly(8, 0),
            new TimeOnly(17, 0),
            [new ShiftTimeCalculator.BreakPair(new TimeOnly(8, 0), new TimeOnly(8, 15))]);
        Assert.Equal(540 - 15, net);
    }

    [Fact]
    public void Overnight_break_crossing_midnight()
    {
        var net = ShiftTimeCalculator.ComputeNetMinutes(
            new TimeOnly(23, 0),
            new TimeOnly(7, 0),
            [new ShiftTimeCalculator.BreakPair(new TimeOnly(23, 30), new TimeOnly(0, 30))]);
        Assert.Equal(480 - 60, net);
    }
}
