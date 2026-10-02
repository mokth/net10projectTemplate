using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrDailyProductionList : PageBase, IDisposable
{
    [Inject] private IProductionOutputService Outputs { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<ProductionOutputListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected bool IsApplyingAction;
    protected string ConfirmAction = string.Empty;
    protected string ConfirmMessage = string.Empty;
    protected string ConfirmReason = string.Empty;
    protected bool ConfirmNeedsReason =>
        string.Equals(ConfirmAction, "ROLLBACK", StringComparison.Ordinal);
    protected string ConfirmButtonText => ConfirmAction switch
    {
        "POST" => "Post",
        "ROLLBACK" => "Rollback",
        _ => "Confirm"
    };
    protected ButtonRenderStyle ConfirmButtonStyle => ConfirmAction switch
    {
        "POST" => ButtonRenderStyle.Primary,
        "ROLLBACK" => ButtonRenderStyle.Warning,
        _ => ButtonRenderStyle.Primary
    };

    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected bool CanAdd;
    protected bool CanPost;
    protected bool CanEdit;
    protected bool CanRollback;
    protected List<ProductionOutputListRow> CompactRows { get; set; } = [];
    protected ProductionDailyOutputGridDataSource DataSource { get; private set; } = default!;
    protected string? AppliedWorkOrderNo;
    protected string? AppliedStatus;
    protected string DraftWorkOrderNo = string.Empty;
    protected string DraftStatus = string.Empty;
    protected string TotalCountLabel => TotalCount == 1 ? "1 document" : $"{TotalCount:N0} documents";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedWorkOrderNo)
        || !string.IsNullOrWhiteSpace(AppliedStatus);

    protected IReadOnlyList<FilterOption> StatusOptions { get; } =
    [
        new(string.Empty, "All statuses"),
        new(ProductionOutputStatuses.New, "New"),
        new(ProductionOutputStatuses.Posted, "Posted"),
        new(ProductionOutputStatuses.Reversed, "Reversed")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Document", FieldName = nameof(ProductionOutputListRow.DocumentNo), Width = "120px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(ProductionOutputListRow.ProductionDate), DataType = "date", DisplayFormat = "dd MMM yyyy", Width = "115px", VisibleIndex = 2 },
        new() { Caption = "Work Order", FieldName = nameof(ProductionOutputListRow.WorkOrderNo), Width = "135px", VisibleIndex = 3 },
        new() { Caption = "Operation", FieldName = nameof(ProductionOutputListRow.OperationCode), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Output item", FieldName = nameof(ProductionOutputListRow.OutputItemCode), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Lot", FieldName = nameof(ProductionOutputListRow.OutputLotNo), Width = "120px", VisibleIndex = 6 },
        new() { Caption = "Status", FieldName = nameof(ProductionOutputListRow.Status), Width = "100px", VisibleIndex = 7 },
        new() { Caption = "Good", FieldName = nameof(ProductionOutputListRow.GoodQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 8 },
        new() { Caption = "Scrap", FieldName = nameof(ProductionOutputListRow.ScrapQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 9 },
        new() { Caption = "Reject", FieldName = nameof(ProductionOutputListRow.RejectQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 10 }
    ];

    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new ProductionDailyOutputGridDataSource(LoadPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Add);
        CanPost = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Post);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Edit);
        CanRollback = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Rollback);

        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "POST", IConClass = "fas fa-check", Style = "success", Enabled = CanPost },
            new() { Text = "ROLLBACK", IConClass = "fas fa-rotate-left", Style = "warning", Enabled = CanRollback }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View daily production" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit draft", Enabled = CanEdit }
        ];

        SyncFilters();
        await RefreshCompactAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;

    protected void OnSelectionsEvent(List<ProductionOutputListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<ProductionOutputListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd)
                {
                    StatusMessage = "Access Denied!!";
                    return;
                }

                Navigation.NavigateTo("/planning/daily-production/new");
                break;
            case "POST":
                await BeginPostAsync();
                break;
            case "ROLLBACK":
                await BeginRollbackAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<ProductionOutputListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "VIEW":
                OpenView(info.SelectedRow.Uid);
                break;
            case "EDIT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                if (!string.Equals(info.SelectedRow.Status, ProductionOutputStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = "Only NEW documents can be edited.";
                    OpenView(info.SelectedRow.Uid);
                    break;
                }

                Navigation.NavigateTo($"/planning/daily-production/edit/{info.SelectedRow.Uid}");
                break;
        }

        return Task.CompletedTask;
    }

    protected async Task ConfirmActionAsync()
    {
        if (IsApplyingAction || _selectedRows.Count == 0)
        {
            ConfirmVisible = false;
            return;
        }

        var action = ConfirmAction;
        string? rollbackReason = null;
        if (action == "ROLLBACK")
        {
            rollbackReason = (ConfirmReason ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rollbackReason))
            {
                ErrorMessage = "Rollback reason is required.";
                return;
            }

            if (rollbackReason.Length > 250)
            {
                ErrorMessage = "Rollback reason cannot exceed 250 characters.";
                return;
            }
        }

        IsApplyingAction = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var succeeded = 0;
            string? firstError = null;

            foreach (var row in _selectedRows)
            {
                if (action == "POST")
                {
                    var result = await Outputs.PostAsync(row.Uid);
                    if (result.Succeeded)
                        succeeded++;
                    else
                        firstError ??= result.Message ?? $"Unable to post {row.DocumentNo}.";
                }
                else if (action == "ROLLBACK")
                {
                    var result = await Outputs.RollbackAsync(new ProductionOutputRollbackRequest
                    {
                        OutputId = row.Uid,
                        PostingRequestId = Guid.NewGuid().ToString("N"),
                        Reason = rollbackReason!
                    });
                    if (result.Succeeded)
                        succeeded++;
                    else
                        firstError ??= result.Message ?? $"Unable to roll back {row.DocumentNo}.";
                }
            }

            if (succeeded == _selectedRows.Count)
            {
                StatusMessage = action == "POST"
                    ? $"Posted {succeeded} document(s)."
                    : $"Rolled back {succeeded} document(s).";
                ConfirmVisible = false;
                ConfirmReason = string.Empty;
                _selectedRows.Clear();
                await ReloadAsync();
            }
            else
            {
                ErrorMessage = firstError ?? $"Unable to {action.ToLowerInvariant()} document(s).";
                ConfirmVisible = false;
                if (succeeded > 0)
                    await ReloadAsync();
            }
        }
        finally
        {
            IsApplyingAction = false;
        }
    }

    protected void OpenView(long outputId) =>
        Navigation.NavigateTo($"/planning/daily-production/view/{outputId}");

    protected void OpenFilterPopup()
    {
        DraftWorkOrderNo = AppliedWorkOrderNo ?? string.Empty;
        DraftStatus = AppliedStatus ?? string.Empty;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedWorkOrderNo = NullIfEmpty(DraftWorkOrderNo);
        AppliedStatus = NullIfEmpty(DraftStatus);
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftWorkOrderNo = DraftStatus = string.Empty;
        await ApplyFiltersAsync();
    }

    protected Task OnSearchTextChanged(string value)
    {
        SearchText = value ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version == _searchVersion)
                await InvokeAsync(ReloadAsync);
        };
        _searchDebounce.Start();
        return Task.CompletedTask;
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private Task BeginPostAsync()
    {
        if (!CanPost)
        {
            StatusMessage = "Access Denied!!";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return Task.CompletedTask;
        }

        var notNew = _selectedRows
            .Where(x => !string.Equals(x.Status, ProductionOutputStatuses.New, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.DocumentNo)
            .ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW documents can be posted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "POST";
        ConfirmMessage = $"Post {_selectedRows.Count} selected daily production document(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginRollbackAsync()
    {
        if (!CanRollback)
        {
            StatusMessage = "Access Denied!!";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return Task.CompletedTask;
        }

        var notPosted = _selectedRows
            .Where(x => !string.Equals(x.Status, ProductionOutputStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.DocumentNo)
            .ToList();
        if (notPosted.Count > 0)
        {
            ErrorMessage = $"Only POSTED documents can be rolled back. Non-POSTED: {string.Join(", ", notPosted)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "ROLLBACK";
        ConfirmReason = string.Empty;
        ConfirmMessage = $"Roll back {_selectedRows.Count} selected daily production document(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private async Task ReloadAsync()
    {
        SyncFilters();
        await RefreshCompactAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncFilters() => DataSource.UpdateFilters(new ProductionOutputSearchQuery
    {
        SearchText = NullIfEmpty(SearchText),
        WorkOrderNo = AppliedWorkOrderNo,
        Status = AppliedStatus
    });

    private async Task<(IReadOnlyList<ProductionOutputListRow>, int)> LoadPageAsync(
        ProductionOutputSearchQuery query,
        CancellationToken ct)
    {
        var result = await Outputs.SearchAsync(query, ct);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() => ErrorMessage = result.Message ?? "Unable to load daily production.");
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task RefreshCompactAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await Outputs.SearchAsync(query);
        CompactRows = result.Data?.Rows.ToList() ?? [];
        TotalCount = result.Data?.TotalCount ?? 0;
        if (!result.Succeeded)
            ErrorMessage = result.Message ?? "Unable to load daily production.";
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    protected sealed record FilterOption(string Key, string Name);
}

public sealed class ProductionDailyOutputGridDataSource : GridCustomDataSource
{
    private readonly Func<ProductionOutputSearchQuery, CancellationToken, Task<(IReadOnlyList<ProductionOutputListRow>, int)>> _loader;
    private ProductionOutputSearchQuery _filters = new();

    public ProductionDailyOutputGridDataSource(
        Func<ProductionOutputSearchQuery, CancellationToken, Task<(IReadOnlyList<ProductionOutputListRow>, int)>> loader)
        => _loader = loader;

    public ProductionOutputSearchQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(ProductionOutputSearchQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken ct)
    {
        var q = Clone(_filters);
        q.Skip = 0;
        q.Take = 1;
        var (_, count) = await _loader(q, ct);
        return count;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken ct)
    {
        var q = Clone(_filters);
        q.Skip = Math.Max(0, options.StartIndex);
        q.Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100);
        var (rows, _) = await _loader(q, ct);
        return rows.ToList();
    }

    private static ProductionOutputSearchQuery Clone(ProductionOutputSearchQuery x) => new()
    {
        SearchText = x.SearchText,
        WorkOrderNo = x.WorkOrderNo,
        Status = x.Status,
        Skip = x.Skip,
        Take = x.Take
    };
}
