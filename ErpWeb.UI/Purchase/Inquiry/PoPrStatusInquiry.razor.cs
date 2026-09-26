using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>Purchase requisition status workbench — live PO consumption + unconverted preset.</summary>
public partial class PoPrStatusInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoPrStatusRow> DataSource { get; private set; } = default!;
    protected PoPrStatusSummary Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(PoPrStatuses.New, "NEW"),
        new(PoPrStatuses.Open, "OPEN"),
        new(PoPrStatuses.Approved, "APPROVED"),
        new(PoPrStatuses.PartiallyOrdered, "PARTIALLY ORDERED"),
        new(PoPrStatuses.FullyOrdered, "FULLY ORDERED"),
        new(PoPrStatuses.Cancelled, "CANCELLED")
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open requisition" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "PR no.", FieldName = nameof(PoPrStatusRow.PrNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(PoPrStatusRow.CreateDt), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 2 },
        new() { Caption = "Status", FieldName = nameof(PoPrStatusRow.HeaderStatus), Width = "130px", VisibleIndex = 3 },
        new() { Caption = "Requester", FieldName = nameof(PoPrStatusRow.Requester), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Dept", FieldName = nameof(PoPrStatusRow.DeptCode), Width = "90px", VisibleIndex = 5 },
        new() { Caption = "Line", FieldName = nameof(PoPrStatusRow.Line), DataType = "int", Width = "70px", VisibleIndex = 6 },
        new() { Caption = "Item", FieldName = nameof(PoPrStatusRow.ICode), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Description", FieldName = nameof(PoPrStatusRow.IDesc), VisibleIndex = 8 },
        new() { Caption = "Purchase qty", FieldName = nameof(PoPrStatusRow.PurchaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Consumed", FieldName = nameof(PoPrStatusRow.ConsumedQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Remaining", FieldName = nameof(PoPrStatusRow.RemainingQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Linked POs", FieldName = nameof(PoPrStatusRow.LinkedPoNos), Width = "140px", VisibleIndex = 12 },
        new() { Caption = "Vendor", FieldName = nameof(PoPrStatusRow.VendorCd), Width = "110px", VisibleIndex = 13 },
        new() { Caption = "Warehouse", FieldName = nameof(PoPrStatusRow.ToWarehouse), Width = "110px", VisibleIndex = 14 },
        new() { Caption = "ETA", FieldName = nameof(PoPrStatusRow.EtaDt), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 15 },
        new() { Caption = "Line status", FieldName = nameof(PoPrStatusRow.LineStatus), Width = "120px", VisibleIndex = 16 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        WorkbenchPreset = PoInquiryWorkbenchPresets.Unconverted;
        DataSource = new PoInquiryGridDataSource<PoPrStatusRow>(SearchPageAsync, OnError);
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
            var q = BuildQuery();
            q.WorkbenchPreset = null;
            var result = await Inquiry.GetPrStatusSummaryAsync(MenuCodes.PurchasePrStatus, q);
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

    private Task<IvMasterOperationResult<PoInquiryPage<PoPrStatusRow>>> SearchPageAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetPrStatusAsync(MenuCodes.PurchasePrStatus, query, cancellationToken);

    protected Task OnUnconvertedClick() =>
        TogglePresetAsync(PoInquiryWorkbenchPresets.Unconverted);

    protected async Task OnButtonClick(SelectedButtonInfo<PoPrStatusRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/pr-status/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoPrStatusRow> info)
    {
        if (!string.Equals(info.SelectedButton.Text, "OPEN", StringComparison.OrdinalIgnoreCase)
            || info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        if (PoInquiryNavigation.TryResolvePr(info.SelectedRow.PrNo, out var url))
        {
            OpenDocument(url);
        }
        else
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
        }

        return Task.CompletedTask;
    }
}
