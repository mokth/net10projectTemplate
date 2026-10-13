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
    protected List<ProductionWorkOrderTransferWorkOrderChoice> SourceWorkOrders { get; set; } = [];
    protected List<ProductionWorkOrderTransferWorkOrderChoice> TargetWorkOrders { get; set; } = [];
    protected ProductionWorkOrderTransferSource? SelectedSource { get; set; }
    protected ProductionWorkOrderTransferTarget? SelectedTarget { get; set; }
    protected string? SourceWorkOrderNo, TargetWorkOrderNo;
    protected string SourceSearch = "", Error = "", Message = "";
    protected bool Loading = true, Busy, SourceLoading, TargetLoading, CanAdd, CanEdit, CanDeleteAccess, CanPostAccess, CanRollbackAccess, CanViewCostAccess;
    protected bool TargetPickerVisible, PostConfirmVisible, RollbackConfirmVisible, DeleteConfirmVisible;
    protected string RollbackReason = "";
    protected int SourceSkip, TargetSkip, SourceTotal, TargetTotal;
    protected const int LookupPageSize = 10;
    private CancellationTokenSource? _sourceSearchCts;
    private ProductionWorkOrderTransferCommand? _pendingPost, _pendingRollback;
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
        CanRollbackAccess = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Rollback);
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

    protected Task SourceSearchChanged(string value) { SourceSearch = value ?? ""; SourceSkip = 0; return DebounceSourceSearchAsync(); }
    protected void BackToList() => Nav.NavigateTo("/planning/work-order-transfer");

    private async Task LoadSourcesAsync()
    {
        SourceLoading = true;
        try
        {
            var result = await Transfers.SearchEligibleSourcesAsync(new ProductionWorkOrderTransferSourceQuery
            { WorkOrderNo = SourceWorkOrderNo, SearchText = SourceSearch, Skip = SourceSkip, Take = LookupPageSize });
            if (!result.Succeeded || result.Data is null) { Error = result.Message ?? "Unable to load eligible WIP."; Sources = []; SourceTotal = 0; }
            else
            {
                Sources = result.Data.Rows.ToList(); SourceTotal = result.Data.TotalCount;
                if (SourceWorkOrderNo is null || SourceWorkOrders.Count == 0)
                    SourceWorkOrders = result.Data.WorkOrders.ToList();
            }
        }
        finally { SourceLoading = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadTargetsAsync()
    {
        var sourceId = SelectedSource?.ProductionBalLotId ?? Document.SourceProductionBalLotId;
        if (sourceId <= 0) return;
        TargetLoading = true;
        try
        {
            var result = await Transfers.SearchEligibleTargetsAsync(sourceId,
                new ProductionWorkOrderTransferTargetQuery { WorkOrderNo = TargetWorkOrderNo, Skip = TargetSkip, Take = LookupPageSize });
            if (!result.Succeeded || result.Data is null) { Error = result.Message ?? "Unable to load compatible target requirements."; Targets = []; TargetTotal = 0; }
            else
            {
                Targets = result.Data.Rows.ToList(); TargetTotal = result.Data.TotalCount;
                TargetWorkOrders = result.Data.WorkOrders.ToList();
            }
        }
        finally { TargetLoading = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task DebounceSourceSearchAsync()
    {
        _sourceSearchCts?.Cancel(); _sourceSearchCts?.Dispose();
        var tokenSource = _sourceSearchCts = new CancellationTokenSource();
        try { await Task.Delay(350, tokenSource.Token); await LoadSourcesAsync(); }
        catch (OperationCanceledException) { }
    }

    protected Task PreviousSourcesAsync() { SourceSkip = Math.Max(0, SourceSkip - LookupPageSize); return LoadSourcesAsync(); }
    protected Task NextSourcesAsync() { SourceSkip += LookupPageSize; return LoadSourcesAsync(); }
    protected Task PreviousTargetsAsync() { TargetSkip = Math.Max(0, TargetSkip - LookupPageSize); return LoadTargetsAsync(); }
    protected Task NextTargetsAsync() { TargetSkip += LookupPageSize; return LoadTargetsAsync(); }

    protected async Task SelectSourceAsync(ProductionWorkOrderTransferSource source)
    {
        if (!CanEditFields || Id != 0) return;
        SelectedSource = source; SelectedTarget = null; Targets = []; TargetWorkOrders = []; TargetWorkOrderNo = null;
        TargetSkip = 0; TargetTotal = 0;
        Document.CanViewCost = CanViewCostAccess;
        Document.TransferTotalCost = source.TotalCost;
        await LoadTargetsAsync();
    }

    protected async Task SourceWorkOrderChanged(string? value)
    {
        if (!CanEditFields || Id != 0 || Busy) return;
        SourceWorkOrderNo = value;
        SourceSearch = ""; SourceSkip = 0; SourceTotal = 0;
        SelectedSource = null; SelectedTarget = null; Targets = []; TargetWorkOrders = [];
        TargetWorkOrderNo = null; TargetSkip = 0; TargetTotal = 0;
        Document.TransferTotalCost = null;
        await LoadSourcesAsync();
    }

    protected async Task TargetWorkOrderChanged(string? value)
    {
        if (!CanEditFields || Busy) return;
        TargetWorkOrderNo = value;
        SelectedTarget = null; Targets = []; TargetSkip = 0; TargetTotal = 0;
        await LoadTargetsAsync();
    }

    protected void ChangeSource()
    {
        if (!CanEditFields || Id != 0 || Busy) return;
        SelectedSource = null;
        SelectedTarget = null;
        Targets = [];
        TargetWorkOrders = [];
        TargetWorkOrderNo = null;
        TargetSkip = 0;
        TargetTotal = 0;
        Document.TransferTotalCost = null;
    }

    protected async Task OpenTargetPickerAsync()
    {
        if (!CanEditFields) return;
        TargetPickerVisible = true; SelectedTarget = null; Targets = []; TargetWorkOrders = [];
        TargetWorkOrderNo = Document.TargetWorkOrderNo;
        await LoadTargetsAsync();
    }

    protected void CancelTargetPicker()
    {
        TargetPickerVisible = false; SelectedTarget = null;
        TargetWorkOrderNo = Document.TargetWorkOrderNo;
    }

    protected void SelectTarget(ProductionWorkOrderTransferTarget target)
    {
        if (!CanEditFields) return;
        SelectedTarget = target;
        TargetWorkOrderNo = target.WorkOrderNo;
        Document.TargetWorkOrderId = target.WorkOrderId;
        Document.TargetWorkOrderNo = target.WorkOrderNo;
        Document.TargetWorkOrderMaterialId = target.WorkOrderMaterialId;
        Document.TargetConsumingWorkCentreCode = target.ConsumingWorkCentreCode;
        Document.TargetConsumingProcessCode = target.ConsumingProcessCode;
        Document.TargetProducerWorkCentreCode = target.ProducerWorkCentreCode;
        Document.TargetProducerProcessCode = target.ProducerProcessCode;
        Document.TargetCurrentWipValue = target.TargetCurrentWipValue;
        Document.ProjectedTargetWipValue = target.ProjectedTargetWipValue;
        Document.TargetSequenceReady = target.SequenceReady;
        Document.TargetSequenceBlockingReason = target.BlockingReason;
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
                _pendingPost = null; _pendingRollback = null;
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
                _pendingPost = null; _pendingRollback = null;
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
            _pendingPost = null;
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
    protected void RequestPost()
    {
        if (Document.Id > 0 && Document.Status == "NEW" && CanPostAccess)
        {
            _pendingPost ??= new ProductionWorkOrderTransferCommand(Document.Id, Document.RowVersion.ToArray(), Guid.NewGuid());
            PostConfirmVisible = true;
        }
    }

    protected void RequestRollback()
    {
        if (Document.Id > 0 && Document.Status == "POSTED" && CanRollbackAccess)
        {
            if (_pendingRollback is null)
                RollbackReason = "";
            _pendingRollback ??= new ProductionWorkOrderTransferCommand(Document.Id, Document.RowVersion.ToArray(), Guid.NewGuid());
            if (!string.IsNullOrEmpty(_pendingRollback.Reason))
                RollbackReason = _pendingRollback.Reason;
            RollbackConfirmVisible = true;
        }
    }

    protected void RollbackReasonChanged(string value)
    {
        RollbackReason = value ?? "";
        if (_pendingRollback is not null)
            _pendingRollback = null;
    }

    protected async Task PostAsync()
    {
        if (Busy || Document.Id <= 0) return;
        Busy = true; Error = "";
        try
        {
            _pendingPost ??= new ProductionWorkOrderTransferCommand(Document.Id, Document.RowVersion.ToArray(), Guid.NewGuid());
            var result = await Transfers.PostAsync(_pendingPost);
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message ?? "Unable to post transfer.");
            Document = result.Data; Mode = "view"; Message = "Work Order Transfer posted."; PostConfirmVisible = false; _pendingPost = null;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected async Task RollbackAsync()
    {
        if (Busy || Document.Id <= 0 || !CanRollbackAccess) return;
        var reason = RollbackReason.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            Error = "Enter a rollback reason of at most 500 characters.";
            return;
        }
        Busy = true; Error = "";
        try
        {
            _pendingRollback ??= new ProductionWorkOrderTransferCommand(Document.Id, Document.RowVersion.ToArray(), Guid.NewGuid(), reason);
            if (!string.Equals(_pendingRollback.Reason, reason, StringComparison.Ordinal))
                _pendingRollback = new ProductionWorkOrderTransferCommand(Document.Id, Document.RowVersion.ToArray(), Guid.NewGuid(), reason);
            var result = await Transfers.RollbackAsync(_pendingRollback);
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message ?? "Unable to roll back transfer.");
            Document = result.Data; Mode = CanEdit ? "edit" : "view"; Message = "Work Order Transfer rolled back.";
            RollbackConfirmVisible = false; _pendingRollback = null; RollbackReason = "";
            Nav.NavigateTo($"/planning/work-order-transfer/{Document.Id}/{Mode}", replace: true);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
}
