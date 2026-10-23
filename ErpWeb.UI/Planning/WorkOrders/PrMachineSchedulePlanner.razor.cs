using System.Collections;
using System.Globalization;
using System.Text.Json;
using DevExpress.Blazor;
using DevExtreme.AspNet.Data;
using DevExtreme.AspNet.Data.ResponseModel;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrMachineSchedulePlanner : PageBase, IDisposable
{
    [Inject] private IMachineSchedulePlannerService Planner { get; set; } = default!;
    [Inject] private IMachineScheduleRescheduleService Rescheduler { get; set; } = default!;
    [Inject] private IMachineScheduleAdvancedPlanningService AdvancedPlanner { get; set; } = default!;
    [Inject] private IMachineScheduleAllocationBackfillService AllocationBackfill { get; set; } = default!;
    [Inject] private IMachineSchedulePlannerReadinessService Readiness { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private MachineSchedulePlannerBoard? Board;
    private DxSchedulerDataStorage DataStorage = new();
    private DxSchedulerAppointmentItem? SelectedAppointment;
    private bool IsLoading;
    private bool CanOpenWorkOrders;
    private bool CanEditSchedule;
    private bool CanRefreshReadinessEvidence;
    private bool CanManageAllocationBackfill;
    private bool IsDetailsOpen;
    private bool IsRescheduleDialogOpen;
    private bool IsRescheduleBusy;
    private bool IsBackfillDialogOpen;
    private bool IsBackfillBusy;
    private bool IsMoreFiltersOpen;
    private MachineScheduleAllocationBackfillReport? BackfillReport;
    private MachineSchedulePlannerJob? RescheduleDialogJob;
    private bool IsAdvancedDialogOpen;
    private bool IsAdvancedBusy;
    private MachineScheduleAdvancedAction AdvancedAction;
    private MachineSchedulePlannerJob? AdvancedDialogJob;
    private MachineScheduleAdvancedPreview? AdvancedPreview;
    private string AdvancedReason = string.Empty;
    private string AdvancedPartialQuantityText = string.Empty;
    private string AdvancedDateText = string.Empty;
    private string AdvancedTimeText = string.Empty;
    private string AdvancedHoldType = string.Empty;
    private string AdvancedResumeDateText = string.Empty;
    private string AdvancedResumeTimeText = string.Empty;
    private string AdvancedDowntimeStartDateText = string.Empty;
    private string AdvancedDowntimeStartTimeText = string.Empty;
    private string AdvancedDowntimeEndDateText = string.Empty;
    private string AdvancedDowntimeEndTimeText = string.Empty;
    private string AdvancedMaintenanceType = string.Empty;
    private bool AdvancedIncludeRestartSetup = true;
    private string? AdvancedDemandAdvisory => AdvancedPreview?.AffectedWorkOrders
        .Select(x => x.DrAdvisory)
        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
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
    private string? SelectedPriority;
    private bool LateOnly;
    private bool ShowIdleMachines;
    private string? DraftProcess;
    private string? DraftProduct;
    private string? DraftStatus;
    private string? DraftPriority;
    private bool DraftLateOnly;
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

    private IReadOnlyList<PlannerFilterOption> PriorityOptions =>
    [
        new(SaDeliveryRequestPriorities.Normal, "Normal"),
        new(SaDeliveryRequestPriorities.High, "High"),
        new(SaDeliveryRequestPriorities.Urgent, "Urgent")
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

    private bool CanOpenReschedule => CanReschedule(SelectedJob);

    private bool CanEditRescheduleDialog => CanReschedule(RescheduleDialogJob);

    private bool CanDragAppointments => IsReschedulingEnabled && CanEditSchedule;

    private bool AdvancedActionsEnabled => IsReschedulingEnabled
        && Board?.Readiness.SegmentWriteSettingEnabled == true;

    private bool CanUseAdvancedActions => AdvancedActionsEnabled
        && CanEditSchedule
        && SelectedJob is { IsBackwardScheduled: false } job
        && (string.Equals(job.WorkOrderStatus, ProductionWorkOrderStatuses.Released, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.WorkOrderStatus, ProductionWorkOrderStatuses.InProgress, StringComparison.OrdinalIgnoreCase));

    private bool CanInsertSplit => CanUseAdvancedActions && SelectedJob is { IsLocked: false, NeedsRemainingReplan: false, RemainingQuantity: > 0m };
    private bool CanHoldJob => CanUseAdvancedActions && SelectedJob is { HasActiveHold: false, NeedsRemainingReplan: false };
    private bool CanResumeJob => AdvancedActionsEnabled && CanEditSchedule && SelectedJob is { HasActiveHold: true, NeedsRemainingReplan: false };
    private bool CanReplanRemaining => CanUseAdvancedActions
        && SelectedJob is { } job
        && (job.RemainingQuantity > 0m || job.FuturePlannedQty > 0m)
        && (!job.IsLocked || job.NeedsRemainingReplan || job.RemainingQuantity == 0m);
    private bool CanReplanMaintenance => CanUseAdvancedActions && SelectedJob is { HasActiveHold: false, NeedsRemainingReplan: false };

    private string AdvancedTitle => AdvancedAction switch
    {
        MachineScheduleAdvancedAction.InsertSplit => "Insert / Split Job",
        MachineScheduleAdvancedAction.Hold => "Hold scheduled work",
        MachineScheduleAdvancedAction.ReleaseHold => "Resume held work",
        MachineScheduleAdvancedAction.ReplanRemaining => "Replan remaining quantity",
        MachineScheduleAdvancedAction.MaintenanceReplan => "Replan after maintenance",
        _ => "Advanced schedule change"
    };

    private string AdvancedEyebrow => AdvancedAction switch
    {
        MachineScheduleAdvancedAction.InsertSplit => "Urgent insert · split preview",
        MachineScheduleAdvancedAction.Hold => "Pause and hold",
        MachineScheduleAdvancedAction.ReleaseHold => "Resume held segment",
        MachineScheduleAdvancedAction.ReplanRemaining => "Partial execution · replan remaining",
        MachineScheduleAdvancedAction.MaintenanceReplan => "Breakdown / repair / service",
        _ => "Advanced scheduling"
    };

    private bool AdvancedNeedsStart => AdvancedAction is MachineScheduleAdvancedAction.InsertSplit;
    private bool AdvancedNeedsQuantity => AdvancedAction is MachineScheduleAdvancedAction.InsertSplit;
    private bool AdvancedNeedsHoldType => AdvancedAction is MachineScheduleAdvancedAction.Hold;
    private bool AdvancedNeedsResumeNotBefore => AdvancedAction is MachineScheduleAdvancedAction.Hold;
    private bool AdvancedNeedsDowntime => AdvancedAction is MachineScheduleAdvancedAction.MaintenanceReplan;
    private bool AdvancedNeedsReason => AdvancedAction is not MachineScheduleAdvancedAction.ReleaseHold;

    private string HeaderInstructions => CanDragAppointments
        ? "Find a Work Order or machine, then select a job. Drag it to move, or use Change Schedule."
        : CanEditSchedule && !IsReschedulingEnabled
            ? "Find a Work Order or machine, then select a job. Schedule changes require the company setting and planner readiness."
            : "Find a Work Order or machine, then select a job to inspect its schedule.";

    private string ViewOnlyReason => !CanEditSchedule
        ? "Your account does not have permission to change this schedule."
        : Board is null || IsLoading
            ? "Planner readiness is still loading."
            : !Board.Readiness.WriteSettingEnabled
                ? "Schedule changes are disabled by the company setting."
                : "Schedule changes are disabled until planner readiness passes.";

    private string RescheduleUnavailableReason => !CanEditSchedule
        ? "Edit permission is required to change schedules."
        : !IsReschedulingEnabled
            ? "Schedule changes are disabled until runtime readiness passes."
            : SelectedJob is { IsLocked: true, LockReason: { Length: > 0 } lockReason }
                ? lockReason
                : SelectedJob?.IsBackwardScheduled == true
                    ? "Backward-scheduled operations are not eligible for rescheduling."
                    : "This operation is not eligible for rescheduling.";

    private string ScheduleModeText => CanDragAppointments ? "Rescheduling enabled" : "View only";

    private string MoreFiltersText
    {
        get
        {
            var active = CountActiveMoreFilters();
            return active == 0 ? "MORE FILTERS" : $"MORE FILTERS ({active})";
        }
    }

    private string NoJobsInPeriodMessage => string.IsNullOrWhiteSpace(SelectedMachine)
        ? "No current-branch jobs are scheduled in the selected period."
        : "No current-branch jobs are scheduled on this machine in the selected period.";

    private bool IsSelectedWorkOrderVisibleInPeriod => string.IsNullOrWhiteSpace(SelectedWorkOrder)
        || Board?.Jobs.Any(job => string.Equals(job.WorkOrderNo, SelectedWorkOrder, StringComparison.OrdinalIgnoreCase)) == true;

    private MachineSchedulePlannerAppointment? SelectedExternalCapacityAppointment
    {
        get
        {
            var location = SelectedAppointment?.Location;
            return MachineSchedulePlannerAppointmentRules.IsExternalCapacity(location)
                ? Board?.Appointments.FirstOrDefault(x =>
                    string.Equals(x.Location, location, StringComparison.Ordinal))
                : null;
        }
    }

    private string DetailsHeading => SelectedJob is { } job
        ? $"{job.WorkOrderNo} · {job.OperationCode}"
        : SelectedExternalCapacityAppointment is not null
            ? "Reserved · other branch"
            : "Selected operation details";

    private string DetailsSubheading => SelectedJob is { } job
        ? $"{job.MachineCode} · {job.PlannedStart:dd MMM yyyy HH:mm} → {job.PlannedCompletion:dd MMM yyyy HH:mm}"
        : SelectedExternalCapacityAppointment is { } external
            ? $"{external.ResourceId} · {external.Start:dd MMM yyyy HH:mm} → {external.End:dd MMM yyyy HH:mm}"
            : "Select a schedule bar to inspect the Work Order and operation.";

    private string? RescheduleDemandAdvisory
    {
        get
        {
            if (RescheduleDialogJob is not { HasActiveDemandContext: true } job
                || job.RequiredDate is not { } required
                || ReschedulePreview?.WorkOrderCompletionAfter is not { } after)
                return null;
            var days = (after.Date - required.Date).Days;
            return days > 0
                ? $"This proposed schedule completes {days} calendar day(s) after the current DR required date."
                : "This proposed schedule brings the WO on/before the current DR required date.";
        }
    }

    private int CountActiveMoreFilters()
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(SelectedProcess)) count++;
        if (!string.IsNullOrWhiteSpace(SelectedProduct)) count++;
        if (!string.IsNullOrWhiteSpace(SelectedStatus)) count++;
        if (!string.IsNullOrWhiteSpace(SelectedPriority)) count++;
        if (LateOnly) count++;
        return count;
    }

    private static string DemandSourceText(MachineSchedulePlannerJob job) => job.HasActiveDemandContext
        ? $"Delivery Request · {job.DeliveryRequestNo}"
        : job.DeliveryRequestNo is null
            ? "Manual / no Delivery Request"
            : $"Inactive or closed Delivery Request · {job.DeliveryRequestNo}";

    private static string LateText(MachineSchedulePlannerJob job) => job.IsLateToRequiredDate
        ? $"Late vs DR date: {job.LateDays} calendar day(s)"
        : job.HasActiveDemandContext && job.RequiredDate.HasValue
            ? "On/before current DR required date"
            : "—";

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
            return MachineSchedulePlannerAppointmentRules.TryGetOperationId(location, out var operationId)
                ? Board?.Jobs.FirstOrDefault(x => x.OperationId == operationId)
                : null;
        }
    }

    protected override async Task OnPageInitializedAsync()
    {
        CanOpenWorkOrders = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Access);
        var plannerAccess = await AccessRights.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access);
        CanEditSchedule = plannerAccess
            && await AccessRights.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Edit);
        var adminSettingsEdit = await AccessRights.CanAsync(MenuCodes.AdminSettings, PermissionCodes.Edit);
        CanRefreshReadinessEvidence = plannerAccess && adminSettingsEdit;
        CanManageAllocationBackfill = CanEditSchedule && adminSettingsEdit;
        await ReloadAsync();
    }

    private async Task RefreshReadinessEvidenceAsync()
    {
        if (!CanRefreshReadinessEvidence || IsLoading)
            return;

        ErrorMessage = null;
        var result = await Readiness.RefreshEvidenceAsync();
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Readiness evidence could not be refreshed.";
            return;
        }

        await ReloadAsync();
        StatusMessage = result.Data.Message;
        if (!result.Data.EvidenceRecorded && result.Data.BlockingReasons.Count > 0)
            StatusMessage += " " + string.Join(" ", result.Data.BlockingReasons);
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
            SearchText = SearchText,
            Priority = NormalizeFilter(SelectedPriority),
            LateOnly = LateOnly,
            ShowIdleMachines = ShowIdleMachines
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
        SelectedPriority = null;
        LateOnly = false;
        ShowIdleMachines = false;
        SearchText = string.Empty;
        DraftProcess = null;
        DraftProduct = null;
        DraftStatus = null;
        DraftPriority = null;
        DraftLateOnly = false;
        IsMoreFiltersOpen = false;
        await ReloadAsync();
    }

    private async Task ClearMoreFiltersAsync() => await ClearFiltersAsync();

    private void OpenMoreFilters()
    {
        DraftProcess = SelectedProcess;
        DraftProduct = SelectedProduct;
        DraftStatus = SelectedStatus;
        DraftPriority = SelectedPriority;
        DraftLateOnly = LateOnly;
        IsMoreFiltersOpen = true;
    }

    private void CancelMoreFilters() => IsMoreFiltersOpen = false;

    private async Task ApplyMoreFiltersAsync()
    {
        SelectedProcess = NormalizeFilter(DraftProcess);
        SelectedProduct = NormalizeFilter(DraftProduct);
        SelectedStatus = NormalizeFilter(DraftStatus);
        SelectedPriority = NormalizeFilter(DraftPriority);
        LateOnly = DraftLateOnly;
        IsMoreFiltersOpen = false;
        await ReloadAsync();
    }

    private async Task OnShowIdleMachinesChanged(bool value)
    {
        ShowIdleMachines = value;
        await ReloadAsync();
    }

    /// <summary>
    /// Work Order selection resolves the WO's actual machine schedule window and issues exactly
    /// one board reload. Auto-jump only happens here; later manual period navigation is respected.
    /// </summary>
    private async Task OnWorkOrderSelectionChangedAsync()
    {
        if (_disposed)
            return;

        MachineSchedulePlannerWorkOrderWindow? window = null;
        var windowResolved = false;
        if (!string.IsNullOrWhiteSpace(SelectedWorkOrder))
        {
            try
            {
                var result = await Planner.GetWorkOrderMachineScheduleWindowAsync(SelectedWorkOrder);
                if (result.Succeeded && result.Data is not null)
                {
                    window = result.Data;
                    windowResolved = true;
                }
            }
            catch
            {
                // A failed resolver must not swallow the selection. The single reload below
                // simply keeps the current period.
            }
        }

        var plan = MachineSchedulePlannerSelectionPlanner.ResolveSelection(
            SelectedWorkOrder, windowResolved, window, PeriodStart, ViewDuration, CurrentView);
        ApplySelectionPlan(plan);
        await ReloadAsync();
    }

    private async Task GoToWorkOrderScheduleAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(SelectedWorkOrder))
            return;

        MachineSchedulePlannerWorkOrderWindow? window = null;
        var windowResolved = false;
        try
        {
            var result = await Planner.GetWorkOrderMachineScheduleWindowAsync(SelectedWorkOrder);
            if (result.Succeeded && result.Data is not null)
            {
                window = result.Data;
                windowResolved = true;
            }
        }
        catch
        {
            // Fall through to a single reload with the current period.
        }

        var plan = MachineSchedulePlannerSelectionPlanner.ResolveGoToSchedule(
            windowResolved, window, PeriodStart, ViewDuration, CurrentView);
        ApplySelectionPlan(plan);
        await ReloadAsync();
    }

    private void ApplySelectionPlan(MachineSchedulePlannerSelectionPlan plan)
    {
        if (plan.ShouldMovePeriod)
            PeriodStart = plan.PeriodStart;
        if (plan.ClearStatus)
            StatusMessage = null;
        else if (plan.StatusMessage is not null)
            StatusMessage = plan.StatusMessage;
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
        // A drag can update the selected appointment while the reschedule dialog is opening.
        // Keep the preview dialog intact instead of letting the bind-after callback close it.
        if (IsRescheduleDialogOpen)
            return;

        IsDetailsOpen = SelectedJob is not null || SelectedExternalCapacityAppointment is not null;
        ReschedulePreview = null;
        await Task.CompletedTask;
    }

    private bool CanStartAdvanced(MachineScheduleAdvancedAction action) => action switch
    {
        MachineScheduleAdvancedAction.InsertSplit => CanInsertSplit,
        MachineScheduleAdvancedAction.Hold => CanHoldJob,
        MachineScheduleAdvancedAction.ReleaseHold => CanResumeJob,
        MachineScheduleAdvancedAction.ReplanRemaining => CanReplanRemaining,
        MachineScheduleAdvancedAction.MaintenanceReplan => CanReplanMaintenance,
        _ => false
    };

    private void OpenAdvancedDialog(MachineScheduleAdvancedAction action)
    {
        if (SelectedJob is not { } job || !CanStartAdvanced(action))
            return;

        AdvancedAction = action;
        AdvancedDialogJob = job;
        AdvancedPreview = null;
        AdvancedReason = string.Empty;
        AdvancedPartialQuantityText = string.Empty;
        AdvancedIncludeRestartSetup = true;
        AdvancedHoldType = ProductionScheduleHoldTypes.Other;
        AdvancedMaintenanceType = "BREAKDOWN";

        var now = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);
        var insertStart = now < job.PlannedStart ? now : job.PlannedStart;
        AdvancedDateText = insertStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        AdvancedTimeText = insertStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        AdvancedResumeDateText = string.Empty;
        AdvancedResumeTimeText = string.Empty;
        var downtimeStart = job.ResumeNotBefore ?? job.PlannedStart;
        AdvancedDowntimeStartDateText = downtimeStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        AdvancedDowntimeStartTimeText = downtimeStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        AdvancedDowntimeEndDateText = string.Empty;
        AdvancedDowntimeEndTimeText = string.Empty;

        ErrorMessage = null;
        IsAdvancedDialogOpen = true;
    }

    private void CloseAdvancedDialog()
    {
        if (IsAdvancedBusy)
            return;
        IsAdvancedDialogOpen = false;
        AdvancedDialogJob = null;
        AdvancedPreview = null;
    }

    private void InvalidateAdvancedPreview()
    {
        AdvancedPreview = null;
        ErrorMessage = null;
    }

    private void OnAdvancedDateChanged(ChangeEventArgs args)
    {
        AdvancedDateText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedTimeChanged(ChangeEventArgs args)
    {
        AdvancedTimeText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedResumeDateChanged(ChangeEventArgs args)
    {
        AdvancedResumeDateText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedResumeTimeChanged(ChangeEventArgs args)
    {
        AdvancedResumeTimeText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedDowntimeStartDateChanged(ChangeEventArgs args)
    {
        AdvancedDowntimeStartDateText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedDowntimeStartTimeChanged(ChangeEventArgs args)
    {
        AdvancedDowntimeStartTimeText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedDowntimeEndDateChanged(ChangeEventArgs args)
    {
        AdvancedDowntimeEndDateText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedDowntimeEndTimeChanged(ChangeEventArgs args)
    {
        AdvancedDowntimeEndTimeText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedPartialQuantityChanged(ChangeEventArgs args)
    {
        AdvancedPartialQuantityText = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private void OnAdvancedReasonChanged(ChangeEventArgs args)
    {
        AdvancedReason = args.Value?.ToString() ?? string.Empty;
        InvalidateAdvancedPreview();
    }

    private bool TryBuildAdvancedRequest(out MachineScheduleAdvancedRequest request)
    {
        request = new MachineScheduleAdvancedRequest();
        if (AdvancedDialogJob is not { } job)
            return false;

        DateTime? requestedStart = null;
        if (AdvancedNeedsStart)
        {
            if (!TryGetAdvancedSlot(AdvancedDateText, AdvancedTimeText, out var start))
            {
                ErrorMessage = "Enter a valid requested start date and time.";
                return false;
            }
            requestedStart = start;
        }

        decimal? partialQuantity = null;
        if (AdvancedNeedsQuantity && !string.IsNullOrWhiteSpace(AdvancedPartialQuantityText))
        {
            if (!decimal.TryParse(AdvancedPartialQuantityText, NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity)
                && !decimal.TryParse(AdvancedPartialQuantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out quantity))
            {
                ErrorMessage = "Enter a valid partial quantity.";
                return false;
            }
            if (quantity < 0m || quantity > job.RemainingQuantity)
            {
                ErrorMessage = $"Partial quantity must be between 0 and {job.RemainingQuantity:n4} {job.OutputUom}.";
                return false;
            }
            partialQuantity = quantity;
        }

        DateTime? resumeNotBefore = null;
        if (AdvancedNeedsResumeNotBefore)
        {
            if (!string.IsNullOrWhiteSpace(AdvancedResumeDateText) || !string.IsNullOrWhiteSpace(AdvancedResumeTimeText))
            {
                if (!TryGetAdvancedSlot(AdvancedResumeDateText, AdvancedResumeTimeText, out var resume))
                {
                    ErrorMessage = "Enter both a valid resume-not-before date and time, or leave both blank.";
                    return false;
                }
                resumeNotBefore = resume;
            }
        }

        DateTime? downtimeStart = null;
        DateTime? downtimeEnd = null;
        if (AdvancedNeedsDowntime)
        {
            if (!TryGetAdvancedSlot(AdvancedDowntimeStartDateText, AdvancedDowntimeStartTimeText, out var downStart))
            {
                ErrorMessage = "Enter a valid downtime start date and time.";
                return false;
            }
            downtimeStart = downStart;
            if (!string.IsNullOrWhiteSpace(AdvancedDowntimeEndDateText) || !string.IsNullOrWhiteSpace(AdvancedDowntimeEndTimeText))
            {
                if (!TryGetAdvancedSlot(AdvancedDowntimeEndDateText, AdvancedDowntimeEndTimeText, out var downEnd)
                    || downEnd <= downStart)
                {
                    ErrorMessage = "Downtime end must be after the downtime start, or leave it blank for an open-ended repair.";
                    return false;
                }
                downtimeEnd = downEnd;
            }
        }

        var reason = AdvancedReason.Trim();
        if (AdvancedNeedsReason && reason.Length == 0)
        {
            ErrorMessage = "Enter a reason for this schedule change.";
            return false;
        }
        if (reason.Length > 500)
            reason = reason[..500];

        request = new MachineScheduleAdvancedRequest
        {
            Action = AdvancedAction,
            WorkOrderId = job.WorkOrderId,
            OperationId = job.OperationId,
            WorkOrderMachineId = job.WorkOrderMachineId,
            MachineCode = job.MachineCode,
            ExpectedWorkOrderScheduleRevision = job.ScheduleRevision,
            ExpectedWorkOrderRowVersion = job.WorkOrderRowVersion,
            ExpectedMachineScheduleRevision = job.MachineScheduleRevision,
            ExpectedMachineRowVersion = job.MachineRowVersion,
            RequestedStart = requestedStart,
            PartialQuantity = partialQuantity,
            IncludeRestartSetup = AdvancedIncludeRestartSetup,
            Reason = reason,
            HoldType = AdvancedNeedsHoldType ? AdvancedHoldType : null,
            ResumeNotBefore = resumeNotBefore,
            DowntimeStart = downtimeStart,
            DowntimeEnd = downtimeEnd,
            MaintenanceType = AdvancedNeedsDowntime
                ? (string.IsNullOrWhiteSpace(AdvancedMaintenanceType) ? "BREAKDOWN" : AdvancedMaintenanceType.Trim())
                : null
        };
        return true;
    }

    private static bool TryGetAdvancedSlot(string dateText, string timeText, out DateTime value) =>
        MachineSchedulePlannerInputParser.TryParseRequestedStart(
            dateText, timeText, CultureInfo.CurrentCulture, out value);

    private async Task<IvMasterOperationResult<MachineScheduleAdvancedPreview>?> PreviewAdvancedAsync(
        MachineScheduleAdvancedRequest request)
    {
        return AdvancedAction switch
        {
            MachineScheduleAdvancedAction.InsertSplit => await AdvancedPlanner.PreviewInsertAsync(request),
            MachineScheduleAdvancedAction.Hold => await AdvancedPlanner.PreviewHoldAsync(request),
            MachineScheduleAdvancedAction.ReleaseHold => await AdvancedPlanner.PreviewReleaseHoldAsync(request),
            MachineScheduleAdvancedAction.ReplanRemaining => await AdvancedPlanner.PreviewReplanRemainingAsync(request),
            MachineScheduleAdvancedAction.MaintenanceReplan => await AdvancedPlanner.PreviewMaintenanceReplanAsync(request),
            _ => null
        };
    }

    private async Task<IvMasterOperationResult<MachineScheduleAdvancedCommitResult>?> CommitAdvancedAsync(
        MachineScheduleAdvancedRequest request)
    {
        return AdvancedAction switch
        {
            MachineScheduleAdvancedAction.InsertSplit => await AdvancedPlanner.CommitInsertAsync(request),
            MachineScheduleAdvancedAction.Hold => await AdvancedPlanner.CommitHoldAsync(request),
            MachineScheduleAdvancedAction.ReleaseHold => await AdvancedPlanner.CommitReleaseHoldAsync(request),
            MachineScheduleAdvancedAction.ReplanRemaining => await AdvancedPlanner.CommitReplanRemainingAsync(request),
            MachineScheduleAdvancedAction.MaintenanceReplan => await AdvancedPlanner.CommitMaintenanceReplanAsync(request),
            _ => null
        };
    }

    private async Task PreviewAdvancedActionAsync()
    {
        if (!IsReschedulingEnabled || !CanEditSchedule || !TryBuildAdvancedRequest(out var request))
            return;

        IsAdvancedBusy = true;
        ErrorMessage = null;
        AdvancedPreview = null;
        try
        {
            var result = await PreviewAdvancedAsync(request);
            if (result is null)
                return;
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "The advanced schedule preview could not be created.";
                return;
            }
            AdvancedPreview = result.Data;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The advanced schedule preview could not be created. {ex.Message}";
        }
        finally
        {
            IsAdvancedBusy = false;
        }
    }

    private async Task CommitAdvancedActionAsync()
    {
        if (!IsReschedulingEnabled || !CanEditSchedule
            || AdvancedPreview is not { CanCommit: true, PreviewToken: { Length: > 0 } token }
            || !TryBuildAdvancedRequest(out var request))
            return;

        IsAdvancedBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await CommitAdvancedAsync(request with { PreviewToken = token });
            if (result is null)
                return;
            if (!result.Succeeded || result.Data is null)
            {
                var failureMessage = result.Message ?? "The advanced schedule change could not be committed.";
                AdvancedPreview = null;
                await ReloadAsync();
                var refreshedJob = Board?.Jobs.FirstOrDefault(x => x.OperationId == AdvancedDialogJob?.OperationId);
                if (refreshedJob is null)
                    CloseAdvancedDialog();
                else
                    AdvancedDialogJob = refreshedJob;
                ErrorMessage = failureMessage;
                return;
            }

            var successMessage = result.Data.Message;
            var hasUnscheduled = result.Data.HasUnscheduledRemaining;
            CloseAdvancedDialog();
            await ReloadAsync();
            StatusMessage = hasUnscheduled
                ? $"{successMessage} Completion is unknown until the remaining quantity is scheduled."
                : successMessage;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The advanced schedule change could not be committed. {ex.Message}";
        }
        finally
        {
            IsAdvancedBusy = false;
        }
    }

    private void OpenRescheduleDialog()
    {
        if (SelectedJob is not { } job || !CanReschedule(job))
            return;

        OpenRescheduleDialog(job, job.PlannedStart);
    }

    private void OpenRescheduleDialog(MachineSchedulePlannerJob job, DateTime requestedStart)
    {
        if (!CanReschedule(job))
            return;

        RescheduleDialogJob = job;
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

        if (IsExternalCapacityAppointment(args.Appointment))
        {
            args.Cancel = true;
            StatusMessage = MachineSchedulePlannerAppointmentRules.ExternalCapacityDragMessage;
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
        if (IsExternalCapacityAppointment(args.Appointment))
        {
            StatusMessage = MachineSchedulePlannerAppointmentRules.ExternalCapacityDragMessage;
            return Task.CompletedTask;
        }

        if (!CanDragAppointments)
        {
            StatusMessage = "Drag rescheduling is unavailable because planner readiness or edit permission is missing.";
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
        if (!CanReschedule(job))
        {
            StatusMessage = job.LockReason ?? "This operation is not eligible for rescheduling.";
            return Task.CompletedTask;
        }

        OpenRescheduleDialog(job, args.Appointment.Start);
        return Task.CompletedTask;
    }

    private static bool IsExternalCapacityAppointment(DxSchedulerAppointmentItem appointment) =>
        MachineSchedulePlannerAppointmentRules.IsExternalCapacity(appointment.Location);

    private void OnAppointmentFormShowing(SchedulerAppointmentFormEventArgs args) => args.Cancel = true;

    private MachineSchedulePlannerJob? FindJob(DxSchedulerAppointmentItem appointment)
    {
        return MachineSchedulePlannerAppointmentRules.TryGetOperationId(appointment.Location, out var operationId)
            ? Board?.Jobs.FirstOrDefault(x => x.OperationId == operationId)
            : null;
    }

    private bool CanReschedule(MachineSchedulePlannerJob? job) => IsReschedulingEnabled
        && CanEditSchedule
        && job is not null
        && !job.IsLocked
        && !job.NeedsRemainingReplan
        && !job.IsBackwardScheduled
        && (string.Equals(job.WorkOrderStatus, ProductionWorkOrderStatuses.Released, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.WorkOrderStatus, ProductionWorkOrderStatuses.InProgress, StringComparison.OrdinalIgnoreCase));

    private void CloseRescheduleDialog()
    {
        IsRescheduleDialogOpen = false;
        RescheduleDialogJob = null;
        ReschedulePreview = null;
        IsRescheduleBusy = false;
    }

    private async Task PreviewAllocationBackfillAsync()
    {
        if (!CanManageAllocationBackfill)
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
        if (!CanManageAllocationBackfill || BackfillReport is not { CanApply: true, MissingAllocationCount: > 0 } report)
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
        if (!CanEditRescheduleDialog || RescheduleDialogJob is not { } job)
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
        if (!CanEditRescheduleDialog || RescheduleDialogJob is not { } job
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
                var failureMessage = result.Message ?? "The schedule change could not be committed.";
                ReschedulePreview = null;
                await ReloadAsync();
                var refreshedJob = Board?.Jobs.FirstOrDefault(x => x.OperationId == job.OperationId);
                if (refreshedJob is null)
                    CloseRescheduleDialog();
                else
                    RescheduleDialogJob = refreshedJob;
                ErrorMessage = failureMessage;
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
        => MachineSchedulePlannerInputParser.TryParseRequestedStart(
            RequestedDateText, RequestedTimeText, CultureInfo.CurrentCulture, out requestedStart);

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
            return MachineSchedulePlannerAppointmentRules.TryGetOperationId(location, out var operationId)
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
        new(MachineSchedulePlannerLabelIds.Scheduled, "Scheduled", "pr-machine-schedule-label--scheduled", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.ExecutionLocked, "Execution locked", "pr-machine-schedule-label--locked", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.PreventiveMaintenance, "Preventive maintenance", "pr-machine-schedule-label--maintenance", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.LateToRequiredDate, "Late vs DR date", "pr-machine-schedule-label--late", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.ReservedOtherBranch, "Reserved · other branch", "pr-machine-schedule-label--external", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.ManualSplit, "Part 1 / split", "pr-machine-schedule-label--split", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.UrgentInsert, "URGENT", "pr-machine-schedule-label--urgent", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.Resume, "Resume", "pr-machine-schedule-label--resume", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.HoldResume, "Hold resume", "pr-machine-schedule-label--hold-resume", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.MaintenanceResume, "Maintenance resume", "pr-machine-schedule-label--maintenance-resume", "pr-machine-schedule-label__text"),
        new(MachineSchedulePlannerLabelIds.RemainingReplan, "Remaining replan", "pr-machine-schedule-label--remaining", "pr-machine-schedule-label__text")
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
}

internal enum PlannerView { Day, Week, Month }

/// <summary>
/// Testable period-navigation rules for the Machine Schedule planner. Auto-jump only runs on
/// initial Work Order selection; manual period navigation is never forced back.
/// </summary>
internal static class MachineSchedulePlannerPeriodNavigation
{
    internal static bool IsInPeriod(DateTime periodStart, TimeSpan duration, DateTime value) =>
        value >= periodStart && value < periodStart.Add(duration);

    internal static DateTime PeriodStartFor(DateTime value, PlannerView view)
    {
        var date = DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        return view switch
        {
            PlannerView.Day => date,
            PlannerView.Month => new DateTime(date.Year, date.Month, 1),
            _ => StartOfWeek(date)
        };
    }

    internal static DateTime ResolveAnchor(
        DateTime? earliestMachineStart,
        DateTime workOrderPlannedStart) =>
        earliestMachineStart ?? workOrderPlannedStart;

    private static DateTime StartOfWeek(DateTime date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return DateTime.SpecifyKind(date.Date.AddDays(-offset), DateTimeKind.Unspecified);
    }
}

/// <summary>
/// The single decision point for Work Order selection. Every selection path produces exactly one
/// plan and therefore exactly one board reload; manual period navigation never produces a plan,
/// so it can never force the planner back to a previously selected Work Order.
/// </summary>
internal sealed record MachineSchedulePlannerSelectionPlan(
    bool ShouldReload,
    bool ShouldMovePeriod,
    DateTime PeriodStart,
    string? StatusMessage,
    bool ClearStatus);

internal static class MachineSchedulePlannerSelectionPlanner
{
    internal static MachineSchedulePlannerSelectionPlan ResolveSelection(
        string? selectedWorkOrder,
        bool windowResolved,
        MachineSchedulePlannerWorkOrderWindow? window,
        DateTime currentPeriodStart,
        TimeSpan currentViewDuration,
        PlannerView view)
    {
        var clearStatus = string.IsNullOrWhiteSpace(selectedWorkOrder);
        return MoveWhenOutsidePeriod(
            windowResolved, window, currentPeriodStart, currentViewDuration, view, announce: true)
            with { ClearStatus = clearStatus };
    }

    /// <summary>Explicit `Go to WO schedule` re-anchors without re-announcing the move.</summary>
    internal static MachineSchedulePlannerSelectionPlan ResolveGoToSchedule(
        bool windowResolved,
        MachineSchedulePlannerWorkOrderWindow? window,
        DateTime currentPeriodStart,
        TimeSpan currentViewDuration,
        PlannerView view) =>
        MoveWhenOutsidePeriod(
            windowResolved, window, currentPeriodStart, currentViewDuration, view, announce: false);

    private static MachineSchedulePlannerSelectionPlan MoveWhenOutsidePeriod(
        bool windowResolved,
        MachineSchedulePlannerWorkOrderWindow? window,
        DateTime currentPeriodStart,
        TimeSpan currentViewDuration,
        PlannerView view,
        bool announce)
    {
        if (windowResolved && window?.AnchorStart is { } anchor
            && !MachineSchedulePlannerPeriodNavigation.IsInPeriod(currentPeriodStart, currentViewDuration, anchor))
        {
            return new MachineSchedulePlannerSelectionPlan(
                ShouldReload: true,
                ShouldMovePeriod: true,
                PeriodStart: MachineSchedulePlannerPeriodNavigation.PeriodStartFor(anchor, view),
                StatusMessage: announce ? $"Moved to {window.WorkOrderNo} scheduled period." : null,
                ClearStatus: false);
        }

        // No move, but the selection still needs its single reload.
        return new MachineSchedulePlannerSelectionPlan(
            ShouldReload: true,
            ShouldMovePeriod: false,
            PeriodStart: currentPeriodStart,
            StatusMessage: null,
            ClearStatus: false);
    }
}

/// <summary>
/// Scheduler location prefixes. External-capacity bars must never fall through to the generic
/// maintenance message or open current-branch Work Order details.
/// </summary>
internal static class MachineSchedulePlannerAppointmentRules
{
    internal const string OperationPrefix = "operation:";
    internal const string MaintenancePrefix = "maintenance:";
    internal const string ExternalCapacityPrefix = "external-capacity:";

    internal const string ExternalCapacityDragMessage =
        "This reserved capacity belongs outside the current branch and cannot be moved here.";

    internal static bool IsExternalCapacity(string? location) =>
        location?.StartsWith(ExternalCapacityPrefix, StringComparison.Ordinal) == true;

    internal static bool IsMaintenance(string? location) =>
        location?.StartsWith(MaintenancePrefix, StringComparison.Ordinal) == true;

    internal static bool TryGetOperationId(string? location, out long operationId)
    {
        operationId = 0;
        return location?.StartsWith(OperationPrefix, StringComparison.Ordinal) == true
            && long.TryParse(location.AsSpan(OperationPrefix.Length), out operationId);
    }

    /// <summary>Only current-branch operation bars may open Work Order details.</summary>
    internal static bool CanOpenWorkOrder(string? location) =>
        TryGetOperationId(location, out _);

    internal static bool CanChangeSchedule(string? location) =>
        TryGetOperationId(location, out _);
}

internal static class MachineSchedulePlannerInputParser
{
    internal static bool TryParseRequestedStart(
        string? dateText,
        string? timeText,
        CultureInfo culture,
        out DateTime requestedStart)
    {
        requestedStart = default;
        if (string.IsNullOrWhiteSpace(dateText)
            || string.IsNullOrWhiteSpace(timeText)
            || !TryParseDate(dateText.Trim(), culture, out var date)
            || !TryParseTime(timeText.Trim(), culture, out var time))
            return false;

        requestedStart = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return true;
    }

    private static bool TryParseDate(string value, CultureInfo culture, out DateOnly date)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
            return true;

        return DateOnly.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out date)
            || DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool TryParseTime(string value, CultureInfo culture, out TimeOnly time)
    {
        if (TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out time))
            return true;

        return TimeOnly.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out time)
            || TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time);
    }
}

