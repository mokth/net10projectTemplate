using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;

namespace ErpWeb.Tests.Planning.Master;
public class ProductionCalendarSchedulerTests
{
    private static ShiftSourceDefinition Shift(TimeOnly start, TimeOnly end, params (TimeOnly a, TimeOnly b)[] breaks) =>
        new()
        {
            ShiftCd = $"{start:HHmm}-{end:HHmm}",
            Start = start,
            End = end,
            Breaks = breaks.Select(x => new ShiftTimeCalculator.BreakPair(x.a, x.b)).ToList()
        };

    private static ScheduleMachineData Build(
        DateOnly date,
        IReadOnlyList<ShiftSourceDefinition> shifts,
        IReadOnlyList<PreventiveWindow>? preventive = null,
        string dateCd = "W")
    {
        var calendar = new Dictionary<DateOnly, CalendarDayRow>
        {
            [date] = new CalendarDayRow { OperationalDate = date, DateCd = dateCd, ShfGrpCd = dateCd == "W" ? "G1" : null }
        };
        // Pad surrounding days as Off so ResolveInitial can walk
        for (var i = -3; i <= 3; i++)
        {
            var d = date.AddDays(i);
            if (calendar.ContainsKey(d)) continue;
            calendar[d] = new CalendarDayRow { OperationalDate = d, DateCd = "O", ShfGrpCd = null };
        }

        var groups = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["G1"] = new ShiftGroupSource { ShiftGroupCode = "G1", Shifts = shifts }
        };
        return ProductionCalendarScheduler.BuildScheduleData("M1", calendar, groups, preventive ?? []);
    }

    [Fact]
    public void IB1_overlapping_shifts_break_covered_by_other_shift()
    {
        var d = new DateOnly(2026, 6, 1);
        var data = Build(d,
        [
            Shift(new TimeOnly(8, 0), new TimeOnly(16, 0), (new TimeOnly(12, 0), new TimeOnly(13, 0))),
            Shift(new TimeOnly(12, 0), new TimeOnly(20, 0))
        ]);
        Assert.Single(data.GlobalUsableIntervals);
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0), data.GlobalUsableIntervals[0].Start);
        Assert.Equal(new DateTime(2026, 6, 1, 20, 0, 0), data.GlobalUsableIntervals[0].End);
    }

    [Fact]
    public void IB2_partial_overlap_leaves_break_gap()
    {
        var d = new DateOnly(2026, 6, 1);
        var data = Build(d,
        [
            Shift(new TimeOnly(8, 0), new TimeOnly(16, 0), (new TimeOnly(12, 0), new TimeOnly(13, 0))),
            Shift(new TimeOnly(12, 30), new TimeOnly(20, 0))
        ]);
        Assert.Equal(2, data.GlobalUsableIntervals.Count);
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0), data.GlobalUsableIntervals[0].Start);
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0), data.GlobalUsableIntervals[0].End);
        Assert.Equal(new DateTime(2026, 6, 1, 12, 30, 0), data.GlobalUsableIntervals[1].Start);
        Assert.Equal(new DateTime(2026, 6, 1, 20, 0, 0), data.GlobalUsableIntervals[1].End);
    }

    [Fact]
    public void IB3_both_shifts_share_break_gap_remains()
    {
        var d = new DateOnly(2026, 6, 1);
        var data = Build(d,
        [
            Shift(new TimeOnly(8, 0), new TimeOnly(16, 0), (new TimeOnly(12, 0), new TimeOnly(13, 0))),
            Shift(new TimeOnly(12, 0), new TimeOnly(20, 0), (new TimeOnly(12, 0), new TimeOnly(13, 0)))
        ]);
        Assert.Equal(2, data.GlobalUsableIntervals.Count);
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0), data.GlobalUsableIntervals[0].End);
        Assert.Equal(new DateTime(2026, 6, 1, 13, 0, 0), data.GlobalUsableIntervals[1].Start);
    }

    [Fact]
    public void PA1_forward_preventive_at_shift_start()
    {
        var d = new DateOnly(2026, 6, 1);
        var data = Build(d,
            [Shift(new TimeOnly(8, 0), new TimeOnly(17, 0))],
            [new PreventiveWindow { Start = new DateTime(2026, 6, 1, 8, 0, 0), End = new DateTime(2026, 6, 1, 10, 0, 0) }]);
        var anchor = ProductionCalendarScheduler.ResolveInitialForwardAnchor(data, d);
        Assert.Equal(new DateTime(2026, 6, 1, 10, 0, 0), anchor);
    }

    [Fact]
    public void PA2_backward_preventive_at_shift_end()
    {
        var d = new DateOnly(2026, 6, 1);
        var data = Build(d,
            [Shift(new TimeOnly(8, 0), new TimeOnly(17, 0))],
            [new PreventiveWindow { Start = new DateTime(2026, 6, 1, 15, 0, 0), End = new DateTime(2026, 6, 1, 17, 0, 0) }]);
        var anchor = ProductionCalendarScheduler.ResolveInitialBackwardAnchor(data, d);
        Assert.Equal(new DateTime(2026, 6, 1, 15, 0, 0), anchor);
    }

    [Fact]
    public void PA4_overnight_rule_a_backward_with_preventive()
    {
        var d = new DateOnly(2026, 6, 1);
        var calendar = new Dictionary<DateOnly, CalendarDayRow>
        {
            [d] = new() { OperationalDate = d, DateCd = "W", ShfGrpCd = "G1" },
            [d.AddDays(1)] = new() { OperationalDate = d.AddDays(1), DateCd = "O", ShfGrpCd = null }
        };
        var groups = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["G1"] = new ShiftGroupSource
            {
                ShiftGroupCode = "G1",
                Shifts = [Shift(new TimeOnly(20, 0), new TimeOnly(4, 0))]
            }
        };
        var preventive = new[]
        {
            new PreventiveWindow
            {
                Start = new DateTime(2026, 6, 2, 2, 0, 0),
                End = new DateTime(2026, 6, 2, 4, 0, 0)
            }
        };
        var data = ProductionCalendarScheduler.BuildScheduleData("M1", calendar, groups, preventive);
        var anchor = ProductionCalendarScheduler.ResolveInitialBackwardAnchor(data, d);
        Assert.Equal(new DateTime(2026, 6, 2, 2, 0, 0), anchor);
    }

    [Fact]
    public void MR1_overnight_into_missing_tuesday_succeeds_within_monday_owned()
    {
        var mon = new DateOnly(2026, 6, 1);
        var calendar = new Dictionary<DateOnly, CalendarDayRow>
        {
            [mon] = new() { OperationalDate = mon, DateCd = "W", ShfGrpCd = "G1" }
            // Tuesday missing intentionally
        };
        var groups = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["G1"] = new ShiftGroupSource
            {
                ShiftGroupCode = "G1",
                Shifts = [Shift(new TimeOnly(20, 0), new TimeOnly(4, 0))]
            }
        };
        var data = ProductionCalendarScheduler.BuildScheduleData("M1", calendar, groups, []);
        var result = ProductionCalendarScheduler.ScheduleMachine(
            data,
            new DateTime(2026, 6, 1, 22, 0, 0),
            TimeSpan.FromHours(4),
            ScheduleDirection.Forward);
        Assert.Equal(new DateTime(2026, 6, 2, 2, 0, 0), result.ActualCompletion);
    }

    [Fact]
    public void Missing_row_resolve_initial_fails()
    {
        var data = Build(new DateOnly(2026, 6, 1), [Shift(new TimeOnly(8, 0), new TimeOnly(17, 0))]);
        Assert.Throws<ScheduleFailureException>(() =>
            ProductionCalendarScheduler.ResolveInitialForwardAnchor(data, new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void Forward_schedule_skips_off_day()
    {
        var start = new DateOnly(2026, 6, 5); // Friday
        var calendar = new Dictionary<DateOnly, CalendarDayRow>();
        for (var i = 0; i < 5; i++)
        {
            var d = start.AddDays(i);
            var off = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            calendar[d] = new CalendarDayRow
            {
                OperationalDate = d,
                DateCd = off ? "O" : "W",
                ShfGrpCd = off ? null : "G1"
            };
        }
        var groups = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["G1"] = new ShiftGroupSource
            {
                ShiftGroupCode = "G1",
                Shifts = [Shift(new TimeOnly(8, 0), new TimeOnly(17, 0), (new TimeOnly(12, 0), new TimeOnly(13, 0)))]
            }
        };
        var data = ProductionCalendarScheduler.BuildScheduleData("M1", calendar, groups, []);
        // 8h net/day; request 10h → Fri + Mon
        var result = ProductionCalendarScheduler.ScheduleMachine(
            data,
            ProductionCalendarScheduler.ResolveInitialForwardAnchor(data, start),
            TimeSpan.FromHours(10),
            ScheduleDirection.Forward);
        Assert.True(result.ActualCompletion.Date > start.ToDateTime(TimeOnly.MinValue).Date);
        Assert.Equal(DayOfWeek.Monday, result.ActualCompletion.DayOfWeek);
    }

    [Fact]
    public void Scheduling_input_hash_stable_under_reorder()
    {
        var opsA = new List<ProductionWorkOrderOperationVm>
        {
            new()
            {
                SequenceNo = 10,
                OperationCode = "B",
                Resources =
                [
                    new() { SequenceNo = 2, ResourceType = "MACHINE", ResourceCode = "M2", RunMinutes = 5 },
                    new() { SequenceNo = 1, ResourceType = "MACHINE", ResourceCode = "M1", RunMinutes = 10 }
                ]
            },
            new()
            {
                SequenceNo = 10,
                OperationCode = "A",
                Resources = [new() { SequenceNo = 1, ResourceType = "MACHINE", ResourceCode = "M1", RunMinutes = 3 }]
            }
        };
        var opsB = opsA.AsEnumerable().Reverse().ToList();
        var h1 = ProductionSchedulingInputHasher.Compute("FORWARD", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 1m, opsA);
        var h2 = ProductionSchedulingInputHasher.Compute("FORWARD", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 1m, opsB);
        Assert.Equal(h1, h2);
    }
}
