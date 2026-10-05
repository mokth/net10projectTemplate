using System.Text.Json;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrFinishedGoodReceiptEntry : PageBase, IDisposable
{
    [Parameter] public int Id { get; set; }
    [Inject] private IProductionFinishedGoodReceiptService Receipts { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    protected FinishedGoodReceiptDocument Document = new();
    protected List<FinishedGoodSourceRow> Sources = [];
    protected int SourceCount;
    protected string SourceSearch = "", Action = "", Reason = "";
    protected bool Busy, ConfirmVisible, CanAdd, CanEdit, CanDelete, CanPost, CanRollback;
    private string _saved = "";
    private bool _navigating;
    private CancellationTokenSource? _search;
    private FinishedGoodReceiptCommand? _pending;
    protected bool Editable => Document.Status == "NEW" && (Id == 0 ? CanAdd : CanEdit) && !Navigation.Uri.EndsWith("/view", StringComparison.OrdinalIgnoreCase);
    protected bool Dirty => Editable && _saved != JsonSerializer.Serialize(ToRequest());
    protected override async Task OnParametersSetAsync()
    {
        CanAdd = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Edit);
        CanDelete = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Delete);
        CanPost = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Post);
        CanRollback = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Rollback);
        if (Id > 0)
        {
            var result = await Receipts.GetAsync(Id);
            if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message; return; }
            Document = result.Data;
        }
        else Document = new() { EffectiveDate = Clock.Now };
        _saved = JsonSerializer.Serialize(ToRequest());
        if (Editable) await SearchSources("");
    }
    protected async Task SearchSources(string text)
    {
        SourceSearch = text; _search?.Cancel(); _search?.Dispose(); _search = new(); var ct = _search.Token;
        try
        {
            await Task.Delay(300, ct);
            var result = await Receipts.SearchSourcesAsync(new() { SearchText = text, WorkOrderId = Document.WorkOrderId == 0 ? null : Document.WorkOrderId, Take = 30 }, ct);
            Sources = result.Data?.Rows.ToList() ?? []; SourceCount = result.Data?.TotalCount ?? 0;
            if (!result.Succeeded) ErrorMessage = result.Message;
        }
        catch (OperationCanceledException) { }
    }
    protected void AddSource(FinishedGoodSourceRow source)
    {
        if (Document.WorkOrderId != 0 && Document.WorkOrderId != source.WorkOrderId) return;
        Document.WorkOrderId = source.WorkOrderId; Document.WorkOrderNo = source.WorkOrderNo;
        Document.Lines.Add(new() { ProductionBalLotId = source.Id, ItemCode = source.ItemCode, SourceLot = source.LotNo, SourceUom = source.Uom,
            AvailableQty = source.AvailableQty, LotNo = source.LotNo, WorkCentre = source.WorkCentre, Process = source.Process });
    }
    protected void Remove(FinishedGoodReceiptLine line) { Document.Lines.Remove(line); if (Document.Lines.Count == 0) Document.WorkOrderId = 0; }
    private FinishedGoodReceiptSaveRequest ToRequest() => new() { Id = Document.Id, ExpectedVersion = Document.RowVersion, WorkOrderId = Document.WorkOrderId,
        EffectiveDate = Document.EffectiveDate, RefNo = Document.RefNo, Remarks = Document.Remarks,
        Lines = Document.Lines.Select(x => new FinishedGoodReceiptLineInput { ProductionBalLotId = x.ProductionBalLotId, Quantity = x.Quantity,
            Warehouse = x.Warehouse, Location = x.Location, LotNo = x.LotNo, ExpiryDate = x.ExpiryDate }).ToList() };
    protected async Task Save()
    {
        if (Busy) return; Busy = true; ErrorMessage = null;
        try
        {
            var result = await Receipts.SaveAsync(ToRequest());
            if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message; return; }
            Document = result.Data; _saved = JsonSerializer.Serialize(ToRequest()); _pending = null;
            if (Id == 0) { _navigating = true; Navigation.NavigateTo($"/planning/finished-good-receipts/{Document.Id}/edit"); }
        }
        catch (Exception) { ErrorMessage = "Unable to save the receipt. Reload the list before retrying if the response was lost."; }
        finally { Busy = false; }
    }
    protected void Confirm(string action)
    {
        if (action == "POST" && Dirty) { ErrorMessage = "Save the draft before posting."; return; }
        Action = action; ConfirmVisible = true;
    }
    protected async Task Apply()
    {
        if (Busy) return;
        if (Action == "ROLLBACK" && string.IsNullOrWhiteSpace(Reason)) { ErrorMessage = "Enter a rollback reason."; return; }
        Busy = true; ErrorMessage = null;
        try
        {
            if (Action == "DELETE")
            {
                var deleted = await Receipts.DeleteAsync(Id, Document.RowVersion);
                if (!deleted.Succeeded) { ErrorMessage = deleted.Message; return; }
                _navigating = true; Back(); return;
            }
            _pending ??= new(Id, Document.RowVersion, Guid.NewGuid(), Action == "ROLLBACK" ? Reason : null);
            var result = Action == "POST" ? await Receipts.PostAsync(_pending) : await Receipts.RollbackAsync(_pending);
            if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message; _pending = null; return; }
            Document = result.Data; _saved = JsonSerializer.Serialize(ToRequest()); _pending = null; ConfirmVisible = false;
        }
        catch (Exception) { ErrorMessage = "The response could not be received. Retry to resolve the same request safely."; }
        finally { Busy = false; }
    }
    protected async Task Correction()
    {
        if (Busy) return; Busy = true;
        try
        {
            var result = await Receipts.CreateCorrectionAsync(Id);
            if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message; return; }
            _navigating = true; Navigation.NavigateTo($"/planning/finished-good-receipts/{result.Data.Id}/edit");
        }
        finally { Busy = false; }
    }
    protected void Back() => Navigation.NavigateTo("/planning/finished-good-receipts");
    protected async Task BeforeNavigate(LocationChangingContext context)
    {
        if (!_navigating && Dirty && !await Js.InvokeAsync<bool>("confirm", "Discard unsaved receipt changes?")) context.PreventNavigation();
    }
    public void Dispose() { _search?.Cancel(); _search?.Dispose(); }
}
