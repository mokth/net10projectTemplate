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
/// <b>Inventory Value</b> — the authoritative sealed-ledger value grouped by Item, Warehouse or Class.
///
/// <para>The report uses the latest applicable valuation snapshot and replays sealed valuation facts
/// through the requested as-of date. Warehouse rows are labelled as allocations when the financial
/// ledger is only branch/item scoped.</para>
///
/// <para>
/// It is the Stock Summary composition with a value-first emphasis, so it reuses
/// <see cref="IIvStockSummaryService"/> under its OWN menu code — a second grouped query would only give
/// the two screens a way to disagree. Its <c>ACCESS</c> and <c>VIEW_PRICE</c> are therefore checked
/// against <c>INV_STOCK_VALUE</c>, not against the summary screen's grant (D11 Option B).
/// </para>
/// </summary>
public partial class IvStockValue : PageBase
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

    protected string AppliedGroupBy = IvStockSummaryGroupBys.Default;
    protected string? AppliedICode;
    protected string? AppliedWhCode;
    protected string? AppliedIClassCode;
    protected IReadOnlyList<string> AppliedStatuses { get; set; } = [];
    protected bool AppliedIncludeZeroQty = true;
    protected bool AppliedIncludeInactive = true;
    protected bool AppliedIncludeNonStockControl = true;
    protected bool AppliedShowMixedQty;

    protected string? DraftGroupBy;
    protected string? DraftICode;
    protected string? DraftWhCode;
    protected string? DraftIClassCode;
    protected IEnumerable<string> DraftStatuses { get; set; } = [];
    protected bool DraftIncludeZeroQty = true;
    protected bool DraftIncludeInactive = true;
    protected bool DraftIncludeNonStockControl = true;
    protected bool DraftShowMixedQty;

    protected int SummaryItemCount;
    protected int SummaryPileCount;
    protected decimal SummaryTotalQty;
    protected decimal? SummaryTotalValue;

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Statuses { get; set; } = [];

    /// <summary>
    /// Item / Warehouse / Class — deliberately NOT Item × Warehouse: on this screen the question is
    /// "what is it worth, grouped this way", and the extra split belongs to the Stock Summary screen.
    /// </summary>
    public sealed record GroupByOption(string Token, string Name);

    protected IReadOnlyList<GroupByOption> GroupByOptions { get; } =
    [
        new(IvStockSummaryGroupBys.Item, "Item"),
        new(IvStockSummaryGroupBys.Warehouse, "Warehouse"),
        new(IvStockSummaryGroupBys.Class, "Item class")
    ];

    protected IvStockSummaryGridDataSource DataSource { get; private set; } = default!;

    protected string GroupByCaption => IvStockSummaryGroupBys.Describe(AppliedGroupBy);

    protected string GroupCountLabel => TotalCount == 1 ? "1 group" : $"{TotalCount:N0} groups";

    protected string GridKey => $"inv-stock-value-{AppliedGroupBy.ToLowerInvariant()}";

    protected string GridTitle => $"Inventory valuation — {GroupCountLabel}";

    protected bool GroupsByIdentifiableKey =>
        AppliedGroupBy is IvStockSummaryGroupBys.Warehouse or IvStockSummaryGroupBys.Class;

    protected bool GroupsByItem => AppliedGroupBy == IvStockSummaryGroupBys.Item;

    /// <summary>D16: a quantity total is only a quantity where the group holds one standard UOM.</summary>
    protected bool ShowsQuantityColumn => GroupsByItem || AppliedShowMixedQty;

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
            columns.Add(new() { Caption = "Class", FieldName = nameof(IvStockSummaryRow.IClassCode), Width = "100px", VisibleIndex = index++ });
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

        columns.Add(new() { Caption = "Cost method", FieldName = nameof(IvStockSummaryRow.CostMethod), Width = "150px", VisibleIndex = index++ });
        columns.Add(new() { Caption = "Valuation status", FieldName = nameof(IvStockSummaryRow.ValuationStatus), Width = "150px", VisibleIndex = index++ });
        columns.Add(new() { Caption = "Unit cost", FieldName = nameof(IvStockSummaryRow.UnitCost), DataType = "decimal", DisplayFormat = "n4", Width = "120px", Visible = CanViewValue, VisibleIndex = index++ });
        columns.Add(new() { Caption = "Inventory value", FieldName = nameof(IvStockSummaryRow.InventoryValue), DataType = "decimal", DisplayFormat = "n4", Width = "140px", Visible = CanViewValue, VisibleIndex = index });

        return columns;
    }

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvStockSummaryGridDataSource(SearchPageAsync);

        CanExport = await AccessRights.CanAsync(MenuCodes.InventoryStockValue, PermissionCodes.Export);
        CanViewValue = await AccessRights.CanAsync(MenuCodes.InventoryStockValue, PermissionCodes.ViewPrice);

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

        // Item × Warehouse is not offered here; if it arrives, the page falls back to Item rather than
        // rendering a column set that belongs to the other screen.
        if (AppliedGroupBy == IvStockSummaryGroupBys.ItemWarehouse)
        {
            AppliedGroupBy = IvStockSummaryGroupBys.Item;
        }

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
        if (AppliedGroupBy == IvStockSummaryGroupBys.ItemWarehouse)
        {
            AppliedGroupBy = IvStockSummaryGroupBys.Item;
        }

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

        var result = await Summaries.GetSummaryAsync(MenuCodes.InventoryStockValue, query);
        if (result.Succeeded && result.Data is not null)
        {
            TotalCount = result.Data.GroupCount;
            SummaryItemCount = result.Data.ItemCount;
            SummaryPileCount = result.Data.PileCount;
            SummaryTotalQty = result.Data.TotalQty;
            SummaryTotalValue = result.Data.TotalValue;
        }
        else
        {
            TotalCount = 0;
            SummaryItemCount = 0;
            SummaryPileCount = 0;
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
        var result = await Summaries.SearchAsync(
            MenuCodes.InventoryStockValue, query, cancellationToken);

        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load the authoritative inventory value.";
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

        var url = QueryHelpers.AddQueryString("/inventory/valuation/export", BuildQueryDictionary());
        Navigation.NavigateTo(url, forceLoad: true);
        await Task.CompletedTask;
    }

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
