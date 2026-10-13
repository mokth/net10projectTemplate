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

public partial class PrProductionStockAdjustmentList : PageBase, IDisposable
{
    [Inject] private IProductionStockAdjustmentService Adjustments { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    private DxGrid? _grid; private Timer? _debounce; private int _version; private readonly List<ProductionStockAdjustmentSummary> _selected=[];
    private readonly Dictionary<(int,string),ProductionStockAdjustmentCommand> _requests=[];
    protected bool Loading=true,Busy,CanAdd,CanEdit,CanPost,CanRollback,ConfirmVisible,FilterPopupVisible; protected int TotalCount;
    protected string SearchText="",StatusFilter="",DraftStatus="",ConfirmAction="",RollbackReason=""; protected string? StatusMessage;
    protected List<ProductionStockAdjustmentSummary> CompactRows=[]; protected ProductionStockAdjustmentGridDataSource DataSource=default!;
    protected string ConfirmCopy=>ConfirmAction=="ROLLBACK"?$"Roll back {_selected.Count} adjustment(s)? The Production pool correction will be reversed.":$"Post {_selected.Count} adjustment(s)? Production quantity and value will change without an Inventory movement.";
    protected bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText)||!string.IsNullOrWhiteSpace(StatusFilter);
    protected IReadOnlyList<StatusChoice> Statuses{get;}=[new("","All statuses"),new("NEW","New"),new("POSTED","Posted")];
    protected List<GridColumnData> Columns{get;}=[new(){Caption="Batch",FieldName=nameof(ProductionStockAdjustmentSummary.BatchNo),VisibleIndex=1,Size=GridColumnSize.DocumentNo},new(){Caption="Date",FieldName=nameof(ProductionStockAdjustmentSummary.EffectiveDate),DataType="date",DisplayFormat="dd MMM yyyy",VisibleIndex=2,Size=GridColumnSize.Date},new(){Caption="Work Order",FieldName=nameof(ProductionStockAdjustmentSummary.WorkOrderNo),VisibleIndex=3,Size=GridColumnSize.DocumentNo},new(){Caption="Items",FieldName=nameof(ProductionStockAdjustmentSummary.ItemSummary),VisibleIndex=4,Size=GridColumnSize.Description},new(){Caption="Qty",FieldName=nameof(ProductionStockAdjustmentSummary.QtySummary),VisibleIndex=5,Size=GridColumnSize.Quantity},new(){Caption="Status",FieldName=nameof(ProductionStockAdjustmentSummary.Status),VisibleIndex=6,Size=GridColumnSize.Status}];
    protected List<ButtonInfo> Buttons{get;private set;}=[]; protected List<ButtonInfo> ActionButtons{get;private set;}=[];
    protected override async Task OnPageInitializedAsync(){DataSource=new(LoadAsync);CanAdd=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Add);CanEdit=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Edit);CanPost=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Post);CanRollback=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Rollback);ActionButtons=[new(){Text="VIEW",IConClass="fa-regular fa-eye",Style="primary"},new(){Text="EDIT",IConClass="far fa-edit",Style="primary",Enabled=CanEdit,ToolTip="Edit draft"}];await ReloadAsync();Loading=false;}
    protected void OnGridInstance(DxGrid grid)=>_grid=grid; protected void OnSelections(List<ProductionStockAdjustmentSummary> rows){_selected.Clear();_selected.AddRange(rows);}
    protected Task OnSearchChanged(string value){SearchText=value??"";_debounce?.Stop();_debounce?.Dispose();_debounce=new Timer(400){AutoReset=false};var n=++_version;_debounce.Elapsed+=async(_,_)=>{if(n==_version)await InvokeAsync(ReloadAsync);};_debounce.Start();return Task.CompletedTask;}
    protected void OpenFilterPopup(){DraftStatus=StatusFilter;FilterPopupVisible=true;} protected async Task ApplyFiltersAsync(){StatusFilter=DraftStatus;FilterPopupVisible=false;await ReloadAsync();} protected async Task ClearFiltersAsync(){DraftStatus="";await ApplyFiltersAsync();}
    protected void OpenView(int id)=>Navigation.NavigateTo($"/planning/stock-adjustments/{id}/view");
    protected Task OnAction(SelectedButtonInfo<ProductionStockAdjustmentSummary> info){if(info.SelectedRow is null)return Task.CompletedTask;if(CanEdit&&(info.SelectedButton.Text??"").Equals("EDIT",StringComparison.OrdinalIgnoreCase)&&info.SelectedRow.Status=="NEW")Navigation.NavigateTo($"/planning/stock-adjustments/{info.SelectedRow.Id}/edit");else OpenView(info.SelectedRow.Id);return Task.CompletedTask;}
    protected Task OnButton(SelectedButtonInfo<ProductionStockAdjustmentSummary> info)
    {
        var action=(info.SelectedButton.Text??"").ToUpperInvariant();
        switch(action)
        {
            case "NEW":
                if(!CanAdd){StatusMessage="Access denied.";return Task.CompletedTask;}
                Navigation.NavigateTo("/planning/stock-adjustments/new");return Task.CompletedTask;
            case "POST":
                if(_selected.Count==0){StatusMessage="Select at least one adjustment.";return Task.CompletedTask;}
                if(!CanPost){StatusMessage="Access denied.";return Task.CompletedTask;}
                if(_selected.Any(x=>x.Status!="NEW")){StatusMessage="Only NEW Production Stock Adjustments can be posted.";return Task.CompletedTask;}
                break;
            case "ROLLBACK":
                if(_selected.Count==0){StatusMessage="Select at least one adjustment.";return Task.CompletedTask;}
                if(!CanRollback){StatusMessage="Access denied.";return Task.CompletedTask;}
                if(_selected.Any(x=>x.Status!="POSTED")){StatusMessage="Only POSTED Production Stock Adjustments can be rolled back.";return Task.CompletedTask;}
                break;
            default: ErrorMessage=$"Unsupported action '{action}'.";return Task.CompletedTask;
        }
        ConfirmAction=action;RollbackReason="";ConfirmVisible=true;return Task.CompletedTask;
    }
    protected async Task ConfirmAsync(){if(Busy)return;if(ConfirmAction=="ROLLBACK"&&string.IsNullOrWhiteSpace(RollbackReason)){ErrorMessage="Rollback reason is required.";return;}Busy=true;ErrorMessage=null;try{foreach(var row in _selected.ToArray()){var key=(row.Id,ConfirmAction);if(!_requests.TryGetValue(key,out var req))_requests[key]=req=new(row.Id,row.RowVersion,Guid.NewGuid(),ConfirmAction=="ROLLBACK"?RollbackReason.Trim():null);var result=ConfirmAction=="POST"?await Adjustments.PostAsync(req):await Adjustments.RollbackAsync(req);if(!result.Succeeded)throw new InvalidOperationException(result.Message);_requests.Remove(key);}ConfirmVisible=false;StatusMessage=ConfirmAction=="ROLLBACK"?$"Rolled back {_selected.Count} adjustment(s); each is open as NEW for editing and reposting.":$"POST completed for {_selected.Count} adjustment(s).";_selected.Clear();await ReloadAsync();}catch(Exception ex){ErrorMessage=ex.Message;}finally{Busy=false;}}
    private void RebuildButtons(){Buttons=[new(){Text="NEW",IConClass="fas fa-plus",Style="primary",Enabled=CanAdd},new(){Text="POST",IConClass="fas fa-check",Style="success",Enabled=CanPost},new(){Text="ROLLBACK",IConClass="fa-solid fa-rotate-left",Style="warning",Enabled=CanRollback}];}
    private async Task ReloadAsync(){DataSource?.Update(SearchText,StatusFilter);await RefreshCompactAsync();RebuildButtons();_grid?.Reload();await InvokeAsync(StateHasChanged);}
    private async Task<(IReadOnlyList<ProductionStockAdjustmentSummary>,int)> LoadAsync(ProductionStockAdjustmentQuery q,CancellationToken ct){var r=await Adjustments.SearchAsync(q,ct);if(!r.Succeeded||r.Data is null){await InvokeAsync(()=>ErrorMessage=r.Message);return([],0);}TotalCount=r.Data.TotalCount;return(r.Data.Rows,r.Data.TotalCount);}
    private async Task RefreshCompactAsync(){var q=DataSource.Current;q.Skip=0;q.Take=50;var r=await Adjustments.SearchAsync(q);CompactRows=r.Data?.Rows.ToList()??[];TotalCount=r.Data?.TotalCount??0;}
    public void Dispose(){_debounce?.Stop();_debounce?.Dispose();} protected sealed record StatusChoice(string Value,string Text);
}
public sealed class ProductionStockAdjustmentGridDataSource(Func<ProductionStockAdjustmentQuery,CancellationToken,Task<(IReadOnlyList<ProductionStockAdjustmentSummary>,int)>> loader):GridCustomDataSource
{
    private string? _search,_status;public ProductionStockAdjustmentQuery Current=>new(){SearchText=_search,Status=_status};public void Update(string s,string t){_search=string.IsNullOrWhiteSpace(s)?null:s.Trim();_status=string.IsNullOrWhiteSpace(t)?null:t;}
    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions o,CancellationToken ct){var q=Current;q.Take=1;return(await loader(q,ct)).Item2;}
    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions o,CancellationToken ct){var q=Current;q.Skip=Math.Max(0,o.StartIndex);q.Take=Math.Clamp(o.Count<=0?20:o.Count,1,100);return(await loader(q,ct)).Item1.ToList();}
}
