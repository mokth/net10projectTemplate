using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>Delivery Order status — all DO lines with billing state and SO / invoice references.</summary>
public partial class SaDoStatusInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaDoStatusRow> DataSource { get; private set; } = default!;

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(SaDoStatuses.New, "NEW"),
        new(SaDoStatuses.Posted, "POSTED"),
        new(SaDoStatuses.Closed, "CLOSED")
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "DO no.", FieldName = nameof(SaDoStatusRow.DoNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(SaDoStatusRow.DoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 2 },
        new() { Caption = "Status", FieldName = nameof(SaDoStatusRow.Status), Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Billing", FieldName = nameof(SaDoStatusRow.BillingStatus), Width = "90px", VisibleIndex = 4 },
        new() { Caption = "Customer", FieldName = nameof(SaDoStatusRow.CustCode), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Name", FieldName = nameof(SaDoStatusRow.CustName), VisibleIndex = 6 },
        new() { Caption = "Sales rep", FieldName = nameof(SaDoStatusRow.SalesRep), Width = "100px", VisibleIndex = 7 },
        new() { Caption = "Amount", FieldName = nameof(SaDoStatusRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 8 },
        new() { Caption = "Line", FieldName = nameof(SaDoStatusRow.Line), DataType = "int", Width = "70px", VisibleIndex = 9 },
        new() { Caption = "Item", FieldName = nameof(SaDoStatusRow.ICode), Width = "120px", VisibleIndex = 10 },
        new() { Caption = "Description", FieldName = nameof(SaDoStatusRow.IDesc), VisibleIndex = 11 },
        new() { Caption = "Qty", FieldName = nameof(SaDoStatusRow.Qty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 12 },
        new() { Caption = "SO no.", FieldName = nameof(SaDoStatusRow.SoNo), Width = "120px", VisibleIndex = 13 },
        new() { Caption = "Invoice no.", FieldName = nameof(SaDoStatusRow.InvNo), Width = "120px", VisibleIndex = 14 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaDoStatusRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaDoStatusRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetDoStatusAsync(MenuCodes.SalesDoStatus, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaDoStatusRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/do-status/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaDoStatusRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            TryOpenByDocType("DO", row.DoNo);
        }

        return Task.CompletedTask;
    }
}
