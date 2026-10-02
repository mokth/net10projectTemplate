using System.Globalization;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
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
    [Inject] private IPrProductDefService ProductDefs { get; set; } = default!;
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
    protected bool ReopenConfirmVisible;
    protected bool HelpVisible;
    protected bool RefreshVisible;
    protected bool ChangeDefinitionVisible;
    protected bool MaterialChangeVisible;
    protected string RefreshReason = string.Empty;
    protected string ChangeDefinitionReason = string.Empty;
    protected string? SelectedChangeDefinitionCode;
    protected ProductionWorkOrderRefreshPreview? RefreshPreviewModel;
    protected ProductionWorkOrderChangeDefinitionPreview? ChangeDefinitionPreviewModel;
    protected ProductionWorkOrderMaterialVm? MaterialChangeSource;
    protected IReadOnlyList<ProductionWorkOrderMaterialAlternateVm> MaterialAlternates { get; set; } = [];
    protected IReadOnlyList<DefinitionOption> DefinitionOptions { get; set; } = [];
    protected IReadOnlyList<DefinitionOption> ChangeDefinitionOptions { get; set; } = [];
    protected long? SelectedAlternateBomLineId;
    protected bool ConfirmDiscardVisible;
    protected bool ConcurrencyVisible;
    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanRelease;
    protected bool CanCancel;
    protected bool CanReopen;
    protected bool CanAccessMaterialIssue;
    protected bool CanAddMaterialIssue;
    protected int ActiveTabIndex;
    protected bool InputsExpanded = true;
    protected long? FocusedRouteUid { get; set; }
    protected long? FocusedOperationUid { get; set; }
    protected string? StatusMessage;
    protected string CancellationReason = string.Empty;
    protected string ReopenReason = string.Empty;
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
    /// <summary>Format 1/2 drafts must Refresh before any input/structural edits.</summary>
    protected bool NeedsDefinitionUpgrade =>
        DetailModel is not null && IsDraft && !IsCurrentSnapshot;
    protected bool CanEditInputs => CanEditFields && !NeedsDefinitionUpgrade;
    protected bool CanEditDefinition => IsNewMode && CanEditInputs;
    protected bool CanOpenEdit => IsViewMode && IsDraft && CanEdit && DetailModel is not null;
    protected bool CanReopenForEdit => IsViewMode
        && DetailModel is { Status: ProductionWorkOrderStatuses.Released }
        && CanEdit
        && CanReopen;
    protected bool CanReleaseAction => DetailModel is { Status: ProductionWorkOrderStatuses.Draft }
        && CanRelease
        && IsCurrentSnapshot
        && !HasUnsavedInputChanges;
    protected bool IsCurrentSnapshot =>
        DetailModel?.SnapshotFormatVersion >= ProductionSnapshotFormatVersions.Current;
    protected bool CanCancelAction => DetailModel is { Status: ProductionWorkOrderStatuses.Draft } && CanCancel;
    protected bool CanIssueMaterials => DetailModel is not null
        && DetailModel.Status is ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress
        && CanAccessMaterialIssue;
    protected bool CanStartMaterialIssue => CanIssueMaterials && CanAddMaterialIssue && !IsSubmitting;
    protected bool CanSaveDraft => CanEditInputs && !IsSubmitting;
    protected bool CanRefreshDefinition => CanEditFields
        && DetailModel is not null
        && !HasUnsavedInputChanges
        && !IsSubmitting;
    protected bool CanChangeDefinition => CanEditFields
        && IsCurrentSnapshot
        && DetailModel is not null
        && !HasUnsavedInputChanges
        && !IsSubmitting;
    protected bool IsBackwardSchedule => string.Equals(
        Request.SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.Ordinal);

    protected string PageHeading => IsNewMode ? "New Work Order"
        : IsEditMode ? "Edit Work Order"
        : "View Work Order";
    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";
    protected string CurrentStatus => DetailModel?.Status ?? ProductionWorkOrderStatuses.Draft;
    protected string? CurrentWorkOrderNo => DetailModel?.WorkOrderNo ?? Request.WorkOrderNo;
    protected int? CurrentBomVersion => PreviewModel?.SourceBomVersion
        ?? (RequestMatchesSavedIdentity ? DetailModel?.SourceBomVersion : null);
    protected int? CurrentSnapshotRevision => DetailModel?.SnapshotRevision;
    protected string CurrentDefinitionCode => PreviewModel?.SourceDefinitionCode
        ?? DetailModel?.SourceDefinitionCode
        ?? Request.DefinitionCode
        ?? string.Empty;
    protected string? CurrentDefinitionName => PreviewModel?.SourceDefinitionName
        ?? DetailModel?.SourceDefinitionName
        ?? DefinitionOptions.FirstOrDefault(x =>
            string.Equals(x.Code, CurrentDefinitionCode, StringComparison.OrdinalIgnoreCase))?.Name;
    protected string DefinitionHeroChip
    {
        get
        {
            var code = CurrentDefinitionCode;
            if (string.IsNullOrWhiteSpace(code))
            {
                return string.Empty;
            }

            return CurrentBomVersion is int version ? $"{code} · V{version}" : code;
        }
    }
    protected string DefinitionDisplayLabel
    {
        get
        {
            var code = CurrentDefinitionCode;
            if (string.IsNullOrWhiteSpace(code))
            {
                return "—";
            }

            return string.IsNullOrWhiteSpace(CurrentDefinitionName) ? code : $"{code} — {CurrentDefinitionName}";
        }
    }
    protected bool HasUnsavedInputChanges => !IsViewMode
        && !IsLoading
        && _savedInputFingerprint is not null
        && !string.Equals(_savedInputFingerprint, InputFingerprint(), StringComparison.Ordinal);
    protected bool IsPreviewCurrent => PreviewModel is not null
        && string.Equals(_previewInputFingerprint, InputFingerprint(), StringComparison.Ordinal);
    protected bool RequestMatchesSavedProduct => DetailModel is not null
        && string.Equals(Request.ProductCode?.Trim(), DetailModel.ProductCode, StringComparison.OrdinalIgnoreCase);
    protected bool RequestMatchesSavedIdentity => RequestMatchesSavedProduct
        && string.Equals(
            (Request.DefinitionCode ?? string.Empty).Trim(),
            DetailModel?.SourceDefinitionCode ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    protected string CurrentProductDescription => PreviewModel?.ProductDescription
        ?? _selectedProductDescription
        ?? (RequestMatchesSavedIdentity ? DetailModel?.ProductDescription : null)
        ?? string.Empty;
    protected string CurrentOutputUom => PreviewModel?.OutputUom
        ?? _selectedProductUom
        ?? (RequestMatchesSavedIdentity ? DetailModel?.OutputUom : null)
        ?? string.Empty;
    protected string SnapshotState => PreviewModel is not null
        ? IsPreviewCurrent ? "Unsaved processed preview" : "Preview out of date"
        : NeedsDefinitionUpgrade ? "Refresh required (older snapshot format)"
        : HasUnsavedInputChanges ? "Changes not processed"
        : DetailModel is not null ? "Saved server snapshot"
        : "Not processed";
    protected IReadOnlyList<string> SnapshotWarnings
    {
        get
        {
            if (NeedsDefinitionUpgrade)
            {
                return
                [
                    $"This Draft uses snapshot format v{DetailModel!.SnapshotFormatVersion}. Use Refresh Definition to upgrade to the current format before editing structure, changing definition, or releasing."
                ];
            }

            var warnings = PreviewModel?.Warnings ?? [];
            if (PreviewModel is not null && !IsPreviewCurrent)
            {
                return
                [
                    "Inputs changed after this preview. Run Calculate Preview again before relying on the displayed requirements.",
                    .. warnings
                ];
            }

            if (PreviewModel is null && HasUnsavedInputChanges)
            {
                return ["Inputs differ from the saved snapshot. Use Calculate Preview to inspect the changes, or Save Draft to apply them."];
            }

            return warnings;
        }
    }
    protected IReadOnlyList<ProductionWorkOrderRouteStepVm> RouteRows =>
        PreviewModel?.RouteSteps ?? DetailModel?.RouteSteps ?? [];
    protected IReadOnlyList<ProductionWorkOrderMaterialVm> MaterialRows =>
        PreviewModel?.Materials ?? DetailModel?.Materials ?? [];
    protected IReadOnlyList<ProductionWorkOrderMaterialVm> ExecutionMaterialRows => DetailModel?.Materials ?? [];
    protected decimal ExecutionRequiredQty => IvQty.Round(ExecutionMaterialRows.Sum(x => x.RequiredQty));
    protected decimal ExecutionIssuedQty => IvQty.Round(ExecutionMaterialRows.Sum(x => x.IssuedQty));
    protected decimal ExecutionReturnedQty => IvQty.Round(ExecutionMaterialRows.Sum(x => x.ReturnedQty));
    protected decimal ExecutionNetIssuedQty => IvQty.Round(ExecutionMaterialRows.Sum(x => x.IssuedQty - x.ReturnedQty));
    protected decimal ExecutionConsumedQty => IvQty.Round(ExecutionMaterialRows.Sum(x => x.ConsumedQty));
    protected decimal ExecutionOutstandingQty => IvQty.Round(ExecutionMaterialRows.Sum(x => x.OpenRequirementQty));
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
                ?? (RequestMatchesSavedIdentity ? DetailModel?.BomBaseQty : null);
            var uom = PreviewModel?.BomBaseUom
                ?? (RequestMatchesSavedIdentity ? DetailModel?.BomBaseUom : null);
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
        CanReopen = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Reopen);
        CanAccessMaterialIssue = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access);
        CanAddMaterialIssue = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Add);

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
                DefinitionOptions = [];
                ChangeDefinitionOptions = [];
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
            await LoadDefinitionOptionsAsync(result.Data.ProductCode, selectDefaultWhenEmpty: false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected async Task OnProductCodeChanged(string? value)
    {
        Request.ProductCode = value ?? string.Empty;
        PreviewModel = null;
        _previewInputFingerprint = null;
        if (!string.Equals(value?.Trim(), DetailModel?.ProductCode, StringComparison.OrdinalIgnoreCase))
        {
            _selectedProductDescription = null;
            _selectedProductUom = null;
        }

        if (IsNewMode)
        {
            await LoadDefinitionOptionsAsync(Request.ProductCode, selectDefaultWhenEmpty: true);
        }
    }

    protected async Task OnProductSelectedAsync(IvStockMasterLookupRow row)
    {
        Request.ProductCode = row.ICode;
        _selectedProductDescription = row.IDesc;
        _selectedProductUom = row.StdUom;
        PreviewModel = null;
        _previewInputFingerprint = null;
        if (IsNewMode)
        {
            await LoadDefinitionOptionsAsync(row.ICode, selectDefaultWhenEmpty: true);
        }

        await InvokeAsync(StateHasChanged);
    }

    protected Task OnDefinitionCodeChanged(string? value)
    {
        if (!CanEditDefinition)
        {
            return Task.CompletedTask;
        }

        Request.DefinitionCode = value ?? string.Empty;
        PreviewModel = null;
        _previewInputFingerprint = null;
        return Task.CompletedTask;
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
            if (!string.IsNullOrWhiteSpace(result.Data.SourceDefinitionCode))
            {
                Request.DefinitionCode = result.Data.SourceDefinitionCode;
            }
            _previewInputFingerprint = InputFingerprint();
            _selectedProductDescription = result.Data.ProductDescription;
            _selectedProductUom = result.Data.OutputUom;
            StatusMessage = $"Preview calculated from Product Definition {result.Data.SourceDefinitionCode} V{result.Data.SourceBomVersion} using the current Work Order quantity and scheduling rules.";
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
            if (NeedsDefinitionUpgrade)
            {
                ErrorMessage =
                    "This Draft uses an older snapshot format. Refresh Definition before saving or making structural edits.";
                return;
            }

            PrepareRequestIdentity();
            var wasNew = IsNewMode;
            IvMasterOperationResult<ProductionWorkOrderDetail> result;
            if (wasNew)
            {
                result = await WorkOrders.CreateDraftAsync(Request);
            }
            else if (IsCurrentSnapshot)
            {
                result = await WorkOrders.UpdateDraftHeaderAsync(new ProductionWorkOrderHeaderUpdate
                {
                    WorkOrderNo = DetailModel!.WorkOrderNo,
                    PlannedQty = Request.PlannedQty,
                    SchedulingDirection = Request.SchedulingDirection,
                    ScheduleAnchorDateTime = GetRequestedScheduleAnchor(),
                    SourceReference = Request.SourceReference,
                    Remark = Request.Remark,
                    RowVersion = DetailModel.RowVersion
                });
            }
            else
            {
                ErrorMessage =
                    "This Draft uses an older snapshot format. Refresh Definition before saving or making structural edits.";
                return;
            }

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

    protected async Task OpenChangeDefinitionAsync()
    {
        if (DetailModel is null || !CanChangeDefinition)
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            await LoadDefinitionOptionsAsync(DetailModel.ProductCode, selectDefaultWhenEmpty: false);
            ChangeDefinitionOptions = DefinitionOptions
                .Where(x => !string.Equals(x.Code, DetailModel.SourceDefinitionCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (ChangeDefinitionOptions.Count == 0)
            {
                ErrorMessage = "No other ACTIVE Product Definitions are available for this product.";
                return;
            }

            SelectedChangeDefinitionCode = ChangeDefinitionOptions[0].Code;
            ChangeDefinitionPreviewModel = null;
            ChangeDefinitionReason = string.Empty;
            ChangeDefinitionVisible = true;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OnChangeDefinitionTargetChanged(string? value)
    {
        SelectedChangeDefinitionCode = value;
        ChangeDefinitionPreviewModel = null;
    }

    protected async Task PreviewChangeDefinitionAsync()
    {
        if (DetailModel is null || string.IsNullOrWhiteSpace(SelectedChangeDefinitionCode))
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var result = await WorkOrders.PreviewChangeDefinitionAsync(
                DetailModel.WorkOrderNo, SelectedChangeDefinitionCode);
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            ChangeDefinitionPreviewModel = result.Data;
            StatusMessage =
                $"Change Definition preview ready for {result.Data.TargetDefinitionCode} V{result.Data.TargetSourceBomVersion}.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ConfirmChangeDefinitionAsync()
    {
        if (DetailModel is null || ChangeDefinitionPreviewModel is null)
        {
            return;
        }

        IsSubmitting = true;
        ClearFeedback();
        try
        {
            var result = await WorkOrders.ConfirmChangeDefinitionAsync(new ProductionWorkOrderChangeDefinitionConfirm
            {
                WorkOrderNo = DetailModel.WorkOrderNo,
                RowVersion = ChangeDefinitionPreviewModel.RowVersion,
                TargetDefinitionCode = ChangeDefinitionPreviewModel.TargetDefinitionCode,
                TargetSourceProductDefinitionRevisionId =
                    ChangeDefinitionPreviewModel.TargetSourceProductDefinitionRevisionId ?? 0,
                TargetDefinitionSourceHashVersion = ChangeDefinitionPreviewModel.TargetDefinitionSourceHashVersion,
                TargetDefinitionSourceHash = ChangeDefinitionPreviewModel.TargetDefinitionSourceHash,
                Reason = ChangeDefinitionReason
            });
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            ApplyDetail(result.Data);
            PreviewModel = null;
            ChangeDefinitionVisible = false;
            StatusMessage =
                $"Draft {result.Data.WorkOrderNo} now uses Product Definition {result.Data.SourceDefinitionCode} V{result.Data.SourceBomVersion}.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected bool CanSelectMachine(ProductionWorkOrderMachineVm machine) =>
        CanEditInputs
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
        CanEditInputs
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

    protected void OpenReopenPopup()
    {
        ReopenReason = string.Empty;
        ValidationErrors.Remove("Reason");
        ReopenConfirmVisible = true;
    }

    protected async Task ReopenForEditAsync()
    {
        if (DetailModel is null || IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        ValidationErrors.Clear();
        try
        {
            var result = await WorkOrders.ReopenForEditAsync(new ProductionWorkOrderReopenRequest
            {
                WorkOrderNo = DetailModel.WorkOrderNo,
                RowVersion = DetailModel.RowVersion,
                Reason = ReopenReason
            });
            if (!result.Succeeded || result.Data is null)
            {
                ApplyFailure(result);
                return;
            }

            ReopenConfirmVisible = false;
            ReopenReason = string.Empty;
            Navigation.NavigateTo(
                $"/planning/work-orders/edit/{Uri.EscapeDataString(result.Data.WorkOrderNo)}");
        }
        finally
        {
            IsSubmitting = false;
        }
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

    protected void OpenMaterialIssue()
    {
        if (DetailModel is null || !CanStartMaterialIssue)
        {
            return;
        }

        Navigation.NavigateTo($"/planning/material-issues/new/{Uri.EscapeDataString(DetailModel.WorkOrderNo)}");
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
            DefinitionCode = detail.SourceDefinitionCode,
            PlannedQty = detail.PlannedQty,
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
        ChangeDefinitionPreviewModel = null;
        SelectedChangeDefinitionCode = null;
    }

    private async Task LoadDefinitionOptionsAsync(string? productCode, bool selectDefaultWhenEmpty)
    {
        var code = (productCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            DefinitionOptions = [];
            if (selectDefaultWhenEmpty)
            {
                Request.DefinitionCode = PrProductDefinitionCodes.Standard;
            }

            return;
        }

        var result = await ProductDefs.ListActiveDefinitionsAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            DefinitionOptions = [];
            if (selectDefaultWhenEmpty && string.IsNullOrWhiteSpace(Request.DefinitionCode))
            {
                Request.DefinitionCode = PrProductDefinitionCodes.Standard;
            }

            if (!result.Succeeded && !string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }

            return;
        }

        DefinitionOptions = result.Data
            .Select(x => new DefinitionOption(
                x.DefinitionCode,
                x.DefinitionName,
                x.Version,
                x.IsDefaultDefinition,
                FormatDefinitionOptionLabel(x)))
            .ToList();

        if (!selectDefaultWhenEmpty)
        {
            return;
        }

        var selected = DefinitionOptions.FirstOrDefault(x => x.IsDefault)
            ?? (DefinitionOptions.Count == 1 ? DefinitionOptions[0] : null)
            ?? DefinitionOptions.FirstOrDefault(x =>
                string.Equals(x.Code, PrProductDefinitionCodes.Standard, StringComparison.OrdinalIgnoreCase))
            ?? DefinitionOptions.FirstOrDefault();

        Request.DefinitionCode = selected?.Code ?? PrProductDefinitionCodes.Standard;
    }

    private static string FormatDefinitionOptionLabel(PrProductDefinitionLookupRow row)
    {
        var name = string.IsNullOrWhiteSpace(row.DefinitionName)
            ? row.DefinitionCode
            : $"{row.DefinitionCode} — {row.DefinitionName}";
        var revision = row.ActiveVersion ?? row.Version;
        var suffix = revision > 0 ? $" · V{revision}" : string.Empty;
        return row.IsDefaultDefinition ? $"{name}{suffix} (default)" : $"{name}{suffix}";
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
            DefinitionCode = PrProductDefinitionCodes.Standard,
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
        (Request.DefinitionCode ?? string.Empty).Trim().ToUpperInvariant(),
        IvQty.Round(Request.PlannedQty).ToString("0.0000", CultureInfo.InvariantCulture),
        (Request.SchedulingDirection ?? string.Empty).Trim().ToUpperInvariant(),
        (IsBackwardSchedule ? Request.PlannedCompletionDate : Request.PlannedStartDate)
            .Date.Ticks.ToString(CultureInfo.InvariantCulture),
        (Request.SourceType ?? string.Empty).Trim().ToUpperInvariant(),
        (Request.SourceReference ?? string.Empty).Trim(),
        (Request.Remark ?? string.Empty).Trim());

    protected sealed record Option(string Key, string Name);

    protected sealed record DefinitionOption(
        string Code,
        string? Name,
        int Version,
        bool IsDefault,
        string Label);
}

