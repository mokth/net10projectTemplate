using System.Collections;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrFinishedGoodReceiptList : PageBase, IDisposable
{
    [Inject] private IProductionFinishedGoodReceiptService Receipts { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    protected string SearchText = "", Status = "All", ConfirmAction = "", Reason = "";
    protected string[] Statuses = ["All", "NEW", "POSTED", "REVERSED"];
    protected bool CanAdd, Busy, ConfirmVisible;
    protected int TotalCount;
    protected List<FinishedGoodReceiptSummary> CompactRows = [], Selected = [];
    protected List<ButtonInfo> Buttons = [], Actions = [];
    private readonly Dictionary<(int, string), FinishedGoodReceiptCommand> _requests = [];
    private CancellationTokenSource? _debounce;
    private DxGrid? _grid;
    protected FinishedGoodGridDataSource? DataSource;
    protected List<GridColumnData> Columns = [
        new() { Caption = "Receipt", FieldName = "BatchNo", Width = "120px" },
        new() { Caption = "Date", FieldName = "EffectiveDate", DataType = "date", DisplayFormat = "dd MMM yyyy", Width = "140px" },
        new() { Caption = "Work Order", FieldName = "WorkOrderNo", Width = "180px" },
        new() { Caption = "Status", FieldName = "Status", Width = "120px" }
    ];
    protected override async Task OnPageInitializedAsync()
    {
        CanAdd = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Add);
        Buttons = [new() { Text = "NEW", Enabled = CanAdd },
            new() { Text = "POST", Enabled = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Post) },
            new() { Text = "ROLLBACK", Enabled = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Rollback) }];
        Actions = [new() { Text = "VIEW" }, new() { Text = "EDIT", Enabled = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Edit) }];
        DataSource = new(LoadPage); await Reload();
    }
    protected void GridReady(DxGrid grid) => _grid = grid;
    protected void SelectionChanged(List<FinishedGoodReceiptSummary> rows) => Selected = rows;
    protected void NewReceipt() => Navigation.NavigateTo("/planning/finished-good-receipts/new");
    protected void Open(int id) => Navigation.NavigateTo($"/planning/finished-good-receipts/{id}/view");
    protected void ActionClicked(SelectedButtonInfo<FinishedGoodReceiptSummary> info)
    {
        if (info.SelectedRow is not { } row) return;
        Navigation.NavigateTo($"/planning/finished-good-receipts/{row.Id}/{(info.SelectedButton.Text == "EDIT" && row.Status == "NEW" ? "edit" : "view")}");
    }
    protected void ButtonClicked(SelectedButtonInfo<FinishedGoodReceiptSummary> info)
    {
        var action = info.SelectedButton.Text;
        if (action == "NEW") { NewReceipt(); return; }
        if (Selected.Count == 0 || Selected.Count > 50) { ErrorMessage = "Select between one and 50 receipts."; return; }
        if (Selected.Any(x => x.Status != (action == "POST" ? "NEW" : "POSTED"))) { ErrorMessage = "Select receipts with the appropriate status."; return; }
        ConfirmAction = action ?? ""; Reason = ""; ConfirmVisible = true;
    }
    protected async Task ApplyAction()
    {
        if (Busy) return;
        if (ConfirmAction == "ROLLBACK" && string.IsNullOrWhiteSpace(Reason)) { ErrorMessage = "Enter a rollback reason."; return; }
        Busy = true; ErrorMessage = null;
        try
        {
            foreach (var row in Selected.ToArray())
            {
                var key = (row.Id, ConfirmAction);
                if (!_requests.TryGetValue(key, out var request)) _requests[key] = request = new(row.Id, row.RowVersion, Guid.NewGuid(), ConfirmAction == "ROLLBACK" ? Reason : null);
                var result = ConfirmAction == "POST" ? await Receipts.PostAsync(request) : await Receipts.RollbackAsync(request);
                if (!result.Succeeded) { ErrorMessage = result.Message; break; }
                _requests.Remove(key);
            }
            ConfirmVisible = false; await Reload();
        }
        catch (Exception) { ErrorMessage = "The response could not be received. Retry with the same action to resolve its saved request."; }
        finally { Busy = false; }
    }
    protected async Task SearchChanged(string value)
    {
        SearchText = value; _debounce?.Cancel(); _debounce?.Dispose(); _debounce = new(); var token = _debounce.Token;
        try { await Task.Delay(350, token); await Reload(); } catch (OperationCanceledException) { }
    }
    protected async Task FilterChanged(string value) { Status = value; await Reload(); }
    private async Task<(IReadOnlyList<FinishedGoodReceiptSummary>, int)> LoadPage(int skip, int take, CancellationToken ct)
    {
        var result = await Receipts.SearchAsync(new() { SearchText = SearchText, Status = Status == "All" ? null : Status, Skip = skip, Take = take }, ct);
        if (!result.Succeeded) await InvokeAsync(() => ErrorMessage = result.Message);
        return (result.Data?.Rows ?? [], result.Data?.TotalCount ?? 0);
    }
    private async Task Reload()
    {
        var (rows, count) = await LoadPage(0, 50, default); CompactRows = rows.ToList(); TotalCount = count; _grid?.Reload();
    }
    public void Dispose() { _debounce?.Cancel(); _debounce?.Dispose(); }
}

public sealed class FinishedGoodGridDataSource(Func<int, int, CancellationToken, Task<(IReadOnlyList<FinishedGoodReceiptSummary>, int)>> loader) : GridCustomDataSource
{
    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken ct) => (await loader(0, 1, ct)).Item2;
    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken ct)
        => (await loader(Math.Max(0, options.StartIndex), Math.Clamp(options.Count, 1, 100), ct)).Item1.ToList();
}
