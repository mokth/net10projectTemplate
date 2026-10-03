using DevExpress.Blazor;
using ErpWeb.Core.Production;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Inquiry;

public partial class PrStockMovement : PageBase
{
    [Inject] private IProductionStockHistoryService History { get; set; } = default!;

    private DxGrid? _grid;

    protected bool IsBootstrapping = true;
    protected string? CoverageWarning;
    protected string ItemCode = string.Empty;
    protected string BaseUom = "EA";
    protected DateTime? DateFrom;
    protected DateTime? DateTo;
    protected string WorkOrderNo = string.Empty;
    protected string BalanceStage = string.Empty;
    protected string ProductionLocationCode = string.Empty;
    protected string LotIdentity = string.Empty;
    protected long Watermark;
    protected int TotalCount;
    protected InMemoryGridDataSource<ProductionStockHistoryRow> DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 line" : $"{TotalCount:N0} lines";

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Effective", FieldName = nameof(ProductionStockHistoryRow.EffectiveAt), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "140px", VisibleIndex = 1 },
        new() { Caption = "Document", FieldName = nameof(ProductionStockHistoryRow.SourceDocumentNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Work order", FieldName = nameof(ProductionStockHistoryRow.WorkOrderNo), Width = "120px", VisibleIndex = 3 },
        new() { Caption = "Item", FieldName = nameof(ProductionStockHistoryRow.ItemCode), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Stage", FieldName = nameof(ProductionStockHistoryRow.BalanceStage), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Location", FieldName = nameof(ProductionStockHistoryRow.ProductionLocationCode), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Lot", FieldName = nameof(ProductionStockHistoryRow.LotIdentity), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Disposition", FieldName = nameof(ProductionStockHistoryRow.StockStatusCode), Width = "110px", VisibleIndex = 8 },
        new() { Caption = "In", FieldName = nameof(ProductionStockHistoryRow.InQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 9 },
        new() { Caption = "Out", FieldName = nameof(ProductionStockHistoryRow.OutQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Valuation", FieldName = nameof(ProductionStockHistoryRow.ValuationStatus), Width = "110px", VisibleIndex = 11 }
    ];

    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override Task OnPageInitializedAsync()
    {
        DataSource = new InMemoryGridDataSource<ProductionStockHistoryRow>();
        Buttons = [new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" }];
        IsBootstrapping = false;
        return Task.CompletedTask;
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;

    protected async Task OnButtonClick(SelectedButtonInfo<ProductionStockHistoryRow> info)
    {
        if (string.Equals(info.SelectedButton.Text, "REFRESH", StringComparison.OrdinalIgnoreCase))
            await ReloadAsync();
    }

    protected async Task ReloadAsync()
    {
        ErrorMessage = null;
        CoverageWarning = null;
        if (string.IsNullOrWhiteSpace(ItemCode) || string.IsNullOrWhiteSpace(BaseUom))
        {
            ErrorMessage = "Item and base UOM are required.";
            return;
        }

        var result = await History.GetMovementsAsync(new ProductionStockHistoryQuery
        {
            ItemCode = ItemCode.Trim(),
            BaseUom = BaseUom.Trim(),
            From = DateFrom?.Date,
            To = DateTo?.Date.AddDays(1),
            WorkOrderNo = NullIfEmpty(WorkOrderNo),
            BalanceStage = NullIfEmpty(BalanceStage),
            ProductionLocationCode = NullIfEmpty(ProductionLocationCode),
            LotIdentity = NullIfEmpty(LotIdentity),
        });

        if (!result.Succeeded || result.Data is null)
        {
            DataSource.SetRows([]);
            TotalCount = 0;
            ErrorMessage = result.Message ?? "Unable to load production movements.";
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
