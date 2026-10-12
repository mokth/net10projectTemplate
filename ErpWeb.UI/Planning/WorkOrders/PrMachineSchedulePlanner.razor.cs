using System.Collections;
using System.Text.Json;
using DevExpress.Blazor;
using DevExtreme.AspNet.Data;
using DevExtreme.AspNet.Data.ResponseModel;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrMachineSchedulePlanner : PageBase, IDisposable
{
    [Inject] private IMachineSchedulePlannerService Planner { get; set; } = default!;
    [Inject] private IMachineScheduleRescheduleService Rescheduler { get; set; } = default!;
    [Inject] private IMachineScheduleAllocationBackfillService AllocationBackfill { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private MachineSchedulePlannerBoard? Board;
    private DxSchedulerDataStorage DataStorage = new();
    private DxSchedulerAppointmentItem? SelectedAppointment;
    private bool IsLoading;
    private bool CanOpenWorkOrders;
    private bool CanEditSchedule;
    private bool IsDetailsOpen;
    private bool IsRescheduleDialogOpen;
    private bool IsRescheduleBusy;
    private bool IsBackfillDialogOpen;
    private bool IsBackfillBusy;
    private MachineScheduleAllocationBackfillReport? BackfillReport;
    private string RequestedDateText = string.Empty;
    private string RequestedTimeText = string.Empty;
    private string RescheduleReason = string.Empty;
    private MachineScheduleReschedulePreview? ReschedulePreview;
    private int ZoomLevel;
    private string? StatusMessage;
    private string? SelectedWorkCentre;
    private string? SelectedMachine;
    private string? SelectedProcess;
    private string? SelectedWorkOrder;
    private string? SelectedProduct;
    private string? SelectedStatus;
    private string SearchText = string.Empty;
    private DateTime PeriodStart = StartOfWeek(DateTime.Today);
    private PlannerView CurrentView = PlannerView.Week;
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _searchDebounceCts;
    private int _reloadVersion;
    private bool _disposed;

    private IReadOnlyList<PlannerFilterOption> StatusOptions =>
    [
        new("RELEASED", "Released"),
        new("IN_PROGRESS", "In progress")
    ];

    private TimeSpan ViewDuration => CurrentView switch
    {
        PlannerView.Day => TimeSpan.FromDays(1),
        PlannerView.Month => TimeSpan.FromDays(DateTime.DaysInMonth(PeriodStart.Year, PeriodStart.Month)),
        _ => TimeSpan.FromDays(7)
    };

    private int TimelineCellWidth => CurrentView switch
    {
        PlannerView.Day => CellWidths[Math.Clamp(ZoomLevel + 4, 0, CellWidths.Length - 1)],
        PlannerView.Month => CellWidths[Math.Clamp(ZoomLevel + 3, 2, CellWidths.Length - 1)],
        _ => CellWidths[Math.Clamp(ZoomLevel + 3, 0, CellWidths.Length - 1)]
    };

    private int MinimumZoomLevel => CurrentView == PlannerView.Month ? -1 : -3;

    private bool IsReschedulingEnabled => Board?.Readiness.IsWriteReady == true;

    private bool IsSelectedJobEligible => SelectedJob is { } job
        && !job.IsLocked
        && !job.IsBackwardScheduled
        && (string.Equals(job.WorkOrderStatus, ProductionWorkOrderStatuses.Released, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.WorkOrderStatus, ProductionWorkOrderStatuses.InProgress, StringComparison.OrdinalIgnoreCase));

    private bool CanOpenReschedule => IsReschedulingEnabled && CanEditSchedule && IsSelectedJobEligible;

    // Keep drag disabled until the installed DevExpress 26.1.4 flow passes the planner browser UAT.
    private const bool SchedulerDragBrowserAcceptancePassed = false;
    private bool CanDragAppointments => SchedulerDragBrowserAcceptancePassed
        && IsReschedulingEnabled
        && CanEditSchedule;

    private string HeaderInstructions => CanDragAppointments
        ? "Find a Work Order or machine, then select a job. Drag it to move, or use Change Schedule."
        : IsReschedulingEnabled
            ? "Find a Work Order or machine, then select a job and use Change Schedule."
            : "Find a Work Order or machine, then select a job to inspect its schedule.";

    private string RescheduleUnavailableReason => !CanEditSchedule
        ? "Edit permission is required to change schedules."
        : !IsReschedulingEnabled
            ? "Schedule changes are disabled until runtime readiness passes."
            : SelectedJob is { IsLocked: true, LockReason: { Length: > 0 } lockReason }
                ? lockReason
                : SelectedJob?.IsBackwardScheduled == true
                    ? "Backward-scheduled operations are not eligible for rescheduling."
                    : "This operation is not eligible for rescheduling.";

    private string ScheduleModeText => IsReschedulingEnabled ? "Rescheduling enabled" : "View only";

    private static readonly int[] CellWidths = [48, 64, 80, 100, 125, 150, 180];

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
        CanEditSchedule = await AccessRights.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Edit);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_disposed)
            return;

        Interlocked.Exchange(ref _searchDebounceCts, null)?.Cancel();

        var requestVersion = Interlocked.Increment(ref _reloadVersion);
        var requestCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _reloadCts, requestCts);
        previousCts?.Cancel();
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;

        var selectedOperationId = SelectedOperationId;
        var duration = ViewDuration;
        var query = new MachineSchedulePlannerQuery
        {
            WindowStart = PeriodStart,
            WindowEnd = PeriodStart.Add(duration),
            WorkCentreCode = NormalizeFilter(SelectedWorkCentre),
            MachineCode = NormalizeFilter(SelectedMachine),
            OperationCode = NormalizeFilter(SelectedProcess),
            WorkOrderNo = NormalizeFilter(SelectedWorkOrder),
            ProductCode = NormalizeFilter(SelectedProduct),
            WorkOrderStatus = NormalizeFilter(SelectedStatus),
            SearchText = SearchText
        };

        try
        {
            var result = await Planner.GetBoardAsync(query, requestCts.Token);
            if (_disposed || requestCts.IsCancellationRequested || requestVersion != Volatile.Read(ref _reloadVersion))
                return;

            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "The machine schedule could not be loaded.";
                return;
            }

            Board = result.Data;
            DataStorage = CreateStorage(Board);
            if (selectedOperationId.HasValue)
            {
                var selectedAppointmentId = Board.Appointments.FirstOrDefault(x =>
                    string.Equals(x.Location, $"operation:{selectedOperationId.Value}", StringComparison.Ordinal))?.Id;
                SelectedAppointment = selectedAppointmentId is null
                    ? null
                    : DataStorage.GetAppointmentItemById(selectedAppointmentId);
                if (SelectedAppointment is null)
                    IsDetailsOpen = false;
            }
            else
            {
                SelectedAppointment = null;
            }
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // A newer date or filter request superseded this one.
        }
        catch (Exception ex)
        {
            if (!_disposed && requestVersion == Volatile.Read(ref _reloadVersion))
                ErrorMessage = $"The machine schedule could not be loaded. {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _reloadCts, null, requestCts), requestCts))
                IsLoading = false;
            requestCts.Dispose();
        }
    }

    private async Task PreviousPeriodAsync()
    {
        PeriodStart = CurrentView switch
        {
            PlannerView.Day => PeriodStart.AddDays(-1),
            PlannerView.Month => new DateTime(PeriodStart.Year, PeriodStart.Month, 1).AddMonths(-1),
            _ => PeriodStart.AddDays(-7)
        };
        await ReloadAsync();
    }

    private async Task TodayAsync()
    {
        PeriodStart = CurrentView switch
        {
            PlannerView.Day => DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified),
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
            PlannerView.Month => new DateTime(PeriodStart.Year, PeriodStart.Month, 1).AddMonths(1),
            _ => PeriodStart.AddDays(7)
        };
        await ReloadAsync();
    }

    private async Task ChangeViewAsync(PlannerView view)
    {
        if (CurrentView == view)
            return;

        var selectedDate = PeriodStart;
        CurrentView = view;
        PeriodStart = view switch
        {
            PlannerView.Day => DateTime.SpecifyKind(selectedDate.Date, DateTimeKind.Unspecified),
            PlannerView.Month => new DateTime(selectedDate.Year, selectedDate.Month, 1),
            _ => StartOfWeek(selectedDate)
        };
        await ReloadAsync();
    }

    private void ZoomIn() => ZoomLevel = Math.Min(3, ZoomLevel + 1);
    private void ZoomOut() => ZoomLevel = Math.Max(MinimumZoomLevel, ZoomLevel - 1);
    private void ResetZoom() => ZoomLevel = 0;
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    private ButtonRenderStyle ViewButtonStyle(PlannerView view) =>
        CurrentView == view ? ButtonRenderStyle.Primary : ButtonRenderStyle.Secondary;

    private async Task ClearFiltersAsync()
    {
        SelectedWorkCentre = null;
        SelectedMachine = null;
        SelectedProcess = null;
        SelectedWorkOrder = null;
        SelectedProduct = null;
        SelectedStatus = null;
        SearchText = string.Empty;
        await ReloadAsync();
    }

    private async Task OnSearchTextChangedAsync(string? value)
    {
        SearchText = value ?? string.Empty;
        var debounceCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _searchDebounceCts, debounceCts);
        previousCts?.Cancel();

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), debounceCts.Token);
            if (!_disposed && ReferenceEquals(Volatile.Read(ref _searchDebounceCts), debounceCts))
                await ReloadAsync();
        }
        catch (OperationCanceledException) when (debounceCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchDebounceCts, null, debounceCts), debounceCts))
                debounceCts.Dispose();
        }
    }

    private async Task OnAppointmentSelectedAsync()
    {
        IsDetailsOpen = SelectedJob is not null;
        IsRescheduleDialogOpen = false;
        ReschedulePreview = null;
        await Task.CompletedTask;
    }

    private void OpenRescheduleDialog()
    {
        if (!CanOpenReschedule || SelectedJob is not { } job)
            return;

        OpenRescheduleDialog(job, job.PlannedStart);
    }

    private void OpenRescheduleDialog(MachineSchedulePlannerJob job, DateTime requestedStart)
    {
        if (!CanOpenReschedule)
            return;

        RequestedDateText = requestedStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        RequestedTimeText = requestedStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        RescheduleReason = string.Empty;
        ReschedulePreview = null;
        ErrorMessage = null;
        IsRescheduleDialogOpen = true;
    }

    private void OnAppointmentStartDragging(SchedulerAppointmentOperationEventArgs args)
    {
        if (!CanDragAppointments)
        {
            args.Cancel = true;
            return;
        }

        var job = FindJob(args.Appointment);
        if (job is null)
        {
            args.Cancel = true;
            StatusMessage = "Maintenance and non-operation bars cannot be moved.";
        }
        else if (job.IsLocked || job.IsBackwardScheduled)
        {
            args.Cancel = true;
            StatusMessage = job.LockReason ?? "This operation is locked and cannot be rescheduled.";
        }
    }

    private Task OnAppointmentUpdating(SchedulerAppointmentOperationEventArgs args)
    {
        // Never let the scheduler persist proposed dates. Convert the drag into the same
        // server Preview flow used by Change Schedule and cancel the default collection update.
        args.Cancel = true;
        if (!CanDragAppointments)
        {
            StatusMessage = "Drag rescheduling remains disabled until browser acceptance passes.";
            return Task.CompletedTask;
        }

        var job = FindJob(args.Appointment);
        if (job is null)
        {
            StatusMessage = "Maintenance and non-operation bars cannot be moved.";
            return Task.CompletedTask;
        }

        SelectedAppointment = args.Appointment;
        IsDetailsOpen = true;
        if (!CanOpenReschedule)
        {
            StatusMessage = job.LockReason ?? "This operation is not eligible for rescheduling.";
            return Task.CompletedTask;
        }

        OpenRescheduleDialog(job, args.Appointment.Start);
        return Task.CompletedTask;
    }

    private void OnAppointmentFormShowing(SchedulerAppointmentFormEventArgs args) => args.Cancel = true;

    private MachineSchedulePlannerJob? FindJob(DxSchedulerAppointmentItem appointment)
    {
        var location = appointment.Location;
        if (string.IsNullOrWhiteSpace(location) || !location.StartsWith("operation:", StringComparison.Ordinal)
            || !long.TryParse(location.AsSpan("operation:".Length), out var operationId))
            return null;
        return Board?.Jobs.FirstOrDefault(x => x.OperationId == operationId);
    }

    private void CloseRescheduleDialog()
    {
        IsRescheduleDialogOpen = false;
        ReschedulePreview = null;
        IsRescheduleBusy = false;
    }

    private async Task PreviewAllocationBackfillAsync()
    {
        if (!CanEditSchedule)
            return;

        IsBackfillBusy = true;
        ErrorMessage = null;
        BackfillReport = null;
        try
        {
            var result = await AllocationBackfill.PreviewBackfillAsync();
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "The allocation dry-run could not be completed.";
                return;
            }

            BackfillReport = result.Data;
            IsBackfillDialogOpen = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The allocation dry-run could not be completed. {ex.Message}";
        }
        finally
        {
            IsBackfillBusy = false;
        }
    }

    private void CloseBackfillDialog()
    {
        if (IsBackfillBusy)
            return;
        IsBackfillDialogOpen = false;
        BackfillReport = null;
    }

    private async Task ApplyAllocationBackfillAsync()
    {
        if (!CanEditSchedule || BackfillReport is not { CanApply: true, MissingAllocationCount: > 0 } report)
            return;

        var operationCount = report.MissingAllocationCount;
        IsBackfillBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await AllocationBackfill.ApplyVerifiedBackfillAsync(report.PreviewToken);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "The verified allocation backfill could not be applied.";
                BackfillReport = null;
                IsBackfillDialogOpen = false;
                return;
            }

            BackfillReport = result.Data;
            await ReloadAsync();
            StatusMessage = $"Backfill verified {operationCount} active machine operation(s); schedule dates were unchanged.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The verified allocation backfill could not be applied. {ex.Message}";
            BackfillReport = null;
            IsBackfillDialogOpen = false;
        }
        finally
        {
            IsBackfillBusy = false;
        }
    }

    private void InvalidateReschedulePreview()
    {
        ReschedulePreview = null;
        ErrorMessage = null;
    }

    private void OnRequestedDateChanged(ChangeEventArgs args)
    {
        RequestedDateText = args.Value?.ToString() ?? string.Empty;
        InvalidateReschedulePreview();
    }

    private void OnRequestedTimeChanged(ChangeEventArgs args)
    {
        RequestedTimeText = args.Value?.ToString() ?? string.Empty;
        InvalidateReschedulePreview();
    }

    private async Task PreviewRescheduleAsync()
    {
        if (!CanOpenReschedule || SelectedJob is not { } job)
        {
            ErrorMessage = "Schedule changes are unavailable until edit permission and runtime readiness are enabled.";
            return;
        }

        if (!TryGetRequestedStart(out var requestedStart))
        {
            ErrorMessage = "Enter a valid requested date and time.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RescheduleReason))
        {
            ErrorMessage = "Enter a reason for the schedule change.";
            return;
        }

        IsRescheduleBusy = true;
        ErrorMessage = null;
        ReschedulePreview = null;
        try
        {
            var result = await Rescheduler.PreviewRescheduleAsync(CreateRescheduleRequest(job, requestedStart));
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "The schedule preview could not be created.";
                return;
            }

            ReschedulePreview = result.Data;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The schedule preview could not be created. {ex.Message}";
        }
        finally
        {
            IsRescheduleBusy = false;
        }
    }

    private async Task CommitRescheduleAsync()
    {
        if (!CanOpenReschedule || SelectedJob is not { } job
            || ReschedulePreview is not { CanCommit: true, PreviewToken: { Length: > 0 } token }
            || !TryGetRequestedStart(out var requestedStart))
            return;

        IsRescheduleBusy = true;
        ErrorMessage = null;
        try
        {
            var request = CreateRescheduleRequest(job, requestedStart);
            request = CopyWithPreviewToken(request, token);
            var result = await Rescheduler.CommitRescheduleAsync(request);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "The schedule change could not be committed.";
                ReschedulePreview = null;
                return;
            }

            var successMessage = result.Data.Message;
            CloseRescheduleDialog();
            await ReloadAsync();
            StatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The schedule change could not be committed. {ex.Message}";
        }
        finally
        {
            IsRescheduleBusy = false;
        }
    }

    private bool TryGetRequestedStart(out DateTime requestedStart)
    {
        requestedStart = default;
        if (!DateOnly.TryParseExact(RequestedDateText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)
            || !TimeOnly.TryParseExact(RequestedTimeText, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var time))
            return false;

        requestedStart = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return true;
    }

    private MachineScheduleRescheduleRequest CreateRescheduleRequest(
        MachineSchedulePlannerJob job,
        DateTime requestedStart) => new()
    {
        WorkOrderId = job.WorkOrderId,
        OperationId = job.OperationId,
        WorkOrderMachineId = job.WorkOrderMachineId,
        MachineCode = job.MachineCode,
        RequestedStart = requestedStart,
        ExpectedWorkOrderScheduleRevision = job.ScheduleRevision,
        ExpectedWorkOrderRowVersion = job.WorkOrderRowVersion,
        ExpectedMachineScheduleRevision = job.MachineScheduleRevision,
        ExpectedMachineRowVersion = job.MachineRowVersion,
        Reason = RescheduleReason.Trim()
    };

    private MachineScheduleRescheduleRequest CopyWithPreviewToken(
        MachineScheduleRescheduleRequest request,
        string previewToken) => new()
    {
        WorkOrderId = request.WorkOrderId,
        OperationId = request.OperationId,
        WorkOrderMachineId = request.WorkOrderMachineId,
        MachineCode = request.MachineCode,
        RequestedStart = request.RequestedStart,
        ExpectedWorkOrderScheduleRevision = request.ExpectedWorkOrderScheduleRevision,
        ExpectedWorkOrderRowVersion = request.ExpectedWorkOrderRowVersion,
        ExpectedMachineScheduleRevision = request.ExpectedMachineScheduleRevision,
        ExpectedMachineRowVersion = request.ExpectedMachineRowVersion,
        Reason = RescheduleReason.Trim(),
        PreviewToken = previewToken
    };

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

    private Task<LoadResult> LoadWorkCentreOptionsAsync(DataSourceLoadOptionsBase options, CancellationToken ct) =>
        LoadLookupOptionsAsync(MachineSchedulePlannerLookupKind.WorkCentre, options, ct);

    private Task<LoadResult> LoadMachineOptionsAsync(DataSourceLoadOptionsBase options, CancellationToken ct) =>
        LoadLookupOptionsAsync(MachineSchedulePlannerLookupKind.Machine, options, ct);

    private Task<LoadResult> LoadProcessOptionsAsync(DataSourceLoadOptionsBase options, CancellationToken ct) =>
        LoadLookupOptionsAsync(MachineSchedulePlannerLookupKind.Process, options, ct);

    private Task<LoadResult> LoadWorkOrderOptionsAsync(DataSourceLoadOptionsBase options, CancellationToken ct) =>
        LoadLookupOptionsAsync(MachineSchedulePlannerLookupKind.WorkOrder, options, ct);

    private Task<LoadResult> LoadProductOptionsAsync(DataSourceLoadOptionsBase options, CancellationToken ct) =>
        LoadLookupOptionsAsync(MachineSchedulePlannerLookupKind.Product, options, ct);

    private async Task<LoadResult> LoadLookupOptionsAsync(
        MachineSchedulePlannerLookupKind kind,
        DataSourceLoadOptionsBase options,
        CancellationToken cancellationToken)
    {
        var result = await Planner.GetFilterLookupAsync(new MachineSchedulePlannerLookupQuery
        {
            Kind = kind,
            SearchText = ExtractLookupSearch(options.Filter),
            Skip = options.Skip,
            Take = options.Take
        }, cancellationToken);

        if (!result.Succeeded || result.Data is null)
        {
            if (!cancellationToken.IsCancellationRequested)
                ErrorMessage = result.Message ?? "A planner filter lookup could not be loaded.";
            return new LoadResult { data = Array.Empty<MachineSchedulePlannerLookupOption>(), totalCount = 0 };
        }

        return new LoadResult { data = result.Data.Items, totalCount = result.Data.TotalCount };
    }

    private static string? ExtractLookupSearch(object? filter)
    {
        if (filter is IList list)
        {
            if (list.Count >= 3 && list[0] is string && list[1] is string operation
                && IsSearchOperator(operation) && list[2] is string value)
                return NormalizeFilter(value);

            for (var i = list.Count - 1; i >= 0; i--)
            {
                var nested = ExtractLookupSearch(list[i]);
                if (nested is not null)
                    return nested;
            }
        }
        else if (filter is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToArray();
            if (items.Length >= 3 && items[0].ValueKind == JsonValueKind.String
                && items[1].ValueKind == JsonValueKind.String && IsSearchOperator(items[1].GetString()!)
                && items[2].ValueKind == JsonValueKind.String)
                return NormalizeFilter(items[2].GetString());

            for (var i = items.Length - 1; i >= 0; i--)
            {
                var nested = ExtractLookupSearch(items[i]);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

    private static bool IsSearchOperator(string value) =>
        value.Equals("contains", StringComparison.OrdinalIgnoreCase)
        || value.Equals("startswith", StringComparison.OrdinalIgnoreCase)
        || value.Equals("endswith", StringComparison.OrdinalIgnoreCase)
        || value.Equals("=", StringComparison.OrdinalIgnoreCase);

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

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _reloadCts, null)?.Cancel();
        Interlocked.Exchange(ref _searchDebounceCts, null)?.Cancel();
    }

    private sealed record PlannerFilterOption(string Key, string Name);
    private sealed record PlannerAppointmentLabel(int Id, string Caption, string BackgroundCssClass, string TextCssClass);
    private enum PlannerView { Day, Week, Month }
}

