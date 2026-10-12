using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrWorkOrderTransferEntry
{
    [Parameter] public long Id { get; set; }
    [Parameter] public string Mode { get; set; } = "";
    [Inject] private IProductionWorkOrderTransferService Transfers { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    protected ProductionWorkOrderTransferDocument Document { get; set; } = new() { Status = "NEW" };
    protected List<ProductionWorkOrderTransferSource> Sources { get; set; } = [];
    protected List<ProductionWorkOrderTransferTarget> Targets { get; set; } = [];
    protected ProductionWorkOrderTransferSource? SelectedSource { get; set; }
    protected ProductionWorkOrderTransferTarget? SelectedTarget { get; set; }
    protected string SourceSearch = "", TargetSearch = "", Error = "", Message = "";
    protected bool Loading = true, Busy, CanAdd, CanEdit, CanDeleteAccess, CanPostAccess, CanViewCostAccess;
    protected bool TargetPickerVisible, PostConfirmVisible, DeleteConfirmVisible;
    protected bool CanEditFields => Document.Status == "NEW" && (Id == 0 ? CanAdd : CanEdit) && Mode != "view";
    protected bool CanSave => CanEditFields && (Id > 0
        ? !TargetPickerVisible || SelectedTarget is not null
        : SelectedSource is not null && SelectedTarget is not null);
    protected string Heading => Id == 0 ? "New Work Order Transfer" : CanEditFields ? "Edit Work Order Transfer" : "View Work Order Transfer";

    protected override async Task OnParametersSetAsync()
    {
        Loading = true; Error = ""; Message = "";
        CanAdd = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Edit);
        CanDeleteAccess = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Delete);
        CanPostAccess = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Post);
        CanViewCostAccess = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.ViewCost);
        if (Id > 0)
        {
            var result = await Transfers.GetAsync(Id);
            if (!result.Succeeded || result.Data is null) Error = result.Message ?? "Work Order Transfer was not found.";
            else Document = result.Data;
            if (Mode.Equals("edit", StringComparison.OrdinalIgnoreCase) && Document.Status == "NEW" && !CanEdit)
                Nav.NavigateTo($"/planning/work-order-transfer/{Id}/view", replace: true);
        }
        else
        {
            Document = new ProductionWorkOrderTransferDocument { Status = "NEW", TransferDateTime = Clock.Now, CanViewCost = CanViewCostAccess };
            await LoadSourcesAsync();
        }
        Loading = false;
    }

    protected Task SourceSearchChanged(string value) { SourceSearch = value ?? ""; return LoadSourcesAsync(); }
    protected Task TargetSearchChanged(string value) { TargetSearch = value ?? ""; return LoadTargetsAsync(); }
    protected void BackToList() => Nav.NavigateTo("/planning/work-order-transfer");

    private async Task LoadSourcesAsync()
    {
        var result = await Transfers.SearchEligibleSourcesAsync(new ProductionWorkOrderTransferSourceQuery
        { SearchText = SourceSearch, Skip = 0, Take = 100 });
        if (!result.Succeeded || result.Data is null) { Error = result.Message ?? "Unable to load eligible WIP."; Sources = []; }
        else Sources = result.Data.Rows.ToList();
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadTargetsAsync()
    {
        var sourceId = SelectedSource?.ProductionBalLotId ?? Document.SourceProductionBalLotId;
        if (sourceId <= 0) return;
        var result = await Transfers.SearchEligibleTargetsAsync(sourceId,
            new ProductionWorkOrderTransferTargetQuery { SearchText = TargetSearch, Skip = 0, Take = 100 });
        if (!result.Succeeded || result.Data is null) { Error = result.Message ?? "Unable to load compatible target requirements."; Targets = []; }
        else Targets = result.Data.Rows.ToList();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task SelectSourceAsync(ProductionWorkOrderTransferSource source)
    {
        if (!CanEditFields || Id != 0) return;
        SelectedSource = source; SelectedTarget = null; Targets = []; TargetSearch = "";
        Document.CanViewCost = CanViewCostAccess;
        Document.TransferTotalCost = source.TotalCost;
        await LoadTargetsAsync();
    }

    protected async Task OpenTargetPickerAsync()
    {
        if (!CanEditFields) return;
        TargetPickerVisible = true; SelectedTarget = null; Targets = [];
        await LoadTargetsAsync();
    }

    protected void CancelTargetPicker() { TargetPickerVisible = false; SelectedTarget = null; }

    protected void SelectTarget(ProductionWorkOrderTransferTarget target)
    {
        if (!CanEditFields) return;
        SelectedTarget = target;
        Document.TargetWorkOrderId = target.WorkOrderId;
        Document.TargetWorkOrderNo = target.WorkOrderNo;
        Document.TargetWorkOrderMaterialId = target.WorkOrderMaterialId;
        Document.TargetConsumingWorkCentreCode = target.ConsumingWorkCentreCode;
        Document.TargetConsumingProcessCode = target.ConsumingProcessCode;
        Document.TargetProducerWorkCentreCode = target.ProducerWorkCentreCode;
        Document.TargetProducerProcessCode = target.ProducerProcessCode;
        Document.TargetCurrentWipValue = target.TargetCurrentWipValue;
        Document.ProjectedTargetWipValue = target.ProjectedTargetWipValue;
        TargetPickerVisible = false;
    }

    protected async Task SaveAsync()
    {
        if (Busy || !CanSave) return;
        Busy = true; Error = ""; Message = "";
        try
        {
            if (Id == 0)
            {
                var result = await Transfers.CreateAsync(new ProductionWorkOrderTransferCreateRequest
                {
                    SourceProductionBalLotId = SelectedSource!.ProductionBalLotId,
                    TargetWorkOrderMaterialId = SelectedTarget!.WorkOrderMaterialId,
                    TransferDateTime = Document.TransferDateTime, Remark = Document.Remark
                });
                if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message ?? "Unable to save transfer.");
                Document = result.Data; Id = Document.Id; Mode = "edit";
                Message = "Work Order Transfer saved as NEW.";
                Nav.NavigateTo($"/planning/work-order-transfer/{Id}/edit", replace: true);
            }
            else
            {
                var result = await Transfers.UpdateAsync(new ProductionWorkOrderTransferUpdateRequest
                {
                    Id = Document.Id, ExpectedVersion = Document.RowVersion,
                    TargetWorkOrderMaterialId = Document.TargetWorkOrderMaterialId,
                    TransferDateTime = Document.TransferDateTime, Remark = Document.Remark
                });
                if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message ?? "Unable to save transfer.");
                Document = result.Data; Message = "Work Order Transfer updated.";
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected async Task RefreshSourceAsync()
    {
        if (Busy || Document.Id <= 0) return;
        Busy = true; Error = "";
        try
        {
            var result = await Transfers.RefreshSourceSnapshotAsync(Document.Id, Document.RowVersion);
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message ?? "Unable to refresh source WIP.");
            Document = result.Data; Message = "Source WIP quantity and value snapshot refreshed.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected async Task DeleteAsync()
    {
        if (Busy || Document.Id <= 0) return;
        Busy = true; Error = "";
        try
        {
            var result = await Transfers.DeleteAsync(Document.Id, Document.RowVersion);
            if (!result.Succeeded) throw new InvalidOperationException(result.Message ?? "Unable to delete transfer.");
            Nav.NavigateTo("/planning/work-order-transfer");
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected void RequestDelete() { if (CanEditFields && CanDeleteAccess) DeleteConfirmVisible = true; }
    protected void RequestPost() { if (Document.Id > 0 && Document.Status == "NEW" && CanPostAccess) PostConfirmVisible = true; }

    protected async Task PostAsync()
    {
        if (Busy || Document.Id <= 0) return;
        Busy = true; Error = "";
        try
        {
            var result = await Transfers.PostAsync(Document.Id, Document.RowVersion);
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message ?? "Unable to post transfer.");
            Document = result.Data; Mode = "view"; Message = "Work Order Transfer posted."; PostConfirmVisible = false;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
}
