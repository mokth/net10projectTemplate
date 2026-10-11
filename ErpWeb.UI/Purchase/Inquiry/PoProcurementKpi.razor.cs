using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.UI.Purchase.Inquiry;

public partial class PoProcurementKpi : PoInquiryPageBase
{
    [Inject] private IPoProcurementKpiService KpiService { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;

    protected bool IsFeatureEnabled { get; private set; }
    protected bool IsLoading { get; private set; }
    protected DateTime AsOfDate { get; private set; }
    protected DateTime? LastRefreshed { get; private set; }
    protected PoProcurementKpiSummary? Summary { get; private set; }
    protected string? KpiError { get; private set; }
    protected PoInquiryGridDataSource<PoDeliveryPerformanceRow> SupplierDataSource { get; private set; } = default!;
    private DxGrid? _supplierGrid;

    protected List<ButtonInfo> SupplierButtons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> SupplierActionButtons =>
    [new() { Text = "SUPPLIER TRX", IConClass = "fa-solid fa-arrow-right-arrow-left", ToolTip = "Open supplier transactions" }];

    protected List<GridColumnData> SupplierColumns =>
    [
        new() { Caption = "Supplier", FieldName = nameof(PoDeliveryPerformanceRow.SuppCode), Width = "120px", VisibleIndex = 1 },
        new() { Caption = "Name", FieldName = nameof(PoDeliveryPerformanceRow.SuppName), VisibleIndex = 2 },
        new() { Caption = "POs", FieldName = nameof(PoDeliveryPerformanceRow.PurchaseOrderCount), DataType = "int", Width = "80px", VisibleIndex = 3 },
        new() { Caption = "Lines", FieldName = nameof(PoDeliveryPerformanceRow.PoLinesEvaluated), DataType = "int", Width = "80px", VisibleIndex = 4 },
        new() { Caption = "Completed", FieldName = nameof(PoDeliveryPerformanceRow.CompletedLines), DataType = "int", Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Partial", FieldName = nameof(PoDeliveryPerformanceRow.PartialLines), DataType = "int", Width = "85px", VisibleIndex = 6 },
        new() { Caption = "On-time", FieldName = nameof(PoDeliveryPerformanceRow.OnTimeLines), DataType = "int", Width = "90px", VisibleIndex = 7 },
        new() { Caption = "Late", FieldName = nameof(PoDeliveryPerformanceRow.LateLines), DataType = "int", Width = "80px", VisibleIndex = 8 },
        new() { Caption = "On-time %", FieldName = nameof(PoDeliveryPerformanceRow.OnTimePct), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 9 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        IsFeatureEnabled = Configuration.GetValue<bool>("Features:ProcurementKpi");
        AsOfDate = Dates.Today;
        if (!IsFeatureEnabled)
        {
            return;
        }

        SupplierDataSource = new(SearchSupplierPerformanceAsync, OnSupplierGridError);
        SupplierDataSource.UpdateFilters(BuildSupplierQuery());
        await RefreshKpisAsync();
    }

    protected async Task ApplyKpiFiltersAsync()
    {
        AsOfDate = Dates.Today;
        await RefreshKpisAsync();
        if (SupplierDataSource is not null)
        {
            SupplierDataSource.UpdateFilters(BuildSupplierQuery());
            _supplierGrid?.Reload();
        }
    }

    protected async Task RefreshKpisAsync()
    {
        if (!IsFeatureEnabled)
        {
            return;
        }

        IsLoading = true;
        KpiError = null;
        try
        {
            var result = await KpiService.GetSummaryAsync(BuildKpiQuery());
            if (!result.Succeeded || result.Data is null)
            {
                KpiError = result.Message ?? "Procurement KPI is unavailable.";
                Summary = null;
                return;
            }

            Summary = result.Data;
            LastRefreshed = Dates.Now;
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected async Task OnSupplierButtonClick(SelectedButtonInfo<PoDeliveryPerformanceRow> info)
    {
        switch ((info.SelectedButton.Text ?? string.Empty).ToUpperInvariant())
        {
            case "REFRESH":
                await ApplyKpiFiltersAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/kpi/suppliers/export"), forceLoad: true);
                break;
        }
    }

    protected Task OnSupplierActionClick(SelectedButtonInfo<PoDeliveryPerformanceRow> info)
    {
        if (string.IsNullOrWhiteSpace(info.SelectedRow?.SuppCode))
        {
            return Task.CompletedTask;
        }

        var url = QueryHelpers.AddQueryString("/purchase/inquiry/supplier", "suppCode", info.SelectedRow.SuppCode);
        Navigation.NavigateTo(url);
        return Task.CompletedTask;
    }

    protected void OnSupplierGrid(DxGrid grid) => _supplierGrid = grid;

    protected void ExportSummary() =>
        Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/kpi/export"), forceLoad: true);

    protected void OpenPurchaseOrders() => Navigation.NavigateTo("/purchase/inquiry/po-outstanding");

    protected void OpenMatching() => Navigation.NavigateTo("/purchase/inquiry/matching");

    protected void OpenDeliveryPerformance() => Navigation.NavigateTo("/purchase/inquiry/delivery-performance");

    protected void OpenPriceHistory() => Navigation.NavigateTo("/purchase/inquiry/price-history");

    protected override PoInquiryQuery BuildQuery() => BuildKpiQuery();

    private PoInquiryQuery BuildKpiQuery() => new()
    {
        DateFrom = DateFrom?.Date,
        DateTo = DateTo?.Date,
        SuppCode = Clean(SuppCode),
        BuyerCode = Clean(BuyerCode),
        ItemCode = Clean(ItemCode),
        Currency = Clean(Currency),
        AsOfDate = AsOfDate == default ? Dates.Today : AsOfDate.Date
    };

    private PoInquiryQuery BuildSupplierQuery()
    {
        var query = BuildKpiQuery();
        query.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        return query;
    }

    private Task<ErpWeb.Core.Inventory.IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceRow>>> SearchSupplierPerformanceAsync(
        PoInquiryQuery query, CancellationToken cancellationToken)
    {
        query.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        query.AsOfDate = AsOfDate;
        return Inquiry.GetDeliveryPerformanceSummaryAsync(MenuCodes.PurchaseKpi, query, cancellationToken);
    }

    private void OnSupplierGridError(string? message) => KpiError = message;

    private string BuildExportUrl(string path)
    {
        var query = BuildKpiQuery();
        var parameters = new Dictionary<string, string?>
        {
            ["dateFrom"] = query.DateFrom?.ToString("yyyy-MM-dd"),
            ["dateTo"] = query.DateTo?.ToString("yyyy-MM-dd"),
            ["suppCode"] = query.SuppCode,
            ["buyerCode"] = query.BuyerCode,
            ["itemCode"] = query.ItemCode,
            ["currency"] = query.Currency,
            ["asOfDate"] = query.AsOfDate?.ToString("yyyy-MM-dd")
        };
        return QueryHelpers.AddQueryString(path, parameters);
    }

}
