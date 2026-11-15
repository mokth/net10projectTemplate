using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Purchase.Transactions;

public partial class PoSourcingList : PageBase, IDisposable
{
    [Inject] private IPoSourcingService SourcingService { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private bool _disposed;
    private readonly List<PoSourcingListRowDto> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmDeleteVisible;
    protected bool ConfirmCancelVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<PoSourcingListRowDto> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanCancel;

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string CancelReason { get; set; } = string.Empty;

    protected PoSourcingGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 sourcing RFQ" : $"{TotalCount:N0} sourcing RFQs";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(PoSourcingStatuses.Draft, "DRAFT"),
        new(PoSourcingStatuses.CollectingQuotes, "COLLECTING QUOTES"),
        new(PoSourcingStatuses.ReadyToCompare, "READY TO COMPARE"),
        new(PoSourcingStatuses.AwaitingAward, "AWAITING AWARD"),
        new(PoSourcingStatuses.Awarded, "AWARDED"),
        new(PoSourcingStatuses.PartiallyConverted, "PARTIALLY CONVERTED"),
        new(PoSourcingStatuses.Converted, "CONVERTED"),
        new(PoSourcingStatuses.Cancelled, "CANCELLED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "RFQ No.", FieldName = nameof(PoSourcingListRowDto.SourcingNo), SortIndex = 0, VisibleIndex = 1, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Date", FieldName = nameof(PoSourcingListRowDto.RequestDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", VisibleIndex = 2, Size = GridColumnSize.Date },
        new() { Caption = "Due Date", FieldName = nameof(PoSourcingListRowDto.ResponseDueDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", VisibleIndex = 3, Size = GridColumnSize.Date },
        new() { Caption = "Status", FieldName = nameof(PoSourcingListRowDto.Status), Width = "150px", VisibleIndex = 4, Size = GridColumnSize.Status },
        new() { Caption = "Source", FieldName = nameof(PoSourcingListRowDto.SourceType), Width = "130px", VisibleIndex = 5, Size = GridColumnSize.Code },
        new() { Caption = "Title", FieldName = nameof(PoSourcingListRowDto.Title), VisibleIndex = 6, Size = GridColumnSize.Name },
        new() { Caption = "Buyer", FieldName = nameof(PoSourcingListRowDto.BuyerCode), VisibleIndex = 7, Size = GridColumnSize.Code },
        new() { Caption = "Dept", FieldName = nameof(PoSourcingListRowDto.DepartmentCode), VisibleIndex = 8, Size = GridColumnSize.Code },
        new() { Caption = "Project", FieldName = nameof(PoSourcingListRowDto.ProjectId), VisibleIndex = 9, Size = GridColumnSize.Code },
        new() { Caption = "Base Curr", FieldName = nameof(PoSourcingListRowDto.BaseCurrency), VisibleIndex = 10, Size = GridColumnSize.Tiny },
        new() { Caption = "Lines", FieldName = nameof(PoSourcingListRowDto.LineCount), Width = "80px", VisibleIndex = 11, Size = GridColumnSize.Tiny },
        new() { Caption = "Suppliers", FieldName = nameof(PoSourcingListRowDto.SupplierCount), Width = "95px", VisibleIndex = 12, Size = GridColumnSize.Tiny },
        new() { Caption = "Quotes", FieldName = nameof(PoSourcingListRowDto.ActiveQuoteCount), Width = "85px", VisibleIndex = 13, Size = GridColumnSize.Tiny },
        ..AuditColumns.For(startVisibleIndex: 14)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new PoSourcingGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PurchaseSourcing, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseSourcing, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PurchaseSourcing, PermissionCodes.Delete);
        CanCancel = await AccessRights.CanAsync(MenuCodes.PurchaseSourcing, PermissionCodes.Cancel);

        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "CANCEL", IConClass = "fas fa-ban", Style = "warning", Enabled = CanCancel },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View sourcing RFQ" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Open sourcing workbench", Enabled = CanEdit }
        ];
        SyncToolbarEnabled();
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<PoSourcingListRowDto> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
        SyncToolbarEnabled();
    }

    private void SyncToolbarEnabled()
    {
        foreach (var button in Buttons)
        {
            var text = (button.Text ?? string.Empty).ToUpperInvariant();
            button.Enabled = text switch
            {
                "NEW" => CanAdd,
                "DELETE" => CanDelete && _selectedRows.Count > 0 && _selectedRows.All(r => r.CanDelete),
                "CANCEL" => CanCancel && _selectedRows.Count > 0 && _selectedRows.All(r => r.CanCancel),
                _ => button.Enabled
            };
        }
    }

    protected async Task OnButtonClick(SelectedButtonInfo<PoSourcingListRowDto> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/purchase/sourcing/new");
                break;
            case "DELETE":
                await BeginDeleteAsync();
                break;
            case "CANCEL":
                await BeginCancelAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoSourcingListRowDto> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            NavigateView(info.SelectedRow.SourcingId);
        }
        else if (mode == "EDIT")
        {
            if (!CanEdit)
            {
                StatusMessage = "Access Denied!!";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/purchase/sourcing/edit/{info.SelectedRow.SourcingId}");
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(long sourcingId) =>
        Navigation.NavigateTo($"/purchase/sourcing/view/{sourcingId}");

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
        DraftDateFrom = AppliedDateFrom;
        DraftDateTo = AppliedDateTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        CancelPendingSearch();
        AppliedStatus = string.Equals(DraftStatusKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftStatusKey;
        AppliedDateFrom = DraftDateFrom;
        AppliedDateTo = DraftDateTo;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        CancelPendingSearch();
        DraftStatusKey = "all";
        DraftDateFrom = null;
        DraftDateTo = null;
        AppliedStatus = null;
        AppliedDateFrom = null;
        AppliedDateTo = null;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    private Task BeginDeleteAsync()
    {
        if (!CanDelete) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        if (_selectedRows.Any(x => !x.CanDelete))
        {
            ErrorMessage = "Only DRAFT sourcing RFQs without quotes or awards can be deleted.";
            return Task.CompletedTask;
        }

        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected sourcing RFQ(s)?";
        ConfirmDeleteVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginCancelAsync()
    {
        if (!CanCancel) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        if (_selectedRows.Any(x => !x.CanCancel))
        {
            ErrorMessage = "One or more selected sourcing RFQs cannot be cancelled.";
            return Task.CompletedTask;
        }

        CancelReason = string.Empty;
        ConfirmMessage = $"Cancel {_selectedRows.Count} selected sourcing RFQ(s)?";
        ConfirmCancelVisible = true;
        return Task.CompletedTask;
    }

    protected async Task ConfirmDeleteAsync()
    {
        if (IsSubmitting || _selectedRows.Count == 0)
        {
            ConfirmDeleteVisible = false;
            return;
        }

        using var blocking = BeginBlockingWork("Please wait. This action is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var failed = new List<string>();
            var deleted = 0;
            foreach (var row in _selectedRows.ToList())
            {
                var result = await SourcingService.DeleteDraftAsync(row.SourcingId, row.RowVersion);
                if (result.Succeeded)
                {
                    deleted++;
                }
                else
                {
                    failed.Add($"{row.SourcingNo}: {result.ErrorMessage ?? "Unable to delete."}");
                }
            }

            ConfirmDeleteVisible = false;
            if (failed.Count == 0)
            {
                StatusMessage = deleted == 1 ? "Sourcing RFQ deleted." : $"{deleted} sourcing RFQ(s) deleted.";
                _selectedRows.Clear();
            }
            else
            {
                ErrorMessage = string.Join("\n", failed);
            }

            await ReloadGridAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ConfirmCancelAsync()
    {
        if (IsSubmitting || _selectedRows.Count == 0)
        {
            ConfirmCancelVisible = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(CancelReason))
        {
            ErrorMessage = "Cancel reason is required.";
            return;
        }

        using var blocking = BeginBlockingWork("Please wait. This action is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var reason = CancelReason.Trim();
            var failed = new List<string>();
            var cancelled = 0;
            foreach (var row in _selectedRows.ToList())
            {
                var result = await SourcingService.CancelAsync(new PoSourcingCancelRequest
                {
                    SourcingId = row.SourcingId,
                    CancelReason = reason,
                    RowVersion = row.RowVersion
                });
                if (result.Succeeded)
                {
                    cancelled++;
                }
                else
                {
                    failed.Add($"{row.SourcingNo}: {result.ErrorMessage ?? "Unable to cancel."}");
                }
            }

            ConfirmCancelVisible = false;
            if (failed.Count == 0)
            {
                StatusMessage = cancelled == 1 ? "Sourcing RFQ cancelled." : $"{cancelled} sourcing RFQ(s) cancelled.";
                CancelReason = string.Empty;
                _selectedRows.Clear();
            }
            else
            {
                ErrorMessage = string.Join("\n", failed);
            }

            await ReloadGridAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected static string StatusChipClass(string? status) =>
        string.Equals(status, PoSourcingStatuses.Awarded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, PoSourcingStatuses.Converted, StringComparison.OrdinalIgnoreCase) ? "is-on" :
        string.Equals(status, PoSourcingStatuses.Cancelled, StringComparison.OrdinalIgnoreCase) ? "is-off" : "is-hold";

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new PoSourcingSearchFilter
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = AppliedStatus,
            FromDate = AppliedDateFrom,
            ToDate = AppliedDateTo
        });
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var filter = DataSource.CurrentFilter;
        filter.PageIndex = 0;
        filter.PageSize = 50;
        var result = await SourcingService.SearchAsync(filter);
        if (result.Succeeded && result.Page is not null)
        {
            CompactRows = result.Page.Rows.ToList();
            TotalCount = result.Page.TotalCount;
        }
        else
        {
            CompactRows = [];
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ErrorMessage = result.ErrorMessage;
            }
        }
    }

    private async Task<(IReadOnlyList<PoSourcingListRowDto> Rows, int TotalCount)> SearchPageAsync(
        PoSourcingSearchFilter filter,
        CancellationToken cancellationToken)
    {
        var result = await SourcingService.SearchAsync(filter, cancellationToken);
        if (!result.Succeeded || result.Page is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load purchase sourcing RFQs.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Page.TotalCount);
        return (result.Page.Rows, result.Page.TotalCount);
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _searchVersion);
        var timer = Interlocked.Exchange(ref _searchDebounce, null);
        timer?.Stop();
        timer?.Dispose();
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class PoSourcingGridDataSource : GridCustomDataSource
{
    private readonly Func<PoSourcingSearchFilter, CancellationToken, Task<(IReadOnlyList<PoSourcingListRowDto> Rows, int TotalCount)>> _loader;
    private PoSourcingSearchFilter _filters = new();

    public PoSourcingGridDataSource(
        Func<PoSourcingSearchFilter, CancellationToken, Task<(IReadOnlyList<PoSourcingListRowDto> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public PoSourcingSearchFilter CurrentFilter => Clone(_filters);
    public void UpdateFilters(PoSourcingSearchFilter filter) => _filters = Clone(filter);

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken cancellationToken)
    {
        var filter = Clone(_filters);
        filter.PageIndex = 0;
        filter.PageSize = 1;
        var (_, total) = await _loader(filter, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken cancellationToken)
    {
        var filter = Clone(_filters);
        var count = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100);
        filter.PageIndex = Math.Max(0, options.StartIndex) / count;
        filter.PageSize = count;
        var (rows, _) = await _loader(filter, cancellationToken);
        return rows.ToList();
    }

    private static PoSourcingSearchFilter Clone(PoSourcingSearchFilter source) =>
        new()
        {
            SearchText = source.SearchText,
            Status = source.Status,
            SourceType = source.SourceType,
            BuyerCode = source.BuyerCode,
            FromDate = source.FromDate,
            ToDate = source.ToDate,
            PageIndex = source.PageIndex,
            PageSize = source.PageSize
        };
}
