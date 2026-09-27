using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>PO-line delivery performance workbench with posted GR chronology and supplier summary.</summary>
public partial class PoDeliveryPerformanceInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoDeliveryPerformanceLineRow> LineDataSource { get; private set; } = default!;
    protected PoInquiryGridDataSource<PoDeliveryPerformanceRow> SummaryDataSource { get; private set; } = default!;
    protected PoDeliveryPerformanceLineRow? SelectedLine { get; private set; }

    protected bool ReceiptPopupVisible { get; set; }
    protected bool ReceiptLoading { get; set; }
    protected string ReceiptPopupTitle { get; set; } = "Receipts";
    protected IReadOnlyList<PoDeliveryPerformanceReceiptRow> ReceiptRows { get; set; } = [];

    protected IReadOnlyList<StatusFilterOption> DeliveryStatusOptions { get; } =
    [
        new(PoDeliveryPerformanceStatuses.NotDue, "Not due"),
        new(PoDeliveryPerformanceStatuses.Overdue, "Overdue"),
        new(PoDeliveryPerformanceStatuses.PartiallyReceived, "Partially received"),
        new(PoDeliveryPerformanceStatuses.Early, "Early"),
        new(PoDeliveryPerformanceStatuses.OnTime, "On time"),
        new(PoDeliveryPerformanceStatuses.Late, "Late"),
        new(PoDeliveryPerformanceStatuses.Exception, "Exception")
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open purchase order" },
        new() { Text = "RECEIPTS", IConClass = "fa-solid fa-list", ToolTip = "Show posted GR/NG receipts" }
    ];

    protected List<ButtonInfo> SummaryButtons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> SummaryActionButtons =>
    [
        new() { Text = "SUPPLIER TRX", IConClass = "fa-solid fa-arrow-right-arrow-left", ToolTip = "Open supplier transactions" }
    ];

    protected List<GridColumnData> LineColumns =>
    [
        new() { Caption = "Supplier", FieldName = nameof(PoDeliveryPerformanceLineRow.SuppCode), Width = "110px", VisibleIndex = 1 },
        new() { Caption = "PO no.", FieldName = nameof(PoDeliveryPerformanceLineRow.PoNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "PO date", FieldName = nameof(PoDeliveryPerformanceLineRow.PoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Item", FieldName = nameof(PoDeliveryPerformanceLineRow.ICode), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "PO qty", FieldName = nameof(PoDeliveryPerformanceLineRow.PoPurQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Received", FieldName = nameof(PoDeliveryPerformanceLineRow.NetReceivedQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Outstanding", FieldName = nameof(PoDeliveryPerformanceLineRow.BalanceQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Expected", FieldName = nameof(PoDeliveryPerformanceLineRow.EtaDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 8 },
        new() { Caption = "First GRN", FieldName = nameof(PoDeliveryPerformanceLineRow.FirstGrnDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Fully received", FieldName = nameof(PoDeliveryPerformanceLineRow.FullyReceivedDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "120px", VisibleIndex = 10 },
        new() { Caption = "Days late", FieldName = nameof(PoDeliveryPerformanceLineRow.DaysLate), DataType = "int", Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Delivery status", FieldName = nameof(PoDeliveryPerformanceLineRow.DeliveryStatus), Width = "140px", VisibleIndex = 12 },
        new() { Caption = "Buyer", FieldName = nameof(PoDeliveryPerformanceLineRow.Buyer), Width = "100px", VisibleIndex = 13 },
        new() { Caption = "Warehouse", FieldName = nameof(PoDeliveryPerformanceLineRow.ToWarehouse), Width = "110px", VisibleIndex = 14 }
    ];

    protected List<GridColumnData> SummaryColumns =>
    [
        new() { Caption = "Supplier", FieldName = nameof(PoDeliveryPerformanceRow.SuppCode), Width = "110px", VisibleIndex = 1 },
        new() { Caption = "Name", FieldName = nameof(PoDeliveryPerformanceRow.SuppName), VisibleIndex = 2 },
        new() { Caption = "Purchase orders", FieldName = nameof(PoDeliveryPerformanceRow.PurchaseOrderCount), DataType = "int", Width = "120px", VisibleIndex = 3 },
        new() { Caption = "PO lines", FieldName = nameof(PoDeliveryPerformanceRow.PoLinesEvaluated), DataType = "int", Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Completed", FieldName = nameof(PoDeliveryPerformanceRow.CompletedLines), DataType = "int", Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Partial", FieldName = nameof(PoDeliveryPerformanceRow.PartialLines), DataType = "int", Width = "90px", VisibleIndex = 6 },
        new() { Caption = "Ordered qty", FieldName = nameof(PoDeliveryPerformanceRow.OrderedQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Received qty", FieldName = nameof(PoDeliveryPerformanceRow.ReceivedQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 8 },
        new() { Caption = "On-time lines", FieldName = nameof(PoDeliveryPerformanceRow.OnTimeLines), DataType = "int", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Late lines", FieldName = nameof(PoDeliveryPerformanceRow.LateLines), DataType = "int", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "On-time %", FieldName = nameof(PoDeliveryPerformanceRow.OnTimePct), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Avg days late", FieldName = nameof(PoDeliveryPerformanceRow.AvgDaysLate), DataType = "decimal", DisplayFormat = "n2", Width = "110px", VisibleIndex = 12 },
        new() { Caption = "Max days late", FieldName = nameof(PoDeliveryPerformanceRow.MaxDaysLate), DataType = "int", Width = "110px", VisibleIndex = 13 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        WorkbenchPreset = PoInquiryWorkbenchPresets.AllOpen;
        LineDataSource = new PoInquiryGridDataSource<PoDeliveryPerformanceLineRow>(SearchLinesAsync, OnError);
        SummaryDataSource = new PoInquiryGridDataSource<PoDeliveryPerformanceRow>(SearchSummaryAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids()
    {
        LineDataSource.UpdateFilters(AppliedQuery);
        SummaryDataSource.UpdateFilters(AppliedQuery);
    }

    protected override async Task ReloadGridsAsync()
    {
        await base.ReloadGridsAsync();
        // Summary grid is a second instance — force reload via filter update already applied.
    }

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

    private Task OnPresetClick(string preset) =>
        TogglePresetAsync(preset, PoInquiryWorkbenchPresets.AllOpen);

    private Task<IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceLineRow>>> SearchLinesAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetDeliveryPerformanceLinesAsync(MenuCodes.PurchaseDeliveryPerformance, query, cancellationToken);

    private Task<IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceRow>>> SearchSummaryAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetDeliveryPerformanceSummaryAsync(MenuCodes.PurchaseDeliveryPerformance, query, cancellationToken);

    protected async Task OnLineButtonClick(SelectedButtonInfo<PoDeliveryPerformanceLineRow> info)
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

    protected async Task OnLineActionClick(SelectedButtonInfo<PoDeliveryPerformanceLineRow> info)
    {
        SelectedLine = info.SelectedRow;
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "OPEN":
                OpenSelectedPo();
                break;
            case "RECEIPTS":
                await OpenReceiptsAsync();
                break;
        }
    }

    protected void OpenSelectedPo()
    {
        if (SelectedLine is null
            || !PoInquiryNavigation.TryResolvePo(SelectedLine.PoNo, SelectedLine.PoRelNo, out var url))
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        OpenDocument(url);
    }

    protected async Task OpenReceiptsAsync()
    {
        if (SelectedLine is null)
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        if (SelectedLine.SingleBatchNo is int single
            && SelectedLine.GrBatchCount == 1
            && PoInquiryNavigation.TryResolveGr(single, out var grUrl))
        {
            OpenDocument(grUrl);
            return;
        }

        ReceiptPopupTitle = $"Receipts — {SelectedLine.PoNo} / {SelectedLine.PoRelNo} line {SelectedLine.Line}";
        ReceiptPopupVisible = true;
        ReceiptLoading = true;
        ReceiptRows = [];
        try
        {
            var result = await Inquiry.GetDeliveryPerformanceReceiptsAsync(
                MenuCodes.PurchaseDeliveryPerformance,
                SelectedLine.PoNo,
                SelectedLine.PoRelNo,
                SelectedLine.Line,
                Dates.Now.Date);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            ReceiptRows = result.Data ?? [];
        }
        finally
        {
            ReceiptLoading = false;
        }
    }

    protected void OpenSelectedGr(int batchNo)
    {
        if (!PoInquiryNavigation.TryResolveGr(batchNo, out var url))
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        OpenDocument(url);
    }

    protected async Task OnSummaryButtonClick(SelectedButtonInfo<PoDeliveryPerformanceRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(
                    BuildExportUrl("/purchase/inquiry/delivery-performance/summary/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnSummaryActionClick(SelectedButtonInfo<PoDeliveryPerformanceRow> info)
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
