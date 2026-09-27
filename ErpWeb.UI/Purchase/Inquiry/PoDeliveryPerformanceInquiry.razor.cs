using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>ETA-based supplier delivery performance using latest PoOrderDetail.RecvDate.</summary>
public partial class PoDeliveryPerformanceInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoDeliveryPerformanceRow> DataSource { get; private set; } = default!;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "SUPPLIER TRX", IConClass = "fa-solid fa-arrow-right-arrow-left", ToolTip = "Open supplier transactions" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Supplier", FieldName = nameof(PoDeliveryPerformanceRow.SuppCode), Width = "110px", VisibleIndex = 1 },
        new() { Caption = "Name", FieldName = nameof(PoDeliveryPerformanceRow.SuppName), VisibleIndex = 2 },
        new() { Caption = "Purchase orders", FieldName = nameof(PoDeliveryPerformanceRow.PurchaseOrderCount), DataType = "int", Width = "120px", VisibleIndex = 3 },
        new() { Caption = "PO lines evaluated", FieldName = nameof(PoDeliveryPerformanceRow.PoLinesEvaluated), DataType = "int", Width = "130px", VisibleIndex = 4 },
        new() { Caption = "Ordered qty", FieldName = nameof(PoDeliveryPerformanceRow.OrderedQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Received qty", FieldName = nameof(PoDeliveryPerformanceRow.ReceivedQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 6 },
        new() { Caption = "On-time lines", FieldName = nameof(PoDeliveryPerformanceRow.OnTimeLines), DataType = "int", Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Late lines", FieldName = nameof(PoDeliveryPerformanceRow.LateLines), DataType = "int", Width = "100px", VisibleIndex = 8 },
        new() { Caption = "On-time %", FieldName = nameof(PoDeliveryPerformanceRow.OnTimePct), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 9 },
        new() { Caption = "Avg days late", FieldName = nameof(PoDeliveryPerformanceRow.AvgDaysLate), DataType = "decimal", DisplayFormat = "n2", Width = "110px", VisibleIndex = 10 },
        new() { Caption = "Max days late", FieldName = nameof(PoDeliveryPerformanceRow.MaxDaysLate), DataType = "int", Width = "110px", VisibleIndex = 11 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new PoInquiryGridDataSource<PoDeliveryPerformanceRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task OnDraftItemCodeChangedAsync(string? code)
    {
        DraftItemCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        return Task.CompletedTask;
    }

    private Task OnDraftItemSelectedAsync(IvStockMasterLookupRow item)
    {
        DraftItemCode = item?.ICode;
        return Task.CompletedTask;
    }

    private Task<IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceRow>>> SearchPageAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetSupplierDeliveryPerformanceAsync(MenuCodes.PurchaseDeliveryPerformance, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<PoDeliveryPerformanceRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(
                    BuildExportUrl("/purchase/inquiry/delivery-performance/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoDeliveryPerformanceRow> info)
    {
        if (info.SelectedRow is null || string.IsNullOrWhiteSpace(info.SelectedRow.SuppCode))
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            return Task.CompletedTask;
        }

        var url = QueryHelpers.AddQueryString(
            "/purchase/inquiry/supplier",
            "suppCode",
            info.SelectedRow.SuppCode);
        OpenDocument(url);
        return Task.CompletedTask;
    }
}
