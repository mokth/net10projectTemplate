using System.Collections;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Inventory.Inquiry;

/// <summary>
/// Stock Summary — server-side GROUP BY over the balance slice (D1): Item, Warehouse, Item × Warehouse
/// or Class.
///
/// <para>
/// <b>D16 lives on this page.</b> The grouping mode decides the column set AND the quantity caption:
/// <c>ITEM</c>/<c>ITEM_WAREHOUSE</c> show a real UOM and a plain "Total qty"; <c>WAREHOUSE</c>/<c>CLASS</c>
/// hide the quantity column (a mixed-unit grand total is not a quantity) unless the user opts in, and
/// then it is captioned "Total qty (mixed Std UOM)".
/// </para>
/// </summary>
public partial class IvStockSummary : PageBase
{
    [Inject] private IIvStockSummaryService Summaries { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected int TotalCount;

    protected bool CanExport;
    protected bool CanViewValue;

    // Applied state.
    protected string AppliedGroupBy = IvStockSummaryGroupBys.Default;
    protected string? AppliedICode;
    protected string? AppliedWhCode;
    protected string? AppliedIClassCode;
    protected IReadOnlyList<string> AppliedStatuses { get; set; } = [];
    protected bool AppliedIncludeZeroQty = true;
    protected bool AppliedIncludeInactive = true;
    protected bool AppliedIncludeNonStockControl = true;

    /// <summary>The D16 opt-in. Default OFF: a mixed-unit "15,382" misleads even with a tooltip.</summary>
    protected bool AppliedShowMixedQty;

    // Draft (popup) state.
    protected string? DraftGroupBy;
    protected string? DraftICode;
    protected string? DraftWhCode;
    protected string? DraftIClassCode;
    protected IEnumerable<string> DraftStatuses { get; set; } = [];
    protected bool DraftIncludeZeroQty = true;
    protected bool DraftIncludeInactive = true;
    protected bool DraftIncludeNonStockControl = true;
    protected bool DraftShowMixedQty;

    // Summary strip.
    protected int SummaryItemCount;
    protected int SummaryPileCount;
    protected int SummaryZeroQtyPileCount;
    protected decimal SummaryTotalQty;
    protected decimal? SummaryTotalValue;

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Statuses { get; set; } = [];

    /// <summary>The grouping selector's option shape — the house pattern for a combo over a code list.</summary>
    public sealed record GroupByOption(string Token, string Name);

    protected IReadOnlyList<GroupByOption> GroupByOptions { get; } =
    [
        new(IvStockSummaryGroupBys.Item, "Item"),
        new(IvStockSummaryGroupBys.Warehouse, "Warehouse"),
        new(IvStockSummaryGroupBys.ItemWarehouse, "Item × Warehouse"),
        new(IvStockSummaryGroupBys.Class, "Item class")
    ];

    protected IvStockSummaryGridDataSource DataSource { get; private set; } = default!;

    protected string? SelectedGroupBy
    {
        get => AppliedGroupBy;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                AppliedGroupBy = IvStockSummaryGroupBys.Normalize(value);
            }
        }
    }

    protected string GroupByCaption => IvStockSummaryGroupBys.Describe(AppliedGroupBy);

    protected string GroupCountLabel => TotalCount == 1 ? "1 group" : $"{TotalCount:N0} groups";

    protected string GridKey => $"inv-stock-summary-{AppliedGroupBy.ToLowerInvariant()}";

    protected string GridTitle => $"{GroupByCaption} — {GroupCountLabel}";

    protected bool GroupsByIdentifiableKey =>
        AppliedGroupBy is IvStockSummaryGroupBys.Warehouse or IvStockSummaryGroupBys.Class;

    protected bool GroupsByItem =>
        AppliedGroupBy is IvStockSummaryGroupBys.Item or IvStockSummaryGroupBys.ItemWarehouse;

    /// <summary>
    /// True when a quantity total is a quantity at all (D16). Warehouse/Class groups mix standard UOMs,
    /// so the column appears only after an explicit opt-in.
    /// </summary>
    protected bool ShowsQuantityColumn =>
        GroupsByItem || AppliedShowMixedQty;

    /// <summary>
    /// The quantity caption is per grouping mode, never one caption for all: "Total qty" where the group
    /// holds a single standard UOM, and an explicit mixed-unit caveat where it does not (D16).
    /// </summary>
    protected string QuantityCaption =>
        GroupsByItem ? "Total qty" : "Total qty (mixed Std UOM)";

    protected List<GridColumnData> Columns()
    {
        var index = 1;
        var columns = new List<GridColumnData>();

        if (AppliedGroupBy == IvStockSummaryGroupBys.Warehouse)
        {
            columns.Add(new() { Caption = "Warehouse", FieldName = nameof(IvStockSummaryRow.WhCode), Width = "130px", VisibleIndex = index++, SortIndex = 0 });
            columns.Add(new() { Caption = "Description", FieldName = nameof(IvStockSummaryRow.WhDesc), VisibleIndex = index++ });
        }
        else if (AppliedGroupBy == IvStockSummaryGroupBys.Class)
        {
            columns.Add(new() { Caption = "Class", FieldName = nameof(IvStockSummaryRow.IClassCode), Width = "130px", VisibleIndex = index++, SortIndex = 0 });
            columns.Add(new() { Caption = "Description", FieldName = nameof(IvStockSummaryRow.IClassDesc), VisibleIndex = index++ });
        }
        else
        {
            columns.Add(new() { Caption = "Item", FieldName = nameof(IvStockSummaryRow.ICode), Width = "130px", VisibleIndex = index++, SortIndex = 0 });
            columns.Add(new() { Caption = "Description", FieldName = nameof(IvStockSummaryRow.IDesc), VisibleIndex = index++ });

            if (AppliedGroupBy == IvStockSummaryGroupBys.ItemWarehouse)
            {
                columns.Add(new() { Caption = "Warehouse", FieldName = nameof(IvStockSummaryRow.WhCode), Width = "120px", VisibleIndex = index++ });
            }
            else
            {
                columns.Add(new() { Caption = "Class", FieldName = nameof(IvStockSummaryRow.IClassCode), Width = "100px", VisibleIndex = index++ });
            }

            // A real UOM exists only when the group holds one (Item and Item × Warehouse — D16).
            columns.Add(new() { Caption = "UOM", FieldName = nameof(IvStockSummaryRow.UomDisplay), Width = "80px", VisibleIndex = index++ });
        }

        if (ShowsQuantityColumn)
        {
            columns.Add(new() { Caption = QuantityCaption, FieldName = nameof(IvStockSummaryRow.TotalQty), DataType = "decimal", DisplayFormat = "n4", Width = "150px", VisibleIndex = index++ });
        }

        if (GroupsByIdentifiableKey)
        {
            columns.Add(new() { Caption = "Items", FieldName = nameof(IvStockSummaryRow.ItemCount), DataType = "int", Width = "80px", VisibleIndex = index++ });
        }

        columns.Add(new() { Caption = "Piles", FieldName = nameof(IvStockSummaryRow.PileCount), DataType = "int", Width = "80px", VisibleIndex = index++ });
        columns.Add(new() { Caption = "Zero-qty piles", FieldName = nameof(IvStockSummaryRow.ZeroQtyPileCount), DataType = "int", Width = "110px", VisibleIndex = index++ });

        // The money column is omitted, never blanked, when the caller may not see value (D11 Option B).
        columns.Add(new() { Caption = "Est. value", FieldName = nameof(IvStockSummaryRow.EstValue), DataType = "decimal", DisplayFormat = "n4", Width = "120px", Visible = CanViewValue, VisibleIndex = index });

        return columns;
    }

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvStockSummaryGridDataSource(SearchPageAsync);

        CanExport = await AccessRights.CanAsync(MenuCodes.InventoryStockSummary, PermissionCodes.Export);
        CanViewValue = await AccessRights.CanAsync(MenuCodes.InventoryStockSummary, PermissionCodes.ViewPrice);

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

    protected async Task OnButtonClick(SelectedButtonInfo<IvStockSummaryRow> info)
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

    protected async Task OnGroupByChanged(string? groupBy)
    {
        AppliedGroupBy = IvStockSummaryGroupBys.Normalize(groupBy);
        await ReloadGridAsync();
    }

    protected void OpenFilterPopup()
    {
        DraftGroupBy = AppliedGroupBy;
        DraftICode = AppliedICode;
        DraftWhCode = AppliedWhCode;
        DraftIClassCode = AppliedIClassCode;
        DraftStatuses = AppliedStatuses;
        DraftIncludeZeroQty = AppliedIncludeZeroQty;
        DraftIncludeInactive = AppliedIncludeInactive;
        DraftIncludeNonStockControl = AppliedIncludeNonStockControl;
        DraftShowMixedQty = AppliedShowMixedQty;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedGroupBy = IvStockSummaryGroupBys.Normalize(DraftGroupBy);
        AppliedICode = Normalize(DraftICode);
        AppliedWhCode = Normalize(DraftWhCode);
        AppliedIClassCode = Normalize(DraftIClassCode);
        AppliedStatuses = Clean(DraftStatuses);
        AppliedIncludeZeroQty = DraftIncludeZeroQty;
        AppliedIncludeInactive = DraftIncludeInactive;
        AppliedIncludeNonStockControl = DraftIncludeNonStockControl;
        AppliedShowMixedQty = DraftShowMixedQty;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftGroupBy = IvStockSummaryGroupBys.Default;
        DraftICode = null;
        DraftWhCode = null;
        DraftIClassCode = null;
        DraftStatuses = [];
        DraftIncludeZeroQty = true;
        DraftIncludeInactive = true;
        DraftIncludeNonStockControl = true;
        DraftShowMixedQty = false;

        AppliedGroupBy = IvStockSummaryGroupBys.Default;
        AppliedICode = null;
        AppliedWhCode = null;
        AppliedIClassCode = null;
        AppliedStatuses = [];
        AppliedIncludeZeroQty = true;
        AppliedIncludeInactive = true;
        AppliedIncludeNonStockControl = true;
        AppliedShowMixedQty = false;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    private async Task ReloadGridAsync()
    {
        DataSource.UpdateFilters(BuildQuery());
        await RefreshSummaryAsync();

        // The column set depends on the grouping mode, so the grid is rebuilt as well as reloaded.
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private IvStockSummaryQuery BuildQuery() =>
        new()
        {
            GroupBy = AppliedGroupBy,
            ICode = AppliedICode,
            WhCode = AppliedWhCode,
            IClassCode = AppliedIClassCode,
            IStatuses = AppliedStatuses,
            IncludeZeroQty = AppliedIncludeZeroQty,
            IncludeInactive = AppliedIncludeInactive,
            IncludeNonStockControl = AppliedIncludeNonStockControl
        };

    private async Task RefreshSummaryAsync()
    {
        var query = BuildQuery();
        query.Skip = 0;
        query.Take = 1;

        var result = await Summaries.GetSummaryAsync(MenuCodes.InventoryStockSummary, query);
        if (result.Succeeded && result.Data is not null)
        {
            TotalCount = result.Data.GroupCount;
            SummaryItemCount = result.Data.ItemCount;
            SummaryPileCount = result.Data.PileCount;
            SummaryZeroQtyPileCount = result.Data.ZeroQtyPileCount;
            SummaryTotalQty = result.Data.TotalQty;
            SummaryTotalValue = result.Data.TotalValue;
        }
        else
        {
            TotalCount = 0;
            SummaryItemCount = 0;
            SummaryPileCount = 0;
            SummaryZeroQtyPileCount = 0;
            SummaryTotalQty = 0m;
            SummaryTotalValue = null;
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
        }
    }

    private async Task<(IReadOnlyList<IvStockSummaryRow> Rows, int TotalCount)> SearchPageAsync(
        IvStockSummaryQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Summaries.SearchAsync(MenuCodes.InventoryStockSummary, query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load the summary.";
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

        var url = QueryHelpers.AddQueryString("/inventory/stock-summary/export", BuildQueryDictionary());
        Navigation.NavigateTo(url, forceLoad: true);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The applied grouping and filters — never the grid's current page. Company and branch are absent
    /// on purpose: the endpoint resolves them server-side (D19).
    /// </summary>
    private Dictionary<string, string?> BuildQueryDictionary()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["groupBy"] = AppliedGroupBy,
            ["includeZeroQty"] = AppliedIncludeZeroQty ? "true" : "false",
            ["includeInactive"] = AppliedIncludeInactive ? "true" : "false",
            ["includeNonStockControl"] = AppliedIncludeNonStockControl ? "true" : "false",

            // The D16 opt-in travels with the export: the file must not carry a mixed-unit total the
            // screen refused to show.
            ["showMixedQty"] = AppliedShowMixedQty ? "true" : "false"
        };

        AddText(dict, "iCode", AppliedICode);
        AddText(dict, "whCode", AppliedWhCode);
        AddText(dict, "iClassCode", AppliedIClassCode);

        if (AppliedStatuses.Count > 0)
        {
            dict["statuses"] = string.Join(',', AppliedStatuses);
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
/// Server-side paging source for the summary via <see cref="IIvStockSummaryService.SearchAsync"/>. The
/// filters are rebuilt from the applied state on every reload, so a mode change can never page with the
/// previous mode's grouping.
/// </summary>
public sealed class IvStockSummaryGridDataSource : GridCustomDataSource
{
    private readonly Func<IvStockSummaryQuery, CancellationToken, Task<(IReadOnlyList<IvStockSummaryRow> Rows, int TotalCount)>> _loader;
    private IvStockSummaryQuery _filters = new();

    public IvStockSummaryGridDataSource(
        Func<IvStockSummaryQuery, CancellationToken, Task<(IReadOnlyList<IvStockSummaryRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public void UpdateFilters(IvStockSummaryQuery query) => _filters = Clone(query);

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

    private static IvStockSummaryQuery Clone(IvStockSummaryQuery source) =>
        new()
        {
            GroupBy = source.GroupBy,
            ICode = source.ICode,
            WhCode = source.WhCode,
            IClassCode = source.IClassCode,
            IStatuses = source.IStatuses,
            SearchText = source.SearchText,
            IncludeZeroQty = source.IncludeZeroQty,
            IncludeInactive = source.IncludeInactive,
            IncludeNonStockControl = source.IncludeNonStockControl,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
