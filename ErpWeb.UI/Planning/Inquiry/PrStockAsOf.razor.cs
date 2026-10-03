using DevExpress.Blazor;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Inquiry;

public partial class PrStockAsOf : PageBase
{
    [Inject] private IProductionStockHistoryService History { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;

    private DxGrid? _grid;

    protected bool IsBootstrapping = true;
    protected string? CoverageWarning;
    protected string ItemCode = string.Empty;
    protected string BaseUom = "EA";
    protected DateTime? AsOf;
    protected string WorkOrderNo = string.Empty;
    protected long Watermark;
    protected int TotalCount;
    protected InMemoryGridDataSource<ProductionStockAsOfRow> DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 identity" : $"{TotalCount:N0} identities";

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Item", FieldName = nameof(ProductionStockAsOfRow.ItemCode), Width = "120px", VisibleIndex = 1 },
        new() { Caption = "Base UOM", FieldName = nameof(ProductionStockAsOfRow.BaseUom), Width = "90px", VisibleIndex = 2 },
        new() { Caption = "Work order", FieldName = nameof(ProductionStockAsOfRow.WorkOrderNo), Width = "130px", VisibleIndex = 3 },
        new() { Caption = "Stage", FieldName = nameof(ProductionStockAsOfRow.BalanceStage), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Location", FieldName = nameof(ProductionStockAsOfRow.ProductionLocationCode), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Lot", FieldName = nameof(ProductionStockAsOfRow.LotIdentity), Width = "120px", VisibleIndex = 6 },
        new() { Caption = "Disposition", FieldName = nameof(ProductionStockAsOfRow.StockStatusCode), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Base qty", FieldName = nameof(ProductionStockAsOfRow.BaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 8 }
    ];

    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override Task OnPageInitializedAsync()
    {
        DataSource = new InMemoryGridDataSource<ProductionStockAsOfRow>();
        Buttons = [new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" }];
        AsOf = Dates.Today;
        IsBootstrapping = false;
        return Task.CompletedTask;
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;

    protected async Task OnButtonClick(SelectedButtonInfo<ProductionStockAsOfRow> info)
    {
        if (string.Equals(info.SelectedButton.Text, "REFRESH", StringComparison.OrdinalIgnoreCase))
            await ReloadAsync();
    }

    protected async Task ReloadAsync()
    {
        ErrorMessage = null;
        CoverageWarning = null;
        if (string.IsNullOrWhiteSpace(ItemCode) || string.IsNullOrWhiteSpace(BaseUom) || AsOf is null)
        {
            ErrorMessage = "Item, base UOM and an as-of date are required.";
            return;
        }

        var result = await History.GetAsOfAsync(new ProductionStockHistoryQuery
        {
            ItemCode = ItemCode.Trim(),
            BaseUom = BaseUom.Trim(),
            AsOf = AsOf.Value.Date.AddDays(1),
            WorkOrderNo = string.IsNullOrWhiteSpace(WorkOrderNo) ? null : WorkOrderNo.Trim(),
        });

        if (!result.Succeeded || result.Data is null)
        {
            DataSource.SetRows([]);
            TotalCount = 0;
            ErrorMessage = result.Message ?? "Unable to load as-of balances.";
            _grid?.Reload();
            return;
        }

        CoverageWarning = result.Data.CoverageWarning;
        Watermark = result.Data.Watermark;
        TotalCount = result.Data.Rows.Count;
        DataSource.SetRows(result.Data.Rows);
        _grid?.Reload();
    }

    protected void DismissError() => ErrorMessage = null;
}
