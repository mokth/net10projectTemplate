using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Inventory.Inquiry;

public partial class IvBalanceLot : PageBase, IDisposable
{
    [Inject] private IIvBalanceLotService BalanceLot { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;

    protected bool CanExport;

    // Applied filters.
    protected string? AppliedICode;
    protected string? AppliedWhCode;
    protected string? AppliedLocCode;
    protected string? AppliedLotNo;
    protected IReadOnlyList<string> AppliedStatuses { get; set; } = [];
    protected decimal? AppliedMinQty;
    protected decimal? AppliedMaxQty;
    protected DateTime? AppliedExpiryBefore;
    protected DateTime? AppliedTransDateFrom;
    protected DateTime? AppliedTransDateTo;
    protected bool AppliedIncludeZeroQty = true;
    protected bool AppliedIncludeInactive = true;
    protected bool AppliedIncludeNonStockControl = true;

    // Draft (popup) filters.
    protected string? DraftICode;
    protected string? DraftWhCode;
    protected string? DraftLocCode;
    protected string? DraftLotNo;
    protected IEnumerable<string> DraftStatuses { get; set; } = [];
    protected decimal? DraftMinQty;
    protected decimal? DraftMaxQty;
    protected DateTime? DraftExpiryBefore;
    protected DateTime? DraftTransDateFrom;
    protected DateTime? DraftTransDateTo;
    protected bool DraftIncludeZeroQty = true;
    protected bool DraftIncludeInactive = true;
    protected bool DraftIncludeNonStockControl = true;

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Statuses { get; set; } = [];

    // Summary block.
    protected decimal TotalQty;
    protected decimal? TotalValue;
    protected int ZeroQtyRowCount;
    protected int ExpiredRowCount;

    protected IvBalanceLotGridDataSource DataSource { get; private set; } = default!;

    protected bool CanViewPrice => CurrentUser.CanViewPrice;

    protected string TotalCountLabel => TotalCount == 1 ? "1 pile" : $"{TotalCount:N0} piles";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedICode)
        || !string.IsNullOrWhiteSpace(AppliedWhCode)
        || !string.IsNullOrWhiteSpace(AppliedLocCode)
        || !string.IsNullOrWhiteSpace(AppliedLotNo)
        || AppliedStatuses.Count > 0
        || AppliedMinQty is not null
        || AppliedMaxQty is not null
        || AppliedExpiryBefore is not null
        || AppliedTransDateFrom is not null
        || AppliedTransDateTo is not null
        || !AppliedIncludeZeroQty
        || !AppliedIncludeInactive
        || !AppliedIncludeNonStockControl;

    protected List<GridColumnData> Columns() =>
    [
        new() { Caption = "Item", FieldName = nameof(IvBalanceLotRow.ICode), Width = "110px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Description", FieldName = nameof(IvBalanceLotRow.IDesc), VisibleIndex = 2 },
        new() { Caption = "Warehouse", FieldName = nameof(IvBalanceLotRow.WhCode), Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Bin", FieldName = nameof(IvBalanceLotRow.LocCode), Width = "90px", VisibleIndex = 4 },
        new() { Caption = "Lot", FieldName = nameof(IvBalanceLotRow.LotNo), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Status", FieldName = nameof(IvBalanceLotRow.IStatus), Width = "90px", VisibleIndex = 6 },
        new() { Caption = "Qty", FieldName = nameof(IvBalanceLotRow.StdQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 7 },
        new() { Caption = "UOM", FieldName = nameof(IvBalanceLotRow.StdUom), Width = "70px", VisibleIndex = 8 },
        new() { Caption = "Expiry", FieldName = nameof(IvBalanceLotRow.ExpiryDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Last movement", FieldName = nameof(IvBalanceLotRow.TransDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "120px", VisibleIndex = 10 },
        new() { Caption = "Unit price", FieldName = nameof(IvBalanceLotRow.UnitPrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", Visible = false, VisibleIndex = 11 },
        new() { Caption = "Est. value", FieldName = nameof(IvBalanceLotRow.Value), DataType = "decimal", DisplayFormat = "n4", Width = "120px", Visible = CanViewPrice, VisibleIndex = 12 },
        new() { Caption = "PO no.", FieldName = nameof(IvBalanceLotRow.PoNo), Visible = false, VisibleIndex = 13 },
        new() { Caption = "Ref", FieldName = nameof(IvBalanceLotRow.RefNo), Visible = false, VisibleIndex = 14 },
        new() { Caption = "Remarks", FieldName = nameof(IvBalanceLotRow.Remarks), Visible = false, VisibleIndex = 15 },
        new() { Caption = "Lot id", FieldName = nameof(IvBalanceLotRow.LotId), DataType = "int", Visible = false, VisibleIndex = 16 },
        ..AuditColumns.For(startVisibleIndex: 17)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvBalanceLotGridDataSource(SearchPageAsync);

        CanExport = await AccessRights.CanAsync(MenuCodes.InventoryBalanceLot, PermissionCodes.Export);

        Buttons =
        [
            new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
            new() { Text = "EXPORT", IConClass = "fa-solid fa-file-excel", Style = "primary", Enabled = CanExport }
        ];

        await LoadLookupsAsync();
        SyncDataSourceFilters();
        await ReloadGridAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected async Task OnButtonClick(SelectedButtonInfo<IvBalanceLotRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadGridAsync();
                break;
            case "EXPORT":
                await OnExportAsync();
                break;
        }
    }

    protected async Task OnSearchTextChanged(string text)
    {
        SearchText = text ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version != _searchVersion)
            {
                return;
            }

            await InvokeAsync(async () => await ReloadGridAsync());
        };
        _searchDebounce.Start();
        await Task.CompletedTask;
    }

    protected void OpenFilterPopup()
    {
        DraftICode = AppliedICode;
        DraftWhCode = AppliedWhCode;
        DraftLocCode = AppliedLocCode;
        DraftLotNo = AppliedLotNo;
        DraftStatuses = AppliedStatuses;
        DraftMinQty = AppliedMinQty;
        DraftMaxQty = AppliedMaxQty;
        DraftExpiryBefore = AppliedExpiryBefore;
        DraftTransDateFrom = AppliedTransDateFrom;
        DraftTransDateTo = AppliedTransDateTo;
        DraftIncludeZeroQty = AppliedIncludeZeroQty;
        DraftIncludeInactive = AppliedIncludeInactive;
        DraftIncludeNonStockControl = AppliedIncludeNonStockControl;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedICode = Normalize(DraftICode);
        AppliedWhCode = Normalize(DraftWhCode);
        AppliedLocCode = Normalize(DraftLocCode);
        AppliedLotNo = Normalize(DraftLotNo);
        AppliedStatuses = [.. DraftStatuses.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct()];
        AppliedMinQty = DraftMinQty;
        AppliedMaxQty = DraftMaxQty;
        AppliedExpiryBefore = DraftExpiryBefore?.Date;
        AppliedTransDateFrom = DraftTransDateFrom?.Date;
        AppliedTransDateTo = DraftTransDateTo?.Date;
        AppliedIncludeZeroQty = DraftIncludeZeroQty;
        AppliedIncludeInactive = DraftIncludeInactive;
        AppliedIncludeNonStockControl = DraftIncludeNonStockControl;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftICode = null;
        DraftWhCode = null;
        DraftLocCode = null;
        DraftLotNo = null;
        DraftStatuses = [];
        DraftMinQty = null;
        DraftMaxQty = null;
        DraftExpiryBefore = null;
        DraftTransDateFrom = null;
        DraftTransDateTo = null;
        DraftIncludeZeroQty = true;
        DraftIncludeInactive = true;
        DraftIncludeNonStockControl = true;

        AppliedICode = null;
        AppliedWhCode = null;
        AppliedLocCode = null;
        AppliedLotNo = null;
        AppliedStatuses = [];
        AppliedMinQty = null;
        AppliedMaxQty = null;
        AppliedExpiryBefore = null;
        AppliedTransDateFrom = null;
        AppliedTransDateTo = null;
        AppliedIncludeZeroQty = true;
        AppliedIncludeInactive = true;
        AppliedIncludeNonStockControl = true;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshSummaryAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new IvBalanceLotQuery
        {
            ICode = AppliedICode,
            WhCode = AppliedWhCode,
            LocCode = AppliedLocCode,
            LotNo = AppliedLotNo,
            IStatuses = AppliedStatuses,
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            MinQty = AppliedMinQty,
            MaxQty = AppliedMaxQty,
            ExpiryBefore = AppliedExpiryBefore,
            TransDateFrom = AppliedTransDateFrom,
            TransDateTo = AppliedTransDateTo,
            IncludeZeroQty = AppliedIncludeZeroQty,
            IncludeInactive = AppliedIncludeInactive,
            IncludeNonStockControl = AppliedIncludeNonStockControl
        });
    }

    private async Task RefreshSummaryAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 1;
        var result = await BalanceLot.GetSummaryAsync(query);
        if (result.Succeeded && result.Data is not null)
        {
            TotalCount = result.Data.TotalRows;
            TotalQty = result.Data.TotalQty;
            TotalValue = result.Data.TotalValue;
            ZeroQtyRowCount = result.Data.ZeroQtyRowCount;
            ExpiredRowCount = result.Data.ExpiredRowCount;
        }
        else
        {
            TotalCount = 0;
            TotalQty = 0m;
            TotalValue = null;
            ZeroQtyRowCount = 0;
            ExpiredRowCount = 0;
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
        }
    }

    private async Task<(IReadOnlyList<IvBalanceLotRow> Rows, int TotalCount)> SearchPageAsync(
        IvBalanceLotQuery query,
        CancellationToken cancellationToken)
    {
        var result = await BalanceLot.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load balances.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task LoadLookupsAsync()
    {
        var warehouses = await Lookups.ListActiveWarehousesAsync();
        var statuses = await Lookups.ListActiveStatusesAsync();

        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];
        Statuses = statuses.Succeeded ? statuses.Rows : [];

        if (!warehouses.Succeeded || !statuses.Succeeded)
        {
            ErrorMessage = warehouses.ErrorMessage ?? statuses.ErrorMessage ?? "Unable to load filter lookups.";
        }
    }

    private async Task OnExportAsync()
    {
        if (!CanExport)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var query = BuildQueryDictionary();
        var url = QueryHelpers.AddQueryString("/inventory/balance-by-lot/export", query);
        Navigation.NavigateTo(url, forceLoad: true);
        await Task.CompletedTask;
    }

    private Dictionary<string, string?> BuildQueryDictionary()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            dict["searchText"] = SearchText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(AppliedICode))
        {
            dict["iCode"] = AppliedICode;
        }

        if (!string.IsNullOrWhiteSpace(AppliedWhCode))
        {
            dict["whCode"] = AppliedWhCode;
        }

        if (!string.IsNullOrWhiteSpace(AppliedLocCode))
        {
            dict["locCode"] = AppliedLocCode;
        }

        if (!string.IsNullOrWhiteSpace(AppliedLotNo))
        {
            dict["lotNo"] = AppliedLotNo;
        }

        if (AppliedStatuses.Count > 0)
        {
            dict["statuses"] = string.Join(',', AppliedStatuses);
        }

        if (AppliedMinQty is decimal minQty)
        {
            dict["minQty"] = minQty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (AppliedMaxQty is decimal maxQty)
        {
            dict["maxQty"] = maxQty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (AppliedExpiryBefore is DateTime expiry)
        {
            dict["expiryBefore"] = expiry.ToString("yyyy-MM-dd");
        }

        if (AppliedTransDateFrom is DateTime from)
        {
            dict["transDateFrom"] = from.ToString("yyyy-MM-dd");
        }

        if (AppliedTransDateTo is DateTime to)
        {
            dict["transDateTo"] = to.ToString("yyyy-MM-dd");
        }

        dict["includeZeroQty"] = AppliedIncludeZeroQty ? "1" : "0";
        dict["includeInactive"] = AppliedIncludeInactive ? "1" : "0";
        dict["includeNonStockControl"] = AppliedIncludeNonStockControl ? "1" : "0";

        return dict;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Server-side paging source for the Balance-by-Lot inquiry via <see cref="IIvBalanceLotService.SearchAsync"/>.
/// </summary>
public sealed class IvBalanceLotGridDataSource : GridCustomDataSource
{
    private readonly Func<IvBalanceLotQuery, CancellationToken, Task<(IReadOnlyList<IvBalanceLotRow> Rows, int TotalCount)>> _loader;
    private IvBalanceLotQuery _filters = new();

    public IvBalanceLotGridDataSource(
        Func<IvBalanceLotQuery, CancellationToken, Task<(IReadOnlyList<IvBalanceLotRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public IvBalanceLotQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(IvBalanceLotQuery query) =>
        _filters = Clone(query);

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
        query.Take = Math.Clamp(options.Count <= 0 ? 50 : options.Count, 1, 100);

        if (options.SortInfo is { Count: > 0 })
        {
            var sort = options.SortInfo[0];
            query.SortField = sort.FieldName;
            query.SortDescending = sort.DescendingSortOrder;
        }

        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static IvBalanceLotQuery Clone(IvBalanceLotQuery source) =>
        new()
        {
            ICode = source.ICode,
            WhCode = source.WhCode,
            LocCode = source.LocCode,
            LotNo = source.LotNo,
            IStatuses = [.. source.IStatuses],
            SearchText = source.SearchText,
            MinQty = source.MinQty,
            MaxQty = source.MaxQty,
            ExpiryBefore = source.ExpiryBefore,
            TransDateFrom = source.TransDateFrom,
            TransDateTo = source.TransDateTo,
            IncludeZeroQty = source.IncludeZeroQty,
            IncludeInactive = source.IncludeInactive,
            IncludeNonStockControl = source.IncludeNonStockControl,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
