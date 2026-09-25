using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Inventory.Inquiry;

/// <summary>
/// Posted-movement inquiry over <c>dbo.IvTrxHistory</c>. The TrxType list box is the single control
/// that turns this page into Transaction Inquiry (all types), Adjustment Inquiry or Adjustment
/// Analysis (<c>ADJ</c> only) — one page per user question, not one page per filter (D1).
/// </summary>
public partial class IvTrxInquiry : PageBase, IDisposable
{
    [Inject] private IIvTrxHistoryService TrxHistory { get; set; } = default!;
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
    protected bool CanViewValue;

    // Applied filters.
    protected string? AppliedICode;
    protected string? AppliedWhCode;
    protected string? AppliedLocCode;
    protected string? AppliedLotNo;
    protected IReadOnlyList<string> AppliedTrxTypes { get; set; } = [];
    protected IReadOnlyList<string> AppliedStatuses { get; set; } = [];
    protected int? AppliedBatchNo;
    protected string? AppliedRefNo;
    protected string? AppliedDocumentNo;
    protected DateTime? AppliedTrxDateFrom;
    protected DateTime? AppliedTrxDateTo;

    // Draft (popup) filters.
    protected string? DraftICode;
    protected string? DraftWhCode;
    protected string? DraftLocCode;
    protected string? DraftLotNo;
    protected IEnumerable<string> DraftTrxTypes { get; set; } = [];
    protected IEnumerable<string> DraftStatuses { get; set; } = [];
    protected int? DraftBatchNo;
    protected string? DraftRefNo;
    protected string? DraftDocumentNo;
    protected DateTime? DraftTrxDateFrom;
    protected DateTime? DraftTrxDateTo;

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Statuses { get; set; } = [];

    /// <summary>
    /// Transaction types offered by the filter. Built from the <c>IvTrxTypes</c> constants because
    /// there is no trx-type master table — the code is the persisted token.
    /// </summary>
    protected IReadOnlyList<IvCodeLookupRow> TrxTypes { get; set; } =
    [
        new() { Code = IvTrxTypes.MiscellaneousReceipt, Desc = "Misc. receipt" },
        new() { Code = IvTrxTypes.CustomerReturn, Desc = "Customer return" },
        new() { Code = IvTrxTypes.GoodsReceive, Desc = "Goods receipt" },
        new() { Code = IvTrxTypes.NonStockGoodsReceive, Desc = "Non-stock goods receipt" },
        new() { Code = IvTrxTypes.FinishedGoods, Desc = "Finished goods" },
        new() { Code = IvTrxTypes.VendorReturn, Desc = "Vendor return" },
        new() { Code = IvTrxTypes.StockTransfer, Desc = "Stock transfer" },
        new() { Code = IvTrxTypes.IssueToProduction, Desc = "Issue to production" },
        new() { Code = IvTrxTypes.MiscellaneousIssue, Desc = "Misc. issue" },
        new() { Code = IvTrxTypes.Scrap, Desc = "Scrap" },
        new() { Code = IvTrxTypes.StockAdjustment, Desc = "Stock adjustment" },
        new() { Code = IvTrxTypes.SalesOut, Desc = "Sales out" }
    ];

    // Summary block.
    protected decimal SummaryInQty;
    protected decimal SummaryOutQty;
    protected decimal SummaryNetQty;
    protected decimal SummaryAdjustNetQty;
    protected decimal? SummaryTotalValue;
    protected int SummaryBatchCount;

    protected IvTrxHistoryGridDataSource DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 line" : $"{TotalCount:N0} lines";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedICode)
        || !string.IsNullOrWhiteSpace(AppliedWhCode)
        || !string.IsNullOrWhiteSpace(AppliedLocCode)
        || !string.IsNullOrWhiteSpace(AppliedLotNo)
        || AppliedTrxTypes.Count > 0
        || AppliedStatuses.Count > 0
        || AppliedBatchNo is not null
        || !string.IsNullOrWhiteSpace(AppliedRefNo)
        || !string.IsNullOrWhiteSpace(AppliedDocumentNo)
        || AppliedTrxDateFrom is not null
        || AppliedTrxDateTo is not null;

    protected List<GridColumnData> Columns() =>
    [
        new() { Caption = "Date", FieldName = nameof(IvTrxHistoryRow.TrxDtTime), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "140px", SortIndex = 0, SortOrder = GridColumnSortOrder.Descending, VisibleIndex = 1 },
        new() { Caption = "Type", FieldName = nameof(IvTrxHistoryRow.TrxType), Width = "80px", VisibleIndex = 2 },
        new() { Caption = "Batch", FieldName = nameof(IvTrxHistoryRow.BatchNo), DataType = "int", Width = "80px", VisibleIndex = 3 },
        new() { Caption = "Ref", FieldName = nameof(IvTrxHistoryRow.RefNo), Width = "140px", VisibleIndex = 4 },
        new() { Caption = "Item", FieldName = nameof(IvTrxHistoryRow.ICode), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Description", FieldName = nameof(IvTrxHistoryRow.IDesc), VisibleIndex = 6 },
        new() { Caption = "Status", FieldName = nameof(IvTrxHistoryRow.IStatus), Width = "90px", VisibleIndex = 7 },
        new() { Caption = "From wh.", FieldName = nameof(IvTrxHistoryRow.FrWarehouse), Width = "100px", VisibleIndex = 8 },
        new() { Caption = "To wh.", FieldName = nameof(IvTrxHistoryRow.ToWarehouse), Width = "100px", VisibleIndex = 9 },
        new() { Caption = "In", FieldName = nameof(IvTrxHistoryRow.InQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Out", FieldName = nameof(IvTrxHistoryRow.OutQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Net", FieldName = nameof(IvTrxHistoryRow.NetQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 12 },
        new() { Caption = "UOM", FieldName = nameof(IvTrxHistoryRow.StdUom), Width = "70px", VisibleIndex = 13 },
        new() { Caption = "Reason", FieldName = nameof(IvTrxHistoryRow.Reason), Width = "110px", VisibleIndex = 14 },
        // Money is omitted, never blanked, when the caller may not see value (D11 Option B).
        new() { Caption = "Est. value", FieldName = nameof(IvTrxHistoryRow.EstValue), DataType = "decimal", DisplayFormat = "n4", Width = "120px", Visible = CanViewValue, VisibleIndex = 15 },
        new() { Caption = "Unit price", FieldName = nameof(IvTrxHistoryRow.UnitPrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", Visible = false, VisibleIndex = 16 },
        new() { Caption = "From bin", FieldName = nameof(IvTrxHistoryRow.FrLocation), Width = "90px", Visible = false, VisibleIndex = 17 },
        new() { Caption = "From lot", FieldName = nameof(IvTrxHistoryRow.FrLotNo), Width = "120px", Visible = false, VisibleIndex = 18 },
        new() { Caption = "To bin", FieldName = nameof(IvTrxHistoryRow.ToLocation), Width = "90px", Visible = false, VisibleIndex = 19 },
        new() { Caption = "To lot", FieldName = nameof(IvTrxHistoryRow.ToLotNo), Width = "120px", Visible = false, VisibleIndex = 20 },
        new() { Caption = "Line", FieldName = nameof(IvTrxHistoryRow.TrxLineNo), DataType = "int", Width = "70px", Visible = false, VisibleIndex = 21 },
        new() { Caption = "DO", FieldName = nameof(IvTrxHistoryRow.DoNo), Visible = false, VisibleIndex = 22 },
        new() { Caption = "INV", FieldName = nameof(IvTrxHistoryRow.InvNo), Visible = false, VisibleIndex = 23 },
        new() { Caption = "SO", FieldName = nameof(IvTrxHistoryRow.SoNo), Visible = false, VisibleIndex = 24 },
        new() { Caption = "PO", FieldName = nameof(IvTrxHistoryRow.PoNo), Visible = false, VisibleIndex = 25 },
        new() { Caption = "Remarks", FieldName = nameof(IvTrxHistoryRow.Remarks), Visible = false, VisibleIndex = 26 },
        ..AuditColumns.For(startVisibleIndex: 27)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvTrxHistoryGridDataSource(SearchPageAsync);

        CanExport = await AccessRights.CanAsync(MenuCodes.InventoryTrxInquiry, PermissionCodes.Export);
        CanViewValue = await AccessRights.CanAsync(MenuCodes.InventoryTrxInquiry, PermissionCodes.ViewPrice);

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

    protected async Task OnButtonClick(SelectedButtonInfo<IvTrxHistoryRow> info)
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
        DraftTrxTypes = AppliedTrxTypes;
        DraftStatuses = AppliedStatuses;
        DraftBatchNo = AppliedBatchNo;
        DraftRefNo = AppliedRefNo;
        DraftDocumentNo = AppliedDocumentNo;
        DraftTrxDateFrom = AppliedTrxDateFrom;
        DraftTrxDateTo = AppliedTrxDateTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedICode = Normalize(DraftICode);
        AppliedWhCode = Normalize(DraftWhCode);
        AppliedLocCode = Normalize(DraftLocCode);
        AppliedLotNo = Normalize(DraftLotNo);
        AppliedTrxTypes = Clean(DraftTrxTypes);
        AppliedStatuses = Clean(DraftStatuses);
        AppliedBatchNo = DraftBatchNo;
        AppliedRefNo = Normalize(DraftRefNo);
        AppliedDocumentNo = Normalize(DraftDocumentNo);
        AppliedTrxDateFrom = DraftTrxDateFrom?.Date;
        AppliedTrxDateTo = DraftTrxDateTo?.Date;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftICode = null;
        DraftWhCode = null;
        DraftLocCode = null;
        DraftLotNo = null;
        DraftTrxTypes = [];
        DraftStatuses = [];
        DraftBatchNo = null;
        DraftRefNo = null;
        DraftDocumentNo = null;
        DraftTrxDateFrom = null;
        DraftTrxDateTo = null;

        AppliedICode = null;
        AppliedWhCode = null;
        AppliedLocCode = null;
        AppliedLotNo = null;
        AppliedTrxTypes = [];
        AppliedStatuses = [];
        AppliedBatchNo = null;
        AppliedRefNo = null;
        AppliedDocumentNo = null;
        AppliedTrxDateFrom = null;
        AppliedTrxDateTo = null;
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

    private void SyncDataSourceFilters() =>
        DataSource.UpdateFilters(BuildQuery());

    private IvTrxHistoryQuery BuildQuery() =>
        new()
        {
            ICode = AppliedICode,
            WhCode = AppliedWhCode,
            LocCode = AppliedLocCode,
            LotNo = AppliedLotNo,
            TrxTypes = AppliedTrxTypes,
            IStatuses = AppliedStatuses,
            BatchNo = AppliedBatchNo,
            RefNo = AppliedRefNo,
            DocumentNo = AppliedDocumentNo,
            TrxDateFrom = AppliedTrxDateFrom,
            TrxDateTo = AppliedTrxDateTo,
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim()
        };

    private async Task RefreshSummaryAsync()
    {
        var query = BuildQuery();
        query.Skip = 0;
        query.Take = 1;
        var result = await TrxHistory.GetSummaryAsync(MenuCodes.InventoryTrxInquiry, query);
        if (result.Succeeded && result.Data is not null)
        {
            TotalCount = result.Data.TotalRows;
            SummaryBatchCount = result.Data.BatchCount;
            SummaryInQty = result.Data.InQty;
            SummaryOutQty = result.Data.OutQty;
            SummaryNetQty = result.Data.NetQty;
            SummaryAdjustNetQty = result.Data.AdjustNetQty;
            SummaryTotalValue = result.Data.TotalValue;
        }
        else
        {
            TotalCount = 0;
            SummaryBatchCount = 0;
            SummaryInQty = 0m;
            SummaryOutQty = 0m;
            SummaryNetQty = 0m;
            SummaryAdjustNetQty = 0m;
            SummaryTotalValue = null;
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
        }
    }

    private async Task<(IReadOnlyList<IvTrxHistoryRow> Rows, int TotalCount)> SearchPageAsync(
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var result = await TrxHistory.SearchAsync(MenuCodes.InventoryTrxInquiry, query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load movements.";
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

        var url = QueryHelpers.AddQueryString("/inventory/trx-inquiry/export", BuildQueryDictionary());
        Navigation.NavigateTo(url, forceLoad: true);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The applied filters, never the grid's current page. Company and branch are deliberately absent
    /// — the endpoint resolves them server-side (D19).
    /// </summary>
    private Dictionary<string, string?> BuildQueryDictionary()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddText(dict, "searchText", SearchText);
        AddText(dict, "iCode", AppliedICode);
        AddText(dict, "whCode", AppliedWhCode);
        AddText(dict, "locCode", AppliedLocCode);
        AddText(dict, "lotNo", AppliedLotNo);
        AddText(dict, "refNo", AppliedRefNo);
        AddText(dict, "documentNo", AppliedDocumentNo);

        if (AppliedTrxTypes.Count > 0)
        {
            dict["trxTypes"] = string.Join(',', AppliedTrxTypes);
        }

        if (AppliedStatuses.Count > 0)
        {
            dict["statuses"] = string.Join(',', AppliedStatuses);
        }

        if (AppliedBatchNo is int batchNo)
        {
            dict["batchNo"] = batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (AppliedTrxDateFrom is DateTime from)
        {
            dict["trxDateFrom"] = from.ToString("yyyy-MM-dd");
        }

        if (AppliedTrxDateTo is DateTime to)
        {
            dict["trxDateTo"] = to.ToString("yyyy-MM-dd");
        }

        return dict;
    }

    private static void AddText(Dictionary<string, string?> dict, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            dict[key] = value.Trim();
        }
    }

    private static IReadOnlyList<string> Clean(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Server-side paging source for the transaction inquiry via
/// <see cref="IIvTrxHistoryService.SearchAsync"/>. Filters are rebuilt from the applied state on every
/// reload so the grid can never page with a stale predicate.
/// </summary>
public sealed class IvTrxHistoryGridDataSource : GridCustomDataSource
{
    private readonly Func<IvTrxHistoryQuery, CancellationToken, Task<(IReadOnlyList<IvTrxHistoryRow> Rows, int TotalCount)>> _loader;
    private IvTrxHistoryQuery _filters = new();

    public IvTrxHistoryGridDataSource(
        Func<IvTrxHistoryQuery, CancellationToken, Task<(IReadOnlyList<IvTrxHistoryRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public IvTrxHistoryQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(IvTrxHistoryQuery query) => _filters = Clone(query);

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

    private static IvTrxHistoryQuery Clone(IvTrxHistoryQuery source) =>
        new()
        {
            ICode = source.ICode,
            WhCode = source.WhCode,
            LocCode = source.LocCode,
            LotNo = source.LotNo,
            TrxTypes = [.. source.TrxTypes],
            BatchNo = source.BatchNo,
            RefNo = source.RefNo,
            DocumentNo = source.DocumentNo,
            IStatuses = [.. source.IStatuses],
            TrxDateFrom = source.TrxDateFrom,
            TrxDateTo = source.TrxDateTo,
            SearchText = source.SearchText,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
