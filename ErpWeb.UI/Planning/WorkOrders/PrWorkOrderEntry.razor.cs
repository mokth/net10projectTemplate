using System.Globalization;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrWorkOrderEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public string? WorkOrderNo { get; set; }

    [Inject] private IProductionWorkOrderService WorkOrders { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private string? _loadedKey;
    private string? _selectedProductDescription;
    private string? _selectedProductUom;
    private string? _savedInputFingerprint;
    private string? _previewInputFingerprint;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool ReleaseConfirmVisible;
    protected bool CancelConfirmVisible;
    protected bool RefreshVisible;
    protected bool MaterialChangeVisible;
    protected string RefreshReason = string.Empty;
    protected ProductionWorkOrderRefreshPreview? RefreshPreviewModel;
    protected ProductionWorkOrderMaterialVm? MaterialChangeSource;
    protected IReadOnlyList<ProductionWorkOrderMaterialAlternateVm> MaterialAlternates { get; set; } = [];
    protected long? SelectedAlternateBomLineId;
    protected bool ConfirmDiscardVisible;
    protected bool ConcurrencyVisible;
    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanRelease;
    protected bool CanCancel;
    protected int ActiveTabIndex;
    protected bool InputsExpanded = true;
    protected long? FocusedRouteUid { get; set; }
    protected long? FocusedOperationUid { get; set; }
    protected string? StatusMessage;
    protected string CancellationReason = string.Empty;
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    protected ProductionWorkOrderDraftRequest Request { get; set; } = NewRequest();
    protected ProductionWorkOrderDetail? DetailModel { get; set; }
    protected ProductionWorkOrderPreview? PreviewModel { get; set; }

    protected IReadOnlyList<Option> DirectionOptions { get; } =
    [
        new(ProductionSchedulingDirections.Forward, "From start"),
        new(ProductionSchedulingDirections.Backward, "From end")
    ];

    protected IReadOnlyList<Option> SourceOptions { get; } =
    [
        new(ProductionSourceTypes.Manual, "Manual")
    ];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool IsDraft => DetailModel is null || DetailModel.Status == ProductionWorkOrderStatuses.Draft;
    protected bool CanEditFields => !IsLoading && IsDraft &&
        ((IsNewMode && CanAdd) || (IsEditMode && CanEdit));
    protected bool CanOpenEdit => IsViewMode && IsDraft && CanEdit && DetailModel is not null;
    protected bool CanReleaseAction => DetailModel is { Status: ProductionWorkOrderStatuses.Draft }
        && CanRelease
        && !HasUnsavedInputChanges;
    protected bool IsCurrentSnapshot =>
        DetailModel?.SnapshotFormatVersion >= ProductionSnapshotFormatVersions.Current;
    protected bool CanCancelAction => DetailModel is { Status: ProductionWorkOrderStatuses.Draft } && CanCancel;
    protected bool IsBackwardSchedule => string.Equals(
        Request.SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.Ordinal);

    protected string PageHeading => IsNewMode ? "New Work Order"
        : IsEditMode ? "Edit Work Order"
        : "View Work Order";
    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";
    protected string CurrentStatus => DetailModel?.Status ?? ProductionWorkOrderStatuses.Draft;
    protected string? CurrentWorkOrderNo => DetailModel?.WorkOrderNo ?? Request.WorkOrderNo;
    protected int? CurrentBomVersion => PreviewModel?.SourceBomVersion
        ?? (RequestMatchesSavedProduct ? DetailModel?.SourceBomVersion : null);
    protected int? CurrentSnapshotRevision => DetailModel?.SnapshotRevision;
    protected DateTime? CurrentSnapshotDate => PreviewModel?.SnapshotAsOfDate ?? DetailModel?.SnapshotAsOfDate;
    protected bool HasUnsavedInputChanges => !IsViewMode
        && !IsLoading
        && _savedInputFingerprint is not null
        && !string.Equals(_savedInputFingerprint, InputFingerprint(), StringComparison.Ordinal);
    protected bool IsPreviewCurrent => PreviewModel is not null
        && string.Equals(_previewInputFingerprint, InputFingerprint(), StringComparison.Ordinal);
    protected bool RequestMatchesSavedProduct => DetailModel is not null
        && string.Equals(Request.ProductCode?.Trim(), DetailModel.ProductCode, StringComparison.OrdinalIgnoreCase);
    protected string CurrentProductDescription => PreviewModel?.ProductDescription
        ?? _selectedProductDescription
        ?? (RequestMatchesSavedProduct ? DetailModel?.ProductDescription : null)
        ?? string.Empty;
    protected string CurrentOutputUom => PreviewModel?.OutputUom
        ?? _selectedProductUom
        ?? (RequestMatchesSavedProduct ? DetailModel?.OutputUom : null)
        ?? string.Empty;
    protected string SnapshotState => PreviewModel is not null
        ? IsPreviewCurrent ? "Unsaved processed preview" : "Preview out of date"
        : HasUnsavedInputChanges ? "Changes not processed"
        : DetailModel is not null ? "Saved server snapshot"
        : "Not processed";
    protected IReadOnlyList<string> SnapshotWarnings
    {
        get
        {
            var warnings = PreviewModel?.Warnings ?? [];
            if (PreviewModel is not null && !IsPreviewCurrent)
            {
                return
                [
                    "Inputs changed after this preview. Run Process Preview again before relying on the displayed requirements.",
                    .. warnings
                ];
            }

            if (PreviewModel is null && HasUnsavedInputChanges)
            {
                return ["Inputs differ from the saved snapshot. Process Preview or Save Draft to recalculate requirements."];
            }

            return warnings;
        }
    }
    protected IReadOnlyList<ProductionWorkOrderRouteStepVm> RouteRows =>
        PreviewModel?.RouteSteps ?? DetailModel?.RouteSteps ?? [];
    protected IReadOnlyList<ProductionWorkOrderMaterialVm> MaterialRows =>
        PreviewModel?.Materials ?? DetailModel?.Materials ?? [];
    protected IReadOnlyList<ProductionWorkOrderOperationVm> OperationRows =>
        PreviewModel?.Operations ?? DetailModel?.Operations ?? [];
    protected IReadOnlyList<ProductionWorkOrderOperationVm> VisibleOperationRows
    {
        get
        {
            if (FocusedRouteUid is long routeId)
            {
                var step = RouteRows.FirstOrDefault(x => x.Uid == routeId);
                if (step is not null)
                {
                    return step.Operations;
                }
            }

            return OperationRows;
        }
    }
    protected IReadOnlyList<ProductionWorkOrderMaterialVm> VisibleMaterialRows
    {
        get
        {
            if (FocusedOperationUid is not long operationId || MaterialRows.All(x => x.WorkOrderOperationId is null))
            {
                return MaterialRows;
            }

            return MaterialRows.Where(x => x.WorkOrderOperationId == operationId).ToList();
        }
    }
    protected IReadOnlyList<ProductionWorkOrderMachineVm> VisibleMachineRows
    {
        get
        {
            // Route focus still scopes via VisibleOperationRows. Operation focus filters materials
            // only — DxGrid auto-focuses the first process, which previously hid every other
            // process's machines/labour and made a multi-process WO look incomplete.
            return VisibleOperationRows
                .SelectMany(o => o.Machines)
                .ToList();
        }
    }
    protected IReadOnlyList<ProductionWorkOrderLabourVm> VisibleLabourRows
    {
        get
        {
            return VisibleOperationRows
                .SelectMany(o => o.Labours.Concat(o.Machines.SelectMany(m => m.Labours)))
                .ToList();
        }
    }
    protected IReadOnlyList<ProductionAuditEventVm> AuditRows => DetailModel?.AuditEvents ?? [];
    protected decimal GoodQty => DetailModel?.GoodQty ?? 0m;
    protected decimal RemainingQty => DetailModel?.RemainingQty ?? Request.PlannedQty;
    protected string SnapshotFormatLabel => DetailModel is null
        ? "—"
        : DetailModel.IsLegacySnapshot
            ? $"Legacy v{DetailModel.SnapshotFormatVersion}"
            : $"v{DetailModel.SnapshotFormatVersion}";
    protected string DirectionLabel => IsBackwardSchedule ? "From end" : "From start";

    protected void OnRouteFocused(GridFocusedRowChangedEventArgs args)
    {
        var next = args.DataItem is ProductionWorkOrderRouteStepVm step ? step.Uid : (long?)null;
        if (next == FocusedRouteUid)
        {
            return;
        }

        FocusedRouteUid = next;
        FocusedOperationUid = null;
    }

    protected void OnOperationFocused(GridFocusedRowChangedEventArgs args)
    {
        FocusedOperationUid = args.DataItem is ProductionWorkOrderOperationVm operation ? operation.Uid : null;
    }

    protected void ClearHierarchyFocus()
    {
        FocusedRouteUid = null;
        FocusedOperationUid = null;
    }

    protected static string DurationLabel(DateTime? start, DateTime? end)
    {
        if (start is null || end is null || end < start)
        {
            return "—";
        }

        return ((end.Value - start.Value).TotalMinutes).ToString("n0", CultureInfo.InvariantCulture) + " min";
    }

    protected static string OptionalQty(decimal? value) => value is null ? "—" : value.Value.ToString("n4", CultureInfo.InvariantCulture);

    protected string BomBaseLabel
    {
        get
        {
            var qty = PreviewModel?.BomBaseQty
                ?? (RequestMatchesSavedProduct ? DetailModel?.BomBaseQty : null);
            var uom = PreviewModel?.BomBaseUom
                ?? (RequestMatchesSavedProduct ? DetailModel?.BomBaseUom : null);
            return qty is null ? "—" : $"{qty.Value:n4} {uom}".Trim();
        }
    }
    protected string SourceLabel => string.IsNullOrWhiteSpace(Request.SourceReference)
        ? Request.SourceType
        : $"{Request.SourceType} · {Request.SourceReference}";
    protected string ShortSnapshotHash
    {
        get
        {
            var hash = PreviewModel?.SnapshotHash ?? DetailModel?.SnapshotHash;
            return string.IsNullOrWhiteSpace(hash) ? "—" : hash[..Math.Min(16, hash.Length)] + "…";
        }
    }
    protected string CreatedLabel => AuditLabel(DetailModel?.CreatedDate, DetailModel?.CreatedBy);
    protected string UpdatedLabel => AuditLabel(DetailModel?.ModifiedDate, DetailModel?.ModifiedBy);
    protected string ReleasedLabel => AuditLabel(DetailModel?.ReleasedDate, DetailModel?.ReleasedBy);

    protected override async Task OnParametersSetAsync()
    {
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Edit);
        CanRelease = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, ProductionPermissionCodes.Release);
        CanCancel = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Cancel);

        var key = $"{Mode}|{WorkOrderNo}";
        if (string.Equals(key, _loadedKey, StringComparison.OrdinalIgnoreCase) && !IsLoading)
        {
            return;
        }

        _loadedKey = key;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        PreviewModel = null;
        _previewInputFingerprint = null;
        try
        {
            if (IsNewMode)
            {
                DetailModel = null;
                Request = NewRequest();
                _selectedProductDescription = null;
                _selectedProductUom = null;
                _savedInputFingerprint = InputFingerprint();
                return;
            }

            if (string.IsNullOrWhiteSpace(WorkOrderNo))
            {
                ErrorMessage = "Work Order number is required.";
                return;
            }

            var result = await WorkOrders.GetAsync(WorkOrderNo);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Work Order not found.";
                return;
            }

            ApplyDetail(result.Data);
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected Task OnProductCodeChanged(string? value)
    {
        Request.ProductCode = value ?? string.Empty;
        PreviewModel = null;
        _previewInputFingerprint = null;
        if (!string.Equals(value?.Trim(), DetailModel?.ProductCode, StringComparison.OrdinalIgnoreCase))
        {
            _selectedProductDescription = null;
            _selectedProductUom = null;
        }

        return Task.CompletedTask;
    }

    protected async Task OnProductSelectedAsync(IvStockMasterLookupRow row)
    {
        Request.ProductCode = row.ICode;
        _selectedProductDescription = row.IDesc;
        _selectedProductUom = row.StdUom;
        PreviewModel = null;
        _previewInputFingerprint = null;
        await InvokeAsync(StateHasChanged);
    }

    protected async Task ProcessPreviewAsync()
    {
        IsSubmitting = true;
        ClearFeedback();
        try
        {
            PrepareRequestIdentity();
            var result = await WorkOrders.ProcessPreviewAsync(Request);
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            PreviewModel = result.Data;
            Request.PlannedQty = result.Data.PlannedQty;
            Request.PlannedStartDate = result.Data.PlannedStartDate;
            Request.PlannedCompletionDate = result.Data.PlannedCompletionDate;
            Request.SchedulingDirection = result.Data.SchedulingDirection;
            _previewInputFingerprint = InputFingerprint();
            _selectedProductDescription = result.Data.ProductDescription;
            _selectedProductUom = result.Data.OutputUom;
            StatusMessage = $"Preview calculated from Product Definition V{result.Data.SourceBomVersion} using the current Work Order quantity and scheduling rules.";
            ActiveTabIndex = 1;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task SaveDraftAsync()
    {
        IsSubmitting = true;
        ClearFeedback();
        try
        {
            PrepareRequestIdentity();
            var wasNew = IsNewMode;
            var result = wasNew
                ? await WorkOrders.CreateDraftAsync(Request)
                : IsCurrentSnapshot
                    ? await WorkOrders.UpdateDraftHeaderAsync(new ProductionWorkOrderHeaderUpdate
                    {
                        WorkOrderNo = DetailModel!.WorkOrderNo,
                        PlannedQty = Request.PlannedQty,
                        SchedulingDirection = Request.SchedulingDirection,
                        ScheduleAnchorDateTime = GetRequestedScheduleAnchor(),
                        SourceReference = Request.SourceReference,
                        Remark = Request.Remark,
                        RowVersion = DetailModel.RowVersion
                    })
                    : await WorkOrders.SaveDraftAsync(Request);
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            ApplyDetail(result.Data);
            PreviewModel = null;
            StatusMessage = wasNew
                ? $"Work Order {result.Data.WorkOrderNo} created as Draft."
                : $"Draft {result.Data.WorkOrderNo} saved.";

            if (wasNew)
            {
                Mode = "edit";
                WorkOrderNo = result.Data.WorkOrderNo;
                _loadedKey = $"edit|{WorkOrderNo}";
                Navigation.NavigateTo($"/planning/work-orders/edit/{Uri.EscapeDataString(WorkOrderNo)}", replace: true);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task RecalculateScheduleAsync()
    {
        if (DetailModel is null)
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var result = await WorkOrders.RecalculateDraftScheduleAsync(new ProductionWorkOrderRecalculateRequest
            {
                WorkOrderNo = DetailModel.WorkOrderNo,
                RowVersion = DetailModel.RowVersion,
                SnapshotRevision = DetailModel.SnapshotRevision,
                SnapshotHash = DetailModel.SnapshotHash
            });
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            ApplyDetail(result.Data);
            StatusMessage = $"Schedule recalculated for {result.Data.WorkOrderNo}.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OpenRefreshAsync()
    {
        if (DetailModel is null)
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var result = await WorkOrders.PreviewRefreshFromDefinitionAsync(DetailModel.WorkOrderNo);
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            RefreshPreviewModel = result.Data;
            RefreshReason = string.Empty;
            RefreshVisible = true;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ConfirmRefreshAsync()
    {
        if (DetailModel is null || RefreshPreviewModel is null)
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var result = await WorkOrders.RefreshDraftFromDefinitionAsync(new ProductionWorkOrderRefreshConfirm
            {
                WorkOrderNo = DetailModel.WorkOrderNo,
                RowVersion = RefreshPreviewModel.RowVersion,
                SourceProductDefinitionRevisionId = RefreshPreviewModel.SourceProductDefinitionRevisionId ?? 0,
                DefinitionSourceHashVersion = RefreshPreviewModel.DefinitionSourceHashVersion,
                DefinitionSourceHash = RefreshPreviewModel.DefinitionSourceHash,
                Reason = RefreshReason
            });
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            ApplyDetail(result.Data);
            RefreshVisible = false;
            StatusMessage = $"Draft {result.Data.WorkOrderNo} refreshed from its Product Definition.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected bool CanSelectMachine(ProductionWorkOrderMachineVm machine) =>
        CanEditFields
        && IsCurrentSnapshot
        && !machine.IsSelected
        && DetailModel is not null
        && !IsSubmitting;

    protected async Task SelectMachineAsync(ProductionWorkOrderMachineVm machine)
    {
        if (DetailModel is null || !CanSelectMachine(machine))
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var result = await WorkOrders.SelectDraftMachineAsync(new ProductionWorkOrderMachineSelectRequest
            {
                WorkOrderNo = DetailModel.WorkOrderNo,
                RowVersion = DetailModel.RowVersion,
                SnapshotRevision = DetailModel.SnapshotRevision,
                SnapshotHash = DetailModel.SnapshotHash,
                WorkOrderOperationId = machine.WorkOrderOperationId,
                WorkOrderMachineId = machine.Uid
            });
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            var focusedOperation = FocusedOperationUid;
            var focusedRoute = FocusedRouteUid;
            ApplyDetail(result.Data);
            PreviewModel = null;
            FocusedOperationUid = focusedOperation;
            FocusedRouteUid = focusedRoute;
            StatusMessage = $"Selected machine {machine.MachineCode} for process {machine.OperationCode}.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected bool CanChangeMaterial(ProductionWorkOrderMaterialVm material) =>
        CanEditFields
        && IsCurrentSnapshot
        && DetailModel is not null
        && !string.IsNullOrWhiteSpace(material.AlternateGroupCode)
        && !IsSubmitting;

    protected async Task OpenMaterialChangeAsync(ProductionWorkOrderMaterialVm material)
    {
        if (DetailModel is null || !CanChangeMaterial(material))
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var result = await WorkOrders.GetDraftMaterialAlternatesAsync(DetailModel.WorkOrderNo, material.Uid);
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            MaterialChangeSource = material;
            MaterialAlternates = result.Data;
            SelectedAlternateBomLineId = result.Data.FirstOrDefault()?.SourceBomLineId;
            MaterialChangeVisible = true;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ConfirmMaterialChangeAsync()
    {
        if (DetailModel is null || MaterialChangeSource is null || SelectedAlternateBomLineId is not > 0)
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var oldCode = MaterialChangeSource.ComponentCode;
            var result = await WorkOrders.SubstituteDraftMaterialAsync(new ProductionWorkOrderMaterialSubstituteRequest
            {
                WorkOrderNo = DetailModel.WorkOrderNo,
                RowVersion = DetailModel.RowVersion,
                SnapshotRevision = DetailModel.SnapshotRevision,
                SnapshotHash = DetailModel.SnapshotHash,
                WorkOrderMaterialId = MaterialChangeSource.Uid,
                ReplacementSourceBomLineId = SelectedAlternateBomLineId.Value
            });
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            var focusedOperation = FocusedOperationUid;
            var focusedRoute = FocusedRouteUid;
            ApplyDetail(result.Data);
            PreviewModel = null;
            MaterialChangeVisible = false;
            FocusedOperationUid = focusedOperation;
            FocusedRouteUid = focusedRoute;
            var replacement = result.Data.Materials.FirstOrDefault(m => m.Uid == MaterialChangeSource.Uid)
                ?? result.Data.Materials.FirstOrDefault(m =>
                    string.Equals(m.AlternateGroupCode, MaterialChangeSource.AlternateGroupCode, StringComparison.Ordinal));
            StatusMessage = replacement is null
                ? $"Material {oldCode} changed."
                : $"Material {oldCode} → {replacement.ComponentCode}.";
            MaterialChangeSource = null;
            MaterialAlternates = [];
            SelectedAlternateBomLineId = null;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ReleaseAsync()
    {
        if (DetailModel is null)
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var result = IsCurrentSnapshot
                ? await WorkOrders.ReleaseCurrentAsync(new ProductionWorkOrderReleaseRequest
                {
                    WorkOrderNo = DetailModel.WorkOrderNo,
                    RowVersion = DetailModel.RowVersion,
                    SnapshotRevision = DetailModel.SnapshotRevision,
                    SnapshotHash = DetailModel.SnapshotHash,
                    SourceProductDefinitionRevisionId = DetailModel.SourceProductDefinitionRevisionId
                })
                : await WorkOrders.ReleaseAsync(DetailModel.WorkOrderNo, DetailModel.RowVersion);
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            ApplyDetail(result.Data);
            ReleaseConfirmVisible = false;
            StatusMessage = $"Work Order {result.Data.WorkOrderNo} released. No inventory transaction was posted.";
            SwitchToViewRoute(result.Data.WorkOrderNo);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OpenCancelPopup()
    {
        CancellationReason = string.Empty;
        ValidationErrors.Remove("CancellationReason");
        CancelConfirmVisible = true;
    }

    protected async Task CancelDraftAsync()
    {
        if (DetailModel is null)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        ValidationErrors.Clear();
        try
        {
            var result = await WorkOrders.CancelDraftAsync(
                DetailModel.WorkOrderNo,
                DetailModel.RowVersion,
                CancellationReason);
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            ApplyDetail(result.Data);
            CancelConfirmVisible = false;
            StatusMessage = $"Draft {result.Data.WorkOrderNo} cancelled.";
            SwitchToViewRoute(result.Data.WorkOrderNo);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        await LoadAsync();
    }

    protected void BackToList()
    {
        if (HasUnsavedInputChanges)
        {
            ConfirmDiscardVisible = true;
            return;
        }

        Navigation.NavigateTo("/planning/work-orders");
    }

    protected void ConfirmDiscard()
    {
        ConfirmDiscardVisible = false;
        Navigation.NavigateTo("/planning/work-orders");
    }
    protected void OpenEdit()
    {
        if (DetailModel is not null)
        {
            Navigation.NavigateTo($"/planning/work-orders/edit/{Uri.EscapeDataString(DetailModel.WorkOrderNo)}");
        }
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;
    protected string? FieldError(string field) => ValidationErrors.GetValueOrDefault(field);

    private void SwitchToViewRoute(string workOrderNo)
    {
        Mode = "view";
        WorkOrderNo = workOrderNo;
        _loadedKey = $"view|{workOrderNo}";
        Navigation.NavigateTo($"/planning/work-orders/view/{Uri.EscapeDataString(workOrderNo)}", replace: true);
    }

    protected RenderFragment Kpi(string label, string value, string? note) => builder =>
    {
        builder.OpenElement(0, "article");
        builder.AddAttribute(1, "class", "pwo-kpi");
        builder.OpenElement(2, "span");
        builder.AddContent(3, label);
        builder.CloseElement();
        builder.OpenElement(4, "strong");
        builder.AddContent(5, value);
        builder.CloseElement();
        if (!string.IsNullOrWhiteSpace(note))
        {
            builder.OpenElement(6, "small");
            builder.AddContent(7, note);
            builder.CloseElement();
        }
        builder.CloseElement();
    };

    protected RenderFragment Detail(string label, string? value) => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "pwo-detail");
        builder.OpenElement(2, "span");
        builder.AddContent(3, label);
        builder.CloseElement();
        builder.OpenElement(4, "strong");
        builder.AddContent(5, string.IsNullOrWhiteSpace(value) ? "—" : value);
        builder.CloseElement();
        builder.CloseElement();
    };

    protected RenderFragment Gate(string label, string message) => builder =>
    {
        builder.OpenElement(0, "article");
        builder.AddAttribute(1, "class", "pwo-gate");
        builder.OpenElement(2, "i");
        builder.AddAttribute(3, "class", "fa-solid fa-lock");
        builder.CloseElement();
        builder.OpenElement(4, "div");
        builder.OpenElement(5, "strong");
        builder.AddContent(6, label);
        builder.CloseElement();
        builder.OpenElement(7, "span");
        builder.AddContent(8, message);
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
    };

    private void PrepareRequestIdentity()
    {
        Request.WorkOrderNo = DetailModel?.WorkOrderNo;
        Request.RowVersion = DetailModel?.RowVersion;
    }

    private DateTime GetRequestedScheduleAnchor()
    {
        var plannerDate = IsBackwardSchedule
            ? Request.PlannedCompletionDate
            : Request.PlannedStartDate;
        return ProductionSchedulingDirections.NormalizePlannerDateAnchor(
            plannerDate, Request.SchedulingDirection);
    }

    private void ApplyDetail(ProductionWorkOrderDetail detail)
    {
        DetailModel = detail;
        Request = new ProductionWorkOrderDraftRequest
        {
            WorkOrderNo = detail.WorkOrderNo,
            ProductCode = detail.ProductCode,
            PlannedQty = detail.PlannedQty,
            SnapshotAsOfDate = detail.SnapshotAsOfDate,
            PlannedStartDate = detail.PlannedStartDate,
            PlannedCompletionDate = detail.PlannedCompletionDate,
            SchedulingDirection = detail.SchedulingDirection,
            SourceType = detail.SourceType,
            SourceReference = detail.SourceReference,
            Remark = detail.Remark,
            RowVersion = detail.RowVersion
        };
        _selectedProductDescription = detail.ProductDescription;
        _selectedProductUom = detail.OutputUom;
        _savedInputFingerprint = InputFingerprint();
        _previewInputFingerprint = null;
    }

    private void ApplyFailure<T>(IvMasterOperationResult<T> result)
    {
        ErrorMessage = result.Message ?? "The operation failed.";
        ValidationErrors = new Dictionary<string, string>(result.ValidationErrors, StringComparer.OrdinalIgnoreCase);
        if (result.ErrorCode == IvMasterErrorCode.Concurrency)
        {
            ConcurrencyVisible = true;
        }
    }

    private void ClearFeedback()
    {
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
    }

    private static ProductionWorkOrderDraftRequest NewRequest()
    {
        var today = DateTime.Today;
        return new ProductionWorkOrderDraftRequest
        {
            SnapshotAsOfDate = today,
            PlannedStartDate = today,
            PlannedCompletionDate = today,
            PlannedQty = 1m,
            SchedulingDirection = ProductionSchedulingDirections.Forward,
            SourceType = ProductionSourceTypes.Manual
        };
    }

    private static string AuditLabel(DateTime? date, string? user) =>
        date is null ? "—" : $"{date.Value.ToLocalTime():dd MMM yyyy HH:mm} · {user}";

    private string InputFingerprint() => string.Join('\u001f',
        (Request.ProductCode ?? string.Empty).Trim().ToUpperInvariant(),
        IvQty.Round(Request.PlannedQty).ToString("0.0000", CultureInfo.InvariantCulture),
        Request.SnapshotAsOfDate.Date.Ticks.ToString(CultureInfo.InvariantCulture),
        (Request.SchedulingDirection ?? string.Empty).Trim().ToUpperInvariant(),
        (IsBackwardSchedule ? Request.PlannedCompletionDate : Request.PlannedStartDate)
            .Date.Ticks.ToString(CultureInfo.InvariantCulture),
        (Request.SourceType ?? string.Empty).Trim().ToUpperInvariant(),
        (Request.SourceReference ?? string.Empty).Trim(),
        (Request.Remark ?? string.Empty).Trim());

    protected sealed record Option(string Key, string Name);
}

