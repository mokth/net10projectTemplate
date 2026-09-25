using System.Collections;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Inventory.Inquiry;

/// <summary>
/// Stock Count Variance — variance and line accuracy over POSTED count sheets (Phase 3, item 14).
///
/// <para>
/// The page's job is to be honest about what it measures: the sheet's Generate-time <c>SystemQty</c>,
/// not the live quantity at post. The disclosure banner and the stale-line count are therefore part of
/// the screen, not an optional extra (D4).
/// </para>
/// </summary>
public partial class IvStockCountVariance : PageBase
{
    [Inject] private IIvStockCountService StockCounts { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected int TotalCount;

    protected bool CanExport;

    // Applied state.
    protected string? AppliedICode;
    protected string? AppliedWhCode;
    protected string? AppliedIClassCode;
    protected IReadOnlyList<string> AppliedStatuses { get; set; } = [];
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string? AppliedSearchText;

    // Draft (popup) state.
    protected string? DraftICode;
    protected string? DraftWhCode;
    protected string? DraftIClassCode;
    protected IEnumerable<string> DraftStatuses { get; set; } = [];
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;
    protected string? DraftSearchText;

    // Summary strip (D17).
    protected int SummarySheetCount;
    protected int SummaryCountedLines;
    protected int SummaryExactMatchLines;
    protected int SummaryIncreaseLines;
    protected int SummaryDecreaseLines;
    protected int SummaryPostedStaleLines;
    protected decimal SummaryNetVarianceQty;
    protected decimal SummaryAbsVarianceQty;
    protected decimal? SummaryVarianceValue;
    protected decimal? SummaryLineAccuracyPercent;

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Statuses { get; set; } = [];

    protected IvStockCountVarianceGridDataSource DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 line" : $"{TotalCount:N0} lines";

    protected string GridTitle => $"Count variance — {TotalCountLabel}";

    /// <summary>
    /// "—" when nothing was counted. Never "0%" and never "100%": with an empty denominator the figure
    /// is undefined, and printing 0 would read as "everything was wrong" (D17).
    /// </summary>
    protected string AccuracyCaption =>
        SummaryLineAccuracyPercent is decimal percent ? $"{percent:0.##}%" : "—";

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Count no.", FieldName = nameof(IvStockCountVarianceRow.CountNo), Width = "120px", VisibleIndex = 1 },
        new() { Caption = "Count date", FieldName = nameof(IvStockCountVarianceRow.CountDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = GridColumnSortOrder.Descending, VisibleIndex = 2 },
        new() { Caption = "Batch", FieldName = nameof(IvStockCountVarianceRow.PostedBatchNo), DataType = "int", Width = "80px", VisibleIndex = 3 },
        new() { Caption = "Line", FieldName = nameof(IvStockCountVarianceRow.LineNumber), DataType = "int", Width = "70px", VisibleIndex = 4 },
        new() { Caption = "Item", FieldName = nameof(IvStockCountVarianceRow.ICode), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Description", FieldName = nameof(IvStockCountVarianceRow.IDesc), VisibleIndex = 6 },
        new() { Caption = "Warehouse", FieldName = nameof(IvStockCountVarianceRow.WHCode), Width = "100px", VisibleIndex = 7 },
        new() { Caption = "Bin", FieldName = nameof(IvStockCountVarianceRow.LocCode), Width = "80px", VisibleIndex = 8 },
        new() { Caption = "Lot", FieldName = nameof(IvStockCountVarianceRow.LotNo), Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Status", FieldName = nameof(IvStockCountVarianceRow.IStatus), Width = "90px", VisibleIndex = 10 },
        new() { Caption = "UOM", FieldName = nameof(IvStockCountVarianceRow.StdUom), Width = "70px", VisibleIndex = 11 },
        new() { Caption = "System qty", FieldName = nameof(IvStockCountVarianceRow.SystemQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 12 },
        new() { Caption = "Physical qty", FieldName = nameof(IvStockCountVarianceRow.PhysicalQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 13 },
        new() { Caption = "Variance", FieldName = nameof(IvStockCountVarianceRow.Variance), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 14 },
        new() { Caption = "Direction", FieldName = nameof(IvStockCountVarianceRow.Direction), Width = "110px", VisibleIndex = 15 },
        new() { Caption = "Snapshot price", FieldName = nameof(IvStockCountVarianceRow.SnapshotUnitPrice), DataType = "decimal", DisplayFormat = "n4", Width = "120px", VisibleIndex = 16 },
        new() { Caption = "Variance value", FieldName = nameof(IvStockCountVarianceRow.VarianceValue), DataType = "decimal", DisplayFormat = "n4", Width = "120px", VisibleIndex = 17 },
        // Per-row disclosure: a stale sheet is never silently averaged into the accuracy figure.
        new() { Caption = "Stale lines", FieldName = nameof(IvStockCountVarianceRow.PostedStaleLines), DataType = "int", Width = "100px", VisibleIndex = 18 },
        new() { Caption = "Counted by", FieldName = nameof(IvStockCountVarianceRow.CountedBy), Width = "110px", VisibleIndex = 19 },
        new() { Caption = "Counted on", FieldName = nameof(IvStockCountVarianceRow.CountedOn), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "140px", VisibleIndex = 20 },
        new() { Caption = "Class", FieldName = nameof(IvStockCountVarianceRow.IClassCode), Visible = false, VisibleIndex = 21 },
        new() { Caption = "Pile id", FieldName = nameof(IvStockCountVarianceRow.BalLocId), DataType = "int", Visible = false, VisibleIndex = 22 },
        ..AuditColumns.For(startVisibleIndex: 23)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvStockCountVarianceGridDataSource(SearchPageAsync);

        CanExport = await AccessRights.CanAsync(MenuCodes.InventoryStockCountVar, PermissionCodes.Export);

        Buttons =
        [
            new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
            new() { Text = "EXPORT", IConClass = "fa-solid fa-file-excel", Style = "primary", Enabled = CanExport }
        ];

        await LoadLookupsAsync();
        await ReloadGridAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected async Task OnButtonClick(SelectedButtonInfo<IvStockCountVarianceRow> info)
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

    protected async Task OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        AppliedICode = item?.ICode;
        await ReloadGridAsync();
    }

    protected Task OnItemCodeChangedAsync(string? code)
    {
        AppliedICode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        return Task.CompletedTask;
    }

    protected void OpenFilterPopup()
    {
        DraftICode = AppliedICode;
        DraftWhCode = AppliedWhCode;
        DraftIClassCode = AppliedIClassCode;
        DraftStatuses = AppliedStatuses;
        DraftDateFrom = AppliedDateFrom;
        DraftDateTo = AppliedDateTo;
        DraftSearchText = AppliedSearchText;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedICode = Normalize(DraftICode);
        AppliedWhCode = Normalize(DraftWhCode);
        AppliedIClassCode = Normalize(DraftIClassCode);
        AppliedStatuses = Clean(DraftStatuses);
        AppliedDateFrom = DraftDateFrom?.Date;
        AppliedDateTo = DraftDateTo?.Date;
        AppliedSearchText = Normalize(DraftSearchText);
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftICode = null;
        DraftWhCode = null;
        DraftIClassCode = null;
        DraftStatuses = [];
        DraftDateFrom = null;
        DraftDateTo = null;
        DraftSearchText = null;

        AppliedICode = null;
        AppliedWhCode = null;
        AppliedIClassCode = null;
        AppliedStatuses = [];
        AppliedDateFrom = null;
        AppliedDateTo = null;
        AppliedSearchText = null;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    private async Task ReloadGridAsync()
    {
        DataSource.UpdateFilters(BuildQuery());
        await RefreshSummaryAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private IvStockCountVarianceQuery BuildQuery() =>
        new()
        {
            ICode = AppliedICode,
            WhCode = AppliedWhCode,
            IClassCode = AppliedIClassCode,
            IStatuses = AppliedStatuses,
            DateFrom = AppliedDateFrom,
            DateTo = AppliedDateTo,
            SearchText = AppliedSearchText
        };

    private async Task RefreshSummaryAsync()
    {
        var result = await StockCounts.GetVarianceSummaryAsync(
            MenuCodes.InventoryStockCountVar, BuildQuery());

        if (result.Succeeded && result.VarianceSummary is not null)
        {
            var summary = result.VarianceSummary;
            SummarySheetCount = summary.SheetCount;
            TotalCount = summary.LineCount;
            SummaryCountedLines = summary.CountedLines;
            SummaryExactMatchLines = summary.ExactMatchLines;
            SummaryIncreaseLines = summary.IncreaseLines;
            SummaryDecreaseLines = summary.DecreaseLines;
            SummaryPostedStaleLines = summary.PostedStaleLines;
            SummaryNetVarianceQty = summary.NetVarianceQty;
            SummaryAbsVarianceQty = summary.AbsVarianceQty;
            SummaryVarianceValue = summary.VarianceValue;
            SummaryLineAccuracyPercent = summary.LineAccuracyPercent;
        }
        else
        {
            ResetSummary();
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ErrorMessage = result.ErrorMessage;
            }
        }
    }

    private void ResetSummary()
    {
        SummarySheetCount = 0;
        TotalCount = 0;
        SummaryCountedLines = 0;
        SummaryExactMatchLines = 0;
        SummaryIncreaseLines = 0;
        SummaryDecreaseLines = 0;
        SummaryPostedStaleLines = 0;
        SummaryNetVarianceQty = 0m;
        SummaryAbsVarianceQty = 0m;
        SummaryVarianceValue = null;
        SummaryLineAccuracyPercent = null;
    }

    private async Task<(IReadOnlyList<IvStockCountVarianceRow> Rows, int TotalCount)> SearchPageAsync(
        IvStockCountVarianceQuery query,
        CancellationToken cancellationToken)
    {
        var result = await StockCounts.SearchVarianceAsync(
            MenuCodes.InventoryStockCountVar, query, cancellationToken);

        if (!result.Succeeded || result.VariancePage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load the variance report.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.VariancePage.TotalCount);
        return (result.VariancePage.Rows, result.VariancePage.TotalCount);
    }

    private async Task LoadLookupsAsync()
    {
        var warehouses = await Lookups.ListActiveWarehousesAsync();
        var classes = await Lookups.ListActiveClassesAsync();
        var statuses = await Lookups.ListActiveStatusesAsync();

        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];
        Classes = classes.Succeeded ? classes.Rows : [];
        Statuses = statuses.Succeeded ? statuses.Rows : [];

        if (!warehouses.Succeeded || !classes.Succeeded || !statuses.Succeeded)
        {
            ErrorMessage = warehouses.ErrorMessage ?? classes.ErrorMessage ?? statuses.ErrorMessage
                ?? "Unable to load filter lookups.";
        }
    }

    private async Task OnExportAsync()
    {
        if (!CanExport)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var url = QueryHelpers.AddQueryString(
            "/inventory/stock-count-variance/export", BuildQueryDictionary());
        Navigation.NavigateTo(url, forceLoad: true);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The applied filters, never the grid's current page. Company and branch are absent on purpose:
    /// the endpoint resolves them server-side (D19).
    /// </summary>
    private Dictionary<string, string?> BuildQueryDictionary()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddText(dict, "iCode", AppliedICode);
        AddText(dict, "whCode", AppliedWhCode);
        AddText(dict, "iClassCode", AppliedIClassCode);
        AddText(dict, "searchText", AppliedSearchText);

        if (AppliedStatuses.Count > 0)
        {
            dict["statuses"] = string.Join(',', AppliedStatuses);
        }

        if (AppliedDateFrom is DateTime from)
        {
            dict["dateFrom"] = from.ToString("yyyy-MM-dd");
        }

        if (AppliedDateTo is DateTime to)
        {
            dict["dateTo"] = to.ToString("yyyy-MM-dd");
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
/// Server-side paging source for the variance report. The filters are rebuilt from the applied state on
/// every reload, so the grid can never page with a stale predicate.
/// </summary>
public sealed class IvStockCountVarianceGridDataSource : GridCustomDataSource
{
    private readonly Func<IvStockCountVarianceQuery, CancellationToken, Task<(IReadOnlyList<IvStockCountVarianceRow> Rows, int TotalCount)>> _loader;
    private IvStockCountVarianceQuery _filters = new();

    public IvStockCountVarianceGridDataSource(
        Func<IvStockCountVarianceQuery, CancellationToken, Task<(IReadOnlyList<IvStockCountVarianceRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public void UpdateFilters(IvStockCountVarianceQuery query) => _filters = Clone(query);

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
        query.Take = options.Count <= 0 ? 50 : options.Count;
        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static IvStockCountVarianceQuery Clone(IvStockCountVarianceQuery source) =>
        new()
        {
            DateFrom = source.DateFrom,
            DateTo = source.DateTo,
            ICode = source.ICode,
            WhCode = source.WhCode,
            IClassCode = source.IClassCode,
            IStatuses = source.IStatuses,
            SearchText = source.SearchText,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
