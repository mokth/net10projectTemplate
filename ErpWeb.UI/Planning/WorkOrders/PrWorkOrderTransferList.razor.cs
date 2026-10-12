using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrWorkOrderTransferList
{
    [Inject] private IProductionWorkOrderTransferService Transfers { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    protected List<ProductionWorkOrderTransferSummary> Rows { get; set; } = [];
    protected List<string> Statuses { get; } = ["NEW", "POSTED"];
    protected bool Loading = true, Busy, CanAdd, CanEdit, CanPost, CanDelete;
    protected string SearchText = "", StatusFilter = "", Message = "", Error = "";
    protected int TotalCount, Skip, PageSize = 30;
    protected ProductionWorkOrderTransferSummary? ConfirmRow;
    protected bool ConfirmPost;

    protected override async Task OnPageInitializedAsync()
    {
        CanAdd = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Edit);
        CanPost = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Post);
        CanDelete = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Delete);
        await ReloadAsync();
    }

    protected Task SearchChanged(string value)
    {
        SearchText = value ?? string.Empty;
        Skip = 0;
        return ReloadAsync();
    }

    protected Task ReloadAsync() => LoadPageAsync();
    protected Task PreviousAsync() { Skip = Math.Max(0, Skip - PageSize); return LoadPageAsync(); }
    protected Task NextAsync() { Skip += PageSize; return LoadPageAsync(); }
    protected Task NewAsync() { Nav.NavigateTo("/planning/work-order-transfer/new"); return Task.CompletedTask; }
    protected void Open(long id, bool edit) => Nav.NavigateTo($"/planning/work-order-transfer/{id}/{(edit ? "edit" : "view")}");
    protected void BeginPost(ProductionWorkOrderTransferSummary row) { ConfirmRow = row; ConfirmPost = true; }
    protected void BeginDelete(ProductionWorkOrderTransferSummary row) { ConfirmRow = row; ConfirmPost = false; }
    protected void CancelConfirm() { ConfirmRow = null; }

    protected async Task ConfirmAsync()
    {
        if (ConfirmRow is null || Busy) return;
        Busy = true; Error = ""; Message = "";
        try
        {
            if (ConfirmPost)
            {
                var result = await Transfers.PostAsync(ConfirmRow.Id, ConfirmRow.RowVersion);
                if (!result.Succeeded) throw new InvalidOperationException(result.Message ?? "The action failed.");
            }
            else
            {
                var result = await Transfers.DeleteAsync(ConfirmRow.Id, ConfirmRow.RowVersion);
                if (!result.Succeeded) throw new InvalidOperationException(result.Message ?? "The action failed.");
            }
            Message = ConfirmPost ? $"{ConfirmRow.TransferNo} posted." : $"{ConfirmRow.TransferNo} deleted.";
            ConfirmRow = null;
            await LoadPageAsync();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    private async Task LoadPageAsync()
    {
        Loading = true; Error = "";
        var result = await Transfers.SearchAsync(new ProductionWorkOrderTransferQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = string.IsNullOrWhiteSpace(StatusFilter) ? null : StatusFilter,
            Skip = Skip, Take = PageSize
        });
        if (!result.Succeeded || result.Data is null)
        {
            Error = result.Message ?? "Unable to load Work Order Transfers.";
            Rows = []; TotalCount = 0;
        }
        else { Rows = result.Data.Rows.ToList(); TotalCount = result.Data.TotalCount; }
        Loading = false;
        await InvokeAsync(StateHasChanged);
    }
}
