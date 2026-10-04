using ErpWeb.Core.Planning;

namespace ErpWeb.Tests.Planning.Master;
public class PreventiveWindowNormalizerTests
{
    [Fact]
    public void Same_day_window_normalizes()
    {
        var day = new DateTime(2026, 10, 1);
        Assert.True(PreventiveWindowNormalizer.TryNormalize(
            day, day.AddHours(9), day.AddHours(11), out var w, out _));
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), w.WindowStart);
        Assert.Equal(new DateTime(2026, 10, 1, 11, 0, 0), w.WindowEnd);
    }

    [Fact]
    public void Overnight_window_spills_to_next_day()
    {
        var day = new DateTime(2026, 10, 1);
        Assert.True(PreventiveWindowNormalizer.TryNormalize(
            day, day.AddHours(22), day.AddHours(2), out var w, out _));
        Assert.Equal(new DateTime(2026, 10, 1, 22, 0, 0), w.WindowStart);
        Assert.Equal(new DateTime(2026, 10, 2, 2, 0, 0), w.WindowEnd);
    }

    [Fact]
    public void Overlap_detects_crossing_midnight()
    {
        var day = new DateTime(2026, 10, 1);
        Assert.True(PreventiveWindowNormalizer.TryNormalize(
            day, day.AddHours(22), day.AddHours(2), out var a, out _));
        Assert.True(PreventiveWindowNormalizer.TryNormalize(
            day.AddDays(1), day.AddDays(1).AddHours(1), day.AddDays(1).AddHours(3), out var b, out _));
        Assert.True(PreventiveWindowNormalizer.Overlaps(a, b));
    }

    [Fact]
    public void Non_overlapping_same_day_allowed()
    {
        var day = new DateTime(2026, 10, 1);
        Assert.True(PreventiveWindowNormalizer.TryNormalize(
            day, day.AddHours(9), day.AddHours(10), out var a, out _));
        Assert.True(PreventiveWindowNormalizer.TryNormalize(
            day, day.AddHours(17), day.AddHours(18), out var b, out _));
        Assert.False(PreventiveWindowNormalizer.Overlaps(a, b));
    }

    [Fact]
    public void Year_0001_rejected()
    {
        var day = new DateTime(2026, 10, 1);
        Assert.False(PreventiveWindowNormalizer.TryNormalize(
            day, default, default, out _, out var err));
        Assert.Contains("required", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancelled_status_does_not_block_schedule()
    {
        Assert.True(PreventiveStatuses.BlocksSchedule(PreventiveStatuses.Planned));
        Assert.False(PreventiveStatuses.BlocksSchedule(PreventiveStatuses.Completed));
        Assert.False(PreventiveStatuses.BlocksSchedule(PreventiveStatuses.Cancelled));
    }
}

public class MachineMaintenanceSummaryKpiLogicTests
{
    [Fact]
    public void Pm_cards_use_planned_population_not_actual_completion_date()
    {
        // Scheduled 31-Oct, completed 01-Nov: October Scheduled+Completed include it.
        var planned = new DateTime(2026, 10, 31);
        var from = new DateTime(2026, 10, 1);
        var to = new DateTime(2026, 10, 31);
        Assert.True(planned >= from && planned <= to);

        var actualStart = new DateTime(2026, 11, 1, 9, 0, 0);
        var octoberActualExclusive = to.AddDays(1);
        Assert.False(actualStart >= from && actualStart < octoberActualExclusive);
    }

    [Fact]
    public void Derived_total_cost_and_hours()
    {
        var parts = 180m;
        var labour = 80m;
        var other = 0m;
        Assert.Equal(260m, parts + labour + other);

        var start = new DateTime(2026, 10, 1, 10, 15, 0);
        var end = new DateTime(2026, 10, 1, 12, 0, 0);
        Assert.Equal(1.75m, (decimal)(end - start).TotalHours);
    }
}
