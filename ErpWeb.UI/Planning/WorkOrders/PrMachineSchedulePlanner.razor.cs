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
    private readonly MachineScheduleDraftWorkspace DraftWorkspace = new();
    private bool IsDraftPreviewDialogOpen;
    private bool IsDraftCommitBusy;
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
    private string? RescheduleDragNote;
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
        PlannerView.Month => CellWidths[Math.Clamp(ZoomLevel + 2, 1, CellWidths.Length - 1)],
        _ => CellWidths[Math.Clamp(ZoomLevel + 2, 0, CellWidths.Length - 1)]
    };

    private int MinimumZoomLevel => CurrentView == PlannerView.Month ? -1 : -2;

    private bool IsReschedulingEnabled => Board?.Readiness.IsWriteReady == true;

    private bool CanOpenReschedule => CanReschedule(SelectedJob);

    private bool CanEditRescheduleDialog => CanReschedule(RescheduleDialogJob);

    private bool CanDragAppointments => IsReschedulingEnabled && CanEditSchedule && !IsDraftCommitBusy;
    private bool CanUseDraftMode => CanEditSchedule && IsReschedulingEnabled;

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
        ? "Pick a Work Order or machine, then drag any bar left or right to shift that operation — nothing saves until you confirm."
        : CanEditSchedule && !IsReschedulingEnabled
            ? "Find a Work Order or machine, then select a job. Schedule changes require the company setting and planner readiness."
            : "Find a Work Order or machine, then select a job to inspect its schedule.";

    private string TimelineQuickTip => CanDragAppointments
        ? "Tip: Match each bar's #number and color to the Work Order legend below. Click a legend card to highlight its bars, or drag any bar to shift that Work Order."
        : "View only: Match each bar's #number and color to the Work Order legend below. Click any legend card or bar to inspect full details.";

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

    private static readonly int[] CellWidths = [36, 44, 54, 68, 86, 110, 140];

    private string PeriodLabel => CurrentView switch
    {
        PlannerView.Day => PeriodStart.ToString("dddd, dd MMM yyyy"),
        PlannerView.Month => PeriodStart.ToString("MMMM yyyy"),
        _ => $"{PeriodStart:dd MMM} – {PeriodStart.AddDays(6):dd MMM yyyy}"
    };

    private long? SelectedSegmentId =>
        MachineSchedulePlannerAppointmentRules.TryGetSegmentId(SelectedAppointment?.Location, out var segmentId)
            ? segmentId
            : null;

    private MachineSchedulePlannerJob? SelectedJob =>
        SelectedOperationId is { } operationId
            ? Board?.Jobs.FirstOrDefault(x => x.OperationId == operationId)
            : null;

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
            DraftWorkspace.Initialize(Board);
            RefreshSchedulerStorage();
            if (selectedOperationId.HasValue)
            {
                var activeAppointments = DraftWorkspace.IsActive
                    ? DraftWorkspace.DraftAppointments
                    : Board.Appointments;
                var selectedAppointmentId = activeAppointments.FirstOrDefault(x =>
                    x.OperationId == selectedOperationId.Value
                    || string.Equals(x.Location, $"operation:{selectedOperationId.Value}", StringComparison.Ordinal))?.Id;
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

    /// <summary>
    /// Magnifier action: cancels the pending 350 ms debounce and reloads the schedule with the
    /// latest search text exactly once. The debounce itself is untouched for typing.
    /// </summary>
    private async Task ApplySearchAsync()
    {
        var pending = Volatile.Read(ref _searchDebounceCts);
        if (pending is not null)
        {
            try
            {
                pending.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The debounce owner already completed and disposed itself.
            }
        }

        if (_disposed) return;
        await ReloadAsync();
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

    private void StageAdvancedDraft()
    {
        if (!DraftWorkspace.IsActive || !IsReschedulingEnabled || !CanEditSchedule
            || AdvancedPreview is null || !TryBuildAdvancedRequest(out var request))
            return;

        var kind = request.Action switch
        {
            MachineScheduleAdvancedAction.InsertSplit => MachineScheduleDraftCommandKind.InsertSplit,
            MachineScheduleAdvancedAction.Hold => MachineScheduleDraftCommandKind.Hold,
            MachineScheduleAdvancedAction.ReleaseHold => MachineScheduleDraftCommandKind.ReleaseHold,
            MachineScheduleAdvancedAction.ReplanRemaining => MachineScheduleDraftCommandKind.ReplanRemaining,
            MachineScheduleAdvancedAction.MaintenanceReplan => MachineScheduleDraftCommandKind.MaintenanceReplan,
            _ => throw new InvalidOperationException($"Unsupported draft advanced action: {request.Action}")
        };

        var command = new MachineScheduleDraftCommand
        {
            Kind = kind,
            WorkOrderId = request.WorkOrderId,
            OperationId = request.OperationId,
            WorkOrderMachineId = request.WorkOrderMachineId,
            MachineCode = request.MachineCode,
            WorkOrderNo = AdvancedDialogJob?.WorkOrderNo ?? string.Empty,
            RequestedStart = request.RequestedStart,
            PartialQuantity = request.PartialQuantity,
            IncludeRestartSetup = request.IncludeRestartSetup,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? $"Draft {kind}" : request.Reason,
            HoldType = request.HoldType,
            ResumeNotBefore = request.ResumeNotBefore,
            DowntimeStart = request.DowntimeStart,
            DowntimeEnd = request.DowntimeEnd,
            MaintenanceType = request.MaintenanceType
        };

        DraftWorkspace.StageAdvancedResult(command, AdvancedPreview);
        var operationId = request.OperationId;
        var woNo = command.WorkOrderNo;
        CloseAdvancedDialog();
        RefreshSchedulerStorage();
        SelectOperationAppointment(operationId);
        StatusMessage = DraftWorkspace.ConflictCount > 0
            ? $"Staged {kind} for {woNo} in Draft Mode ({DraftWorkspace.ConflictCount} conflict(s) detected — resolve before committing)."
            : $"Staged {kind} for {woNo} in Draft Mode ({DraftWorkspace.PendingCount} pending change(s)).";
    }

    private void OpenRescheduleDialog()
    {
        if (SelectedJob is not { } job || !CanReschedule(job))
            return;

        OpenRescheduleDialog(job, job.PlannedStart);
    }

    private void OpenRescheduleDialog(
        MachineSchedulePlannerJob job,
        DateTime requestedStart,
        string? dragNote = null)
    {
        if (!CanReschedule(job))
            return;

        RescheduleDialogJob = job;
        RequestedDateText = requestedStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        RequestedTimeText = requestedStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        RescheduleReason = string.Empty;
        RescheduleDragNote = dragNote;
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

        var apptId = args.Appointment.Id?.ToString();
        DateTime? originalBarStart = !string.IsNullOrWhiteSpace(apptId) && ActiveAppointmentById.TryGetValue(apptId, out var activeAppt)
            ? activeAppt.Start
            : null;
        var currentOperationStart = ResolveCurrentOperationStart(job);
        var dragResult = MachineSchedulePlannerDragRules.ComputeShiftedStart(
            currentOperationStart,
            originalBarStart,
            args.Appointment.Start);

        if (dragResult.Delta == TimeSpan.Zero)
        {
            StatusMessage = $"No schedule change detected for {job.WorkOrderNo} — drag a bar left or right to another slot.";
            return Task.CompletedTask;
        }

        if (DraftWorkspace.IsActive)
        {
            DraftWorkspace.StageMove(job, dragResult.ShiftedStart, $"Draft drag ({dragResult.OffsetSummary})");
            RefreshSchedulerStorage();
            SelectOperationAppointment(job.OperationId);
            StatusMessage = DraftWorkspace.ConflictCount > 0
                ? $"Moved {job.WorkOrderNo} {dragResult.OffsetSummary} in Draft Mode ({DraftWorkspace.ConflictCount} conflict(s) detected — resolve before committing)."
                : $"Moved {job.WorkOrderNo} {dragResult.OffsetSummary} in Draft Mode (proposing start {dragResult.ShiftedStart:dd MMM HH:mm} — click Preview Changes to confirm).";
            return Task.CompletedTask;
        }

        OpenRescheduleDialog(job, dragResult.ShiftedStart, dragResult.DialogNote);
        return Task.CompletedTask;
    }

    private DateTime ResolveCurrentOperationStart(MachineSchedulePlannerJob job)
    {
        if (!DraftWorkspace.IsActive)
            return job.PlannedStart;

        var matchingStart = DraftWorkspace.DraftAppointments
            .Where(x => !x.IsMaintenance
                && !x.IsExternalCapacity
                && (x.OperationId == job.OperationId
                    || string.Equals(x.Location, $"{MachineSchedulePlannerAppointmentRules.OperationPrefix}{job.OperationId}", StringComparison.Ordinal)))
            .Select(x => (DateTime?)x.Start)
            .OrderBy(x => x)
            .FirstOrDefault();
        return matchingStart ?? job.PlannedStart;
    }

    private static bool IsExternalCapacityAppointment(DxSchedulerAppointmentItem appointment) =>
        MachineSchedulePlannerAppointmentRules.IsExternalCapacity(appointment.Location);

    private void OnAppointmentFormShowing(SchedulerAppointmentFormEventArgs args) => args.Cancel = true;

    private MachineSchedulePlannerJob? FindJob(DxSchedulerAppointmentItem appointment) =>
        ResolveOperationId(appointment.Location, appointment.Id?.ToString()) is { } operationId
            ? Board?.Jobs.FirstOrDefault(x => x.OperationId == operationId)
            : null;

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
        RescheduleDragNote = null;
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

    private MachineSchedulePlannerDateHeaderState GetDateHeaderState(
        DateTime intervalStart,
        DateTime intervalEnd,
        SchedulerTimeScaleUnit unit) =>
        MachineSchedulePlannerCalendarHelper.ResolveDateHeaderState(
            Board?.Machines,
            intervalStart,
            intervalEnd,
            unit);

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

        args.CssClass = MachineSchedulePlannerCalendarHelper.ClassifyCell(machine, interval.Start, interval.End) switch
        {
            MachineSchedulePlannerCalendarCellKind.Breakdown => "pr-machine-schedule-cell--maintenance pr-machine-schedule-cell--breakdown",
            MachineSchedulePlannerCalendarCellKind.Maintenance => "pr-machine-schedule-cell--maintenance",
            MachineSchedulePlannerCalendarCellKind.PublicHoliday => "pr-machine-schedule-cell--holiday",
            MachineSchedulePlannerCalendarCellKind.WorkingWithBreak => "pr-machine-schedule-cell--working pr-machine-schedule-cell--has-break",
            MachineSchedulePlannerCalendarCellKind.Working => "pr-machine-schedule-cell--working",
            MachineSchedulePlannerCalendarCellKind.Break => "pr-machine-schedule-cell--break",
            _ => "pr-machine-schedule-cell--off"
        };
    }

    private long? SelectedOperationId =>
        ResolveOperationId(SelectedAppointment?.Location, SelectedAppointment?.Id?.ToString());

    private long? ResolveOperationId(string? location, string? appointmentId = null)
    {
        if (MachineSchedulePlannerAppointmentRules.TryGetOperationId(location, out var operationId))
            return operationId;

        if (MachineSchedulePlannerAppointmentRules.TryGetSegmentId(location, out var segmentId))
        {
            var fromSegment = Board?.Segments.FirstOrDefault(x => x.SegmentId == segmentId)?.OperationId;
            if (fromSegment.HasValue)
                return fromSegment.Value;

            var fromActive = (DraftWorkspace.IsActive ? DraftWorkspace.DraftAppointments : Board?.Appointments)
                ?.FirstOrDefault(x => x.SegmentId == segmentId)?.OperationId;
            if (fromActive.HasValue)
                return fromActive.Value;
        }

        if (!string.IsNullOrWhiteSpace(appointmentId)
            && ActiveAppointmentById.TryGetValue(appointmentId, out var appt))
        {
            return appt.OperationId;
        }

        return null;
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

    private void StageRescheduleDraft()
    {
        if (!DraftWorkspace.IsActive || !CanEditRescheduleDialog || RescheduleDialogJob is not { } job)
            return;

        if (!TryGetRequestedStart(out var requestedStart))
        {
            ErrorMessage = "Enter a valid requested date and time.";
            return;
        }

        var reason = string.IsNullOrWhiteSpace(RescheduleReason)
            ? "Draft schedule move"
            : RescheduleReason.Trim();
        DraftWorkspace.StageMove(job, requestedStart, reason);
        var operationId = job.OperationId;
        var woNo = job.WorkOrderNo;
        CloseRescheduleDialog();
        RefreshSchedulerStorage();
        SelectOperationAppointment(operationId);
        StatusMessage = DraftWorkspace.ConflictCount > 0
            ? $"Staged move for {woNo} in Draft Mode ({DraftWorkspace.ConflictCount} conflict(s) detected — resolve before committing)."
            : $"Staged move for {woNo} in Draft Mode ({DraftWorkspace.PendingCount} pending change(s)).";
    }

    private void ToggleDraftMode()
    {
        if (IsLoading || IsDraftCommitBusy)
            return;

        ErrorMessage = null;
        if (DraftWorkspace.IsActive)
        {
            DraftWorkspace.ExitDraftMode();
            IsDraftPreviewDialogOpen = false;
            RefreshSchedulerStorage();
            StatusMessage = "Exited Draft Reschedule Mode. Discarded unsaved draft changes.";
            return;
        }

        if (!CanUseDraftMode || Board is null)
            return;

        DraftWorkspace.Initialize(Board, enterDraftMode: true);
        RefreshSchedulerStorage();
        StatusMessage = "Draft Reschedule Mode is active. Stage moves or advanced actions without saving to the database, resolve any overlaps, then preview and commit all changes.";
    }

    private void UndoDraft()
    {
        if (!DraftWorkspace.IsActive || IsDraftCommitBusy || !DraftWorkspace.Undo())
            return;

        RefreshSchedulerStorage();
        StatusMessage = $"Undid last draft edit ({DraftWorkspace.PendingCount} pending change(s), {DraftWorkspace.ConflictCount} conflict(s)).";
    }

    private void RedoDraft()
    {
        if (!DraftWorkspace.IsActive || IsDraftCommitBusy || !DraftWorkspace.Redo())
            return;

        RefreshSchedulerStorage();
        StatusMessage = $"Redid draft edit ({DraftWorkspace.PendingCount} pending change(s), {DraftWorkspace.ConflictCount} conflict(s)).";
    }

    private void ResetDraft()
    {
        if (!DraftWorkspace.IsActive || IsDraftCommitBusy)
            return;

        DraftWorkspace.ResetAll();
        IsDraftPreviewDialogOpen = false;
        RefreshSchedulerStorage();
        StatusMessage = "Reset all staged draft changes back to live saved schedules.";
    }

    private async Task OpenDraftPreviewDialogAsync()
    {
        if (!DraftWorkspace.IsActive || !CanUseDraftMode || DraftWorkspace.PendingCount == 0 || IsDraftCommitBusy)
            return;

        IsDraftPreviewDialogOpen = true;
        IsDraftCommitBusy = true;
        ErrorMessage = null;
        DraftWorkspace.SetServerPreview(null);
        try
        {
            var result = await AdvancedPlanner.PreviewDraftAsync(BuildDraftRequest(null));
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "The draft preview could not be generated.";
                return;
            }

            DraftWorkspace.SetServerPreview(result.Data);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The draft preview could not be generated. {ex.Message}";
        }
        finally
        {
            IsDraftCommitBusy = false;
        }
    }

    private void CloseDraftPreviewDialog()
    {
        if (IsDraftCommitBusy)
            return;
        IsDraftPreviewDialogOpen = false;
    }

    private async Task CommitDraftAsync()
    {
        if (!DraftWorkspace.IsActive || !CanUseDraftMode || IsDraftCommitBusy
            || DraftWorkspace.LastServerPreview is not { CanCommit: true, PreviewToken: { Length: > 0 } token })
            return;

        IsDraftCommitBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await AdvancedPlanner.CommitDraftAsync(BuildDraftRequest(token));
            if (!result.Succeeded || result.Data is null)
            {
                DraftWorkspace.SetServerPreview(null);
                ErrorMessage = result.Message ?? "The staged draft changes could not be committed.";
                return;
            }

            var message = result.Data.Message;
            DraftWorkspace.ExitDraftMode();
            IsDraftPreviewDialogOpen = false;
            await ReloadAsync();
            StatusMessage = result.Data.HasUnscheduledRemaining
                ? $"{message} Completion is unknown until remaining quantities are scheduled."
                : message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The staged draft changes could not be committed. {ex.Message}";
        }
        finally
        {
            IsDraftCommitBusy = false;
        }
    }

    private MachineScheduleDraftRequest BuildDraftRequest(string? previewToken)
    {
        var expectedByKey = new Dictionary<(long WoId, long WomId), MachineScheduleDraftExpectedVersion>();
        if (Board is not null)
        {
            foreach (var job in Board.Jobs)
            {
                expectedByKey.TryAdd(
                    (job.WorkOrderId, job.WorkOrderMachineId),
                    new MachineScheduleDraftExpectedVersion
                    {
                        WorkOrderId = job.WorkOrderId,
                        WorkOrderMachineId = job.WorkOrderMachineId,
                        ExpectedWorkOrderScheduleRevision = job.ScheduleRevision,
                        ExpectedWorkOrderRowVersion = job.WorkOrderRowVersion,
                        ExpectedMachineScheduleRevision = job.MachineScheduleRevision,
                        ExpectedMachineRowVersion = job.MachineRowVersion
                    });
            }
        }

        return new MachineScheduleDraftRequest
        {
            BoardBaseFingerprint = Board?.BoardFingerprint ?? DraftWorkspace.BaseBoardFingerprint,
            Commands = DraftWorkspace.PendingCommands.ToList(),
            ExpectedVersions = expectedByKey.Values.ToList(),
            PreviewToken = previewToken
        };
    }

    private void SelectOperationAppointment(long operationId)
    {
        if (Board is null)
            return;

        var active = DraftWorkspace.IsActive ? DraftWorkspace.DraftAppointments : Board.Appointments;
        var id = active.FirstOrDefault(x =>
            !x.IsMaintenance
            && !x.IsExternalCapacity
            && (x.OperationId == operationId || string.Equals(x.Location, $"operation:{operationId}", StringComparison.Ordinal)))?.Id;
        if (id is not null)
            SelectedAppointment = DataStorage.GetAppointmentItemById(id);
    }

    private IReadOnlyDictionary<string, MachineSchedulePlannerAppointment> ActiveAppointmentById { get; set; }
        = new Dictionary<string, MachineSchedulePlannerAppointment>(StringComparer.Ordinal);

    private IReadOnlyList<MachineScheduleWorkOrderLegendEntry> WorkOrderLegend { get; set; } = [];

    private IReadOnlyDictionary<string, MachineScheduleWorkOrderLegendEntry> WorkOrderLegendByNo { get; set; }
        = new Dictionary<string, MachineScheduleWorkOrderLegendEntry>(StringComparer.OrdinalIgnoreCase);

    private void SelectWorkOrderFromLegend(MachineScheduleWorkOrderLegendEntry entry)
    {
        if (string.Equals(SelectedJob?.WorkOrderNo, entry.WorkOrderNo, StringComparison.OrdinalIgnoreCase))
        {
            SelectedAppointment = null;
            return;
        }

        SelectOperationAppointment(entry.PrimaryOperationId);
        IsDetailsOpen = true;
    }

    private void ClearSelectedAppointment() => SelectedAppointment = null;

    private void RefreshSchedulerStorage()
    {
        if (Board is null)
            return;

        WorkOrderLegend = MachineSchedulePlannerColorRules.BuildWorkOrderLegend(Board.Jobs);
        WorkOrderLegendByNo = WorkOrderLegend
            .ToDictionary(x => x.WorkOrderNo, x => x, StringComparer.OrdinalIgnoreCase);

        var appointments = DraftWorkspace.IsActive
            ? DraftWorkspace.DraftAppointments
            : Board.Appointments;
        ActiveAppointmentById = appointments
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        DataStorage = CreateStorage(Board, appointments);
    }

    private MachineSchedulePlannerAppointment? ResolveAppointmentViewModel(DxSchedulerAppointmentView view)
    {
        var id = view.Appointment?.Id?.ToString();
        if (!string.IsNullOrWhiteSpace(id) && ActiveAppointmentById.TryGetValue(id, out var exact))
            return exact;

        var location = view.Appointment?.Location;
        var start = view.Appointment?.Start;
        var source = DraftWorkspace.IsActive ? DraftWorkspace.DraftAppointments : Board?.Appointments;
        if (source is null || string.IsNullOrWhiteSpace(location))
            return null;

        return source.FirstOrDefault(x =>
            string.Equals(x.Location, location, StringComparison.Ordinal)
            && (!start.HasValue || x.Start == start.Value));
    }

    private string AppointmentBarCssClass(DxSchedulerAppointmentView view, MachineSchedulePlannerAppointment? appt)
    {
        var labelId = appt?.LabelId ?? (view.Appointment?.LabelId as int? ?? MachineSchedulePlannerLabelIds.Scheduled);
        var legendEntry = ResolveLegendEntry(appt);
        return MachineSchedulePlannerColorRules.GetBarCssClass(
            appt,
            labelId,
            SelectedJob?.WorkOrderNo,
            SelectedOperationId,
            legendEntry?.PaletteIndex);
    }

    private MachineSchedulePlannerBarDisplay BuildAppointmentBarDisplay(
        DxSchedulerAppointmentView view,
        MachineSchedulePlannerAppointment? appt)
    {
        var legendEntry = ResolveLegendEntry(appt);
        return MachineSchedulePlannerColorRules.FormatBarDisplay(
            appt,
            view.Appointment?.Subject,
            view.Appointment?.Start,
            view.Appointment?.End,
            CurrentView,
            legendEntry?.TagText);
    }

    private MachineScheduleWorkOrderLegendEntry? ResolveLegendEntry(MachineSchedulePlannerAppointment? appt) =>
        !string.IsNullOrWhiteSpace(appt?.WorkOrderNo)
        && WorkOrderLegendByNo.TryGetValue(appt.WorkOrderNo.Trim(), out var entry)
            ? entry
            : null;

    private string AppointmentTooltipText(DxSchedulerAppointmentView view, MachineSchedulePlannerAppointment? appt)
    {
        var legendEntry = ResolveLegendEntry(appt);
        var tagPrefix = legendEntry is not null ? $"{legendEntry.TagText} · " : string.Empty;
        var subject = appt?.Subject ?? view.Appointment?.Subject ?? string.Empty;
        var start = appt?.Start ?? view.Appointment?.Start ?? default;
        var end = appt?.End ?? view.Appointment?.End ?? default;
        var desc = appt?.Description ?? view.Appointment?.Description;
        var status = appt?.IsConflict == true ? " · OVERLAP CONFLICT" : string.Empty;
        return string.IsNullOrWhiteSpace(desc)
            ? $"{tagPrefix}{subject} ({start:dd MMM HH:mm}–{end:HH:mm}){status}"
            : $"{tagPrefix}{subject} ({start:dd MMM HH:mm}–{end:HH:mm}){status}\n{desc}";
    }

    private static DxSchedulerDataStorage CreateStorage(
        MachineSchedulePlannerBoard board,
        IReadOnlyList<MachineSchedulePlannerAppointment> appointments) => new()
    {
        AppointmentsSource = appointments,
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
    internal const string SegmentPrefix = "segment:";
    internal const string OperationPrefix = "operation:";
    internal const string MaintenancePrefix = "maintenance:";
    internal const string ExternalCapacityPrefix = "external-capacity:";

    internal const string ExternalCapacityDragMessage =
        "This reserved capacity belongs outside the current branch and cannot be moved here.";

    internal static bool IsExternalCapacity(string? location) =>
        location?.StartsWith(ExternalCapacityPrefix, StringComparison.Ordinal) == true;

    internal static bool IsMaintenance(string? location) =>
        location?.StartsWith(MaintenancePrefix, StringComparison.Ordinal) == true;

    internal static bool TryGetSegmentId(string? location, out long segmentId)
    {
        segmentId = 0;
        return location?.StartsWith(SegmentPrefix, StringComparison.Ordinal) == true
            && long.TryParse(location.AsSpan(SegmentPrefix.Length), out segmentId);
    }

    internal static bool TryGetOperationId(string? location, out long operationId)
    {
        operationId = 0;
        return location?.StartsWith(OperationPrefix, StringComparison.Ordinal) == true
            && long.TryParse(location.AsSpan(OperationPrefix.Length), out operationId);
    }

    /// <summary>Only current-branch operation or segment bars may open Work Order details.</summary>
    internal static bool CanOpenWorkOrder(string? location) =>
        TryGetOperationId(location, out _) || TryGetSegmentId(location, out _);

    internal static bool CanChangeSchedule(string? location) =>
        TryGetOperationId(location, out _) || TryGetSegmentId(location, out _);
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

internal sealed record MachineSchedulePlannerDragResult(
    DateTime ShiftedStart,
    TimeSpan Delta,
    string OffsetSummary,
    string DialogNote);

/// <summary>
/// Converts any dragged operation bar position into an operation-level delta shift
/// (Option A) so dragging the middle or tail bar of a multi-day job shifts its start
/// by the same offset instead of replacing PlannedStart with that bar's timestamp.
/// </summary>
internal static class MachineSchedulePlannerDragRules
{
    internal static MachineSchedulePlannerDragResult ComputeShiftedStart(
        DateTime currentOperationStart,
        DateTime? originalBarStart,
        DateTime droppedBarStart)
    {
        var normalizedCurrent = DateTime.SpecifyKind(currentOperationStart, DateTimeKind.Unspecified);
        var normalizedDropped = DateTime.SpecifyKind(droppedBarStart, DateTimeKind.Unspecified);
        var normalizedOriginal = originalBarStart.HasValue
            ? DateTime.SpecifyKind(originalBarStart.Value, DateTimeKind.Unspecified)
            : normalizedCurrent;

        var delta = normalizedDropped - normalizedOriginal;
        var shiftedStart = DateTime.SpecifyKind(normalizedCurrent.Add(delta), DateTimeKind.Unspecified);
        var offsetSummary = FormatDeltaSummary(delta);
        var dialogNote = delta == TimeSpan.Zero
            ? $"Keep operation start at {shiftedStart:ddd dd MMM yyyy HH:mm}."
            : $"Moved bar {offsetSummary} ({normalizedOriginal:dd MMM HH:mm} → {normalizedDropped:dd MMM HH:mm}). "
              + $"Proposing new start for the whole Work Order: {shiftedStart:ddd dd MMM yyyy HH:mm} "
              + "(exact shift dates are calculated when you click Preview impact).";
        return new MachineSchedulePlannerDragResult(shiftedStart, delta, offsetSummary, dialogNote);
    }

    internal static string FormatDeltaSummary(TimeSpan delta)
    {
        if (delta == TimeSpan.Zero)
            return "no change";

        var direction = delta < TimeSpan.Zero ? "earlier by" : "later by";
        var abs = delta.Duration();
        var parts = new List<string>(3);
        if (abs.Days > 0)
            parts.Add(abs.Days == 1 ? "1 day" : $"{abs.Days} days");
        if (abs.Hours > 0)
            parts.Add(abs.Hours == 1 ? "1 hr" : $"{abs.Hours} hrs");
        if (abs.Minutes > 0 && abs.Days == 0)
            parts.Add(abs.Minutes == 1 ? "1 min" : $"{abs.Minutes} mins");
        if (parts.Count == 0)
            parts.Add("under 1 min");
        return $"{direction} {string.Join(" ", parts)}";
    }
}

internal enum MachineSchedulePlannerCalendarCellKind
{
    Working,
    WorkingWithBreak,
    Break,
    OffDay,
    PublicHoliday,
    Maintenance,
    Breakdown
}

internal sealed record MachineSchedulePlannerDateHeaderState(
    string CssClass,
    string? BadgeText,
    string? CompactBadgeText,
    string Tooltip);

/// <summary>
/// Classifies machine timeline cells and date column headers using the loaded machine shift,
/// off-day, public holiday, break, and maintenance bands so non-working periods stand out clearly.
/// </summary>
internal static class MachineSchedulePlannerCalendarHelper
{
    internal static MachineSchedulePlannerCalendarCellKind ClassifyCell(
        MachineSchedulePlannerResource machine,
        DateTime intervalStart,
        DateTime intervalEnd)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (machine.CalendarBands.Count == 0)
        {
            if (machine.MaintenanceIntervals.Any(x => x.Start < intervalEnd && x.End > intervalStart))
                return MachineSchedulePlannerCalendarCellKind.Maintenance;
            return machine.UsableIntervals.Any(x => x.Start < intervalEnd && x.End > intervalStart)
                ? MachineSchedulePlannerCalendarCellKind.Working
                : MachineSchedulePlannerCalendarCellKind.OffDay;
        }

        var overlapping = machine.CalendarBands
            .Where(x => x.Start < intervalEnd && x.End > intervalStart)
            .ToList();

        if (overlapping.Any(x => x.BandKind == MachineSchedulePlannerCalendarBandKind.Breakdown))
            return MachineSchedulePlannerCalendarCellKind.Breakdown;
        if (overlapping.Any(x => x.BandKind is MachineSchedulePlannerCalendarBandKind.Preventive
            or MachineSchedulePlannerCalendarBandKind.Repair
            or MachineSchedulePlannerCalendarBandKind.Service))
            return MachineSchedulePlannerCalendarCellKind.Maintenance;
        if (overlapping.Any(x => x.BandKind == MachineSchedulePlannerCalendarBandKind.PublicHoliday))
            return MachineSchedulePlannerCalendarCellKind.PublicHoliday;

        var hasWorking = overlapping.Any(x => x.BandKind == MachineSchedulePlannerCalendarBandKind.WorkingShift);
        var hasBreak = overlapping.Any(x => x.BandKind == MachineSchedulePlannerCalendarBandKind.ShiftBreak);
        if (hasWorking)
            return hasBreak
                ? MachineSchedulePlannerCalendarCellKind.WorkingWithBreak
                : MachineSchedulePlannerCalendarCellKind.Working;
        if (hasBreak)
            return MachineSchedulePlannerCalendarCellKind.Break;
        return MachineSchedulePlannerCalendarCellKind.OffDay;
    }

    internal static MachineSchedulePlannerDateHeaderState ResolveDateHeaderState(
        IReadOnlyList<MachineSchedulePlannerResource>? machines,
        DateTime intervalStart,
        DateTime intervalEnd,
        SchedulerTimeScaleUnit unit)
    {
        var baseRange = $"{intervalStart:dddd, dd MMMM yyyy HH:mm} – {intervalEnd:dddd, dd MMMM yyyy HH:mm}";
        if (machines is null || machines.Count == 0 || unit is SchedulerTimeScaleUnit.Week or SchedulerTimeScaleUnit.Month)
            return new("pr-machine-schedule-date-header", null, null, baseRange);

        var workingCount = 0;
        var holidayCount = 0;
        var offCount = 0;
        var breakCount = 0;
        var maintenanceCount = 0;
        var offMachines = new List<string>();
        var holidayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var machine in machines)
        {
            var kind = ClassifyHeaderMachine(machine, intervalStart, intervalEnd, unit, holidayNames);
            switch (kind)
            {
                case MachineSchedulePlannerCalendarCellKind.Working:
                case MachineSchedulePlannerCalendarCellKind.WorkingWithBreak:
                    workingCount++;
                    break;
                case MachineSchedulePlannerCalendarCellKind.PublicHoliday:
                    holidayCount++;
                    offMachines.Add(machine.MachineCode);
                    break;
                case MachineSchedulePlannerCalendarCellKind.Break:
                    breakCount++;
                    break;
                case MachineSchedulePlannerCalendarCellKind.Maintenance:
                case MachineSchedulePlannerCalendarCellKind.Breakdown:
                    maintenanceCount++;
                    offMachines.Add(machine.MachineCode);
                    break;
                default:
                    offCount++;
                    offMachines.Add(machine.MachineCode);
                    break;
            }
        }

        var total = machines.Count;
        var holidaySuffix = holidayNames.Count > 0 ? $" ({string.Join(", ", holidayNames)})" : string.Empty;

        if (workingCount == 0)
        {
            if (holidayCount > 0 && offCount == 0 && maintenanceCount == 0 && breakCount == 0)
            {
                return new(
                    "pr-machine-schedule-date-header pr-machine-schedule-date-header--holiday",
                    "HOLIDAY",
                    "HOL",
                    $"{baseRange} · Public holiday{holidaySuffix} (non-working)");
            }

            if (breakCount > 0 && holidayCount == 0 && offCount == 0 && maintenanceCount == 0)
            {
                return new(
                    "pr-machine-schedule-date-header pr-machine-schedule-date-header--break",
                    "BREAK",
                    "BRK",
                    $"{baseRange} · Scheduled shift break");
            }

            if (maintenanceCount == total)
            {
                return new(
                    "pr-machine-schedule-date-header pr-machine-schedule-date-header--maintenance",
                    "MAINT",
                    "MNT",
                    $"{baseRange} · Planned maintenance / breakdown across visible machines");
            }

            return new(
                "pr-machine-schedule-date-header pr-machine-schedule-date-header--off",
                "OFF",
                "OFF",
                holidayCount > 0
                    ? $"{baseRange} · Non-working across visible machines (off day / holiday{holidaySuffix})"
                    : $"{baseRange} · Machine off day (no scheduled shift on visible machines)");
        }

        if (offMachines.Count > 0)
        {
            var sample = offMachines.Count <= 3
                ? string.Join(", ", offMachines)
                : $"{string.Join(", ", offMachines.Take(3))} +{offMachines.Count - 3} more";
            return new(
                "pr-machine-schedule-date-header pr-machine-schedule-date-header--partial-off",
                "SOME OFF",
                "PART",
                $"{baseRange} · Mixed shift calendar: {workingCount}/{total} working; off/closed on {sample}");
        }

        return new(
            "pr-machine-schedule-date-header pr-machine-schedule-date-header--working",
            null,
            null,
            $"{baseRange} · Working shift");
    }

    private static MachineSchedulePlannerCalendarCellKind ClassifyHeaderMachine(
        MachineSchedulePlannerResource machine,
        DateTime intervalStart,
        DateTime intervalEnd,
        SchedulerTimeScaleUnit unit,
        HashSet<string> holidayNames)
    {
        if (machine.CalendarBands.Count == 0 || unit != SchedulerTimeScaleUnit.Day)
        {
            if (machine.CalendarBands.Count > 0)
            {
                foreach (var band in machine.CalendarBands)
                {
                    if (band.BandKind == MachineSchedulePlannerCalendarBandKind.PublicHoliday
                        && band.Start < intervalEnd && band.End > intervalStart
                        && !string.IsNullOrWhiteSpace(band.HolidayName))
                    {
                        holidayNames.Add(band.HolidayName!);
                    }
                }
            }
            return ClassifyCell(machine, intervalStart, intervalEnd);
        }

        var overlapping = machine.CalendarBands
            .Where(x => x.Start < intervalEnd && x.End > intervalStart)
            .ToList();

        foreach (var band in overlapping)
        {
            if (band.BandKind == MachineSchedulePlannerCalendarBandKind.PublicHoliday
                && !string.IsNullOrWhiteSpace(band.HolidayName))
            {
                holidayNames.Add(band.HolidayName!);
            }
        }

        // On a full-day column header, treat the day as Working if the machine has any active working shift.
        if (overlapping.Any(x => x.BandKind == MachineSchedulePlannerCalendarBandKind.WorkingShift))
            return MachineSchedulePlannerCalendarCellKind.Working;
        if (overlapping.Any(x => x.BandKind == MachineSchedulePlannerCalendarBandKind.PublicHoliday))
            return MachineSchedulePlannerCalendarCellKind.PublicHoliday;
        if (overlapping.Any(x => x.BandKind == MachineSchedulePlannerCalendarBandKind.Breakdown))
            return MachineSchedulePlannerCalendarCellKind.Breakdown;
        if (overlapping.Any(x => x.BandKind is MachineSchedulePlannerCalendarBandKind.Preventive
            or MachineSchedulePlannerCalendarBandKind.Repair
            or MachineSchedulePlannerCalendarBandKind.Service))
            return MachineSchedulePlannerCalendarCellKind.Maintenance;
        if (overlapping.Any(x => x.BandKind == MachineSchedulePlannerCalendarBandKind.ShiftBreak))
            return MachineSchedulePlannerCalendarCellKind.Break;
        return MachineSchedulePlannerCalendarCellKind.OffDay;
    }
}

internal sealed record MachineScheduleWorkOrderLegendEntry(
    int SequenceNo,
    string TagText,
    int PaletteIndex,
    long WorkOrderId,
    long PrimaryOperationId,
    string WorkOrderNo,
    string ProductCode,
    string? ProductDescription,
    decimal PlannedQuantity,
    string OutputUom,
    string MachineSummary,
    DateTime EarliestStart,
    DateTime LatestCompletion,
    bool IsLate,
    bool IsLocked);

internal sealed record MachineSchedulePlannerBarDisplay(
    string PrimaryText,
    string? SecondaryText,
    string? TimeRangeText,
    string? QtyText,
    string? StatusBadgeText,
    bool IsLocked);

/// <summary>
/// Builds the per-period Work Order legend (#1, #2, ...) and formats compact color-coded
/// timeline bars so Week/Month views display short tags (#1) with narrow columns while the
/// legend strip above the grid carries full Work Order and Product captions.
/// </summary>
internal static class MachineSchedulePlannerColorRules
{
    internal const int WorkOrderPaletteSize = 12;

    internal static IReadOnlyList<MachineScheduleWorkOrderLegendEntry> BuildWorkOrderLegend(
        IReadOnlyList<MachineSchedulePlannerJob>? jobs)
    {
        if (jobs is null || jobs.Count == 0)
            return [];

        var groups = jobs
            .Where(x => !string.IsNullOrWhiteSpace(x.WorkOrderNo))
            .GroupBy(x => x.WorkOrderNo.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.PlannedStart).ThenBy(x => x.OperationId).ToList();
                var first = ordered[0];
                var machines = ordered
                    .Select(x => x.MachineCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var machineSummary = machines.Count <= 2
                    ? string.Join(", ", machines)
                    : $"{machines[0]}, {machines[1]} +{machines.Count - 2}";
                return new
                {
                    WorkOrderNo = first.WorkOrderNo.Trim(),
                    First = first,
                    EarliestStart = ordered.Min(x => x.PlannedStart),
                    LatestCompletion = ordered.Max(x => x.PlannedCompletion),
                    MachineSummary = machineSummary,
                    IsLate = ordered.Any(x => x.IsLateToRequiredDate),
                    IsLocked = ordered.All(x => x.IsLocked)
                };
            })
            .OrderBy(x => x.EarliestStart)
            .ThenBy(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<MachineScheduleWorkOrderLegendEntry>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var seq = i + 1;
            result.Add(new MachineScheduleWorkOrderLegendEntry(
                SequenceNo: seq,
                TagText: $"#{seq}",
                PaletteIndex: i % WorkOrderPaletteSize,
                WorkOrderId: g.First.WorkOrderId,
                PrimaryOperationId: g.First.OperationId,
                WorkOrderNo: g.WorkOrderNo,
                ProductCode: g.First.ProductCode,
                ProductDescription: g.First.ProductDescription,
                PlannedQuantity: g.First.PlannedQuantity,
                OutputUom: g.First.OutputUom ?? string.Empty,
                MachineSummary: g.MachineSummary,
                EarliestStart: g.EarliestStart,
                LatestCompletion: g.LatestCompletion,
                IsLate: g.IsLate,
                IsLocked: g.IsLocked));
        }

        return result;
    }

    internal static int GetWorkOrderPaletteIndex(string? workOrderNo, long? workOrderId)
    {
        if (!string.IsNullOrWhiteSpace(workOrderNo))
        {
            var normalized = workOrderNo.Trim().ToUpperInvariant();
            unchecked
            {
                uint hash = 2166136261u;
                foreach (var ch in normalized)
                {
                    hash ^= ch;
                    hash *= 16777619u;
                }

                hash ^= hash >> 16;
                hash *= 0x85ebca6bu;
                hash ^= hash >> 13;
                hash *= 0xc2b2ae35u;
                hash ^= hash >> 16;
                return (int)(hash % WorkOrderPaletteSize);
            }
        }

        if (workOrderId.HasValue)
            return (int)(Math.Abs(workOrderId.Value) % WorkOrderPaletteSize);

        return 0;
    }

    internal static string GetBarCssClass(
        MachineSchedulePlannerAppointment? appt,
        int labelId,
        string? selectedWorkOrderNo,
        long? selectedOperationId,
        int? legendPaletteIndex = null)
    {
        var baseTone = GetBaseToneClass(appt, labelId, legendPaletteIndex);
        var modifiers = new List<string>(7) { "pr-machine-schedule-bar", baseTone };

        var isSpecialBlock = appt?.IsMaintenance == true
            || appt?.IsExternalCapacity == true
            || labelId is MachineSchedulePlannerLabelIds.PreventiveMaintenance
                or MachineSchedulePlannerLabelIds.ReservedOtherBranch;

        if (!isSpecialBlock && appt is not null)
        {
            if (appt.IsLocked || labelId == MachineSchedulePlannerLabelIds.ExecutionLocked)
                modifiers.Add("pr-machine-schedule-bar--is-locked");
            if (appt.IsLate || labelId == MachineSchedulePlannerLabelIds.LateToRequiredDate)
                modifiers.Add("pr-machine-schedule-bar--is-late");
            if (string.Equals(appt.SegmentKind, ProductionMachineScheduleSegmentKinds.UrgentInsert, StringComparison.Ordinal)
                || labelId == MachineSchedulePlannerLabelIds.UrgentInsert)
                modifiers.Add("pr-machine-schedule-bar--is-urgent");
        }

        if (appt?.IsConflict == true)
            modifiers.Add("pr-machine-schedule-bar--conflict");
        if (appt?.IsDraft == true)
            modifiers.Add("pr-machine-schedule-bar--draft");

        var hasActiveSelection = !string.IsNullOrWhiteSpace(selectedWorkOrderNo) || selectedOperationId.HasValue;
        var isSelected = appt is not null && !isSpecialBlock && (
            (!string.IsNullOrWhiteSpace(selectedWorkOrderNo)
             && !string.IsNullOrWhiteSpace(appt.WorkOrderNo)
             && string.Equals(appt.WorkOrderNo, selectedWorkOrderNo, StringComparison.OrdinalIgnoreCase))
            || (selectedOperationId.HasValue && appt.OperationId == selectedOperationId.Value));

        if (isSelected)
            modifiers.Add("pr-machine-schedule-bar--selected");
        else if (hasActiveSelection && !isSpecialBlock)
            modifiers.Add("pr-machine-schedule-bar--dimmed");

        return string.Join(" ", modifiers);
    }

    internal static MachineSchedulePlannerBarDisplay FormatBarDisplay(
        MachineSchedulePlannerAppointment? appt,
        string? fallbackSubject,
        DateTime? fallbackStart,
        DateTime? fallbackEnd,
        PlannerView currentView,
        string? legendTag = null)
    {
        var start = appt?.Start ?? fallbackStart ?? default;
        var end = appt?.End ?? fallbackEnd ?? default;
        var rawSubject = appt?.Subject ?? fallbackSubject ?? string.Empty;

        string primary;
        string? secondary = null;

        if (appt?.IsExternalCapacity == true)
        {
            primary = currentView == PlannerView.Day ? "Reserved · other branch" : "EXT";
        }
        else if (appt?.IsMaintenance == true)
        {
            primary = currentView == PlannerView.Day
                ? (string.IsNullOrWhiteSpace(rawSubject) ? "Maintenance" : rawSubject)
                : "PM";
        }
        else if (!string.IsNullOrWhiteSpace(appt?.WorkOrderNo))
        {
            var woNo = appt.WorkOrderNo.Trim();
            var shortTag = !string.IsNullOrWhiteSpace(legendTag)
                ? legendTag!
                : ShortWorkOrderTag(woNo);

            if (currentView == PlannerView.Day)
            {
                primary = !string.IsNullOrWhiteSpace(legendTag) ? $"{legendTag} · {woNo}" : woNo;
                secondary = ExtractSecondarySubject(rawSubject, woNo);
            }
            else
            {
                // Week and Month views keep bars compact (legend tag + color only) so narrow
                // date columns fit the full schedule without truncated text clutter.
                primary = shortTag;
                secondary = null;
            }
        }
        else
        {
            var split = rawSubject.Split(" · ", 2, StringSplitOptions.TrimEntries);
            primary = split.Length > 0 ? split[0] : rawSubject;
            secondary = currentView == PlannerView.Day && split.Length > 1 ? split[1] : null;
        }

        string? badge = null;
        if (appt is not null)
        {
            if (appt.IsConflict)
                badge = currentView == PlannerView.Day ? "CONFLICT" : "!";
            else if (string.Equals(appt.SegmentKind, ProductionMachineScheduleSegmentKinds.UrgentInsert, StringComparison.Ordinal))
                badge = currentView == PlannerView.Day ? "URGENT" : "URG";
            else if (appt.IsLate)
                badge = currentView == PlannerView.Day ? "LATE" : "!";
            else if (appt.IsDraft)
                badge = currentView == PlannerView.Day ? "DRAFT" : "*";
        }

        string? qtyText = null;
        if (currentView == PlannerView.Day
            && appt?.PlannedOutputQty is { } qty
            && qty > 0m)
        {
            var unit = string.IsNullOrWhiteSpace(appt.OutputUom) ? string.Empty : $" {appt.OutputUom}";
            qtyText = $"{qty:0.##}{unit}";
        }

        var timeRange = currentView == PlannerView.Day
            ? $"{start:HH:mm}–{end:HH:mm}"
            : null;

        return new MachineSchedulePlannerBarDisplay(
            primary,
            secondary,
            timeRange,
            qtyText,
            badge,
            appt?.IsLocked == true);
    }

    private static string ShortWorkOrderTag(string workOrderNo)
    {
        var dashIdx = workOrderNo.LastIndexOf('-');
        if (dashIdx >= 0 && dashIdx + 1 < workOrderNo.Length)
        {
            var suffix = workOrderNo[(dashIdx + 1)..].TrimStart('0');
            return string.IsNullOrEmpty(suffix) ? "#0" : $"#{suffix}";
        }

        return workOrderNo.Length <= 6 ? workOrderNo : workOrderNo[^4..];
    }

    private static string GetBaseToneClass(
        MachineSchedulePlannerAppointment? appt,
        int labelId,
        int? legendPaletteIndex)
    {
        if (appt?.IsMaintenance == true || labelId == MachineSchedulePlannerLabelIds.PreventiveMaintenance)
            return "pr-machine-schedule-bar--maintenance";

        if (appt?.IsExternalCapacity == true || labelId == MachineSchedulePlannerLabelIds.ReservedOtherBranch)
            return "pr-machine-schedule-bar--external";

        if (legendPaletteIndex.HasValue)
            return $"pr-machine-schedule-bar--wo-{Math.Abs(legendPaletteIndex.Value) % WorkOrderPaletteSize}";

        if (!string.IsNullOrWhiteSpace(appt?.WorkOrderNo) || appt?.WorkOrderId is not null)
        {
            var index = GetWorkOrderPaletteIndex(appt.WorkOrderNo, appt.WorkOrderId);
            return $"pr-machine-schedule-bar--wo-{index}";
        }

        return labelId switch
        {
            MachineSchedulePlannerLabelIds.ExecutionLocked => "pr-machine-schedule-bar--locked",
            MachineSchedulePlannerLabelIds.LateToRequiredDate => "pr-machine-schedule-bar--late",
            MachineSchedulePlannerLabelIds.ManualSplit => "pr-machine-schedule-bar--split",
            MachineSchedulePlannerLabelIds.UrgentInsert => "pr-machine-schedule-bar--urgent",
            MachineSchedulePlannerLabelIds.Resume => "pr-machine-schedule-bar--resume",
            MachineSchedulePlannerLabelIds.HoldResume => "pr-machine-schedule-bar--hold-resume",
            MachineSchedulePlannerLabelIds.MaintenanceResume => "pr-machine-schedule-bar--maintenance-resume",
            MachineSchedulePlannerLabelIds.RemainingReplan => "pr-machine-schedule-bar--remaining",
            _ => "pr-machine-schedule-bar--scheduled"
        };
    }

    private static string? ExtractSecondarySubject(string rawSubject, string workOrderNo)
    {
        if (string.IsNullOrWhiteSpace(rawSubject))
            return null;

        var trimmed = rawSubject.Trim();
        if (trimmed.Equals(workOrderNo, StringComparison.OrdinalIgnoreCase))
            return null;

        var prefixWithSeparator = $"{workOrderNo} · ";
        if (trimmed.StartsWith(prefixWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[prefixWithSeparator.Length..].Trim();
            return string.IsNullOrWhiteSpace(rest) ? null : rest;
        }

        return trimmed;
    }
}

