using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>PO-line delivery performance workbench with posted GR chronology and supplier summary.</summary>
public partial class PoDeliveryPerformanceInquiry : PoInquiryPageBase
{
    [Inject] private IPoDeliveryFollowUpService FollowUpService { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;

    protected PoInquiryGridDataSource<PoDeliveryPerformanceLineRow> LineDataSource { get; private set; } = default!;
    protected PoInquiryGridDataSource<PoDeliveryPerformanceRow> SummaryDataSource { get; private set; } = default!;
    private DxGrid? SummaryGrid { get; set; }
    protected PoDeliveryPerformanceLineRow? SelectedLine { get; private set; }

    protected bool ReceiptPopupVisible { get; set; }
    protected bool ReceiptLoading { get; set; }
    protected string ReceiptPopupTitle { get; set; } = "Receipts";
    protected IReadOnlyList<PoDeliveryPerformanceReceiptRow> ReceiptRows { get; set; } = [];
    protected bool FollowUpFeatureEnabled { get; private set; }
    protected bool CanReadFollowUp { get; private set; }
    protected bool CanAddFollowUp { get; private set; }
    protected bool FollowUpPopupVisible { get; set; }
    protected bool FollowUpLoading { get; private set; }
    protected bool FollowUpSaving { get; private set; }
    protected IReadOnlyList<PoDeliveryFollowUpRow> FollowUpRows { get; private set; } = [];
    protected string FollowUpOutcome { get; set; } = PoDeliveryFollowUpOutcomes.Confirmed;
    protected DateTime? SupplierStatedDate { get; set; }
    protected DateTime? NextFollowUpDate { get; set; }
    protected string? FollowUpNote { get; set; }
    protected IReadOnlyList<string> FollowUpOutcomeOptions => PoDeliveryFollowUpOutcomes.All;

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

    protected IReadOnlyList<StatusFilterOption> DeliveryDateBasisOptions { get; } =
    [
        new(PoDeliveryDateBases.Eta, "ETA / due date"),
        new(PoDeliveryDateBases.PoDate, "PO date")
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons
    {
        get
        {
            var buttons = new List<ButtonInfo>
            {
                new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open purchase order" },
                new() { Text = "RECEIPTS", IConClass = "fa-solid fa-list", ToolTip = "Show posted GR/NG receipts" }
            };
            if (CanReadFollowUp)
            {
                buttons.Add(new() { Text = "FOLLOW UP", IConClass = "fa-solid fa-phone", ToolTip = "View or record supplier follow-up" });
            }

            return buttons;
        }
    }

    protected List<ButtonInfo> SummaryButtons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> SummaryActionButtons =>
    [
        new() { Text = "DUE LINES", IConClass = "fa-solid fa-list", ToolTip = "Show all due commitments in this KPI denominator" },
        new() { Text = "IN FULL", IConClass = "fa-solid fa-check", ToolTip = "Show due lines with no current balance" },
        new() { Text = "NOT IN FULL", IConClass = "fa-solid fa-minus", ToolTip = "Show due lines with a current balance" },
        new() { Text = "COMPLETED", IConClass = "fa-solid fa-flag-checkered", ToolTip = "Show lines with a posted full-receipt event" },
        new() { Text = "LATE LINES", IConClass = "fa-solid fa-clock", ToolTip = "Show lines fully received after ETA" },
        new() { Text = "OPEN OVERDUE", IConClass = "fa-solid fa-triangle-exclamation", ToolTip = "Show currently open lines past ETA" },
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
        new() { Caption = "OTIF result", FieldName = nameof(PoDeliveryPerformanceLineRow.OtifResult), Width = "105px", MinWidth = 95, VisibleIndex = 12 },
        new() { Caption = "Delivery status", FieldName = nameof(PoDeliveryPerformanceLineRow.DeliveryStatus), Width = "140px", MinWidth = 120, VisibleIndex = 13 },
        new() { Caption = "Buyer", FieldName = nameof(PoDeliveryPerformanceLineRow.Buyer), Width = "120px", MinWidth = 100, VisibleIndex = 14 },
        new() { Caption = "Warehouse", FieldName = nameof(PoDeliveryPerformanceLineRow.ToWarehouse), Size = GridColumnSize.Code, MinWidth = 135, VisibleIndex = 15 }
    ];

    protected List<GridColumnData> SummaryColumns =>
    [
        new() { Caption = "Supplier", FieldName = nameof(PoDeliveryPerformanceRow.SuppCode), Size = GridColumnSize.Code, MinWidth = 135, VisibleIndex = 1 },
        new() { Caption = "Name", FieldName = nameof(PoDeliveryPerformanceRow.SuppName), Size = GridColumnSize.Name, MinWidth = 190, VisibleIndex = 2 },
        new() { Caption = "POs", FieldName = nameof(PoDeliveryPerformanceRow.PurchaseOrderCount), DataType = "int", Width = "85px", MinWidth = 75, VisibleIndex = 3 },
        new() { Caption = "Due lines", FieldName = nameof(PoDeliveryPerformanceRow.DueLines), DataType = "int", Width = "95px", MinWidth = 80, VisibleIndex = 4 },
        new() { Caption = "OTIF lines", FieldName = nameof(PoDeliveryPerformanceRow.OtifLines), DataType = "int", Width = "100px", MinWidth = 85, VisibleIndex = 5 },
        new() { Caption = "OTIF %", FieldName = nameof(PoDeliveryPerformanceRow.OtifPct), DataType = "decimal", DisplayFormat = "n2", Width = "100px", MinWidth = 90, VisibleIndex = 6 },
        new() { Caption = "In-full lines", FieldName = nameof(PoDeliveryPerformanceRow.InFullLines), DataType = "int", Width = "105px", MinWidth = 90, VisibleIndex = 7 },
        new() { Caption = "In-full %", FieldName = nameof(PoDeliveryPerformanceRow.InFullPct), DataType = "decimal", DisplayFormat = "n2", Width = "100px", MinWidth = 90, VisibleIndex = 8 },
        new() { Caption = "Completed", FieldName = nameof(PoDeliveryPerformanceRow.CompletedLines), DataType = "int", Width = "100px", MinWidth = 85, VisibleIndex = 9 },
        new() { Caption = "Completed on-time %", FieldName = nameof(PoDeliveryPerformanceRow.CompletedOnTimePct), DataType = "decimal", DisplayFormat = "n2", Width = "155px", MinWidth = 140, VisibleIndex = 10 },
        new() { Caption = "Open overdue", FieldName = nameof(PoDeliveryPerformanceRow.OpenOverdueLines), DataType = "int", Width = "120px", MinWidth = 105, VisibleIndex = 11 },
        new() { Caption = "Avg completed days late", FieldName = nameof(PoDeliveryPerformanceRow.AvgCompletedDaysLate), DataType = "decimal", DisplayFormat = "n2", Width = "170px", MinWidth = 150, VisibleIndex = 12 },
        new() { Caption = "Max days late", FieldName = nameof(PoDeliveryPerformanceRow.MaxCompletedDaysLate), DataType = "int", Width = "115px", MinWidth = 100, VisibleIndex = 13 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        FollowUpFeatureEnabled = Configuration.GetValue<bool>("Features:SupplierDeliveryFollowUp");
        CanReadFollowUp = FollowUpFeatureEnabled
                          && await AccessRights.CanAsync(MenuCodes.PurchaseDeliveryFollowUp, PermissionCodes.Access);
        CanAddFollowUp = CanReadFollowUp
                         && await AccessRights.CanAsync(MenuCodes.PurchaseDeliveryFollowUp, PermissionCodes.Add);
        DeliveryDateBasis = PoDeliveryDateBases.Eta;
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

    protected override void OpenFilterPopup()
    {
        base.OpenFilterPopup();
        DraftDeliveryDateBasis ??= PoDeliveryDateBases.Eta;
    }

    protected override async Task ReloadGridsAsync()
    {
        await base.ReloadGridsAsync();
        SummaryGrid?.Reload();
    }

    protected void OnSummaryGridInstance(DxGrid grid) => SummaryGrid = grid;

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
            case "FOLLOW UP":
                await OpenFollowUpAsync();
                break;
        }
    }

    protected async Task OpenFollowUpAsync()
    {
        if (!FollowUpFeatureEnabled || SelectedLine is null)
        {
            return;
        }

        FollowUpPopupVisible = true;
        SupplierStatedDate = null;
        NextFollowUpDate = null;
        FollowUpNote = null;
        FollowUpOutcome = PoDeliveryFollowUpOutcomes.Confirmed;
        await LoadFollowUpsAsync();
    }

    protected async Task SaveFollowUpAsync()
    {
        if (SelectedLine is null || FollowUpSaving || !CanAddFollowUp)
        {
            return;
        }

        FollowUpSaving = true;
        try
        {
            var result = await FollowUpService.AddAsync(new PoDeliveryFollowUpRequest
            {
                PoNo = SelectedLine.PoNo,
                PoRelNo = SelectedLine.PoRelNo,
                PoLineNo = SelectedLine.Line,
                Outcome = FollowUpOutcome,
                SupplierStatedDate = SupplierStatedDate is DateTime supplierDate
                    ? DateOnly.FromDateTime(supplierDate)
                    : null,
                NextFollowUpDate = NextFollowUpDate is DateTime nextDate
                    ? DateOnly.FromDateTime(nextDate)
                    : null,
                Note = FollowUpNote
            });
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            FollowUpNote = null;
            NextFollowUpDate = null;
            await LoadFollowUpsAsync();
        }
        finally
        {
            FollowUpSaving = false;
        }
    }

    private async Task LoadFollowUpsAsync()
    {
        if (SelectedLine is null)
        {
            FollowUpRows = [];
            return;
        }

        FollowUpLoading = true;
        try
        {
            var result = await FollowUpService.ListAsync(
                SelectedLine.PoNo, SelectedLine.PoRelNo, SelectedLine.Line);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                FollowUpRows = [];
                return;
            }

            FollowUpRows = result.Data ?? [];
        }
        finally
        {
            FollowUpLoading = false;
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

    protected async Task OnSummaryActionClick(SelectedButtonInfo<PoDeliveryPerformanceRow> info)
    {
        if (info.SelectedRow is null || string.IsNullOrWhiteSpace(info.SelectedRow.SuppCode))
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "SUPPLIER TRX")
        {
            var url = QueryHelpers.AddQueryString(
                "/purchase/inquiry/supplier",
                "suppCode",
                info.SelectedRow.SuppCode);
            OpenDocument(url);
            return;
        }

        var preset = mode switch
        {
            "OTIF PASS" => PoInquiryWorkbenchPresets.OtifPass,
            "OTIF FAIL" => PoInquiryWorkbenchPresets.OtifFail,
            "IN FULL" => PoInquiryWorkbenchPresets.InFull,
            "NOT IN FULL" => PoInquiryWorkbenchPresets.NotInFull,
            "COMPLETED" => PoInquiryWorkbenchPresets.Completed,
            "LATE LINES" => PoInquiryWorkbenchPresets.LateCompletion,
            "OPEN OVERDUE" => PoInquiryWorkbenchPresets.Overdue,
            _ => PoInquiryWorkbenchPresets.All
        };

        SuppCode = info.SelectedRow.SuppCode;
        DeliveryDateBasis = PoDeliveryDateBases.Eta;
        WorkbenchPreset = preset;
        Status = null;
        await LoadCommonLookupsAsync();
        await ReloadAsync();
    }
}
