using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrMachineSchedulePlanner : PageBase
{
    [Inject] private IMachineSchedulePlannerService Planner { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private MachineSchedulePlannerBoard? Board;
    private DxSchedulerDataStorage DataStorage = new();
    private DxSchedulerAppointmentItem? SelectedAppointment;
    private bool IsLoading;
    private bool CanOpenWorkOrders;
    private string? StatusMessage;
    private string? SelectedWorkCentre;
    private string? SelectedMachine;
    private string? SelectedStatus;
    private string SearchText = string.Empty;
    private readonly List<string> _workCentreCodes = [];
    private readonly List<PlannerFilterOption> _machineOptionCache = [];
    private DateTime PeriodStart = StartOfWeek(DateTime.Today);
    private PlannerView CurrentView = PlannerView.Week;

    private IReadOnlyList<PlannerFilterOption> WorkCentreOptions =>
        [new(string.Empty, "All work centres"), .. _workCentreCodes
            .Select(x => new PlannerFilterOption(x, x))];

    private IReadOnlyList<PlannerFilterOption> MachineOptions =>
        [new(string.Empty, "All machines"), .. _machineOptionCache];

    private IReadOnlyList<PlannerFilterOption> StatusOptions =>
    [
        new(string.Empty, "All statuses"),
        new("RELEASED", "Released"),
        new("IN_PROGRESS", "In progress")
    ];

    private TimeSpan ViewDuration => CurrentView switch
    {
        PlannerView.Day => TimeSpan.FromDays(1),
        PlannerView.Month => TimeSpan.FromDays(DateTime.DaysInMonth(PeriodStart.Year, PeriodStart.Month)),
        _ => TimeSpan.FromDays(7)
    };

    private int TimelineCellWidth => CurrentView == PlannerView.Month ? 54 : CurrentView == PlannerView.Day ? 130 : 82;
    private string PeriodLabel => CurrentView switch
    {
        PlannerView.Day => PeriodStart.ToString("dddd, dd MMM yyyy"),
        PlannerView.Month => PeriodStart.ToString("MMMM yyyy"),
        _ => $"{PeriodStart:dd MMM} – {PeriodStart.AddDays(6):dd MMM yyyy}"
    };

    private MachineSchedulePlannerJob? SelectedJob
    {
        get
        {
            var location = SelectedAppointment?.Location;
            if (string.IsNullOrWhiteSpace(location) || !location.StartsWith("operation:", StringComparison.Ordinal))
                return null;
            return long.TryParse(location.AsSpan("operation:".Length), out var operationId)
                ? Board?.Jobs.FirstOrDefault(x => x.OperationId == operationId)
                : null;
        }
    }

    protected override async Task OnPageInitializedAsync()
    {
        CanOpenWorkOrders = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Access);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (IsLoading)
            return;
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var duration = ViewDuration;
            var result = await Planner.GetBoardAsync(new MachineSchedulePlannerQuery
            {
                WindowStart = PeriodStart,
                WindowEnd = PeriodStart.Add(duration),
                WorkCentreCode = NormalizeFilter(SelectedWorkCentre),
                MachineCode = NormalizeFilter(SelectedMachine),
                WorkOrderStatus = NormalizeFilter(SelectedStatus),
                SearchText = SearchText
            });
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "The machine schedule could not be loaded.";
                return;
            }

            var selectedOperationId = SelectedOperationId;
            Board = result.Data;
            foreach (var workCentre in Board.WorkCentres)
            {
                if (!_workCentreCodes.Contains(workCentre, StringComparer.OrdinalIgnoreCase))
                    _workCentreCodes.Add(workCentre);
            }
            foreach (var machine in Board.Machines)
            {
                if (_machineOptionCache.All(x => !string.Equals(x.Key, machine.MachineCode, StringComparison.OrdinalIgnoreCase)))
                    _machineOptionCache.Add(new PlannerFilterOption(machine.MachineCode, machine.DisplayName));
            }
            _machineOptionCache.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            DataStorage = CreateStorage(Board);
            if (selectedOperationId.HasValue && !Board.Jobs.Any(x => x.OperationId == selectedOperationId.Value))
                SelectedAppointment = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The machine schedule could not be loaded. {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PreviousPeriodAsync()
    {
        PeriodStart = CurrentView switch
        {
            PlannerView.Day => PeriodStart.AddDays(-1),
            PlannerView.Month => PeriodStart.AddMonths(-1),
            _ => PeriodStart.AddDays(-7)
        };
        await ReloadAsync();
    }

    private async Task TodayAsync()
    {
        PeriodStart = CurrentView switch
        {
            PlannerView.Day => DateTime.Today,
            PlannerView.Month => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
            _ => StartOfWeek(DateTime.Today)
        };
        await ReloadAsync();
    }

    private async Task NextPeriodAsync()
    {
        PeriodStart = CurrentView switch
        {
            PlannerView.Day => PeriodStart.AddDays(1),
            PlannerView.Month => PeriodStart.AddMonths(1),
            _ => PeriodStart.AddDays(7)
        };
        await ReloadAsync();
    }

    private async Task ChangeViewAsync(PlannerView view)
    {
        CurrentView = view;
        PeriodStart = view switch
        {
            PlannerView.Day => DateTime.Today,
            PlannerView.Month => new DateTime(PeriodStart.Year, PeriodStart.Month, 1),
            _ => StartOfWeek(PeriodStart)
        };
        await ReloadAsync();
    }

    private ButtonRenderStyle ViewButtonStyle(PlannerView view) =>
        CurrentView == view ? ButtonRenderStyle.Primary : ButtonRenderStyle.Secondary;

    private void OpenWorkOrder(MachineSchedulePlannerJob job)
    {
        if (!CanOpenWorkOrders)
            return;
        Navigation.NavigateTo($"/planning/work-orders/view/{Uri.EscapeDataString(job.WorkOrderNo)}");
    }

    private void DecorateCalendarCell(SchedulerHtmlCellDecorationEventArgs args)
    {
        if (Board is null || args.CellType != SchedulerCellType.TimeCell)
            return;
        var resource = args.Resources.FirstOrDefault();
        if (resource?.Id is null || args.Intervals.Count == 0)
            return;
        var interval = args.Intervals.FirstOrDefault();
        var machine = Board.Machines.FirstOrDefault(x =>
            string.Equals(x.MachineCode, resource.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        if (machine is null)
            return;
        if (machine.MaintenanceIntervals.Any(x => x.Start < interval.End && x.End > interval.Start))
            args.CssClass = "pr-machine-schedule-cell--maintenance";
        else if (!machine.UsableIntervals.Any(x => x.Start < interval.End && x.End > interval.Start))
            args.CssClass = "pr-machine-schedule-cell--off";
    }

    private long? SelectedOperationId
    {
        get
        {
            var location = SelectedAppointment?.Location;
            return location is not null && location.StartsWith("operation:", StringComparison.Ordinal)
                && long.TryParse(location.AsSpan("operation:".Length), out var operationId)
                    ? operationId
                    : null;
        }
    }

    private static DxSchedulerDataStorage CreateStorage(MachineSchedulePlannerBoard board) => new()
    {
        AppointmentsSource = board.Appointments,
        AppointmentMappings = new DxSchedulerAppointmentMappings
        {
            Id = nameof(MachineSchedulePlannerAppointment.Id),
            Start = nameof(MachineSchedulePlannerAppointment.Start),
            End = nameof(MachineSchedulePlannerAppointment.End),
            Subject = nameof(MachineSchedulePlannerAppointment.Subject),
            Description = nameof(MachineSchedulePlannerAppointment.Description),
            Location = nameof(MachineSchedulePlannerAppointment.Location),
            ResourceId = nameof(MachineSchedulePlannerAppointment.ResourceId),
            LabelId = nameof(MachineSchedulePlannerAppointment.LabelId)
        },
        ResourcesSource = board.Machines,
        ResourceMappings = new DxSchedulerResourceMappings
        {
            Id = nameof(MachineSchedulePlannerResource.MachineCode),
            Caption = nameof(MachineSchedulePlannerResource.DisplayName)
        },
        AppointmentLabelsSource = PlannerLabels,
        AppointmentLabelMappings = new DxSchedulerAppointmentLabelMappings
        {
            Id = nameof(PlannerAppointmentLabel.Id),
            Caption = nameof(PlannerAppointmentLabel.Caption),
            BackgroundCssClass = nameof(PlannerAppointmentLabel.BackgroundCssClass),
            TextCssClass = nameof(PlannerAppointmentLabel.TextCssClass)
        }
    };

    private static readonly IReadOnlyList<PlannerAppointmentLabel> PlannerLabels =
    [
        new(1, "Scheduled", "pr-machine-schedule-label--scheduled", "pr-machine-schedule-label__text"),
        new(2, "Execution locked", "pr-machine-schedule-label--locked", "pr-machine-schedule-label__text"),
        new(3, "Preventive maintenance", "pr-machine-schedule-label--maintenance", "pr-machine-schedule-label__text")
    ];

    private static DateTime StartOfWeek(DateTime date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return DateTime.SpecifyKind(date.Date.AddDays(-offset), DateTimeKind.Unspecified);
    }

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DescriptionSuffix(string? description) =>
        string.IsNullOrWhiteSpace(description) ? string.Empty : $"· {description}";

    private sealed record PlannerFilterOption(string Key, string Name);
    private sealed record PlannerAppointmentLabel(int Id, string Caption, string BackgroundCssClass, string TextCssClass);
    private enum PlannerView { Day, Week, Month }
}
