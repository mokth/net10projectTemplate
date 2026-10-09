using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaDeliveryRequestEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public long? Uid { get; set; }

    [Inject] private ISaDeliveryRequestService Requests { get; set; } = default!;
    [Inject] private ISaDeliveryRequestFulfilmentService Fulfilment { get; set; } = default!;
    [Inject] private IProductionWorkOrderService WorkOrders { get; set; } = default!;
    [Inject] private IPrProductDefService ProductDefs { get; set; } = default!;
    [Inject] private IPoPrService PurchaseRequests { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private string? _loadedKey;
    private bool _isDirty;
    private LifecycleAction _pendingLifecycleAction;

    protected bool IsLoading { get; private set; } = true;
    protected bool IsSubmitting { get; private set; }
    protected bool SourcePickerVisible { get; private set; }
    protected bool ConfirmDiscardVisible { get; private set; }
    protected bool ConfirmLifecycleVisible { get; private set; }
    protected bool ConcurrencyVisible { get; private set; }
    protected bool CanAdd { get; private set; }
    protected bool CanEditPermission { get; private set; }
    protected bool CanReleasePermission { get; private set; }
    protected bool CanCancelPermission { get; private set; }
    protected bool CanDeletePermission { get; private set; }
    protected bool CanCreateWorkOrder { get; private set; }
    protected bool CanCreatePurchaseRequisition { get; private set; }
    protected string? StatusMessage { get; private set; }
    protected string? SourcePickerError { get; private set; }
    protected Dictionary<string, string> ValidationErrors { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);
    protected SaDeliveryRequestDetail? Detail { get; private set; }

    protected string ProductCode { get; private set; } = string.Empty;
    protected string ProductionUom { get; private set; } = string.Empty;
    protected DateTime RequiredDate { get; set; } = DateTime.UtcNow.Date;
    protected string? DefinitionCode { get; set; }
    protected string? WarehouseCode { get; set; }
    protected string? ProjectCode { get; set; }
    protected string? Priority { get; set; }
    protected string? Remark { get; set; }
    protected IReadOnlyList<DefinitionOption> DefinitionOptions { get; private set; } = [];
    protected IReadOnlyList<string> PriorityOptions { get; } =
    [
        SaDeliveryRequestPriorities.Normal,
        SaDeliveryRequestPriorities.High,
        SaDeliveryRequestPriorities.Urgent
    ];

    protected string SourceSearchSoNo { get; set; } = string.Empty;
    protected IReadOnlyList<SaDeliveryRequestEligibleSource> EligibleSources { get; private set; } = [];
    protected List<SourceEditorRow> SourceRows { get; private set; } = [];

    protected decimal WorkOrderQty { get; set; }
    protected DateTime WorkOrderStart { get; set; } = DateTime.UtcNow.Date;
    protected DateTime WorkOrderCompletion { get; set; } = DateTime.UtcNow.Date;

    protected string PageHeading => IsNew
        ? "New Delivery Request"
        : IsReadOnlyPresentation
            ? "View Delivery Request"
            : IsEdit
                ? "Edit Delivery Request"
                : "View Delivery Request";
    protected string ModeDisplay => IsNew ? "New" : IsReadOnlyPresentation ? "View" : IsEdit ? "Edit" : "View";
    protected string DocumentNoDisplay => Detail?.DeliveryRequestNo ?? "AUTO";
    protected string StatusDisplay => Detail?.Status ?? (IsNew ? SaDeliveryRequestStatuses.Draft : "—");
    protected string HeaderKpiLabel => Detail is null ? "Requested" : "Unplanned";
    protected string HeaderKpiValue => Detail is not null
        ? $"{Detail.UnplannedQty:n4} {Detail.ProductionUom}"
        : SourceRows.Count == 0
            ? "—"
            : $"{SourceRows.Sum(x => x.Quantity):n4} {ProductionUom}";

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEdit => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsView => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsReadOnlyPresentation => IsView
        || (Detail is not null && !string.Equals(Detail.Status, SaDeliveryRequestStatuses.Draft, StringComparison.OrdinalIgnoreCase));
    protected bool CanEdit => !IsSubmitting
        && (IsNew
            ? CanAdd
            : IsEdit
                && CanEditPermission
                && string.Equals(Detail?.Status, SaDeliveryRequestStatuses.Draft, StringComparison.OrdinalIgnoreCase));
    protected bool CanEditFromView => IsView
        && IsReadOnlyPresentation
        && string.Equals(Detail?.Status, SaDeliveryRequestStatuses.Draft, StringComparison.OrdinalIgnoreCase)
        && CanEditPermission
        && !IsSubmitting;
    protected bool CanRelease => !IsSubmitting
        && IsReadOnlyPresentation
        && string.Equals(Detail?.Status, SaDeliveryRequestStatuses.Draft, StringComparison.OrdinalIgnoreCase)
        && CanReleasePermission;
    protected bool CanCancel => !IsSubmitting
        && IsReadOnlyPresentation
        && Detail is not null
        && Detail.Status is not (SaDeliveryRequestStatuses.Cancelled or SaDeliveryRequestStatuses.Completed)
        && CanCancelPermission;
    protected bool CanDelete => !IsSubmitting
        && IsReadOnlyPresentation
        && string.Equals(Detail?.Status, SaDeliveryRequestStatuses.Draft, StringComparison.OrdinalIgnoreCase)
        && CanDeletePermission;
    protected bool CanRefreshFulfilment => !IsSubmitting
        && Detail is not null
        && Detail.Status is SaDeliveryRequestStatuses.Released or SaDeliveryRequestStatuses.InProduction;
    protected bool CanSave => CanEdit && SourceRows.Count > 0;
    protected bool CanOfferWorkOrderCreation => !IsSubmitting
        && IsReadOnlyPresentation
        && Detail is not null
        && Detail.Status is SaDeliveryRequestStatuses.Released or SaDeliveryRequestStatuses.InProduction
        && Detail.UnplannedQty > 0.0001m
        && CanCreateWorkOrder;
    protected bool ShowWorkOrderSection => Detail is not null
        && (Detail.WorkOrders.Count > 0 || CanOfferWorkOrderCreation);

    protected string LifecycleConfirmMessage => _pendingLifecycleAction switch
    {
        LifecycleAction.Release => "Release this Delivery Request to production?",
        LifecycleAction.Cancel => "Cancel this Delivery Request? Active or produced Work Order quantity will still be rejected by the server.",
        LifecycleAction.Delete => "Permanently delete this never-released Draft Delivery Request?",
        _ => string.Empty
    };
    protected string LifecycleConfirmButtonText => _pendingLifecycleAction switch
    {
        LifecycleAction.Release => "Release",
        LifecycleAction.Cancel => "Cancel Delivery Request",
        LifecycleAction.Delete => "Delete Draft",
        _ => "Confirm"
    };
    protected ButtonRenderStyle LifecycleConfirmButtonStyle =>
        _pendingLifecycleAction == LifecycleAction.Release
            ? ButtonRenderStyle.Primary
            : ButtonRenderStyle.Danger;

    protected override async Task OnPageInitializedAsync()
    {
        CanAdd = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Add);
        CanEditPermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Edit);
        CanReleasePermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Approve);
        CanCancelPermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Cancel);
        CanDeletePermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Delete);
        CanCreateWorkOrder = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Add);
        CanCreatePurchaseRequisition = await AccessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Add);
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{Mode}:{Uid}";
        if (string.Equals(_loadedKey, key, StringComparison.Ordinal))
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
        SourcePickerVisible = false;
        SourcePickerError = null;
        ConfirmDiscardVisible = false;
        ConfirmLifecycleVisible = false;
        ConcurrencyVisible = false;
        _pendingLifecycleAction = LifecycleAction.None;
        _isDirty = false;
        EligibleSources = [];
        SourceRows.Clear();

        if (IsNew)
        {
            Detail = null;
            ProductCode = string.Empty;
            ProductionUom = string.Empty;
            RequiredDate = DateTime.UtcNow.Date;
            DefinitionCode = null;
            WarehouseCode = null;
            ProjectCode = null;
            Priority = SaDeliveryRequestPriorities.Normal;
            Remark = null;
            DefinitionOptions = [];
            WorkOrderQty = 0m;
            IsLoading = false;
            return;
        }

        if (Uid is not > 0)
        {
            ErrorMessage = "A Delivery Request identifier is required.";
            IsLoading = false;
            return;
        }

        var result = await Requests.GetAsync(Uid.Value);
        if (result.Succeeded && result.Data is not null)
        {
            ApplyDetail(result.Data);
            await LoadDefinitionOptionsAsync(result.Data.ProductCode, selectDefaultWhenEmpty: false);
        }
        else
        {
            ErrorMessage = result.Message ?? "Unable to load the Delivery Request.";
        }

        IsLoading = false;
    }

    private void ApplyDetail(SaDeliveryRequestDetail detail)
    {
        Detail = detail;
        ProductCode = detail.ProductCode;
        ProductionUom = detail.ProductionUom;
        RequiredDate = detail.RequiredDate == default ? DateTime.UtcNow.Date : detail.RequiredDate.Date;
        DefinitionCode = detail.DefinitionCode;
        WarehouseCode = detail.WarehouseCode;
        ProjectCode = detail.ProjectCode;
        Priority = detail.Priority ?? SaDeliveryRequestPriorities.Normal;
        Remark = detail.Remark;
        SourceRows = detail.Sources.Select(SourceEditorRow.From).ToList();
        WorkOrderQty = detail.UnplannedQty;
        WorkOrderStart = detail.RequiredDate.Date;
        WorkOrderCompletion = detail.RequiredDate.Date;
        _isDirty = false;
    }

    protected async Task SaveAsync()
    {
        if (!CanEdit)
        {
            ErrorMessage = "The Delivery Request cannot be edited in its current state.";
            return;
        }

        if (SourceRows.Count == 0)
        {
            ErrorMessage = "Add at least one SO demand source before saving.";
            return;
        }

        IsSubmitting = true;
        ClearOperationMessages();
        try
        {
            var request = BuildDraftRequest();
            IvMasterOperationResult<SaDeliveryRequestDetail> result;
            if (IsNew)
            {
                result = await Requests.CreateDraftAsync(request);
            }
            else
            {
                result = await Requests.UpdateDraftAsync(new SaDeliveryRequestUpdateRequest
                {
                    Uid = Detail!.Uid,
                    RowVersion = Detail.RowVersion,
                    DeliveryRequestNo = request.DeliveryRequestNo,
                    ProductCode = request.ProductCode,
                    ProductionUom = request.ProductionUom,
                    RequestedQty = request.RequestedQty,
                    RequiredDate = request.RequiredDate,
                    DefinitionCode = request.DefinitionCode,
                    WarehouseCode = request.WarehouseCode,
                    ProjectCode = request.ProjectCode,
                    Priority = request.Priority,
                    Remark = request.Remark,
                    Sources = request.Sources
                });
            }

            if (result.Succeeded && result.Data is not null)
            {
                _isDirty = false;
                Navigation.NavigateTo($"/sales/delivery-requests/view/{result.Data.Uid}");
            }
            else
            {
                HandleOperationFailure(result, "Unable to save the Delivery Request.");
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private SaDeliveryRequestDraftRequest BuildDraftRequest() => new()
    {
        ProductCode = string.IsNullOrWhiteSpace(ProductCode) ? null : ProductCode,
        ProductionUom = string.IsNullOrWhiteSpace(ProductionUom) ? null : ProductionUom,
        RequestedQty = SourceRows.Sum(x => x.Quantity),
        RequiredDate = RequiredDate,
        DefinitionCode = DefinitionCode,
        WarehouseCode = WarehouseCode,
        ProjectCode = ProjectCode,
        Priority = Priority,
        Remark = Remark,
        Sources = SourceRows.Select(x => new SaDeliveryRequestSourceInput
        {
            SoNo = x.SoNo,
            CustRel = x.CustRel,
            SoLine = x.SoLine,
            AllocatedProductionQty = x.Quantity
        }).ToList()
    };

    protected void BeginRelease()
    {
        if (!CanRelease)
        {
            return;
        }

        _pendingLifecycleAction = LifecycleAction.Release;
        ConfirmLifecycleVisible = true;
    }

    protected void BeginCancel()
    {
        if (!CanCancel)
        {
            return;
        }

        _pendingLifecycleAction = LifecycleAction.Cancel;
        ConfirmLifecycleVisible = true;
    }

    protected void BeginDelete()
    {
        if (!CanDelete)
        {
            return;
        }

        _pendingLifecycleAction = LifecycleAction.Delete;
        ConfirmLifecycleVisible = true;
    }

    protected async Task ConfirmLifecycleAsync()
    {
        if (IsSubmitting || Detail is null)
        {
            ConfirmLifecycleVisible = false;
            return;
        }

        var action = _pendingLifecycleAction;
        if ((action == LifecycleAction.Release && !CanRelease)
            || (action == LifecycleAction.Cancel && !CanCancel)
            || (action == LifecycleAction.Delete && !CanDelete))
        {
            ConfirmLifecycleVisible = false;
            _pendingLifecycleAction = LifecycleAction.None;
            return;
        }

        ConfirmLifecycleVisible = false;
        IsSubmitting = true;
        ClearOperationMessages();
        try
        {
            if (action == LifecycleAction.Delete)
            {
                var deleteResult = await Requests.DeleteDraftAsync(new SaDeliveryRequestCommandRequest
                {
                    Uid = Detail.Uid,
                    RowVersion = Detail.RowVersion
                });
                if (deleteResult.Succeeded)
                {
                    Navigation.NavigateTo("/sales/delivery-requests");
                }
                else
                {
                    HandleOperationFailure(deleteResult, "Unable to delete the Draft Delivery Request.");
                }

                return;
            }

            var result = action == LifecycleAction.Release
                ? await Requests.ReleaseAsync(new SaDeliveryRequestCommandRequest
                {
                    Uid = Detail.Uid,
                    RowVersion = Detail.RowVersion
                })
                : await Requests.CancelAsync(new SaDeliveryRequestCommandRequest
                {
                    Uid = Detail.Uid,
                    RowVersion = Detail.RowVersion,
                    Reason = "Cancelled from Delivery Request entry."
                });

            if (result.Succeeded && result.Data is not null)
            {
                ApplyDetail(result.Data);
                StatusMessage = action == LifecycleAction.Release
                    ? "Delivery Request released."
                    : "Delivery Request cancelled.";
            }
            else
            {
                HandleOperationFailure(result, "Unable to complete the Delivery Request action.");
            }
        }
        finally
        {
            _pendingLifecycleAction = LifecycleAction.None;
            IsSubmitting = false;
        }
    }

    protected async Task OpenSourcePickerAsync()
    {
        if (!CanEdit)
        {
            return;
        }

        SourcePickerError = null;
        SourcePickerVisible = true;
        EligibleSources = [];
        if (!string.IsNullOrWhiteSpace(SourceSearchSoNo))
        {
            await LoadEligibleAsync();
        }
    }

    protected void CloseSourcePicker()
    {
        SourcePickerVisible = false;
        SourcePickerError = null;
    }

    protected async Task LoadEligibleAsync()
    {
        SourcePickerError = null;
        var result = await Requests.ListEligibleSalesOrderDemandAsync(new SaDeliveryRequestEligibleSourceQuery
        {
            SoNo = string.IsNullOrWhiteSpace(SourceSearchSoNo) ? null : SourceSearchSoNo.Trim(),
            ProductCode = string.IsNullOrWhiteSpace(ProductCode) ? null : ProductCode,
            WarehouseCode = string.IsNullOrWhiteSpace(WarehouseCode) ? null : WarehouseCode.Trim(),
            ProjectCode = string.IsNullOrWhiteSpace(ProjectCode) ? null : ProjectCode.Trim()
        });
        if (result.Succeeded && result.Data is not null)
        {
            EligibleSources = result.Data;
        }
        else
        {
            EligibleSources = [];
            SourcePickerError = result.Message ?? "Unable to load eligible Sales Order demand.";
        }
    }

    protected async Task AddEligibleSourceAsync(SaDeliveryRequestEligibleSource source)
    {
        if (SourceRows.Any(x => string.Equals(x.SoNo, source.SoNo, StringComparison.OrdinalIgnoreCase)
            && x.CustRel == source.CustRel && x.SoLine == source.SoLine))
        {
            SourcePickerError = "That SO revision/line is already in this Delivery Request.";
            return;
        }

        if (SourceRows.Count > 0 && (!string.Equals(ProductCode, source.ProductCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ProductionUom, source.ProductionUom, StringComparison.OrdinalIgnoreCase)))
        {
            SourcePickerError = "All source lines must use the same product and production UOM.";
            return;
        }

        if (SourceRows.Count > 0
            && (!string.Equals(WarehouseCode, source.WarehouseCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ProjectCode, source.ProjectCode, StringComparison.OrdinalIgnoreCase)))
        {
            SourcePickerError = "All source lines must use the same warehouse and project.";
            return;
        }

        ProductCode = source.ProductCode;
        ProductionUom = source.ProductionUom;
        WarehouseCode = source.WarehouseCode;
        ProjectCode = source.ProjectCode;
        await LoadDefinitionOptionsAsync(ProductCode, selectDefaultWhenEmpty: true);
        if (RequiredDate == default
            || (source.RequestedDeliveryDate is DateTime requested && RequiredDate > requested.Date))
        {
            RequiredDate = source.RequestedDeliveryDate?.Date ?? RequiredDate;
        }

        SourceRows.Add(SourceEditorRow.From(source));
        MarkDirty();
        SourcePickerVisible = false;
        SourcePickerError = null;
    }

    protected void RemoveSource(SourceEditorRow source)
    {
        if (!CanEdit)
        {
            return;
        }

        SourceRows.Remove(source);
        if (SourceRows.Count == 0)
        {
            ProductCode = string.Empty;
            ProductionUom = string.Empty;
        }

        MarkDirty();
    }

    protected void OnSourceQuantityChanged(SourceEditorRow row, decimal value)
    {
        if (!CanEdit)
        {
            return;
        }

        row.Quantity = value;
        MarkDirty();
    }

    protected void OnProductCodeChanged(string? value)
    {
        if (!CanEdit || SourceRows.Count > 0)
        {
            return;
        }

        ProductCode = value?.Trim() ?? string.Empty;
        MarkDirty();
    }

    private async Task LoadDefinitionOptionsAsync(string? productCode, bool selectDefaultWhenEmpty)
    {
        var code = (productCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            DefinitionOptions = [];
            if (selectDefaultWhenEmpty)
            {
                DefinitionCode = null;
            }

            return;
        }

        var result = await ProductDefs.ListActiveDefinitionsAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            DefinitionOptions = [];
            return;
        }

        DefinitionOptions = result.Data
            .Select(x => new DefinitionOption(
                x.DefinitionCode,
                x.DefinitionName,
                x.Version,
                x.IsDefaultDefinition,
                string.IsNullOrWhiteSpace(x.DefinitionName)
                    ? $"{x.DefinitionCode} (V{x.Version})"
                    : $"{x.DefinitionCode} — {x.DefinitionName} (V{x.Version})"))
            .ToList();

        if (!selectDefaultWhenEmpty || DefinitionOptions.Count == 0)
        {
            return;
        }

        DefinitionCode = DefinitionOptions.Count == 1
            ? DefinitionOptions[0].Code
            : DefinitionOptions.Count(x => x.IsDefault) == 1
                ? DefinitionOptions.Single(x => x.IsDefault).Code
                : null;
    }

    protected void OnRequiredDateChanged(DateTime value)
    {
        RequiredDate = value.Date;
        MarkDirty();
    }

    protected void OpenSalesOrder(SourceEditorRow source) =>
        Navigation.NavigateTo($"/sales/sales-orders/view/{Uri.EscapeDataString(source.SoNo)}/{source.CustRel}");

    protected void OpenWorkOrder(string workOrderNo) =>
        Navigation.NavigateTo($"/planning/work-orders/view/{Uri.EscapeDataString(workOrderNo)}");

    protected async Task CreateWorkOrderAsync()
    {
        if (Detail is null || !CanOfferWorkOrderCreation || WorkOrderQty <= 0m || Detail.UnplannedQty <= 0m)
        {
            return;
        }

        IsSubmitting = true;
        ClearOperationMessages();
        try
        {
            var result = await WorkOrders.CreateDraftFromDeliveryRequestAsync(new ProductionWorkOrderDeliveryRequestRequest
            {
                DeliveryRequestId = Detail.Uid,
                PlannedQty = WorkOrderQty,
                DefinitionCode = DefinitionCode,
                PlannedStartDate = WorkOrderStart,
                PlannedCompletionDate = WorkOrderCompletion,
                SchedulingDirection = ProductionSchedulingDirections.Forward,
                Remark = Remark,
                DeliveryRequestRowVersion = Detail.RowVersion
            });
            if (result.Succeeded && result.Data is not null)
            {
                Navigation.NavigateTo($"/planning/work-orders/edit/{Uri.EscapeDataString(result.Data.WorkOrderNo)}");
            }
            else
            {
                HandleOperationFailure(result, "Unable to create the draft Work Order.");
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task RefreshFulfilmentAsync()
    {
        if (!CanRefreshFulfilment || Detail is null)
        {
            return;
        }

        IsSubmitting = true;
        ClearOperationMessages();
        try
        {
            var result = await Fulfilment.ReconcileAsync(Detail.Uid);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message ?? "Unable to refresh Delivery Request fulfilment.";
                return;
            }

            var latest = await Requests.GetAsync(Detail.Uid);
            if (latest.Succeeded && latest.Data is not null)
            {
                ApplyDetail(latest.Data);
                StatusMessage = "Fulfilment refreshed.";
            }
            else
            {
                ErrorMessage = latest.Message ?? "Fulfilment refreshed, but the latest Delivery Request could not be loaded.";
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task CreatePurchaseRequisitionAsync(SaDeliveryRequestMaterialShortageTrace shortage)
    {
        if (IsSubmitting
            || !CanCreatePurchaseRequisition
            || shortage.NetProcurementRequiredBaseQty <= 0.0001m
            || !shortage.IsConsistent)
        {
            return;
        }

        IsSubmitting = true;
        ClearOperationMessages();
        try
        {
            var result = await PurchaseRequests.CreateFromWorkOrderMaterialAsync(
                new PoPrCreateFromWorkOrderMaterialRequest
                {
                    WorkOrderMaterialId = shortage.WorkOrderMaterialId,
                    RequestedBaseQty = shortage.NetProcurementRequiredBaseQty
                });
            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to create the Purchase Requisition.";
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
                return;
            }

            StatusMessage = string.IsNullOrWhiteSpace(result.PrNo)
                ? "Purchase Requisition created."
                : $"Purchase Requisition {result.PrNo} created.";
            if (Detail is not null)
            {
                var refreshed = await Requests.GetAsync(Detail.Uid);
                if (refreshed.Succeeded && refreshed.Data is not null)
                {
                    ApplyDetail(refreshed.Data);
                }
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task OnCancelAsync()
    {
        if (_isDirty && CanEdit)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/sales/delivery-requests");
        return Task.CompletedTask;
    }

    protected void OnClose() =>
        DocumentReturnNavigation.NavigateBack(Navigation, "/sales/delivery-requests");

    protected void OnEditFromView()
    {
        if (CanEditFromView && Detail is not null)
        {
            Navigation.NavigateTo($"/sales/delivery-requests/edit/{Detail.Uid}");
        }
    }

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/sales/delivery-requests");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (IsNew || Uid is not > 0)
        {
            return;
        }

        await LoadAsync();
        if (Detail is not null && string.IsNullOrWhiteSpace(ErrorMessage))
        {
            StatusMessage = "Loaded latest version.";
        }
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() =>
        ErrorMessage = null;

    protected void MarkDirtyOnly() => MarkDirty();

    private void MarkDirty()
    {
        if (CanEdit)
        {
            _isDirty = true;
        }
    }

    private void ClearOperationMessages()
    {
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
    }

    private void HandleOperationFailure<T>(IvMasterOperationResult<T> result, string fallback)
    {
        ValidationErrors = result.ValidationErrors.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.OrdinalIgnoreCase);
        ErrorMessage = result.Message ?? fallback;
        if (result.ErrorCode == IvMasterErrorCode.Concurrency)
        {
            ConcurrencyVisible = true;
        }
    }

    public sealed class SourceEditorRow
    {
        public string SoNo { get; init; } = string.Empty;
        public short CustRel { get; init; }
        public short SoLine { get; init; }
        public string ProductCode { get; init; } = string.Empty;
        public string? ProductDescription { get; init; }
        public string ProductionUom { get; init; } = string.Empty;
        public decimal SourceQty { get; init; }
        public decimal OpenProductionDemandQty { get; init; }
        public decimal ActiveAllocatedProductionQty { get; init; }
        public decimal ActiveDrLinkedDoQty { get; init; }
        public decimal ActiveDrOutstandingQty { get; init; }
        public decimal AvailableForDr { get; init; }
        public decimal Quantity { get; set; }
        public DateTime? RequestedDeliveryDate { get; init; }
        public string? CustomerCode { get; init; }
        public string? WarehouseCode { get; init; }
        public string? ProjectCode { get; init; }

        public static SourceEditorRow From(SaDeliveryRequestSourceTrace source) => new()
        {
            SoNo = source.SoNo,
            CustRel = source.CustRel,
            SoLine = source.SoLine,
            ProductCode = source.ProductCode,
            ProductionUom = source.ProductionUom,
            SourceQty = source.ProductionDemandQty,
            OpenProductionDemandQty = source.OpenProductionDemandQty,
            ActiveAllocatedProductionQty = source.ActiveAllocatedProductionQty,
            ActiveDrLinkedDoQty = source.ActiveDrLinkedDoQty,
            ActiveDrOutstandingQty = source.ActiveDrOutstandingQty,
            AvailableForDr = source.AvailableForDr,
            Quantity = source.AllocatedProductionQty,
            RequestedDeliveryDate = source.RequestedDeliveryDate,
            CustomerCode = source.CustomerCode,
            WarehouseCode = source.WarehouseCode,
            ProjectCode = source.ProjectCode
        };

        public static SourceEditorRow From(SaDeliveryRequestEligibleSource source) => new()
        {
            SoNo = source.SoNo,
            CustRel = source.CustRel,
            SoLine = source.SoLine,
            ProductCode = source.ProductCode,
            ProductDescription = source.ProductDescription,
            ProductionUom = source.ProductionUom,
            SourceQty = source.ProductionDemandQty,
            OpenProductionDemandQty = source.OpenProductionDemandQty,
            ActiveAllocatedProductionQty = source.ActiveDrAllocatedQty,
            ActiveDrLinkedDoQty = source.ActiveDrLinkedDoQty,
            ActiveDrOutstandingQty = source.ActiveDrOutstandingQty,
            AvailableForDr = source.AvailableForDr,
            Quantity = source.AvailableForDr,
            RequestedDeliveryDate = source.RequestedDeliveryDate,
            CustomerCode = source.CustomerCode,
            WarehouseCode = source.WarehouseCode,
            ProjectCode = source.ProjectCode
        };
    }

    private enum LifecycleAction
    {
        None,
        Release,
        Cancel,
        Delete
    }

    protected sealed record DefinitionOption(string Code, string? Name, int Version, bool IsDefault, string Label);
}
