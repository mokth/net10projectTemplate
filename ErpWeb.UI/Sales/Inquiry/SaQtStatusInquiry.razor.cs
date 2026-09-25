using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>Quotation status / expiry — live (IsCurrent) revisions with their validity window.</summary>
public partial class SaQtStatusInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaQtStatusRow> DataSource { get; private set; } = default!;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "QT no.", FieldName = nameof(SaQtStatusRow.QtNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(SaQtStatusRow.Rev), DataType = "int", Width = "70px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(SaQtStatusRow.QtDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 3 },
        new() { Caption = "Valid until", FieldName = nameof(SaQtStatusRow.ValidUntil), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Expired", FieldName = nameof(SaQtStatusRow.IsExpired), DataType = "bool", Width = "90px", VisibleIndex = 5 },
        new() { Caption = "Status", FieldName = nameof(SaQtStatusRow.Status), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Conversion", FieldName = nameof(SaQtStatusRow.ConversionStatus), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Customer", FieldName = nameof(SaQtStatusRow.CustCode), Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Name", FieldName = nameof(SaQtStatusRow.CustName), VisibleIndex = 9 },
        new() { Caption = "Sales rep", FieldName = nameof(SaQtStatusRow.SalesRep), Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Amount", FieldName = nameof(SaQtStatusRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 11 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaQtStatusRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaQtStatusRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetQtStatusAsync(MenuCodes.SalesQtStatus, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaQtStatusRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/qt-status/export", []), forceLoad: true);
                break;
        }
    }
}
