using DevExpress.Blazor;
using ErpWeb.Core.Production;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Inquiry;

public partial class PrStockReconciliation : PageBase
{
    [Inject] private IProductionStockHistoryService History { get; set; } = default!;

    private DxGrid? _grid;

    protected bool IsBootstrapping = true;
    protected string? CoverageWarning;
    protected string ItemCode = string.Empty;
    protected string BaseUom = string.Empty;
    protected long Watermark;
    protected int TotalCount;
    protected InMemoryGridDataSource<ProductionStockReconcileFinding> DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 finding" : $"{TotalCount:N0} findings";

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Code", FieldName = nameof(ProductionStockReconcileFinding.Code), Width = "140px", VisibleIndex = 1 },
        new() { Caption = "Severity", FieldName = nameof(ProductionStockReconcileFinding.Severity), Width = "90px", VisibleIndex = 2 },
        new() { Caption = "Message", FieldName = nameof(ProductionStockReconcileFinding.Message), VisibleIndex = 3 },
        new() { Caption = "Item", FieldName = nameof(ProductionStockReconcileFinding.ItemCode), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Work order", FieldName = nameof(ProductionStockReconcileFinding.WorkOrderNo), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Stage", FieldName = nameof(ProductionStockReconcileFinding.BalanceStage), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Lot", FieldName = nameof(ProductionStockReconcileFinding.LotIdentity), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Live", FieldName = nameof(ProductionStockReconcileFinding.LiveBaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 8 },
        new() { Caption = "Ledger", FieldName = nameof(ProductionStockReconcileFinding.LedgerBaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 9 },
        new() { Caption = "Delta", FieldName = nameof(ProductionStockReconcileFinding.Delta), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 10 }
    ];

    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override Task OnPageInitializedAsync()
    {
        DataSource = new InMemoryGridDataSource<ProductionStockReconcileFinding>();
        Buttons = [new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" }];
        IsBootstrapping = false;
        return Task.CompletedTask;
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;

    protected async Task OnButtonClick(SelectedButtonInfo<ProductionStockReconcileFinding> info)
    {
        if (string.Equals(info.SelectedButton.Text, "REFRESH", StringComparison.OrdinalIgnoreCase))
            await ReloadAsync();
    }

    protected async Task ReloadAsync()
    {
        ErrorMessage = null;
        CoverageWarning = null;
        var result = await History.ReconcileAsync(new ProductionStockHistoryQuery
        {
            ItemCode = string.IsNullOrWhiteSpace(ItemCode) ? null : ItemCode.Trim(),
            BaseUom = string.IsNullOrWhiteSpace(BaseUom) ? null : BaseUom.Trim(),
        });

        if (!result.Succeeded || result.Data is null)
        {
            DataSource.SetRows([]);
            TotalCount = 0;
            ErrorMessage = result.Message ?? "Unable to reconcile production stock.";
            _grid?.Reload();
            return;
        }

        CoverageWarning = result.Data.CoverageWarning;
        Watermark = result.Data.Watermark;
        TotalCount = result.Data.Findings.Count;
        DataSource.SetRows(result.Data.Findings);
        _grid?.Reload();
    }

    protected void DismissError() => ErrorMessage = null;
}
