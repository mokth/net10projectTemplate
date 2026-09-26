using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>Purchase Order outstanding workbench — latest PO lines with exception presets and OPEN.</summary>
public partial class PoOrderOutstandingInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoOrderOutstandingRow> DataSource { get; private set; } = default!;
    protected PoOrderOutstandingSummary Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }
    protected PoOrderOutstandingRow? SelectedRow { get; private set; }

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(PoOrderStatuses.New, "NEW"),
        new(PoOrderStatuses.Open, "OPEN"),
        new(PoOrderStatuses.Received, "RECEIVED"),
        new(PoOrderStatuses.Closed, "CLOSED"),
        new(PoOrderStatuses.Pending, "PENDING"),
        new(PoOrderStatuses.Checked, "CHECKED")
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open purchase order" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "PO no.", FieldName = nameof(PoOrderOutstandingRow.PoNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(PoOrderOutstandingRow.PoRelNo), DataType = "int", Width = "70px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(PoOrderOutstandingRow.PoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(PoOrderOutstandingRow.Status), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Supplier", FieldName = nameof(PoOrderOutstandingRow.VendCode), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Name", FieldName = nameof(PoOrderOutstandingRow.VendName), VisibleIndex = 6 },
        new() { Caption = "Buyer", FieldName = nameof(PoOrderOutstandingRow.Buyer), Width = "100px", VisibleIndex = 7 },
        new() { Caption = "Line", FieldName = nameof(PoOrderOutstandingRow.Line), DataType = "int", Width = "70px", VisibleIndex = 8 },
        new() { Caption = "Item", FieldName = nameof(PoOrderOutstandingRow.ICode), Width = "120px", VisibleIndex = 9 },
        new() { Caption = "Description", FieldName = nameof(PoOrderOutstandingRow.IDesc), VisibleIndex = 10 },
        new() { Caption = "Ordered", FieldName = nameof(PoOrderOutstandingRow.PoPurQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Received", FieldName = nameof(PoOrderOutstandingRow.RecvQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 12 },
        new() { Caption = "Returned", FieldName = nameof(PoOrderOutstandingRow.ReturnQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 13 },
        new() { Caption = "Invoiced", FieldName = nameof(PoOrderOutstandingRow.InvoicedQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 14 },
        new() { Caption = "Invoiceable", FieldName = nameof(PoOrderOutstandingRow.InvoiceableQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 15 },
        new() { Caption = "Balance", FieldName = nameof(PoOrderOutstandingRow.BalanceQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 16 },
        new() { Caption = "Outstanding $", FieldName = nameof(PoOrderOutstandingRow.OutstandingValue), DataType = "decimal", DisplayFormat = "n2", Width = "120px", VisibleIndex = 17 },
        new() { Caption = "ETA", FieldName = nameof(PoOrderOutstandingRow.EtaDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 18 },
        new() { Caption = "Days overdue", FieldName = nameof(PoOrderOutstandingRow.DaysOverdue), DataType = "int", Width = "100px", VisibleIndex = 19 },
        new() { Caption = "Warehouse", FieldName = nameof(PoOrderOutstandingRow.ToWarehouse), Width = "110px", VisibleIndex = 20 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        WorkbenchPreset = PoInquiryWorkbenchPresets.AllOpen;
        DataSource = new PoInquiryGridDataSource<PoOrderOutstandingRow>(SearchPageAsync, OnError);
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
            // Summary omits WorkbenchPreset so chips stay the exception dashboard.
            var summaryQuery = BuildQuery();
            summaryQuery.WorkbenchPreset = null;
            var result = await Inquiry.GetPoOutstandingSummaryAsync(
                MenuCodes.PurchaseOrderOutstanding, summaryQuery);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                Summary = new PoOrderOutstandingSummary();
            }
            else
            {
                Summary = result.Data ?? new PoOrderOutstandingSummary();
            }
        }
        finally
        {
            SummaryLoading = false;
        }
    }

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<PoInquiryPage<PoOrderOutstandingRow>>> SearchPageAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetPoOutstandingAsync(MenuCodes.PurchaseOrderOutstanding, query, cancellationToken);

    protected Task OnPresetClick(string preset) =>
        TogglePresetAsync(preset, PoInquiryWorkbenchPresets.AllOpen);

    protected async Task OnButtonClick(SelectedButtonInfo<PoOrderOutstandingRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/po-outstanding/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoOrderOutstandingRow> info)
    {
        SelectedRow = info.SelectedRow;
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "OPEN" && info.SelectedRow is not null)
        {
            if (PoInquiryNavigation.TryResolvePo(info.SelectedRow.PoNo, info.SelectedRow.PoRelNo, out var url))
            {
                OpenDocument(url);
            }
            else
            {
                NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            }
        }

        return Task.CompletedTask;
    }

    protected void OnRowSelected(PoOrderOutstandingRow? row) => SelectedRow = row;

    protected void OpenSelected()
    {
        if (SelectedRow is null)
        {
            return;
        }

        if (PoInquiryNavigation.TryResolvePo(SelectedRow.PoNo, SelectedRow.PoRelNo, out var url))
        {
            OpenDocument(url);
        }
        else
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
        }
    }
}
