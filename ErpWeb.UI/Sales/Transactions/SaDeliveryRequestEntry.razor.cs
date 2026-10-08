using ErpWeb.Core.Menus;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaDeliveryRequestEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public long? Uid { get; set; }

    [Inject] private ISaDeliveryRequestService Requests { get; set; } = default!;
    [Inject] private IProductionWorkOrderService WorkOrders { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private string? _loadedKey;

    protected bool IsLoading { get; private set; } = true;
    protected bool IsSubmitting { get; private set; }
    protected bool SourcePickerVisible { get; private set; }
    protected bool CanAdd { get; private set; }
    protected bool CanEditPermission { get; private set; }
    protected bool CanReleasePermission { get; private set; }
    protected bool CanCancelPermission { get; private set; }
    protected bool CanDeletePermission { get; private set; }
    protected bool CanCreateWorkOrder { get; private set; }
    protected string? StatusMessage { get; private set; }
    protected SaDeliveryRequestDetail? Detail { get; private set; }

    protected string ProductCode { get; private set; } = string.Empty;
    protected string ProductionUom { get; private set; } = string.Empty;
    protected DateTime RequiredDate { get; set; } = DateTime.UtcNow.Date;
    protected string? DefinitionCode { get; set; }
    protected string? WarehouseCode { get; set; }
    protected string? ProjectCode { get; set; }
    protected string? Priority { get; set; }
    protected string? Remark { get; set; }

    protected string SourceSearchSoNo { get; set; } = string.Empty;
    protected IReadOnlyList<SaDeliveryRequestEligibleSource> EligibleSources { get; private set; } = [];
    protected List<SourceEditorRow> SourceRows { get; private set; } = [];

    protected decimal WorkOrderQty { get; set; }
    protected DateTime WorkOrderStart { get; set; } = DateTime.UtcNow.Date;
    protected DateTime WorkOrderCompletion { get; set; } = DateTime.UtcNow.Date;

    protected string PageHeading => IsNew ? "New Delivery Request" : IsEdit ? "Edit Delivery Request" : "View Delivery Request";
    protected string ModeDisplay => IsNew ? "New" : IsEdit ? "Edit" : "View";
    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEdit => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool CanEdit => !IsSubmitting && (IsNew ? CanAdd : IsEdit && CanEditPermission && Detail?.Status == SaDeliveryRequestStatuses.Draft);
    protected bool CanRelease => !IsSubmitting && Detail?.Status == SaDeliveryRequestStatuses.Draft && CanReleasePermission;
    protected bool CanCancel => !IsSubmitting && Detail is not null
        && Detail.Status is not (SaDeliveryRequestStatuses.Cancelled or SaDeliveryRequestStatuses.Completed)
        && CanCancelPermission;
    protected bool CanDelete => !IsSubmitting && Detail?.Status == SaDeliveryRequestStatuses.Draft && CanDeletePermission;

    protected override async Task OnPageInitializedAsync()
    {
        CanAdd = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Add);
        CanEditPermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Edit);
        CanReleasePermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Approve);
        CanCancelPermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Cancel);
        CanDeletePermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Delete);
        CanCreateWorkOrder = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Add);
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
        SourcePickerVisible = false;
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
            Priority = null;
            Remark = null;
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
        Priority = detail.Priority;
        Remark = detail.Remark;
        SourceRows = detail.Sources.Select(SourceEditorRow.From).ToList();
        WorkOrderQty = detail.UnplannedQty;
        WorkOrderStart = detail.RequiredDate.Date;
        WorkOrderCompletion = detail.RequiredDate.Date;
    }

    protected async Task SaveAsync()
    {
        if (!CanEdit || SourceRows.Count == 0)
        {
            ErrorMessage = SourceRows.Count == 0 ? "Add at least one SO demand source before saving." : "The Delivery Request cannot be edited in its current state.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
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
                Navigation.NavigateTo($"/sales/delivery-requests/view/{result.Data.Uid}");
            }
            else
            {
                ErrorMessage = BuildValidationMessage(result.ValidationErrors, result.Message);
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

    protected async Task ReleaseAsync()
    {
        if (Detail is null || !CanRelease) return;
        await RunLifecycleAsync(() => Requests.ReleaseAsync(new SaDeliveryRequestCommandRequest { Uid = Detail.Uid, RowVersion = Detail.RowVersion }), "Delivery Request released.");
    }

    protected async Task CancelAsync()
    {
        if (Detail is null || !CanCancel) return;
        await RunLifecycleAsync(() => Requests.CancelAsync(new SaDeliveryRequestCommandRequest { Uid = Detail.Uid, RowVersion = Detail.RowVersion, Reason = "Cancelled from Delivery Request entry." }), "Delivery Request cancelled.");
    }

    private async Task RunLifecycleAsync(
        Func<Task<IvMasterOperationResult<SaDeliveryRequestDetail>>> operation,
        string successMessage)
    {
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var result = await operation();
            if (result.Succeeded && result.Data is not null)
            {
                ApplyDetail(result.Data);
                StatusMessage = successMessage;
            }
            else
            {
                ErrorMessage = BuildValidationMessage(result.ValidationErrors, result.Message);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task DeleteAsync()
    {
        if (Detail is null || !CanDelete) return;
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Requests.DeleteDraftAsync(new SaDeliveryRequestCommandRequest { Uid = Detail.Uid, RowVersion = Detail.RowVersion });
            if (result.Succeeded)
            {
                Navigation.NavigateTo("/sales/delivery-requests");
            }
            else
            {
                ErrorMessage = result.Message ?? "Unable to delete the Draft Delivery Request.";
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OpenSourcePickerAsync()
    {
        if (!CanEdit) return;
        SourcePickerVisible = true;
        EligibleSources = [];
        if (!string.IsNullOrWhiteSpace(SourceSearchSoNo))
        {
            await LoadEligibleAsync();
        }
    }

    protected void CloseSourcePicker() => SourcePickerVisible = false;

    protected async Task LoadEligibleAsync()
    {
        ErrorMessage = null;
        var result = await Requests.ListEligibleSalesOrderDemandAsync(new SaDeliveryRequestEligibleSourceQuery
        {
            SoNo = string.IsNullOrWhiteSpace(SourceSearchSoNo) ? null : SourceSearchSoNo.Trim(),
            ProductCode = string.IsNullOrWhiteSpace(ProductCode) ? null : ProductCode
        });
        if (result.Succeeded && result.Data is not null)
        {
            EligibleSources = result.Data;
        }
        else
        {
            EligibleSources = [];
            ErrorMessage = result.Message ?? "Unable to load eligible Sales Order demand.";
        }
    }

    protected void AddEligibleSource(SaDeliveryRequestEligibleSource source)
    {
        if (SourceRows.Any(x => string.Equals(x.SoNo, source.SoNo, StringComparison.OrdinalIgnoreCase)
            && x.CustRel == source.CustRel && x.SoLine == source.SoLine))
        {
            ErrorMessage = "That SO revision/line is already in this Delivery Request.";
            return;
        }

        if (SourceRows.Count > 0 && (!string.Equals(ProductCode, source.ProductCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ProductionUom, source.ProductionUom, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = "All source lines must use the same product and production UOM.";
            return;
        }

        ProductCode = source.ProductCode;
        ProductionUom = source.ProductionUom;
        if (RequiredDate == default || RequiredDate > source.RequestedDeliveryDate?.Date)
        {
            RequiredDate = source.RequestedDeliveryDate?.Date ?? RequiredDate;
        }

        SourceRows.Add(SourceEditorRow.From(source));
        SourcePickerVisible = false;
        ErrorMessage = null;
    }

    protected void RemoveSource(SourceEditorRow source)
    {
        if (!CanEdit) return;
        SourceRows.Remove(source);
        if (SourceRows.Count == 0)
        {
            ProductCode = string.Empty;
            ProductionUom = string.Empty;
        }
    }

    protected void OpenSalesOrder(SourceEditorRow source) =>
        Navigation.NavigateTo($"/sales/sales-orders/view/{Uri.EscapeDataString(source.SoNo)}/{source.CustRel}");

    protected void OpenWorkOrder(string workOrderNo) =>
        Navigation.NavigateTo($"/planning/work-orders/view/{Uri.EscapeDataString(workOrderNo)}");

    protected async Task CreateWorkOrderAsync()
    {
        if (Detail is null || !CanCreateWorkOrder || WorkOrderQty <= 0m || Detail.UnplannedQty <= 0m) return;
        IsSubmitting = true;
        ErrorMessage = null;
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
                ErrorMessage = BuildValidationMessage(result.ValidationErrors, result.Message);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void BackToList() => Navigation.NavigateTo("/sales/delivery-requests");

    private RenderFragment Kpi(string label, string value, string? suffix) => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "dr-kpi");
        builder.OpenElement(2, "span");
        builder.AddContent(3, label);
        builder.CloseElement();
        builder.OpenElement(4, "strong");
        builder.AddContent(5, value);
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            builder.OpenElement(6, "small");
            builder.AddContent(7, suffix);
            builder.CloseElement();
        }
        builder.CloseElement();
        builder.CloseElement();
    };

    public sealed class SourceEditorRow
    {
        public string SoNo { get; init; } = string.Empty;
        public short CustRel { get; init; }
        public short SoLine { get; init; }
        public string ProductCode { get; init; } = string.Empty;
        public string? ProductDescription { get; init; }
        public string ProductionUom { get; init; } = string.Empty;
        public decimal SourceQty { get; init; }
        public decimal Quantity { get; set; }
        public DateTime? RequestedDeliveryDate { get; init; }

        public static SourceEditorRow From(SaDeliveryRequestSourceTrace source) => new()
        {
            SoNo = source.SoNo,
            CustRel = source.CustRel,
            SoLine = source.SoLine,
            ProductCode = source.ProductCode,
            ProductionUom = source.ProductionUom,
            SourceQty = source.ProductionDemandQty,
            Quantity = source.AllocatedProductionQty,
            RequestedDeliveryDate = source.RequestedDeliveryDate
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
            Quantity = source.AvailableForDr,
            RequestedDeliveryDate = source.RequestedDeliveryDate
        };
    }
}
