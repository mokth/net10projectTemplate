using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>
/// Supplier Transactions &amp; History — OPEN by DocType; history tab shows period totals from the same net rules.
/// </summary>
public partial class PoSupplierInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoSupplierTransactionRow> DataSource { get; private set; } = default!;

    protected IReadOnlyList<PoSupplierPurchaseHistoryRow> HistoryRows { get; set; } = [];
    protected PoSupplierPurchaseHistoryTotals HistoryTotals { get; set; } = new();
    protected string? HistoryError;
    protected bool HistoryLoaded;

    protected List<GridColumnData> TransactionColumns =>
    [
        new() { Caption = "Type", FieldName = nameof(PoSupplierTransactionRow.DocType), Width = "70px", VisibleIndex = 1 },
        new() { Caption = "Doc no.", FieldName = nameof(PoSupplierTransactionRow.DocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(PoSupplierTransactionRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 3 },
        new() { Caption = "Supplier", FieldName = nameof(PoSupplierTransactionRow.SuppCode), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Name", FieldName = nameof(PoSupplierTransactionRow.SuppName), VisibleIndex = 5 },
        new() { Caption = "Buyer", FieldName = nameof(PoSupplierTransactionRow.Buyer), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Status", FieldName = nameof(PoSupplierTransactionRow.Status), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Amount", FieldName = nameof(PoSupplierTransactionRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 8 },
        new() { Caption = "PO rev", FieldName = nameof(PoSupplierTransactionRow.PoRelNo), DataType = "int", Width = "80px", VisibleIndex = 9 },
        new() { Caption = "Extra", FieldName = nameof(PoSupplierTransactionRow.Extra), VisibleIndex = 10 }
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open document" }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new PoInquiryGridDataSource<PoSupplierTransactionRow>(SearchTransactionsAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    protected override async Task ReloadAsync()
    {
        await base.ReloadAsync();
        await LoadHistoryAsync();
    }

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<PoInquiryPage<PoSupplierTransactionRow>>> SearchTransactionsAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetSupplierTransactionsAsync(MenuCodes.PurchaseSupplierTransaction, query, cancellationToken);

    private async Task LoadHistoryAsync()
    {
        HistoryError = null;
        var result = await Inquiry.GetSupplierPurchaseHistoryAsync(
            MenuCodes.PurchaseSupplierTransaction, AppliedQuery);
        if (!result.Succeeded)
        {
            HistoryError = result.Message ?? "Unable to load the purchase history.";
            HistoryRows = [];
            HistoryTotals = new();
        }
        else
        {
            HistoryRows = result.Data ?? [];
            var totals = await Inquiry.GetSupplierPurchaseHistoryTotalsAsync(
                MenuCodes.PurchaseSupplierTransaction, AppliedQuery);
            HistoryTotals = totals.Succeeded ? (totals.Data ?? new()) : new();
        }

        HistoryLoaded = true;
    }

    protected async Task OnButtonClick(SelectedButtonInfo<PoSupplierTransactionRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/supplier/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoSupplierTransactionRow> info)
    {
        if (!string.Equals(info.SelectedButton.Text, "OPEN", StringComparison.OrdinalIgnoreCase)
            || info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        TryOpenByDocType(info.SelectedRow.DocType, info.SelectedRow.DocNo, info.SelectedRow.PoRelNo);
        return Task.CompletedTask;
    }

    protected async Task OnExportHistoryClick()
    {
        Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/supplier-history/export", []), forceLoad: true);
        await Task.CompletedTask;
    }
}
