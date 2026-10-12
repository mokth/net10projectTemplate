using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrProductionStockReturnList : PageBase, IDisposable
{
    [Inject] private IProductionStockReturnService Returns { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    private DxGrid? _grid; private Timer? _debounce; private int _version;
    private readonly List<ProductionStockReturnSummary> _selected = [];
    private readonly Dictionary<(int, string), ProductionStockReturnCommand> _requests = [];
    protected bool Loading = true, Busy, CanAdd, CanEdit, CanPost, CanRollback, PostingEnabled, ConfirmVisible, FilterPopupVisible;
    protected int TotalCount; protected string SearchText = "", StatusFilter = ""; protected string ConfirmAction = "";
    protected string RollbackReason = "", DraftStatus = ""; protected string? StatusMessage; protected List<ProductionStockReturnSummary> CompactRows = [];
    protected ProductionStockReturnGridDataSource DataSource = default!;
    protected string ConfirmCopy => ConfirmAction == "ROLLBACK" ? $"Roll back {_selected.Count} return(s)? Inventory and Production effects will be reversed." : $"Post {_selected.Count} return(s)? Unused material will move back to its exact source Inventory balances.";
    protected bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(StatusFilter);
    protected IReadOnlyList<StatusChoice> Statuses { get; } = [new("", "All statuses"), new("NEW", "New"), new("POSTED", "Posted"), new("REVERSED", "Reversed")];
    protected List<GridColumnData> Columns { get; } = [
        new() { Caption="Batch", FieldName=nameof(ProductionStockReturnSummary.BatchNo), VisibleIndex=1, Size=GridColumnSize.DocumentNo },
        new() { Caption="Date", FieldName=nameof(ProductionStockReturnSummary.EffectiveDate), DataType="date", DisplayFormat="dd MMM yyyy", VisibleIndex=2, Size=GridColumnSize.Date },
        new() { Caption="Work Order", FieldName=nameof(ProductionStockReturnSummary.WorkOrderNo), VisibleIndex=3, Size=GridColumnSize.DocumentNo },
        new() { Caption="Items", FieldName=nameof(ProductionStockReturnSummary.ItemSummary), VisibleIndex=4, Size=GridColumnSize.Description },
        new() { Caption="Lots", FieldName=nameof(ProductionStockReturnSummary.LotSummary), VisibleIndex=5, Size=GridColumnSize.Reference },
        new() { Caption="Qty", FieldName=nameof(ProductionStockReturnSummary.QtySummary), VisibleIndex=6, Size=GridColumnSize.Quantity },
        new() { Caption="Status", FieldName=nameof(ProductionStockReturnSummary.Status), VisibleIndex=7, Size=GridColumnSize.Status }];
    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];
    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new(LoadAsync);
        CanAdd = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Edit);
        CanPost = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Post);
        CanRollback = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Rollback);
        ActionButtons = [new() { Text="VIEW", IConClass="fa-regular fa-eye", Style="primary", ToolTip="View Production Stock Return" }, new() { Text="EDIT", IConClass="far fa-edit", Style="primary", Enabled=CanEdit, ToolTip="Edit draft" }];
        await ReloadAsync(); Loading = false;
    }
    protected void OnGridInstance(DxGrid grid) => _grid = grid;
    protected void OnSelections(List<ProductionStockReturnSummary> rows) { _selected.Clear(); _selected.AddRange(rows); RebuildButtons(); }
    protected async Task OnSearchChanged(string value) { SearchText = value ?? ""; _debounce?.Stop(); _debounce?.Dispose(); _debounce = new Timer(400) { AutoReset=false }; var n=++_version; _debounce.Elapsed += async (_,_) => { if(n==_version) await InvokeAsync(ReloadAsync); }; _debounce.Start(); }
    protected Task OnStatusChanged(string value) { StatusFilter = value ?? ""; return ReloadAsync(); }
    protected Task NewAsync() { if (CanAdd) Navigation.NavigateTo("/planning/stock-returns/new"); return Task.CompletedTask; }
    protected void OpenView(int id) => Navigation.NavigateTo($"/planning/stock-returns/{id}/view");
    protected Task OnAction(SelectedButtonInfo<ProductionStockReturnSummary> info)
    {
        if (info.SelectedRow is null) return Task.CompletedTask;
        if (CanEdit && (info.SelectedButton.Text ?? "").Equals("EDIT", StringComparison.OrdinalIgnoreCase) && info.SelectedRow.Status == "NEW")
            Navigation.NavigateTo($"/planning/stock-returns/{info.SelectedRow.Id}/edit");
        else OpenView(info.SelectedRow.Id);
        return Task.CompletedTask;
    }
    protected Task OnButton(SelectedButtonInfo<ProductionStockReturnSummary> info)
    {
        var action=(info.SelectedButton.Text??"").ToUpperInvariant();
        switch (action)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access denied."; return Task.CompletedTask; }
                Navigation.NavigateTo("/planning/stock-returns/new");
                return Task.CompletedTask;
            case "POST":
                if (_selected.Count == 0) { StatusMessage = "Select at least one return."; return Task.CompletedTask; }
                if (!PostingEnabled || !CanPost || _selected.Any(x => x.Status != "NEW"))
                { StatusMessage = PostingEnabled ? "Select only NEW returns with POST permission." : "Return posting is disabled pending release acceptance."; return Task.CompletedTask; }
                break;
            case "ROLLBACK":
                if (_selected.Count == 0) { StatusMessage = "Select at least one return."; return Task.CompletedTask; }
                if (!CanRollback || _selected.Any(x => x.Status != "POSTED"))
                { StatusMessage = "Select only POSTED returns with ROLLBACK permission."; return Task.CompletedTask; }
                break;
            default:
                ErrorMessage = $"Unsupported action '{action}'.";
                return Task.CompletedTask;
        }
        ConfirmAction=action; RollbackReason=""; ConfirmVisible=true; return Task.CompletedTask;
    }
    protected async Task ConfirmAsync()
    {
        if (Busy) return; if (ConfirmAction=="ROLLBACK" && string.IsNullOrWhiteSpace(RollbackReason)) { ErrorMessage="Rollback reason is required."; return; }
        Busy=true; ErrorMessage=null;
        try { foreach(var row in _selected.ToArray()) { var key=(row.Id,ConfirmAction); if(!_requests.TryGetValue(key,out var req)) _requests[key]=req=new(row.Id,row.RowVersion,Guid.NewGuid(),ConfirmAction=="ROLLBACK"?RollbackReason.Trim():null);
                var result=ConfirmAction=="POST"?await Returns.PostAsync(req):await Returns.RollbackAsync(req); if(!result.Succeeded) throw new InvalidOperationException(result.Message); _requests.Remove(key); }
            ConfirmVisible=false; StatusMessage=$"{ConfirmAction} completed for {_selected.Count} return(s)."; _selected.Clear(); await ReloadAsync(); }
        catch(Exception ex) { ErrorMessage=ex.Message; } finally { Busy=false; }
    }
    private void RebuildButtons() { Buttons=[new(){Text="NEW",IConClass="fas fa-plus",Style="primary",Enabled=CanAdd},new(){Text="POST",IConClass="fas fa-check",Style="success",Enabled=CanPost&&PostingEnabled&&_selected.Count>0&&_selected.All(x=>x.Status=="NEW")},new(){Text="ROLLBACK",IConClass="fa-solid fa-rotate-left",Style="warning",Enabled=CanRollback&&_selected.Count>0&&_selected.All(x=>x.Status=="POSTED")}]; }
    protected void OpenFilterPopup() { DraftStatus = StatusFilter; FilterPopupVisible = true; }
    protected async Task ApplyFiltersAsync() { StatusFilter = DraftStatus; FilterPopupVisible = false; await ReloadAsync(); }
    protected async Task ClearFiltersAsync() { DraftStatus = ""; await ApplyFiltersAsync(); }
    private async Task ReloadAsync() { DataSource?.Update(SearchText,StatusFilter); await RefreshCompactAsync(); RebuildButtons(); _grid?.Reload(); await InvokeAsync(StateHasChanged); }
    private async Task<(IReadOnlyList<ProductionStockReturnSummary>,int)> LoadAsync(ProductionStockReturnQuery query,CancellationToken ct)
    { var r=await Returns.SearchAsync(query,ct); if(!r.Succeeded||r.Data is null) { await InvokeAsync(()=>ErrorMessage=r.Message); return([],0); } TotalCount=r.Data.TotalCount; PostingEnabled=r.Data.PostingEnabled; return(r.Data.Rows,r.Data.TotalCount); }
    private async Task RefreshCompactAsync() { var q=DataSource.Current; q.Skip=0; q.Take=50; var r=await Returns.SearchAsync(q); CompactRows=r.Data?.Rows.ToList()??[]; TotalCount=r.Data?.TotalCount??0; PostingEnabled=r.Data?.PostingEnabled??false; }
    public void Dispose(){_debounce?.Stop();_debounce?.Dispose();}
    protected sealed record StatusChoice(string Value,string Text);
}

public sealed class ProductionStockReturnGridDataSource(Func<ProductionStockReturnQuery,CancellationToken,Task<(IReadOnlyList<ProductionStockReturnSummary>,int)>> loader) : GridCustomDataSource
{
    private string? _search,_status;
    public ProductionStockReturnQuery Current => new(){SearchText=_search,Status=_status};
    public void Update(string search,string status){_search=string.IsNullOrWhiteSpace(search)?null:search.Trim();_status=string.IsNullOrWhiteSpace(status)?null:status;}
    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options,CancellationToken ct){var q=Current;q.Take=1;return(await loader(q,ct)).Item2;}
    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options,CancellationToken ct){var q=Current;q.Skip=Math.Max(0,options.StartIndex);q.Take=Math.Clamp(options.Count<=0?20:options.Count,1,100);return(await loader(q,ct)).Item1.ToList();}
}
