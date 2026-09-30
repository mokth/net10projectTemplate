using ErpWeb.Core.Planning;
using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public class PlanningMasterFoundationTests
{
    [Fact]
    public void ShiftTime_encode_decode_roundtrip()
    {
        Assert.Equal(8.30, ShiftTimeCalculator.EncodeTotalTime(8 * 60 + 30), 2);
        Assert.Equal(8 * 60 + 30, ShiftTimeCalculator.DecodeTotalTime(8.30));
    }

    [Fact]
    public void ShiftTime_overnight_net_minutes()
    {
        var start = new TimeOnly(22, 0);
        var end = new TimeOnly(6, 0);
        var breaks = new[] { new ShiftTimeCalculator.BreakPair(new TimeOnly(23, 45), new TimeOnly(0, 15)) };
        var net = ShiftTimeCalculator.ComputeNetMinutes(start, end, breaks);
        // 8h span (480) - 30 break = 450
        Assert.Equal(450, net);
    }

    [Fact]
    public void ShiftTime_midnight_break_start_supported()
    {
        var start = new TimeOnly(20, 0);
        var end = new TimeOnly(8, 0);
        var breaks = new[] { new ShiftTimeCalculator.BreakPair(new TimeOnly(0, 0), new TimeOnly(0, 30)) };
        var net = ShiftTimeCalculator.ComputeNetMinutes(start, end, breaks);
        Assert.Equal(12 * 60 - 30, net);
    }

    [Fact]
    public void ShiftTime_start_equals_end_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ShiftTimeCalculator.NormalizeShiftSpan(new TimeOnly(8, 0), new TimeOnly(8, 0)));
    }

    [Fact]
    public void Break_one_sided_is_invalid()
    {
        var err = ShiftTimeCalculator.ValidateBreakPair(new ShiftTimeCalculator.BreakPair(new TimeOnly(12, 0), null), 1);
        Assert.NotNull(err);
    }

    [Fact]
    public void Break_zero_duration_invalid()
    {
        var err = ShiftTimeCalculator.ValidateBreakPair(
            new ShiftTimeCalculator.BreakPair(new TimeOnly(12, 0), new TimeOnly(12, 0)), 1);
        Assert.NotNull(err);
    }

    [Fact]
    public void ShiftGroup_adjacent_overnight_allowed_overlap_rejected()
    {
        var a = new ShiftGroupValidator.ShiftInterval("A", new TimeOnly(22, 0), new TimeOnly(2, 0), 240);
        var b = new ShiftGroupValidator.ShiftInterval("B", new TimeOnly(2, 0), new TimeOnly(6, 0), 240);
        Assert.True(ShiftGroupValidator.Validate([a, b]).Succeeded);

        var c = new ShiftGroupValidator.ShiftInterval("C", new TimeOnly(22, 0), new TimeOnly(2, 1), 241);
        Assert.False(ShiftGroupValidator.Validate([c, b]).Succeeded);
    }

    [Fact]
    public void ShiftGroup_exact_1440_coverage_allowed()
    {
        var a = new ShiftGroupValidator.ShiftInterval("A", new TimeOnly(8, 0), new TimeOnly(20, 0), 720);
        var b = new ShiftGroupValidator.ShiftInterval("B", new TimeOnly(20, 0), new TimeOnly(8, 0), 720);
        Assert.True(ShiftGroupValidator.Validate([a, b]).Succeeded);
    }

    [Fact]
    public void ShiftGroup_three_shifts_rejected()
    {
        var a = new ShiftGroupValidator.ShiftInterval("A", new TimeOnly(0, 0), new TimeOnly(8, 0), 480);
        var b = new ShiftGroupValidator.ShiftInterval("B", new TimeOnly(8, 0), new TimeOnly(16, 0), 480);
        var c = new ShiftGroupValidator.ShiftInterval("C", new TimeOnly(16, 0), new TimeOnly(0, 0), 480);
        Assert.False(ShiftGroupValidator.Validate([a, b, c]).Succeeded);
    }

    [Fact]
    public void ShiftGroup_circular_overnight_overlap_cases()
    {
        Assert.False(ShiftGroupValidator.Validate([
            new("A", new TimeOnly(23, 0), new TimeOnly(7, 0), 480),
            new("B", new TimeOnly(6, 0), new TimeOnly(14, 0), 480)
        ]).Succeeded);

        Assert.True(ShiftGroupValidator.Validate([
            new("A", new TimeOnly(23, 0), new TimeOnly(7, 0), 480),
            new("B", new TimeOnly(7, 0), new TimeOnly(15, 0), 480)
        ]).Succeeded);

        Assert.True(ShiftGroupValidator.Validate([
            new("A", new TimeOnly(22, 0), new TimeOnly(6, 0), 480),
            new("B", new TimeOnly(6, 0), new TimeOnly(14, 0), 480)
        ]).Succeeded);

        Assert.False(ShiftGroupValidator.Validate([
            new("A", new TimeOnly(22, 0), new TimeOnly(6, 0), 480),
            new("B", new TimeOnly(5, 59), new TimeOnly(14, 0), 481)
        ]).Succeeded);
    }

    [Fact]
    public void Calendar_fingerprint_changes_on_row_delete()
    {
        var rows = new[]
        {
            new CalendarYearFingerprint.CalendarDayRow(new DateTime(2026, 1, 1), "W", "G1", null),
            new CalendarYearFingerprint.CalendarDayRow(new DateTime(2026, 1, 2), "O", null, null)
        };
        var fp1 = CalendarYearFingerprint.Compute(rows);
        var fp2 = CalendarYearFingerprint.Compute(rows.Take(1));
        Assert.False(fp1 == fp2);
    }

    [Fact]
    public void Code_normalizer_uppercases_code_only()
    {
        Assert.Equal("CUT01", PlanningCodeNormalizer.NormalizeCode(" cut01 "));
        Assert.Equal("Cutting Process", PlanningCodeNormalizer.NormalizeDescription(" Cutting Process "));
    }

    [Fact]
    public void DefaultGroup_resolve_rules()
    {
        Assert.False(PrShiftGroupPlanningService.ResolveDefaultGroupCode([]).Succeeded);

        var one = new List<PrShiftGroup>
        {
            new() { ShfGrpCd = "G1", ShiftCd = "S1", DefaultGrp = false }
        };
        Assert.Equal("G1", PrShiftGroupPlanningService.ResolveDefaultGroupCode(one).Value);

        var multiNoDefault = new List<PrShiftGroup>
        {
            new() { ShfGrpCd = "G1", ShiftCd = "S1", DefaultGrp = false },
            new() { ShfGrpCd = "G2", ShiftCd = "S1", DefaultGrp = false }
        };
        Assert.False(PrShiftGroupPlanningService.ResolveDefaultGroupCode(multiNoDefault).Succeeded);

        var multiOneDefault = new List<PrShiftGroup>
        {
            new() { ShfGrpCd = "G1", ShiftCd = "S1", DefaultGrp = false },
            new() { ShfGrpCd = "G2", ShiftCd = "S1", DefaultGrp = true }
        };
        Assert.Equal("G2", PrShiftGroupPlanningService.ResolveDefaultGroupCode(multiOneDefault).Value);

        var multiManyDefault = new List<PrShiftGroup>
        {
            new() { ShfGrpCd = "G1", ShiftCd = "S1", DefaultGrp = true },
            new() { ShfGrpCd = "G2", ShiftCd = "S1", DefaultGrp = true }
        };
        Assert.False(PrShiftGroupPlanningService.ResolveDefaultGroupCode(multiManyDefault).Succeeded);
    }
}
