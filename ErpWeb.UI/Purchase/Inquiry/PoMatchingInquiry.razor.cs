using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>PO-line matching workbench — default preset RECEIVED_NOT_INVOICED.</summary>
public partial class PoMatchingInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoMatchingRow> DataSource { get; private set; } = default!;
    protected PoMatchingSummary Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN PO", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open purchase order" },
        new() { Text = "OPEN INV", IConClass = "fa-solid fa-file-invoice", ToolTip = "Open latest posted invoice" },
        new() { Text = "OPEN GR", IConClass = "fa-solid fa-truck-ramp-box", ToolTip = "Open goods receipt (single GR only)" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "PO no.", FieldName = nameof(PoMatchingRow.PoNo), Width = "120px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(PoMatchingRow.PoRelNo), DataType = "int", Width = "60px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(PoMatchingRow.PoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "100px", VisibleIndex = 3 },
        new() { Caption = "Supplier", FieldName = nameof(PoMatchingRow.VendCode), Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Item", FieldName = nameof(PoMatchingRow.ICode), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Ordered", FieldName = nameof(PoMatchingRow.PoPurQty), DataType = "decimal", DisplayFormat = "n4", Width = "90px", VisibleIndex = 6 },
        new() { Caption = "Received", FieldName = nameof(PoMatchingRow.NetReceivedQty), DataType = "decimal", DisplayFormat = "n4", Width = "90px", VisibleIndex = 7 },
        new() { Caption = "Invoiced", FieldName = nameof(PoMatchingRow.InvoicedQty), DataType = "decimal", DisplayFormat = "n4", Width = "90px", VisibleIndex = 8 },
        new() { Caption = "Balance", FieldName = nameof(PoMatchingRow.BalanceQty), DataType = "decimal", DisplayFormat = "n4", Width = "90px", VisibleIndex = 9 },
        new() { Caption = "PO $", FieldName = nameof(PoMatchingRow.PoAmount), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Received $", FieldName = nameof(PoMatchingRow.ReceivedAmount), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Invoice $", FieldName = nameof(PoMatchingRow.InvoiceAmount), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 12 },
        new() { Caption = "Qty var", FieldName = nameof(PoMatchingRow.QtyVariance), DataType = "decimal", DisplayFormat = "n4", Width = "90px", VisibleIndex = 13 },
        new() { Caption = "Status", FieldName = nameof(PoMatchingRow.MatchingStatus), Width = "160px", VisibleIndex = 14 },
        new() { Caption = "Price≠", FieldName = nameof(PoMatchingRow.PriceMismatch), Width = "70px", VisibleIndex = 15 },
        new() { Caption = "Multi INV", FieldName = nameof(PoMatchingRow.HasMultipleInvoices), Width = "80px", VisibleIndex = 16 },
        new() { Caption = "Multi GR", FieldName = nameof(PoMatchingRow.HasMultipleGRs), Width = "80px", VisibleIndex = 17 },
        new() { Caption = "Warehouse", FieldName = nameof(PoMatchingRow.ToWarehouse), Width = "100px", VisibleIndex = 18 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        WorkbenchPreset = PoInquiryWorkbenchPresets.ReceivedNotInvoiced;
        DataSource = new PoInquiryGridDataSource<PoMatchingRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    protected override async Task ReloadAsync()
    {
        await base.ReloadAsync();
        await LoadSummaryAsync();
    }

    private async Task LoadSummaryAsync()
    {
        SummaryLoading = true;
        try
        {
            var summaryQuery = BuildQuery();
            summaryQuery.WorkbenchPreset = null;
            var result = await Inquiry.GetPoMatchingSummaryAsync(MenuCodes.PurchaseMatching, summaryQuery);
            Summary = result.Succeeded ? result.Data ?? new PoMatchingSummary() : new PoMatchingSummary();
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
            }
        }
        finally
        {
            SummaryLoading = false;
        }
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

    private Task<IvMasterOperationResult<PoInquiryPage<PoMatchingRow>>> SearchPageAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetPoMatchingAsync(MenuCodes.PurchaseMatching, query, cancellationToken);

    protected Task OnPresetClick(string preset) =>
        TogglePresetAsync(preset, PoInquiryWorkbenchPresets.ReceivedNotInvoiced);

    protected async Task OnButtonClick(SelectedButtonInfo<PoMatchingRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/matching/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoMatchingRow> info)
    {
        if (info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        var row = info.SelectedRow;

        if (mode == "OPEN PO")
        {
            if (PoInquiryNavigation.TryResolvePo(row.PoNo, row.PoRelNo, out var url))
            {
                OpenDocument(url);
            }
            else
            {
                NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            }
        }
        else if (mode == "OPEN INV")
        {
            if (PoInquiryNavigation.TryResolveInvoice(row.LatestInvoiceDocNo, out var url))
            {
                OpenDocument(url);
            }
            else
            {
                NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            }
        }
        else if (mode == "OPEN GR")
        {
            if (row.HasMultipleGRs || !PoInquiryNavigation.TryResolveGr(row.SingleGrBatchNo, out var url))
            {
                NavMessage = row.HasMultipleGRs
                    ? "Multiple GRs — use Document Relationships"
                    : PoInquiryNavigation.DocumentUnavailableMessage;
            }
            else
            {
                OpenDocument(url);
            }
        }

        return Task.CompletedTask;
    }
}
