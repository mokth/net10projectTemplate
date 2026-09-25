using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>e-Invoice status / reconciliation — ERP IRBM* columns vs the latest submission row.</summary>
public partial class SaEInvoiceInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaEInvoiceStatusRow> StatusDataSource { get; private set; } = default!;
    protected SaInquiryGridDataSource<SaEInvoiceReconciliationRow> ReconciliationDataSource { get; private set; } = default!;
    protected DxGrid? StatusGrid;
    protected DxGrid? ReconciliationGrid;

    protected void OnStatusGridInstance(DxGrid grid) => StatusGrid = grid;
    protected void OnReconciliationGridInstance(DxGrid grid) => ReconciliationGrid = grid;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> StatusColumns =>
    [
        new() { Caption = "Type", FieldName = nameof(SaEInvoiceStatusRow.DocType), Width = "70px", VisibleIndex = 1 },
        new() { Caption = "Doc no.", FieldName = nameof(SaEInvoiceStatusRow.DocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(SaEInvoiceStatusRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 3 },
        new() { Caption = "Customer", FieldName = nameof(SaEInvoiceStatusRow.CustCode), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Name", FieldName = nameof(SaEInvoiceStatusRow.CustName), VisibleIndex = 5 },
        new() { Caption = "Status", FieldName = nameof(SaEInvoiceStatusRow.StatusLabel), Width = "160px", VisibleIndex = 6 },
        new() { Caption = "IRBM status", FieldName = nameof(SaEInvoiceStatusRow.IrbmStatus), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "UUID", FieldName = nameof(SaEInvoiceStatusRow.IrbmUuid), Width = "150px", VisibleIndex = 8 },
        new() { Caption = "Submission ID", FieldName = nameof(SaEInvoiceStatusRow.IrbmSubmitId), Width = "150px", VisibleIndex = 9 }
    ];

    protected List<GridColumnData> ReconciliationColumns =>
    [
        new() { Caption = "Type", FieldName = nameof(SaEInvoiceReconciliationRow.DocType), Width = "70px", VisibleIndex = 1 },
        new() { Caption = "Doc no.", FieldName = nameof(SaEInvoiceReconciliationRow.DocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "ERP status", FieldName = nameof(SaEInvoiceReconciliationRow.ErpStatus), Width = "120px", VisibleIndex = 3 },
        new() { Caption = "ERP UUID", FieldName = nameof(SaEInvoiceReconciliationRow.ErpUuid), Width = "150px", VisibleIndex = 4 },
        new() { Caption = "Registry status", FieldName = nameof(SaEInvoiceReconciliationRow.RegistryStatus), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Finding", FieldName = nameof(SaEInvoiceReconciliationRow.Finding), VisibleIndex = 6 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        StatusDataSource = new SaInquiryGridDataSource<SaEInvoiceStatusRow>(SearchStatusAsync, OnError);
        ReconciliationDataSource = new SaInquiryGridDataSource<SaEInvoiceReconciliationRow>(SearchReconciliationAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    /// <summary>Both tabs read the same applied filters, so both sources are updated together.</summary>
    protected override void ApplyFiltersToGrids()
    {
        StatusDataSource.UpdateFilters(AppliedQuery);
        ReconciliationDataSource.UpdateFilters(AppliedQuery);
    }

    /// <summary>Two grids, so the base's single <c>Grid</c> field is not used.</summary>
    protected override Task ReloadGridsAsync()
    {
        StatusGrid?.Reload();
        ReconciliationGrid?.Reload();
        return Task.CompletedTask;
    }

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaEInvoiceStatusRow>>> SearchStatusAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetEInvoiceStatusAsync(MenuCodes.SalesEInvoiceInquiry, query, cancellationToken);

    private Task<IvMasterOperationResult<SaInquiryPage<SaEInvoiceReconciliationRow>>> SearchReconciliationAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetEInvoiceReconciliationAsync(MenuCodes.SalesEInvoiceInquiry, query, cancellationToken);

    protected async Task OnStatusButtonClick(SelectedButtonInfo<SaEInvoiceStatusRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/einvoice/export", []), forceLoad: true);
                break;
        }
    }

    protected async Task OnReconciliationButtonClick(SelectedButtonInfo<SaEInvoiceReconciliationRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/einvoice-reconciliation/export", []), forceLoad: true);
                break;
        }
    }
}
