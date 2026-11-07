using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Common.OperatorError;
using ErpWeb.UI.Components.Pages;
using ErpWeb.Model.Entities.Production;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrWorkOrderTransferList : PageBase, IDisposable
{
    [Inject] private IProductionWorkOrderTransferService Transfers { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _debounce;
    private int _searchVersion;
    private bool _disposed;
    private readonly List<ProductionWorkOrderTransferSummary> _selected = [];
    private readonly Dictionary<(long Id, string Action), ProductionWorkOrderTransferCommand> _pendingCommands = [];

    protected List<ProductionWorkOrderTransferSummary> CompactRows { get; set; } = [];
    protected ProductionWorkOrderTransferGridDataSource DataSource { get; private set; } = default!;
    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];
    protected bool Loading = true, Busy, CanAdd, CanEdit, CanPost, CanRollback, CanDelete, FilterPopupVisible;
    protected string SearchText = "", StatusFilter = "", DraftStatus = "", Message = "", Error = "", RollbackReason = "";
    protected int TotalCount;
    protected ProductionWorkOrderTransferSummary? ConfirmRow;
    protected bool ConfirmPost, ConfirmRollback;
    protected bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(StatusFilter);

    /// <summary>Operator-language view of the last refused post/rollback, if any.</summary>
    protected ErpOperatorFailureView? OperatorFailure { get; private set; }
    protected IReadOnlyList<StatusChoice> Statuses { get; } =
    [
        new("", "All statuses"),
        new(ProductionWorkOrderTransferStatuses.New, "NEW"),
        new(ProductionWorkOrderTransferStatuses.Posted, "POSTED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Transfer No", FieldName = nameof(ProductionWorkOrderTransferSummary.TransferNo), VisibleIndex = 1, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Date", FieldName = nameof(ProductionWorkOrderTransferSummary.TransferDateTime), DataType = "date", DisplayFormat = "dd MMM yyyy HH:mm", VisibleIndex = 2, Size = GridColumnSize.Date },
        new() { Caption = "Item", FieldName = nameof(ProductionWorkOrderTransferSummary.ItemCode), VisibleIndex = 3, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Lot", FieldName = nameof(ProductionWorkOrderTransferSummary.LotNo), VisibleIndex = 4, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Qty", FieldName = nameof(ProductionWorkOrderTransferSummary.Qty), DataType = "decimal", DisplayFormat = "n4", VisibleIndex = 5, Size = GridColumnSize.Quantity },
        new() { Caption = "UOM", FieldName = nameof(ProductionWorkOrderTransferSummary.Uom), Width = "72px", VisibleIndex = 6, Size = GridColumnSize.Tiny },
        new() { Caption = "From WO", FieldName = nameof(ProductionWorkOrderTransferSummary.SourceWorkOrderNo), VisibleIndex = 7, Size = GridColumnSize.DocumentNo },
        new() { Caption = "From WC / Process", FieldName = nameof(ProductionWorkOrderTransferSummary.SourceWorkCentreProcess), VisibleIndex = 8, Size = GridColumnSize.Description },
        new() { Caption = "To WO", FieldName = nameof(ProductionWorkOrderTransferSummary.TargetWorkOrderNo), VisibleIndex = 9, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Consumer WC / Process", FieldName = nameof(ProductionWorkOrderTransferSummary.TargetConsumingWorkCentreProcess), VisibleIndex = 10, Size = GridColumnSize.Description },
        new() { Caption = "Status", FieldName = nameof(ProductionWorkOrderTransferSummary.Status), VisibleIndex = 11, Size = GridColumnSize.Status }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new(LoadAsync);
        CanAdd = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Edit);
        CanPost = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Post);
        CanRollback = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Rollback);
        CanDelete = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Delete);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "POST", IConClass = "fas fa-check", Style = "success", Enabled = CanPost },
            new() { Text = "ROLLBACK", IConClass = "fas fa-rotate-left", Style = "warning", Enabled = CanRollback },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View transfer" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit NEW transfer", Enabled = CanEdit }
        ];
        await ReloadAsync();
        Loading = false;
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;
    protected void OnSelections(List<ProductionWorkOrderTransferSummary> rows)
    {
        _selected.Clear();
        _selected.AddRange(rows);
    }

    /// <summary>
    /// Invalidates a pending debounced search and any in-flight response so the
    /// newest filter set owns the grid state.
    /// </summary>
    private void CancelPendingSearch()
    {
        Interlocked.Increment(ref _searchVersion);
        var timer = Interlocked.Exchange(ref _debounce, null);
        timer?.Stop();
        timer?.Dispose();
    }

    /// <summary>
    /// Explicit magnifier action: applies the latest text once and cancels the
    /// pending debounce so no second reload follows the click.
    /// </summary>
    protected async Task ApplySearchAsync()
    {
        CancelPendingSearch();
        await ReloadAsync();
    }

    protected Task SearchChanged(string value)
    {
        SearchText = value ?? string.Empty;
        CancelPendingSearch();
        _debounce = new Timer(350) { AutoReset = false };
        var version = Volatile.Read(ref _searchVersion);
        _debounce.Elapsed += async (_, _) =>
        {
            try
            {
                if (version != Volatile.Read(ref _searchVersion)) return;
                await InvokeAsync(async () =>
                {
                    if (_disposed || version != Volatile.Read(ref _searchVersion)) return;
                    await ReloadAsync();
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await InvokeAsync(() =>
                {
                    if (_disposed) return;
                    ErrorMessage = "Search could not be completed.";
                });
            }
        };
        _debounce.Start();
        return Task.CompletedTask;
    }

    protected void OpenFilterPopup() { DraftStatus = StatusFilter; FilterPopupVisible = true; }
    protected async Task ApplyFiltersAsync() { StatusFilter = DraftStatus; FilterPopupVisible = false; await ReloadAsync(); }
    protected async Task ClearFiltersAsync() { DraftStatus = ""; await ApplyFiltersAsync(); }
    protected void Open(long id, bool edit) => Nav.NavigateTo($"/planning/work-order-transfer/{id}/{(edit ? "edit" : "view")}");

    protected Task OnAction(SelectedButtonInfo<ProductionWorkOrderTransferSummary> info)
    {
        if (info.SelectedRow is null) return Task.CompletedTask;
        var action = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (action == "EDIT" && CanEdit && info.SelectedRow.Status == ProductionWorkOrderTransferStatuses.New)
            Open(info.SelectedRow.Id, true);
        else
            Open(info.SelectedRow.Id, false);
        return Task.CompletedTask;
    }

    protected Task OnButton(SelectedButtonInfo<ProductionWorkOrderTransferSummary> info)
    {
        var action = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (action == "NEW")
        {
            if (CanAdd) Nav.NavigateTo("/planning/work-order-transfer/new");
            else Error = "Access denied.";
            return Task.CompletedTask;
        }
        if (_selected.Count != 1)
        {
            Error = "Select exactly one Work Order Transfer for this action.";
            return Task.CompletedTask;
        }

        var row = _selected[0];
        switch (action)
        {
            case "POST" when CanPost && row.Status == ProductionWorkOrderTransferStatuses.New:
                BeginPost(row);
                break;
            case "ROLLBACK" when CanRollback && row.Status == ProductionWorkOrderTransferStatuses.Posted:
                BeginRollback(row);
                break;
            case "DELETE" when CanDelete && row.Status == ProductionWorkOrderTransferStatuses.New:
                BeginDelete(row);
                break;
            default:
                Error = "This action is not available for the selected Work Order Transfer.";
                break;
        }
        return Task.CompletedTask;
    }

    protected void BeginPost(ProductionWorkOrderTransferSummary row)
    {
        ConfirmRow = row; ConfirmPost = true; ConfirmRollback = false;
        var key = (row.Id, "POST");
        if (!_pendingCommands.ContainsKey(key)) _pendingCommands[key] = new(row.Id, row.RowVersion.ToArray(), Guid.NewGuid());
    }

    protected void BeginRollback(ProductionWorkOrderTransferSummary row)
    {
        ConfirmRow = row; ConfirmPost = false; ConfirmRollback = true; RollbackReason = "";
    }

    protected void BeginDelete(ProductionWorkOrderTransferSummary row)
    {
        ConfirmRow = row; ConfirmPost = false; ConfirmRollback = false;
    }

    protected void RollbackReasonChanged(string value) => RollbackReason = value ?? "";
    protected void CancelConfirm() => ConfirmRow = null;

    protected void DismissOperatorFailure()
    {
        OperatorFailure = null;
        Error = "";
    }

    protected async Task ConfirmAsync()
    {
        if (ConfirmRow is null || Busy) return;
        Busy = true; Error = ""; Message = ""; OperatorFailure = null;
        var row = ConfirmRow;
        try
        {
            if (ConfirmPost)
            {
                var key = (row.Id, "POST");
                if (!_pendingCommands.TryGetValue(key, out var command))
                    command = _pendingCommands[key] = new(row.Id, row.RowVersion.ToArray(), Guid.NewGuid());
                var result = await Transfers.PostAsync(command);
                if (!result.Succeeded)
                {
                    Error = result.Message ?? "The action failed.";
                    OperatorFailure = ErpOperatorFailureView.FromError(result.OperatorError);
                    return;
                }
                _pendingCommands.Remove(key);
                Message = $"{row.TransferNo} posted.";
            }
            else if (ConfirmRollback)
            {
                var reason = RollbackReason.Trim();
                if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
                    throw new InvalidOperationException("Enter a rollback reason of at most 500 characters.");
                var key = (row.Id, "ROLLBACK");
                if (!_pendingCommands.TryGetValue(key, out var command) || !string.Equals(command.Reason, reason, StringComparison.Ordinal))
                    command = _pendingCommands[key] = new(row.Id, row.RowVersion.ToArray(), Guid.NewGuid(), reason);
                var result = await Transfers.RollbackAsync(command);
                if (!result.Succeeded)
                {
                    Error = result.Message ?? "The action failed.";
                    OperatorFailure = ErpOperatorFailureView.FromError(result.OperatorError);
                    return;
                }
                _pendingCommands.Remove(key);
                Message = $"{row.TransferNo} rolled back.";
            }
            else
            {
                var result = await Transfers.DeleteAsync(row.Id, row.RowVersion);
                if (!result.Succeeded) throw new InvalidOperationException(result.Message ?? "The action failed.");
                Message = $"{row.TransferNo} deleted.";
            }
            ConfirmRow = null;
            _selected.Clear();
            await ReloadAsync();
        }
        catch (Exception)
        {
            Error = "The response could not be received. Reload the list to confirm the transfer status before retrying.";
        }
        finally { Busy = false; }
    }

    private async Task<(IReadOnlyList<ProductionWorkOrderTransferSummary>, int)> LoadAsync(
        ProductionWorkOrderTransferQuery query, CancellationToken ct)
    {
        var result = await Transfers.SearchAsync(query, ct);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() => Error = result.Message ?? "Unable to load Work Order Transfers.");
            return ([], 0);
        }
        TotalCount = result.Data.TotalCount;
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task ReloadAsync()
    {
        DataSource?.Update(SearchText, StatusFilter);
        await RefreshCompactAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private async Task RefreshCompactAsync()
    {
        var query = DataSource.Current;
        query.Skip = 0; query.Take = 30;
        var result = await Transfers.SearchAsync(query);
        CompactRows = result.Data?.Rows.ToList() ?? [];
        TotalCount = result.Data?.TotalCount ?? 0;
        if (!result.Succeeded) Error = result.Message ?? "Unable to load Work Order Transfers.";
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _searchVersion);
        var timer = Interlocked.Exchange(ref _debounce, null);
        timer?.Stop();
        timer?.Dispose();
    }

    protected sealed record StatusChoice(string Value, string Text);
}

public sealed class ProductionWorkOrderTransferGridDataSource(
    Func<ProductionWorkOrderTransferQuery, CancellationToken, Task<(IReadOnlyList<ProductionWorkOrderTransferSummary>, int)>> loader)
    : GridCustomDataSource
{
    private string? _search;
    private string? _status;
    public ProductionWorkOrderTransferQuery Current => new() { SearchText = _search, Status = _status };
    public void Update(string search, string status)
    {
        _search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        _status = string.IsNullOrWhiteSpace(status) ? null : status;
    }

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken ct)
    {
        var query = Current; query.Take = 1;
        return (await loader(query, ct)).Item2;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken ct)
    {
        var query = Current;
        query.Skip = Math.Max(0, options.StartIndex);
        query.Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100);
        return (await loader(query, ct)).Item1.ToList();
    }
}
