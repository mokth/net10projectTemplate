using System.Globalization;
using DevExpress.Blazor;
using ErpWeb.Core.Production;
using ErpWeb.UI.Planning.WorkOrders;

namespace ErpWeb.Tests.Planning;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class MachineSchedulePlannerInputTests
{
    [Theory]
    [InlineData("2026-10-09", "08:30", "en-US")]
    [InlineData("09/10/2026", "08:30 AM", "en-GB")]
    public void Requested_start_accepts_html_and_localized_browser_values(
        string dateText,
        string timeText,
        string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        var parsed = MachineSchedulePlannerInputParser.TryParseRequestedStart(
            dateText, timeText, culture, out var requestedStart);

        Assert.True(parsed);
        Assert.Equal(new DateTime(2026, 10, 9, 8, 30, 0), requestedStart);
        Assert.Equal(DateTimeKind.Unspecified, requestedStart.Kind);
    }

    [Theory]
    [InlineData("", "08:30")]
    [InlineData("2026-10-09", "not a time")]
    public void Requested_start_rejects_invalid_values(string dateText, string timeText)
    {
        var parsed = MachineSchedulePlannerInputParser.TryParseRequestedStart(
            dateText, timeText, CultureInfo.GetCultureInfo("en-GB"), out _);

        Assert.False(parsed);
    }

    [Fact]
    public void Dragging_tail_bar_shifts_whole_operation_by_drag_offset()
    {
        // Operation spans Mon 02 Nov .. Fri 06 Nov; dragging the 06 Nov bar left to 05 Nov (-1 day)
        // must shift the operation's start earlier by 1 day (01 Nov), NOT jump PlannedStart to 05 Nov.
        var operationStart = new DateTime(2026, 11, 2, 8, 0, 0);
        var tailBarOriginalStart = new DateTime(2026, 11, 6, 8, 0, 0);
        var tailBarDroppedStart = new DateTime(2026, 11, 5, 8, 0, 0);

        var result = MachineSchedulePlannerDragRules.ComputeShiftedStart(
            operationStart,
            tailBarOriginalStart,
            tailBarDroppedStart);

        Assert.Equal(TimeSpan.FromDays(-1), result.Delta);
        Assert.Equal(new DateTime(2026, 11, 1, 8, 0, 0), result.ShiftedStart);
        Assert.Equal(DateTimeKind.Unspecified, result.ShiftedStart.Kind);
        Assert.Equal("earlier by 1 day", result.OffsetSummary);
        Assert.Contains("01 Nov 2026 08:00", result.DialogNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_drags_in_draft_mode_accumulate_from_current_staged_start()
    {
        var stagedStartAfterFirstDrag = new DateTime(2026, 11, 1, 8, 0, 0);
        var movedTailBarOriginalStart = new DateTime(2026, 11, 5, 8, 0, 0);
        var movedTailBarDroppedStart = new DateTime(2026, 11, 7, 10, 0, 0);

        var result = MachineSchedulePlannerDragRules.ComputeShiftedStart(
            stagedStartAfterFirstDrag,
            movedTailBarOriginalStart,
            movedTailBarDroppedStart);

        Assert.Equal(new TimeSpan(2, 2, 0, 0), result.Delta);
        Assert.Equal(new DateTime(2026, 11, 3, 10, 0, 0), result.ShiftedStart);
        Assert.Equal("later by 2 days 2 hrs", result.OffsetSummary);
    }

    [Fact]
    public void Date_header_highlights_off_day_holiday_and_mixed_machine_calendars()
    {
        var dayStart = new DateTime(2026, 11, 1, 0, 0, 0);
        var dayEnd = new DateTime(2026, 11, 2, 0, 0, 0);

        var offMachine = new MachineSchedulePlannerResource
        {
            MachineCode = "MC-01",
            CalendarBands =
            [
                new MachineSchedulePlannerCalendarBand
                {
                    Start = dayStart,
                    End = dayEnd,
                    BandKind = MachineSchedulePlannerCalendarBandKind.OffDay
                }
            ]
        };

        var offState = MachineSchedulePlannerCalendarHelper.ResolveDateHeaderState(
            [offMachine], dayStart, dayEnd, SchedulerTimeScaleUnit.Day);
        Assert.Contains("pr-machine-schedule-date-header--off", offState.CssClass, StringComparison.Ordinal);
        Assert.Equal("OFF", offState.BadgeText);

        var holidayMachine = new MachineSchedulePlannerResource
        {
            MachineCode = "MC-01",
            CalendarBands =
            [
                new MachineSchedulePlannerCalendarBand
                {
                    Start = dayStart,
                    End = dayEnd,
                    BandKind = MachineSchedulePlannerCalendarBandKind.PublicHoliday,
                    HolidayName = "Deepavali"
                }
            ]
        };

        var holidayState = MachineSchedulePlannerCalendarHelper.ResolveDateHeaderState(
            [holidayMachine], dayStart, dayEnd, SchedulerTimeScaleUnit.Day);
        Assert.Contains("pr-machine-schedule-date-header--holiday", holidayState.CssClass, StringComparison.Ordinal);
        Assert.Equal("HOLIDAY", holidayState.BadgeText);
        Assert.Equal("HOL", holidayState.CompactBadgeText);
        Assert.Contains("Deepavali", holidayState.Tooltip, StringComparison.Ordinal);

        var workingMachine = new MachineSchedulePlannerResource
        {
            MachineCode = "MC-02",
            CalendarBands =
            [
                new MachineSchedulePlannerCalendarBand
                {
                    Start = dayStart.AddHours(8),
                    End = dayStart.AddHours(17),
                    BandKind = MachineSchedulePlannerCalendarBandKind.WorkingShift
                }
            ]
        };

        var mixedState = MachineSchedulePlannerCalendarHelper.ResolveDateHeaderState(
            [workingMachine, offMachine], dayStart, dayEnd, SchedulerTimeScaleUnit.Day);
        Assert.Contains("pr-machine-schedule-date-header--partial-off", mixedState.CssClass, StringComparison.Ordinal);
        Assert.Equal("SOME OFF", mixedState.BadgeText);
        Assert.Contains("MC-01", mixedState.Tooltip, StringComparison.Ordinal);

        var workingState = MachineSchedulePlannerCalendarHelper.ResolveDateHeaderState(
            [workingMachine], dayStart, dayEnd, SchedulerTimeScaleUnit.Day);
        Assert.Contains("pr-machine-schedule-date-header--working", workingState.CssClass, StringComparison.Ordinal);
        Assert.Null(workingState.BadgeText);
    }

    [Fact]
    public void Work_orders_keep_consistent_distinct_colors_and_highlight_all_selected_bars()
    {
        var wo1Day1 = new MachineSchedulePlannerAppointment
        {
            Id = "wo-1-1",
            WorkOrderId = 101,
            OperationId = 201,
            WorkOrderNo = "PRWO-2026-0001",
            Subject = "PRWO-2026-0001 · PROD-A",
            LabelId = MachineSchedulePlannerLabelIds.Scheduled
        };
        var wo1Day2Late = new MachineSchedulePlannerAppointment
        {
            Id = "wo-1-2",
            WorkOrderId = 101,
            OperationId = 201,
            WorkOrderNo = "PRWO-2026-0001",
            Subject = "PRWO-2026-0001 · PROD-A",
            IsLate = true,
            LabelId = MachineSchedulePlannerLabelIds.LateToRequiredDate
        };
        var wo2 = new MachineSchedulePlannerAppointment
        {
            Id = "wo-2-1",
            WorkOrderId = 102,
            OperationId = 202,
            WorkOrderNo = "PRWO-2026-0002",
            Subject = "PRWO-2026-0002 · PROD-B",
            LabelId = MachineSchedulePlannerLabelIds.Scheduled
        };
        var maintenance = new MachineSchedulePlannerAppointment
        {
            Id = "pm-1",
            IsMaintenance = true,
            Subject = "PM · Yearly service",
            LabelId = MachineSchedulePlannerLabelIds.PreventiveMaintenance
        };

        var idx1 = MachineSchedulePlannerColorRules.GetWorkOrderPaletteIndex(wo1Day1.WorkOrderNo, wo1Day1.WorkOrderId);
        var idx1Again = MachineSchedulePlannerColorRules.GetWorkOrderPaletteIndex(wo1Day2Late.WorkOrderNo, wo1Day2Late.WorkOrderId);
        var idx2 = MachineSchedulePlannerColorRules.GetWorkOrderPaletteIndex(wo2.WorkOrderNo, wo2.WorkOrderId);

        Assert.Equal(idx1, idx1Again);
        Assert.NotEqual(idx1, idx2);

        var css1 = MachineSchedulePlannerColorRules.GetBarCssClass(wo1Day1, wo1Day1.LabelId, "PRWO-2026-0001", 201);
        var css1Day2 = MachineSchedulePlannerColorRules.GetBarCssClass(wo1Day2Late, wo1Day2Late.LabelId, "PRWO-2026-0001", 201);
        var css2 = MachineSchedulePlannerColorRules.GetBarCssClass(wo2, wo2.LabelId, "PRWO-2026-0001", 201);
        var cssPm = MachineSchedulePlannerColorRules.GetBarCssClass(maintenance, maintenance.LabelId, "PRWO-2026-0001", 201);

        Assert.Contains($"pr-machine-schedule-bar--wo-{idx1}", css1, StringComparison.Ordinal);
        Assert.Contains($"pr-machine-schedule-bar--wo-{idx1}", css1Day2, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-bar--is-late", css1Day2, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-bar--selected", css1, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-bar--selected", css1Day2, StringComparison.Ordinal);

        Assert.Contains($"pr-machine-schedule-bar--wo-{idx2}", css2, StringComparison.Ordinal);
        Assert.DoesNotContain("pr-machine-schedule-bar--selected", css2, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-bar--dimmed", css2, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-bar--maintenance", cssPm, StringComparison.Ordinal);
    }

    [Fact]
    public void Bar_display_uses_compact_legend_tag_in_week_and_month_views_and_full_details_in_day_view()
    {
        var jobs = new[]
        {
            new MachineSchedulePlannerJob
            {
                WorkOrderId = 101,
                OperationId = 501,
                WorkOrderNo = "PRWO-2026-0042",
                ProductCode = "WIDGET-900",
                ProductDescription = "Steel Bracket",
                PlannedQuantity = 500m,
                OutputUom = "PCS",
                MachineCode = "MC-01",
                PlannedStart = new DateTime(2026, 11, 2, 8, 0, 0),
                PlannedCompletion = new DateTime(2026, 11, 4, 17, 0, 0),
                WorkOrderPlannedCompletion = new DateTime(2026, 11, 4, 17, 0, 0),
                RequiredDate = new DateTime(2026, 11, 3, 0, 0, 0),
                DemandAllocationIsActive = true,
                DeliveryRequestStatus = MachineSchedulePlannerDemandStatuses.Released
            },
            new MachineSchedulePlannerJob
            {
                WorkOrderId = 102,
                OperationId = 502,
                WorkOrderNo = "PRWO-2026-0043",
                ProductCode = "SHAFT-200",
                PlannedQuantity = 120m,
                OutputUom = "PCS",
                MachineCode = "MC-02",
                PlannedStart = new DateTime(2026, 11, 3, 8, 0, 0),
                PlannedCompletion = new DateTime(2026, 11, 5, 17, 0, 0)
            }
        };

        var legend = MachineSchedulePlannerColorRules.BuildWorkOrderLegend(jobs);
        Assert.Equal(2, legend.Count);
        Assert.Equal("#1", legend[0].TagText);
        Assert.Equal(0, legend[0].PaletteIndex);
        Assert.Equal("PRWO-2026-0042", legend[0].WorkOrderNo);
        Assert.Equal("#2", legend[1].TagText);
        Assert.Equal(1, legend[1].PaletteIndex);
        Assert.Equal("PRWO-2026-0043", legend[1].WorkOrderNo);

        var appt = new MachineSchedulePlannerAppointment
        {
            WorkOrderNo = "PRWO-2026-0042",
            Subject = "PRWO-2026-0042 · WIDGET-900",
            Start = new DateTime(2026, 11, 2, 8, 0, 0),
            End = new DateTime(2026, 11, 2, 17, 0, 0),
            PlannedOutputQty = 500m,
            OutputUom = "PCS",
            IsLocked = true,
            IsLate = true
        };

        var weekBar = MachineSchedulePlannerColorRules.FormatBarDisplay(appt, null, null, null, PlannerView.Week, legend[0].TagText);
        Assert.Equal("#1", weekBar.PrimaryText);
        Assert.Null(weekBar.SecondaryText);
        Assert.Null(weekBar.TimeRangeText);
        Assert.Null(weekBar.QtyText);
        Assert.Equal("!", weekBar.StatusBadgeText);
        Assert.True(weekBar.IsLocked);

        var monthBar = MachineSchedulePlannerColorRules.FormatBarDisplay(appt, null, null, null, PlannerView.Month, legend[0].TagText);
        Assert.Equal("#1", monthBar.PrimaryText);
        Assert.Null(monthBar.SecondaryText);
        Assert.Null(monthBar.QtyText);
        Assert.Null(monthBar.TimeRangeText);

        var dayBar = MachineSchedulePlannerColorRules.FormatBarDisplay(appt, null, null, null, PlannerView.Day, legend[0].TagText);
        Assert.Equal("#1 · PRWO-2026-0042", dayBar.PrimaryText);
        Assert.Equal("WIDGET-900", dayBar.SecondaryText);
        Assert.Equal("08:00–17:00", dayBar.TimeRangeText);
        Assert.Equal("500 PCS", dayBar.QtyText);
        Assert.Equal("LATE", dayBar.StatusBadgeText);
    }

    [Fact]
    public void Period_navigation_steps_forward_and_backward_by_user_configured_days_defaulting_to_five()
    {
        Assert.Equal(5, MachineSchedulePlannerPeriodNavigation.DefaultStepDays);
        Assert.Equal(5, MachineSchedulePlannerPeriodNavigation.NormalizeStepDays(null));
        Assert.Equal(1, MachineSchedulePlannerPeriodNavigation.NormalizeStepDays(0));
        Assert.Equal(1, MachineSchedulePlannerPeriodNavigation.NormalizeStepDays(-4));
        Assert.Equal(90, MachineSchedulePlannerPeriodNavigation.NormalizeStepDays(180));

        Assert.Equal(5, MachineSchedulePlannerPeriodNavigation.ParseStepDays(""));
        Assert.Equal(7, MachineSchedulePlannerPeriodNavigation.ParseStepDays("invalid", fallback: 7));
        Assert.Equal(10, MachineSchedulePlannerPeriodNavigation.ParseStepDays("10"));
        Assert.Equal(1, MachineSchedulePlannerPeriodNavigation.ParseStepDays("0"));

        var start = new DateTime(2026, 11, 2);
        var forwardDefault = MachineSchedulePlannerPeriodNavigation.StepPeriod(
            start,
            MachineSchedulePlannerPeriodNavigation.DefaultStepDays,
            forward: true);
        var backwardDefault = MachineSchedulePlannerPeriodNavigation.StepPeriod(
            start,
            MachineSchedulePlannerPeriodNavigation.DefaultStepDays,
            forward: false);
        var forwardCustom = MachineSchedulePlannerPeriodNavigation.StepPeriod(
            start,
            stepDays: 3,
            forward: true);

        Assert.Equal(new DateTime(2026, 11, 7), forwardDefault);
        Assert.Equal(DateTimeKind.Unspecified, forwardDefault.Kind);
        Assert.Equal(new DateTime(2026, 10, 28), backwardDefault);
        Assert.Equal(DateTimeKind.Unspecified, backwardDefault.Kind);
        Assert.Equal(new DateTime(2026, 11, 5), forwardCustom);
    }
}
