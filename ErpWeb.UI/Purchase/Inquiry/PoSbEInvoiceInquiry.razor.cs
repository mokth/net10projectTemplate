using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>Self-billed e-Invoice workbench — status chips/presets from existing decoration + OPEN.</summary>
public partial class PoSbEInvoiceInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoSbEInvoiceStatusRow> StatusDataSource { get; private set; } = default!;
    protected PoInquiryGridDataSource<PoSbEInvoiceReconciliationRow> ReconciliationDataSource { get; private set; } = default!;
    protected DxGrid? StatusGrid;
    protected DxGrid? ReconciliationGrid;
    protected PoSbEInvoiceSummary Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }

    protected void OnStatusGridInstance(DxGrid grid) => StatusGrid = grid;
    protected void OnReconciliationGridInstance(DxGrid grid) => ReconciliationGrid = grid;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> StatusActionButtons =>
    [
        new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open self-billed document" }
    ];

    protected List<GridColumnData> StatusColumns =>
    [
        new() { Caption = "Type", FieldName = nameof(PoSbEInvoiceStatusRow.DocType), Width = "70px", VisibleIndex = 1 },
        new() { Caption = "Doc no.", FieldName = nameof(PoSbEInvoiceStatusRow.DocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(PoSbEInvoiceStatusRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 3 },
        new() { Caption = "Supplier", FieldName = nameof(PoSbEInvoiceStatusRow.VendorCode), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Name", FieldName = nameof(PoSbEInvoiceStatusRow.VendorName), VisibleIndex = 5 },
        new() { Caption = "Status", FieldName = nameof(PoSbEInvoiceStatusRow.StatusLabel), Width = "160px", VisibleIndex = 6 },
        new() { Caption = "IRBM status", FieldName = nameof(PoSbEInvoiceStatusRow.IrbmStatus), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "UUID", FieldName = nameof(PoSbEInvoiceStatusRow.IrbmUuid), Width = "150px", VisibleIndex = 8 },
        new() { Caption = "Submission ID", FieldName = nameof(PoSbEInvoiceStatusRow.IrbmSubmitId), Width = "150px", VisibleIndex = 9 }
    ];

    protected List<GridColumnData> ReconciliationColumns =>
    [
        new() { Caption = "Type", FieldName = nameof(PoSbEInvoiceReconciliationRow.DocType), Width = "70px", VisibleIndex = 1 },
        new() { Caption = "Doc no.", FieldName = nameof(PoSbEInvoiceReconciliationRow.DocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "ERP status", FieldName = nameof(PoSbEInvoiceReconciliationRow.ErpStatus), Width = "120px", VisibleIndex = 3 },
        new() { Caption = "ERP UUID", FieldName = nameof(PoSbEInvoiceReconciliationRow.ErpUuid), Width = "150px", VisibleIndex = 4 },
        new() { Caption = "Registry status", FieldName = nameof(PoSbEInvoiceReconciliationRow.RegistryStatus), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Finding", FieldName = nameof(PoSbEInvoiceReconciliationRow.Finding), VisibleIndex = 6 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        StatusDataSource = new PoInquiryGridDataSource<PoSbEInvoiceStatusRow>(SearchStatusAsync, OnError);
        ReconciliationDataSource = new PoInquiryGridDataSource<PoSbEInvoiceReconciliationRow>(SearchReconciliationAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids()
    {
        StatusDataSource.UpdateFilters(AppliedQuery);
        ReconciliationDataSource.UpdateFilters(AppliedQuery);
    }

    protected override async Task ReloadAsync()
    {
        await base.ReloadAsync();
        await LoadSummaryAsync();
    }

    protected override Task ReloadGridsAsync()
    {
        StatusGrid?.Reload();
        ReconciliationGrid?.Reload();
        return Task.CompletedTask;
    }

    private async Task LoadSummaryAsync()
    {
        SummaryLoading = true;
        try
        {
            var q = BuildQuery();
            q.WorkbenchPreset = null;
            var result = await Inquiry.GetSbEInvoiceSummaryAsync(MenuCodes.PurchaseSbEInvoiceInquiry, q);
            Summary = result.Succeeded ? (result.Data ?? new()) : new();
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

    private Task<IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceStatusRow>>> SearchStatusAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetSbEInvoiceStatusAsync(MenuCodes.PurchaseSbEInvoiceInquiry, query, cancellationToken);

    private Task<IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceReconciliationRow>>> SearchReconciliationAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetSbEInvoiceReconciliationAsync(MenuCodes.PurchaseSbEInvoiceInquiry, query, cancellationToken);

    protected Task OnNotSubmittedClick() =>
        TogglePresetAsync(PoInquiryWorkbenchPresets.SbNotSubmitted);

    protected Task OnInvalidClick() =>
        TogglePresetAsync(PoInquiryWorkbenchPresets.SbInvalid);

    protected async Task OnStatusButtonClick(SelectedButtonInfo<PoSbEInvoiceStatusRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/einvoice/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnStatusActionClick(SelectedButtonInfo<PoSbEInvoiceStatusRow> info)
    {
        if (!string.Equals(info.SelectedButton.Text, "OPEN", StringComparison.OrdinalIgnoreCase)
            || info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        if (PoInquiryNavigation.TryResolveSelfBilled(info.SelectedRow.DocType, info.SelectedRow.DocNo, out var url))
        {
            OpenDocument(url);
        }
        else
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
        }

        return Task.CompletedTask;
    }

    protected async Task OnReconciliationButtonClick(SelectedButtonInfo<PoSbEInvoiceReconciliationRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/einvoice-reconciliation/export", []), forceLoad: true);
                break;
        }
    }
}
