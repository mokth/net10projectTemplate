namespace ErpWeb.Tests.UI;

/// <summary>
/// Source guards for the Machine Schedule Planner Work Order selection contract. The planner
/// page has no bUnit harness, so these guards keep the two UI-level rules that the approved plan
/// makes non-negotiable: one board reload per selection, and machine-anchored period navigation.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Shared)]
public sealed class MachineSchedulePlannerSelectionContractTests
{
    private const string CodeBehindPath = "ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.cs";
    private const string RazorPath = "ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor";
    private const string CssPath = "ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.css";

    [Fact]
    public void Work_order_selection_reloads_the_board_exactly_once()
    {
        var body = ReadMethodBody("OnWorkOrderSelectionChangedAsync");

        Assert.Equal(1, CountOccurrences(body, "await ReloadAsync();"));
        Assert.Contains("MachineSchedulePlannerSelectionPlanner.ResolveSelection(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ReloadAsync();", body.Replace("await ReloadAsync();", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Go_to_work_order_schedule_reloads_the_board_exactly_once()
    {
        var body = ReadMethodBody("GoToWorkOrderScheduleAsync");

        Assert.Equal(1, CountOccurrences(body, "await ReloadAsync();"));
        Assert.Contains("MachineSchedulePlannerSelectionPlanner.ResolveGoToSchedule(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_rules_are_centralized_and_period_navigation_does_not_move_them()
    {
        var source = ReadSource(CodeBehindPath);

        // Auto-jump decisions live in one helper; the resolver is never called from a period
        // navigation method, so manual Today/Previous/Next can never force a jump back.
        Assert.Contains("internal static class MachineSchedulePlannerSelectionPlanner", source, StringComparison.Ordinal);
        foreach (var navigation in new[] { "PreviousPeriodAsync", "TodayAsync", "NextPeriodAsync", "ChangeViewAsync" })
        {
            var body = ReadMethodBody(navigation);
            Assert.DoesNotContain("GetWorkOrderMachineScheduleWindowAsync", body, StringComparison.Ordinal);
            Assert.DoesNotContain("MachineSchedulePlannerSelectionPlanner", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Work_order_filter_binds_the_dedicated_selection_handler()
    {
        var razor = ReadSource(RazorPath);

        Assert.Contains("@bind-Value=\"SelectedWorkOrder\" @bind-Value:after=\"OnWorkOrderSelectionChangedAsync\"",
            razor, StringComparison.Ordinal);
        Assert.DoesNotContain("@bind-Value=\"SelectedWorkOrder\" @bind-Value:after=\"ReloadAsync\"",
            razor, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_state_offers_go_to_work_order_schedule_only_for_a_selected_order()
    {
        var razor = ReadSource(RazorPath);

        Assert.Contains("Selected Work Order is not scheduled in this period.", razor, StringComparison.Ordinal);
        Assert.Contains("Text=\"Go to WO schedule\"", razor, StringComparison.Ordinal);
        Assert.Contains("Click=\"GoToWorkOrderScheduleAsync\"", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void Date_headers_and_drag_offset_ux_follow_erp_ui_core_and_accessibility_rules()
    {
        var razor = ReadSource(RazorPath);
        var css = ReadSource(CssPath);

        Assert.DoesNotContain("<style", razor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetDateHeaderState(context.Interval.Start, context.Interval.End, context.Scale.Unit)",
            razor, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-date-header--two-row", razor, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-date-header__dow", razor, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-date-header__day", razor, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-date-header__tag", razor, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-tip", razor, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-dialog__note", razor, StringComparison.Ordinal);
        Assert.Contains("Off day", razor, StringComparison.Ordinal);
        Assert.Contains("Public holiday", razor, StringComparison.Ordinal);

        Assert.Contains("MachineSchedulePlannerDragRules.ComputeShiftedStart(",
            ReadMethodBody("OnAppointmentUpdating"), StringComparison.Ordinal);
        Assert.Contains(".pr-machine-schedule-date-header--off", css, StringComparison.Ordinal);
        Assert.Contains(".pr-machine-schedule-date-header--holiday", css, StringComparison.Ordinal);
        Assert.Contains(".pr-machine-schedule-date-header--partial-off", css, StringComparison.Ordinal);
        Assert.Contains(".pr-machine-schedule-bar--wo-0", css, StringComparison.Ordinal);
        Assert.Contains(".pr-machine-schedule-bar--wo-11", css, StringComparison.Ordinal);
        Assert.Contains(".pr-machine-schedule-wo-legend", css, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-wo-legend", razor, StringComparison.Ordinal);
        Assert.Contains("Work Order Color Legend", razor, StringComparison.Ordinal);
        Assert.Contains("pr-machine-schedule-bar__primary", razor, StringComparison.Ordinal);
        Assert.Contains("Each color = distinct Work Order", razor, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadMethodBody(string methodName)
    {
        var source = ReadSource(CodeBehindPath);
        var signature = source.IndexOf(methodName + "(", StringComparison.Ordinal);
        Assert.True(signature >= 0, $"Method '{methodName}' was not found.");
        var open = source.IndexOf('{', signature);
        Assert.True(open >= 0, $"Method '{methodName}' has no body.");

        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[open..(index + 1)];
                    break;
            }
        }

        throw new InvalidOperationException($"Method '{methodName}' body was not terminated.");
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), relativePath));

    private static string ResolveRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }.Distinct())
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ErpWeb.slnx")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root containing ErpWeb.slnx.");
    }
}
