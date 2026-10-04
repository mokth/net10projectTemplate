using ErpWeb.Core.Planning;

namespace ErpWeb.Tests.Planning.Master;
[Trait(TestCategories.Name, TestCategories.Planning)]
public class PlanningMasterHardeningTests
{
    [Fact]
    public void Calendar_fingerprint_stable_for_same_rows()
    {
        var rows = new[]
        {
            new CalendarYearFingerprint.CalendarDayRow(new DateTime(2024, 2, 29), "W", "G1", null),
            new CalendarYearFingerprint.CalendarDayRow(new DateTime(2024, 3, 1), "O", null, "x")
        };
        var a = CalendarYearFingerprint.Compute(rows);
        var b = CalendarYearFingerprint.Compute(rows.Reverse());
        Assert.Equal(a, b);
    }

    [Fact]
    public void Calendar_fingerprint_leap_year_included()
    {
        var leap = new[] { new CalendarYearFingerprint.CalendarDayRow(new DateTime(2024, 2, 29), "W", null, null) };
        var nonLeap = new[] { new CalendarYearFingerprint.CalendarDayRow(new DateTime(2023, 2, 28), "W", null, null) };
        Assert.False(CalendarYearFingerprint.Compute(leap) == CalendarYearFingerprint.Compute(nonLeap));
    }

    [Fact]
    public void Shift_daytime_five_breaks_net()
    {
        var start = new TimeOnly(8, 0);
        var end = new TimeOnly(17, 0);
        var breaks = new[]
        {
            new ShiftTimeCalculator.BreakPair(new TimeOnly(10, 0), new TimeOnly(10, 15)),
            new ShiftTimeCalculator.BreakPair(new TimeOnly(12, 0), new TimeOnly(12, 30)),
            new ShiftTimeCalculator.BreakPair(new TimeOnly(15, 0), new TimeOnly(15, 10)),
            new ShiftTimeCalculator.BreakPair(null, null),
            new ShiftTimeCalculator.BreakPair(null, null)
        };
        // 9h = 540 - 15 - 30 - 10 = 485
        Assert.Equal(485, ShiftTimeCalculator.ComputeNetMinutes(start, end, breaks));
    }

    [Fact]
    public void ShiftGroup_1439_allowed()
    {
        var a = new ShiftGroupValidator.ShiftInterval("A", new TimeOnly(0, 0), new TimeOnly(12, 0), 720);
        var b = new ShiftGroupValidator.ShiftInterval("B", new TimeOnly(12, 0), new TimeOnly(23, 59), 719);
        Assert.True(ShiftGroupValidator.Validate([a, b]).Succeeded);
    }

    [Fact]
    public void ShiftGroup_full_day_boundary_touch_accepted()
    {
        Assert.True(ShiftGroupValidator.Validate([
            new("A", new TimeOnly(6, 0), new TimeOnly(18, 0), 720),
            new("B", new TimeOnly(18, 0), new TimeOnly(6, 0), 720)
        ]).Succeeded);
    }

    [Fact]
    public void ShiftGroup_one_minute_overlap_rejected()
    {
        Assert.False(ShiftGroupValidator.Validate([
            new("A", new TimeOnly(8, 0), new TimeOnly(20, 1), 721),
            new("B", new TimeOnly(20, 0), new TimeOnly(8, 0), 720)
        ]).Succeeded);
    }

    [Fact]
    public void Import_template_version_constant()
    {
        Assert.Equal("ImportPrdDefV2", PrDefImportService.TemplateVersion);
    }

    [Fact]
    public void Planning_error_codes_cover_relocation()
    {
        Assert.Equal(PlanningErrorCode.RelocationFailed, Enum.Parse<PlanningErrorCode>("RelocationFailed"));
        Assert.Equal(PlanningErrorCode.RelocationBlocked, Enum.Parse<PlanningErrorCode>("RelocationBlocked"));
        Assert.Equal(PlanningErrorCode.ImportTemplateInvalid, Enum.Parse<PlanningErrorCode>("ImportTemplateInvalid"));
    }
}
