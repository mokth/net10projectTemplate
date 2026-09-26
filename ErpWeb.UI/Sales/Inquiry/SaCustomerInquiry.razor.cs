using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>
/// Customer Transactions &amp; History — one page, two tabs, one menu (SA_CUST_TRX). The transaction tab
/// is the movement log (all statuses); the history tab is POSTED invoices netted with POSTED CN/DN.
/// </summary>
public partial class SaCustomerInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaCustomerTransactionRow> DataSource { get; private set; } = default!;

    protected IReadOnlyList<SaCustomerSalesHistoryRow> HistoryRows { get; set; } = [];
    protected string? HistoryError;
    protected bool HistoryLoaded;

    protected List<GridColumnData> TransactionColumns =>
    [
        new() { Caption = "Type", FieldName = nameof(SaCustomerTransactionRow.DocType), Width = "70px", VisibleIndex = 1 },
        new() { Caption = "Doc no.", FieldName = nameof(SaCustomerTransactionRow.DocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(SaCustomerTransactionRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 3 },
        new() { Caption = "Customer", FieldName = nameof(SaCustomerTransactionRow.CustCode), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Name", FieldName = nameof(SaCustomerTransactionRow.CustName), VisibleIndex = 5 },
        new() { Caption = "Sales rep", FieldName = nameof(SaCustomerTransactionRow.SalesRep), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Status", FieldName = nameof(SaCustomerTransactionRow.Status), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Amount", FieldName = nameof(SaCustomerTransactionRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 8 }
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaCustomerTransactionRow>(SearchTransactionsAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    /// <summary>
    /// The history tab is driven by the same applied filters as the transaction grid, so it is reloaded
    /// on every reload — including the toolbar customer pick applying immediately.
    /// </summary>
    protected override async Task ReloadAsync()
    {
        await base.ReloadAsync();
        await LoadHistoryAsync();
    }

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaCustomerTransactionRow>>> SearchTransactionsAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetCustomerTransactionsAsync(MenuCodes.SalesCustomerTransaction, query, cancellationToken);

    private async Task LoadHistoryAsync()
    {
        HistoryError = null;
        var result = await Inquiry.GetCustomerSalesHistoryAsync(
            MenuCodes.SalesCustomerTransaction, AppliedQuery);
        if (!result.Succeeded)
        {
            HistoryError = result.Message ?? "Unable to load the sales history.";
            HistoryRows = [];
        }
        else
        {
            HistoryRows = result.Data ?? [];
        }

        HistoryLoaded = true;
    }

    protected async Task OnButtonClick(SelectedButtonInfo<SaCustomerTransactionRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/customer/export", []), forceLoad: true);
                break;
        }
    }

    /// <summary>
    /// Opens the row's document. The transaction log reports the document number without a revision,
    /// so a REVISABLE document (QT/SO) opens its latest revision — the row never claims otherwise.
    /// </summary>
    protected Task OnActionClick(SelectedButtonInfo<SaCustomerTransactionRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            TryOpenByDocType(row.DocType, row.DocNo);
        }

        return Task.CompletedTask;
    }

    protected async Task OnExportHistoryClick()
    {
        Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/customer-history/export", []), forceLoad: true);
        await Task.CompletedTask;
    }
}
