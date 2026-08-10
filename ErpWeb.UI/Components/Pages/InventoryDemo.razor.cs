using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Models;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

public partial class InventoryDemo : PageBase
{
    [Inject]
    private IAccessRightService AccessRights { get; set; } = default!;

    private string? _status;
    private List<InventoryDemoItem> _items = new();

    private readonly List<GridColumnDefinition> _columns =
    [
        new() { FieldName = "StockCode", Caption = "Stock Code", DataType = "string", Width = "140px", VisibleIndex = 0 },
        new() { FieldName = "Description", Caption = "Description", DataType = "string", VisibleIndex = 1 },
        new() { FieldName = "Uom", Caption = "UOM", DataType = "string", Width = "80px", VisibleIndex = 2 },
        new() { FieldName = "OnHand", Caption = "On Hand", DataType = "decimal", DisplayFormat = "n2", Width = "110px", VisibleIndex = 3 },
        new() { FieldName = "UnitCost", Caption = "Unit Cost", DataType = "decimal", DisplayFormat = "c2", Width = "110px", VisibleIndex = 4 },
        new() { FieldName = "Active", Caption = "Active", DataType = "bool", Width = "90px", VisibleIndex = 5 },
        new() { FieldName = "UpdatedOn", Caption = "Updated", DataType = "datetime", DisplayFormat = "g", Width = "160px", VisibleIndex = 6 }
    ];

    private readonly List<GridToolbarButton> _toolbar =
    [
        new() { Text = "Refresh", IconCssClass = "oi oi-reload", ToolTip = "Reload demo data" }
    ];

    private readonly List<GridRowAction> _actions =
    [
        new() { Text = "View", IconCssClass = "oi oi-eye", ToolTip = "View item" }
    ];

    private readonly List<GridSummaryDefinition> _summaries =
    [
        new() { FieldName = "OnHand", SummaryType = GridSummaryItemType.Sum, DisplayFormat = "n2" },
        new() { FieldName = "UnitCost", SummaryType = GridSummaryItemType.Avg, DisplayFormat = "c2" }
    ];

    protected override void OnInitialized()
    {
        _items = CreateDemoData();
    }

    private List<InventoryDemoItem> CreateDemoData()
    {
        // Demo data only — company/branch/location come from authenticated context.
        var company = CurrentUser.CompanyCode ?? "N/A";
        return
        [
            new InventoryDemoItem { Id = 1, StockCode = $"{company}-A100", Description = "Widget A", Uom = "EA", OnHand = 120, UnitCost = 12.50m, Active = true, UpdatedOn = DateTime.Today.AddDays(-1) },
            new InventoryDemoItem { Id = 2, StockCode = $"{company}-B200", Description = "Widget B", Uom = "BOX", OnHand = 45.5m, UnitCost = 28.00m, Active = true, UpdatedOn = DateTime.Today.AddDays(-3) },
            new InventoryDemoItem { Id = 3, StockCode = $"{company}-C300", Description = "Spare Part C", Uom = "EA", OnHand = 0, UnitCost = 5.75m, Active = false, UpdatedOn = DateTime.Today.AddDays(-10) },
            new InventoryDemoItem { Id = 4, StockCode = $"{company}-D400", Description = "Assembly D", Uom = "SET", OnHand = 18, UnitCost = 140.00m, Active = true, UpdatedOn = DateTime.Today }
        ];
    }

    private async Task OnAddClickAsync()
    {
        // UI hide is not the security boundary — re-check on the action path.
        if (!await AccessRights.CanAsync(MenuCodes.InventoryDemo, PermissionCodes.Add))
        {
            _status = "Access denied: ADD permission required.";
            return;
        }

        var company = CurrentUser.CompanyCode ?? "N/A";
        var nextId = _items.Count == 0 ? 1 : _items.Max(i => i.Id) + 1;
        _items.Add(new InventoryDemoItem
        {
            Id = nextId,
            StockCode = $"{company}-NEW{nextId}",
            Description = "New demo item",
            Uom = "EA",
            OnHand = 0,
            UnitCost = 0,
            Active = true,
            UpdatedOn = DateTime.Now
        });
        _status = $"Added demo item {company}-NEW{nextId}.";
    }

    private Task HandleToolbarAsync(GridToolbarClickEventArgs args)
    {
        if (args.Button.Text == "Refresh")
        {
            _items = CreateDemoData();
            _status = $"Refreshed at {DateTime.Now:T} for company {CurrentUser.CompanyCode}";
        }

        return Task.CompletedTask;
    }

    private Task HandleRowActionAsync(GridRowActionEventArgs<InventoryDemoItem> args)
    {
        _status = $"{args.Action.Text}: {args.Row.StockCode} ({args.Row.Description})";
        return Task.CompletedTask;
    }
}
