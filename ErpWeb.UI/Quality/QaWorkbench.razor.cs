using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Quality;

public partial class QaWorkbench : PageBase, IDisposable
{
    [Inject] private IQaCaseService Cases { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private bool _disposed;
    private readonly List<QaCaseListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected bool HelpVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected int PendingReviewCount;
    protected QaInquiryPage Workload { get; set; } = new();
    protected List<QaCaseListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;

    protected string? AppliedStatus;
    protected string? AppliedSource;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;

    protected string DraftStatusKey = "all";
    protected string DraftSourceKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;
    protected string ConfirmMessage { get; set; } = string.Empty;

    protected QaWorkbenchGridDataSource DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 case" : $"{TotalCount:N0} cases";
    protected string PendingReviewLabel => PendingReviewCount == 1 ? "1 pending review" : $"{PendingReviewCount:N0} pending review";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || !string.IsNullOrWhiteSpace(AppliedSource)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        ..QaLabels.StatusOptions.Select(x => new StatusFilterOption(x.Key, x.Name))
    ];

    protected IReadOnlyList<StatusFilterOption> SourceFilterOptions { get; } =
    [
        new("all", "All"),
        ..QaLabels.SourceOptions.Select(x => new StatusFilterOption(x.Key, x.Name))
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Case No", FieldName = nameof(QaCaseListRow.CaseNo), SortIndex = 0, VisibleIndex = 1, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Date", FieldName = nameof(QaCaseListRow.CaseDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", VisibleIndex = 2, Size = GridColumnSize.Date },
        new() { Caption = "Status", FieldName = nameof(QaCaseListRow.StatusLabel), VisibleIndex = 3, Size = GridColumnSize.Status },
        new() { Caption = "Source", FieldName = nameof(QaCaseListRow.SourceTypeLabel), VisibleIndex = 4, Size = GridColumnSize.Name },
        new() { Caption = "Source doc", FieldName = nameof(QaCaseListRow.SourceDocNo), VisibleIndex = 5, Size = GridColumnSize.Reference },
        new() { Caption = "Supplier", FieldName = nameof(QaCaseListRow.SupplierName), VisibleIndex = 6, Size = GridColumnSize.Name },
        new() { Caption = "Work order", FieldName = nameof(QaCaseListRow.WorkOrderNo), VisibleIndex = 7, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Lines", FieldName = nameof(QaCaseListRow.LineCount), Width = "80px", VisibleIndex = 8, Size = GridColumnSize.Tiny },
        new() { Caption = "Next action", FieldName = nameof(QaCaseListRow.NextAction), VisibleIndex = 9, Size = GridColumnSize.Name },
        new() { Caption = "Note", FieldName = nameof(QaCaseListRow.EligibilityNote), VisibleIndex = 10, Size = GridColumnSize.LongText },
        ..AuditColumns.For(startVisibleIndex: 11)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new QaWorkbenchGridDataSource(SearchPageAsync);

        CanAdd = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Delete);

        Buttons =
        [
            new() { Text = "New QA", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "Delete draft", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];

        ActionButtons =
        [
            new() { Text = "View", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View case" },
            new() { Text = "Edit", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit draft", Enabled = CanEdit }
        ];

        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<QaCaseListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<QaCaseListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW QA":
                NavigateNew();
                break;
            case "DELETE DRAFT":
                await BeginDeleteAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<QaCaseListRow> info)
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
                NavigateView(info.SelectedRow.CaseNo);
                break;
            case "EDIT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                if (!QaCaseStatuses.CanEdit(info.SelectedRow.Status))
                {
                    StatusMessage = "Only draft quality cases can be edited.";
                    NavigateView(info.SelectedRow.CaseNo);
                    break;
                }

                Navigation.NavigateTo($"/quality/cases/edit/{info.SelectedRow.CaseNo}");
                break;
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string caseNo) =>
        Navigation.NavigateTo($"/quality/cases/view/{caseNo}");

    protected void NavigateNew()
    {
        if (!CanAdd)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        Navigation.NavigateTo("/quality/cases/new");
    }

    private void CancelPendingSearch()
    {
        Interlocked.Increment(ref _searchVersion);
        var timer = Interlocked.Exchange(ref _searchDebounce, null);
        timer?.Stop();
        timer?.Dispose();
    }

    protected async Task ApplySearchAsync()
    {
        CancelPendingSearch();
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected async Task OnSearchTextChanged(string text)
    {
        SearchText = text ?? string.Empty;
        CancelPendingSearch();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Volatile.Read(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            await InvokeAsync(async () =>
            {
                if (_disposed || version != Volatile.Read(ref _searchVersion)) return;
                SyncDataSourceFilters();
                await ReloadGridAsync();
            });
        };
        _searchDebounce.Start();
        await Task.CompletedTask;
    }

    protected void OpenFilterPopup()
    {
        DraftStatusKey = string.IsNullOrWhiteSpace(AppliedStatus) ? "all" : AppliedStatus;
        DraftSourceKey = string.IsNullOrWhiteSpace(AppliedSource) ? "all" : AppliedSource;
        DraftDateFrom = AppliedDateFrom;
        DraftDateTo = AppliedDateTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedStatus = string.Equals(DraftStatusKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftStatusKey;
        AppliedSource = string.Equals(DraftSourceKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftSourceKey;
        AppliedDateFrom = DraftDateFrom;
        AppliedDateTo = DraftDateTo;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftStatusKey = "all";
        DraftSourceKey = "all";
        DraftDateFrom = null;
        DraftDateTo = null;
        AppliedStatus = null;
        AppliedSource = null;
        AppliedDateFrom = null;
        AppliedDateTo = null;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    private async Task BeginDeleteAsync()
    {
        var draft = _selectedRows.FirstOrDefault(x => QaCaseStatuses.CanDelete(x.Status));
        if (draft is null)
        {
            ErrorMessage = "Select a draft quality case to delete.";
            return;
        }

        ConfirmMessage = $"Delete draft {draft.CaseNo}? This cannot be undone.";
        ConfirmVisible = true;
    }

    protected async Task ConfirmDeleteAsync()
    {
        var draft = _selectedRows.FirstOrDefault(x => QaCaseStatuses.CanDelete(x.Status));
        if (draft is null)
        {
            ConfirmVisible = false;
            return;
        }

        IsSubmitting = true;
        var result = await Cases.DeleteDraftAsync(new QaReviewCommand { CaseNo = draft.CaseNo });
        IsSubmitting = false;
        ConfirmVisible = false;
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        StatusMessage = $"Deleted {draft.CaseNo}.";
        await ReloadGridAsync();
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private async Task ReloadGridAsync()
    {
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new QaListQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = AppliedStatus,
            SourceType = AppliedSource,
            DateFrom = AppliedDateFrom,
            DateTo = AppliedDateTo,
            SortDescending = true
        });
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await Cases.SearchAsync(query);
        if (result.Succeeded && result.ListPage is not null)
        {
            CompactRows = result.ListPage.Rows.ToList();
            TotalCount = result.ListPage.TotalCount;
        }
        else
        {
            CompactRows = [];
            TotalCount = 0;
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ErrorMessage = result.ErrorMessage;
            }
        }

        var workload = await Cases.GetWorkloadAsync();
        Workload = workload.InquiryPage ?? new QaInquiryPage();
        PendingReviewCount = Workload.PendingReviewCount;
    }

    private async Task<(IReadOnlyList<QaCaseListRow> Rows, int TotalCount)> SearchPageAsync(
        QaListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Cases.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load quality cases.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    public void Dispose()
    {
        _disposed = true;
        _searchDebounce?.Dispose();
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class QaWorkbenchGridDataSource : GridCustomDataSource
{
    private readonly Func<QaListQuery, CancellationToken, Task<(IReadOnlyList<QaCaseListRow> Rows, int TotalCount)>> _loader;
    private QaListQuery _filters = new();

    public QaWorkbenchGridDataSource(
        Func<QaListQuery, CancellationToken, Task<(IReadOnlyList<QaCaseListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public QaListQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(QaListQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(
        GridCustomDataSourceCountOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = 0;
        query.Take = 1;
        var (_, total) = await _loader(query, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(
        GridCustomDataSourceItemsOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = Math.Max(0, options.StartIndex);
        query.Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100);
        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static QaListQuery Clone(QaListQuery source) => new()
    {
        SearchText = source.SearchText,
        Status = source.Status,
        SourceType = source.SourceType,
        DateFrom = source.DateFrom,
        DateTo = source.DateTo,
        SortField = source.SortField,
        SortDescending = source.SortDescending,
        Skip = source.Skip,
        Take = source.Take
    };
}
