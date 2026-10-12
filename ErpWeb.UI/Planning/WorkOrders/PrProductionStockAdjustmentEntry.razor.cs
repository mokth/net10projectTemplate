using System.Text.Json;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrProductionStockAdjustmentEntry : PageBase
{
    [Parameter] public int Id { get; set; }
    [Inject] private IProductionStockAdjustmentService Adjustments { get; set; } = default!;
    [Inject] private IProductionWorkOrderService WorkOrders { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    private string? _loadedKey; private string _saved=""; private ProductionStockAdjustmentCommand? _pending;
    protected ProductionStockAdjustmentDocument Document=new(){EffectiveDate=DateTime.Today,Status="NEW"}; protected ProductionWorkOrderDetail? WorkOrder;
    protected List<ProductionStockAdjustmentSource> Sources=[]; protected List<AdjustmentEditor> Editors=[];
    protected string WorkOrderInput="",SourceSearch="",RollbackReason=""; protected bool BusyLoading=true,Busy,CanAdd,CanEdit,CanDeleteAccess,CanPostAccess,CanRollbackAccess,CanPriceOverride,RollbackVisible;
    protected string? StatusMessage;
    protected IReadOnlyList<string> ReasonOptions{get;}=ProductionStockAdjustmentReasons.All;
    protected IReadOnlyList<string> CostEvidenceOptions{get;}=[InventoryCostEvidenceTypes.ManualApproved,InventoryCostEvidenceTypes.ZeroCostApproved];
    protected bool IsView=>Navigation.RelativePath.Contains("/view",StringComparison.OrdinalIgnoreCase)||Id>0&&Document.Status!="NEW";
    protected bool IsEditRequested=>Navigation.RelativePath.Contains("/edit",StringComparison.OrdinalIgnoreCase);
    protected bool CanEditFields=>!IsView&&Document.Status=="NEW"&&(Id==0?CanAdd:CanEdit);
    protected bool CanSave=>CanEditFields&&WorkOrder is not null&&Editors.Count>0&&Editors.All(x=>x.AdjustmentQty!=0m);
    protected bool Dirty=>CanEditFields&&_saved.Length>0&&_saved!=JsonSerializer.Serialize(ToRequest());
    protected bool CanPost=>Id>0&&Document.Status=="NEW"&&CanPostAccess&&Document.PostingEnabled&&Document.ReadinessErrors.Count==0&&!Dirty;
    protected bool CanRollback=>Id>0&&Document.Status=="POSTED"&&CanRollbackAccess;
    protected bool CanDelete=>Id>0&&Document.Status=="NEW"&&CanDeleteAccess;
    protected bool CanCorrection=>Id>0&&Document.Status=="REVERSED"&&CanAdd;
    protected bool CanEditDraft=>Id>0&&Document.Status=="NEW"&&IsView&&CanEdit;
    protected string Heading=>Id==0?"New Production Stock Adjustment":CanEditFields?"Edit Production Stock Adjustment":"View Production Stock Adjustment";

    protected override async Task OnParametersSetAsync()
    {
        var key=$"{Id}|{Navigation.RelativePath}";if(_loadedKey==key)return;_loadedKey=key;BusyLoading=true;ErrorMessage=null;StatusMessage=null;
        CanAdd=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Add);CanEdit=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Edit);
        CanDeleteAccess=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Delete);CanPostAccess=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Post);
        CanRollbackAccess=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.Rollback);CanPriceOverride=await Access.CanAsync(MenuCodes.PlanningStockAdjustment,PermissionCodes.PriceOverride);
        if(Id>0){var r=await Adjustments.GetAsync(Id);if(!r.Succeeded||r.Data is null){ErrorMessage=r.Message;BusyLoading=false;return;}Document=r.Data;WorkOrderInput=Document.WorkOrderNo;
            var wo=await WorkOrders.GetAsync(Document.WorkOrderNo);if(wo.Succeeded)WorkOrder=wo.Data;
            if(IsEditRequested&&Document.Status=="NEW"&&!CanEdit){Navigation.NavigateTo($"/planning/stock-adjustments/{Id}/view",replace:true);return;}
            Editors=Document.Lines.Select(x=>new AdjustmentEditor(new ProductionStockAdjustmentSource(x.ProductionBalLotId,Document.WorkOrderNo,x.ItemCode,x.Description,x.PoolKind,x.LotNo,x.CurrentQty,x.Uom,x.BaseQty,x.BaseUom,"Saved historical source",null,false),x.AdjustmentQty,x.Reason,x.Remarks,x.ProposedUnitCost,x.CostEvidenceType,x.CostOverrideReason)).ToList();
        }else Document.EffectiveDate=Clock.Now;
        _saved=JsonSerializer.Serialize(ToRequest());BusyLoading=false;
    }
    protected async Task LoadWorkOrderAsync(){if(string.IsNullOrWhiteSpace(WorkOrderInput)){ErrorMessage="Enter a Work Order number.";return;}Busy=true;ErrorMessage=null;try{var r=await WorkOrders.GetAsync(WorkOrderInput.Trim());if(!r.Succeeded||r.Data is null)throw new InvalidOperationException(r.Message);if(r.Data.Status is not(ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))throw new InvalidOperationException("Work Order must be RELEASED or IN_PROGRESS.");WorkOrder=r.Data;Document.WorkOrderId=r.Data.Uid;Document.WorkOrderNo=r.Data.WorkOrderNo;Document.SnapshotRevision=r.Data.SnapshotRevision;Document.SnapshotHash=r.Data.SnapshotHash;Sources=[];await SearchPoolsAsync();}catch(Exception ex){ErrorMessage=ex.Message;WorkOrder=null;}finally{Busy=false;}}
    protected async Task SearchPoolsAsync(){if(WorkOrder is null)return;var r=await Adjustments.SearchSourcesAsync(new(){WorkOrderId=WorkOrder.Uid,SearchText=string.IsNullOrWhiteSpace(SourceSearch)?null:SourceSearch.Trim(),Take=100});if(!r.Succeeded||r.Data is null){ErrorMessage=r.Message;return;}Sources=r.Data.Rows.ToList();}
    protected void AddSource(ProductionStockAdjustmentSource source){if(WorkOrder is null||source.Readiness!="READY"||Editors.Any(x=>x.Source.ProductionBalLotId==source.ProductionBalLotId))return;Editors.Add(new(source,0m,ProductionStockAdjustmentReasons.Count,"",null,null,""));}
    protected void ChangeWorkOrder(){WorkOrder=null;Document.WorkOrderId=0;Document.WorkOrderNo="";Document.SnapshotRevision=0;Document.SnapshotHash="";Editors=[];Sources=[];}
    protected async Task SaveAsync(){if(!CanSave||Busy)return;Busy=true;ErrorMessage=null;try{var r=await Adjustments.SaveAsync(ToRequest());if(!r.Succeeded||r.Data is null)throw new InvalidOperationException(r.Message);Document=r.Data;_saved=JsonSerializer.Serialize(ToRequest());Navigation.NavigateTo($"/planning/stock-adjustments/{Document.Id}/view",replace:true);}catch(Exception ex){ErrorMessage=ex.Message;}finally{Busy=false;}}
    protected async Task PostAsync(){if(!CanPost||Busy)return;Busy=true;try{_pending??=new(Document.Id,Document.RowVersion,Guid.NewGuid());var r=await Adjustments.PostAsync(_pending);if(!r.Succeeded||r.Data is null)throw new InvalidOperationException(r.Message);Document=r.Data;_pending=null;StatusMessage="Production Stock Adjustment posted.";}catch(Exception ex){ErrorMessage=ex.Message;}finally{Busy=false;}}
    protected async Task RollbackAsync(){if(!CanRollback||string.IsNullOrWhiteSpace(RollbackReason)||Busy){ErrorMessage="Rollback reason is required.";return;}Busy=true;try{_pending??=new(Document.Id,Document.RowVersion,Guid.NewGuid(),RollbackReason.Trim());var r=await Adjustments.RollbackAsync(_pending);if(!r.Succeeded||r.Data is null)throw new InvalidOperationException(r.Message);Document=r.Data;_pending=null;RollbackVisible=false;StatusMessage="Production Stock Adjustment reversed.";}catch(Exception ex){ErrorMessage=ex.Message;}finally{Busy=false;}}
    protected async Task DeleteAsync(){if(!CanDelete||Busy)return;Busy=true;try{var r=await Adjustments.DeleteAsync(Id,Document.RowVersion);if(!r.Succeeded)throw new InvalidOperationException(r.Message);Navigation.NavigateTo("/planning/stock-adjustments");}catch(Exception ex){ErrorMessage=ex.Message;}finally{Busy=false;}}
    protected async Task CreateCorrectionAsync(){if(!CanCorrection||Busy)return;Busy=true;try{var r=await Adjustments.CreateCorrectionAsync(Id);if(!r.Succeeded||r.Data is null)throw new InvalidOperationException(r.Message);Navigation.NavigateTo($"/planning/stock-adjustments/{r.Data.Id}/edit");}catch(Exception ex){ErrorMessage=ex.Message;}finally{Busy=false;}}
    protected void EditDraft()=>Navigation.NavigateTo($"/planning/stock-adjustments/{Id}/edit");
    protected async Task BeforeNavigate(LocationChangingContext context){if(Dirty&&!await Js.InvokeAsync<bool>("confirm","Discard unsaved Production Stock Adjustment changes?"))context.PreventNavigation();}
    protected Task BackAsync(){Navigation.NavigateTo("/planning/stock-adjustments");return Task.CompletedTask;}
    private ProductionStockAdjustmentSaveRequest ToRequest()=>new(){Id=Document.Id,ExpectedVersion=Document.RowVersion,WorkOrderId=WorkOrder?.Uid??Document.WorkOrderId,SnapshotRevision=Document.SnapshotRevision,SnapshotHash=Document.SnapshotHash,EffectiveDate=Document.EffectiveDate,RefNo=Document.RefNo,Remarks=Document.Remarks,Lines=Editors.Select(x=>new ProductionStockAdjustmentLineInput{ProductionBalLotId=x.Source.ProductionBalLotId,AdjustmentQty=x.AdjustmentQty,Reason=x.Reason,Remarks=x.Remarks,ProposedUnitCost=x.ProposedUnitCost,CostEvidenceType=x.CostEvidenceType,CostOverrideReason=x.CostOverrideReason}).ToList()};
    protected sealed class AdjustmentEditor(ProductionStockAdjustmentSource source,decimal qty,string reason,string? remarks,decimal? cost,string? evidence,string? costReason){public ProductionStockAdjustmentSource Source{get;}=source;public decimal AdjustmentQty{get;set;}=qty;public string Reason{get;set;}=reason;public string? Remarks{get;set;}=remarks;public decimal? ProposedUnitCost{get;set;}=cost;public string? CostEvidenceType{get;set;}=evidence;public string? CostOverrideReason{get;set;}=costReason;}
}
