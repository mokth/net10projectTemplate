using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.UI.Purchase.Inquiry;

public partial class PoProcurementWorkbench : PoInquiryPageBase
{
    [Inject] private IConfiguration Configuration { get; set; } = default!;

    protected bool IsFeatureEnabled { get; private set; }
    protected bool SummaryLoading { get; private set; }
    protected DateTime AsOfDate { get; private set; }
    protected DateTime? LastRefreshed { get; private set; }
    protected string? DraftSupplier { get; set; }
    protected string? DraftBuyer { get; set; }
    protected string? SelectedMetric { get; private set; }
    protected string? GridErrorMessage { get; private set; }
    protected int? SelectedCount => Cards.FirstOrDefault(x => x.Key == SelectedMetric)?.Count;

    protected PoInquiryGridDataSource<PoPrStatusRow> PrDataSource { get; private set; } = default!;
    protected PoInquiryGridDataSource<PoOrderOutstandingRow> OverdueDataSource { get; private set; } = default!;
    protected PoInquiryGridDataSource<PoOrderOutstandingRow> DueSoonDataSource { get; private set; } = default!;
    protected PoInquiryGridDataSource<PoMatchingRow> RniDataSource { get; private set; } = default!;
    private DxGrid? _workbenchGrid;

    protected IReadOnlyList<WorkbenchCard> Cards { get; private set; } =
    [
        new("pr", "Open PR Lines", "Purchase quantity remaining after non-cancelled PO consumption."),
        new("overdue", "Overdue PO Lines", "Latest live PO revision, positive balance, and current ETA before as-of date."),
        new("due", "PO Due Next 7 Days", "Positive-balance lines with ETA from today through day seven, inclusive."),
        new("rni", "Received Not Invoiced", "Existing PO matching classification; inquiry count only, not AP liability.")
    ];

    protected List<ButtonInfo> GridButtons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> OpenButtons =>
    [new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open source document" }];

    protected List<GridColumnData> PrColumns =>
    [
        new() { Caption = "PR no.", FieldName = nameof(PoPrStatusRow.PrNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(PoPrStatusRow.CreateDt), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 2 },
        new() { Caption = "Status", FieldName = nameof(PoPrStatusRow.HeaderStatus), Width = "100px", VisibleIndex = 3 },
        new() { Caption = "Line", FieldName = nameof(PoPrStatusRow.Line), DataType = "int", Width = "70px", VisibleIndex = 4 },
        new() { Caption = "Item", FieldName = nameof(PoPrStatusRow.ICode), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Description", FieldName = nameof(PoPrStatusRow.IDesc), VisibleIndex = 6 },
        new() { Caption = "Purchase qty", FieldName = nameof(PoPrStatusRow.PurchaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Consumed", FieldName = nameof(PoPrStatusRow.ConsumedQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Remaining", FieldName = nameof(PoPrStatusRow.RemainingQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Supplier", FieldName = nameof(PoPrStatusRow.VendorCd), Width = "110px", VisibleIndex = 10 },
        new() { Caption = "Warehouse", FieldName = nameof(PoPrStatusRow.ToWarehouse), Width = "110px", VisibleIndex = 11 }
    ];

    protected List<GridColumnData> PoColumns =>
    [
        new() { Caption = "PO no.", FieldName = nameof(PoOrderOutstandingRow.PoNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(PoOrderOutstandingRow.PoRelNo), DataType = "int", Width = "70px", VisibleIndex = 2 },
        new() { Caption = "Supplier", FieldName = nameof(PoOrderOutstandingRow.VendCode), Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Name", FieldName = nameof(PoOrderOutstandingRow.VendName), VisibleIndex = 4 },
        new() { Caption = "Buyer", FieldName = nameof(PoOrderOutstandingRow.Buyer), Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Line", FieldName = nameof(PoOrderOutstandingRow.Line), DataType = "int", Width = "70px", VisibleIndex = 6 },
        new() { Caption = "Item", FieldName = nameof(PoOrderOutstandingRow.ICode), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Description", FieldName = nameof(PoOrderOutstandingRow.IDesc), VisibleIndex = 8 },
        new() { Caption = "Balance", FieldName = nameof(PoOrderOutstandingRow.BalanceQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "ETA · current revision", FieldName = nameof(PoOrderOutstandingRow.EtaDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "150px", VisibleIndex = 10 },
        new() { Caption = "Days overdue", FieldName = nameof(PoOrderOutstandingRow.DaysOverdue), DataType = "int", Width = "110px", VisibleIndex = 11 },
        new() { Caption = "Warehouse", FieldName = nameof(PoOrderOutstandingRow.ToWarehouse), Width = "110px", VisibleIndex = 12 }
    ];

    protected List<GridColumnData> MatchingColumns =>
    [
        new() { Caption = "PO no.", FieldName = nameof(PoMatchingRow.PoNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(PoMatchingRow.PoRelNo), DataType = "int", Width = "70px", VisibleIndex = 2 },
        new() { Caption = "Supplier", FieldName = nameof(PoMatchingRow.VendCode), Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Buyer", FieldName = nameof(PoMatchingRow.Buyer), Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Line", FieldName = nameof(PoMatchingRow.Line), DataType = "int", Width = "70px", VisibleIndex = 5 },
        new() { Caption = "Item", FieldName = nameof(PoMatchingRow.ICode), Width = "120px", VisibleIndex = 6 },
        new() { Caption = "Description", FieldName = nameof(PoMatchingRow.IDesc), VisibleIndex = 7 },
        new() { Caption = "Received", FieldName = nameof(PoMatchingRow.NetReceivedQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Invoiceable", FieldName = nameof(PoMatchingRow.InvoiceableQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "ETA · current revision", FieldName = nameof(PoMatchingRow.EtaDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "150px", VisibleIndex = 10 },
        new() { Caption = "Warehouse", FieldName = nameof(PoMatchingRow.ToWarehouse), Width = "110px", VisibleIndex = 11 }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        IsFeatureEnabled = Configuration.GetValue<bool>("Features:ProcurementWorkbench");
        if (!IsFeatureEnabled)
        {
            return;
        }

        AsOfDate = Dates.Today;
        PrDataSource = new(SearchPrAsync, OnGridError);
        OverdueDataSource = new(SearchOverdueAsync, OnGridError);
        DueSoonDataSource = new(SearchDueSoonAsync, OnGridError);
        RniDataSource = new(SearchRniAsync, OnGridError);
        UpdateGridFilters();
        await LoadSummaryAsync();
    }

    protected async Task ApplyWorkbenchFiltersAsync()
    {
        UpdateGridFilters();
        await LoadSummaryAsync();
        _workbenchGrid?.Reload();
    }

    protected async Task RefreshAsync()
    {
        AsOfDate = Dates.Today;
        UpdateGridFilters();
        await LoadSummaryAsync();
        _workbenchGrid?.Reload();
    }

    protected async Task SelectMetricAsync(string key)
    {
        SelectedMetric = key;
        UpdateGridFilters();
        await InvokeAsync(StateHasChanged);
        _workbenchGrid?.Reload();
    }

    protected void OnWorkbenchGridInstance(DxGrid grid) => _workbenchGrid = grid;

    protected async Task OnPrButtonClick(SelectedButtonInfo<PoPrStatusRow> info) =>
        await HandleToolbarClickAsync(info.SelectedButton.Text, "pr");

    protected async Task OnPoButtonClick(SelectedButtonInfo<PoOrderOutstandingRow> info) =>
        await HandleToolbarClickAsync(info.SelectedButton.Text, "overdue");

    protected async Task OnDueButtonClick(SelectedButtonInfo<PoOrderOutstandingRow> info) =>
        await HandleToolbarClickAsync(info.SelectedButton.Text, "due");

    protected async Task OnRniButtonClick(SelectedButtonInfo<PoMatchingRow> info) =>
        await HandleToolbarClickAsync(info.SelectedButton.Text, "rni");

    protected Task OnPrActionClick(SelectedButtonInfo<PoPrStatusRow> info)
    {
        if (info.SelectedRow is not null
            && PoInquiryNavigation.TryResolvePr(info.SelectedRow.PrNo, out var url))
        {
            Navigation.NavigateTo(url);
        }
        else
        {
            ErrorMessage = PoInquiryNavigation.DocumentUnavailableMessage;
        }

        return Task.CompletedTask;
    }

    protected Task OnPoActionClick(SelectedButtonInfo<PoOrderOutstandingRow> info) =>
        OpenPoAsync(info.SelectedRow?.PoNo, info.SelectedRow?.PoRelNo);

    protected Task OnRniActionClick(SelectedButtonInfo<PoMatchingRow> info) =>
        OpenPoAsync(info.SelectedRow?.PoNo, info.SelectedRow?.PoRelNo);

    private Task OpenPoAsync(string? poNo, short? relNo)
    {
        if (PoInquiryNavigation.TryResolvePo(poNo, relNo, out var url))
        {
            Navigation.NavigateTo(url);
        }
        else
        {
            ErrorMessage = PoInquiryNavigation.DocumentUnavailableMessage;
        }

        return Task.CompletedTask;
    }

    private async Task HandleToolbarClickAsync(string? text, string metric)
    {
        switch ((text ?? string.Empty).ToUpperInvariant())
        {
            case "REFRESH":
                await RefreshAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl(metric), forceLoad: true);
                break;
        }
    }

    private async Task LoadSummaryAsync()
    {
        SummaryLoading = true;
        try
        {
            var q = BuildQuery();
            var pr = await Inquiry.GetPrStatusSummaryAsync(MenuCodes.PurchaseWorkbench, Clone(q));
            var po = await Inquiry.GetPoOutstandingSummaryAsync(MenuCodes.PurchaseWorkbench, Clone(q));
            var due = await Inquiry.GetPoDueSoonSummaryAsync(MenuCodes.PurchaseWorkbench, Clone(q));
            var rni = await Inquiry.GetPoMatchingSummaryAsync(MenuCodes.PurchaseWorkbench, Clone(q));

            Cards =
            [
                ToCard("pr", "Open PR Lines", "Purchase quantity remaining after non-cancelled PO consumption.", pr.Succeeded ? pr.Data?.UnconvertedLineCount : null, pr.Message),
                ToCard("overdue", "Overdue PO Lines", "Latest live PO revision, positive balance, and current ETA before as-of date.", po.Succeeded ? po.Data?.OverdueLineCount : null, po.Message),
                ToCard("due", "PO Due Next 7 Days", "Positive-balance lines with ETA from today through day seven, inclusive.", due.Succeeded ? due.Data?.DueSoonLineCount : null, due.Message),
                ToCard("rni", "Received Not Invoiced", "Existing PO matching classification; inquiry count only, not AP liability.", rni.Succeeded ? rni.Data?.ReceivedNotInvoicedCount : null, rni.Message)
            ];
            LastRefreshed = Dates.Now;
            if (SelectedMetric is not null && SelectedCount is null)
            {
                SelectedMetric = null;
            }
        }
        finally
        {
            SummaryLoading = false;
        }
    }

    protected override PoInquiryQuery BuildQuery() => new()
    {
        SuppCode = Clean(DraftSupplier),
        BuyerCode = Clean(DraftBuyer),
        AsOfDate = AsOfDate.Date
    };

    private void UpdateGridFilters()
    {
        if (PrDataSource is null)
        {
            return;
        }

        var q = BuildQuery();
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Unconverted;
        PrDataSource.UpdateFilters(q);

        q = BuildQuery();
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Overdue;
        OverdueDataSource.UpdateFilters(q);

        q = BuildQuery();
        DueSoonDataSource.UpdateFilters(q);

        q = BuildQuery();
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.ReceivedNotInvoiced;
        RniDataSource.UpdateFilters(q);
    }

    private Task<IvMasterOperationResult<PoInquiryPage<PoPrStatusRow>>> SearchPrAsync(
        PoInquiryQuery query, CancellationToken token) =>
        Inquiry.GetPrStatusAsync(MenuCodes.PurchaseWorkbench, query, token);

    private Task<IvMasterOperationResult<PoInquiryPage<PoOrderOutstandingRow>>> SearchOverdueAsync(
        PoInquiryQuery query, CancellationToken token) =>
        Inquiry.GetPoOutstandingAsync(MenuCodes.PurchaseWorkbench, query, token);

    private Task<IvMasterOperationResult<PoInquiryPage<PoOrderOutstandingRow>>> SearchDueSoonAsync(
        PoInquiryQuery query, CancellationToken token) =>
        Inquiry.GetPoDueSoonAsync(MenuCodes.PurchaseWorkbench, query, token);

    private Task<IvMasterOperationResult<PoInquiryPage<PoMatchingRow>>> SearchRniAsync(
        PoInquiryQuery query, CancellationToken token) =>
        Inquiry.GetPoMatchingAsync(MenuCodes.PurchaseWorkbench, query, token);

    private void OnGridError(string? message) => GridErrorMessage = message;

    private string BuildExportUrl(string metric)
    {
        var query = BuildQuery();
        var parameters = new Dictionary<string, string?>
        {
            ["suppCode"] = query.SuppCode,
            ["buyerCode"] = query.BuyerCode,
            ["asOfDate"] = query.AsOfDate?.ToString("yyyy-MM-dd"),
            ["workbenchPreset"] = metric switch
            {
                "pr" => PoInquiryWorkbenchPresets.Unconverted,
                "overdue" => PoInquiryWorkbenchPresets.Overdue,
                "rni" => PoInquiryWorkbenchPresets.ReceivedNotInvoiced,
                _ => null
            }
        };
        var path = metric switch
        {
            "pr" => "/purchase/inquiry/workbench/pr-export",
            "overdue" => "/purchase/inquiry/workbench/overdue-export",
            "due" => "/purchase/inquiry/workbench/due-export",
            _ => "/purchase/inquiry/workbench/rni-export"
        };
        return QueryHelpers.AddQueryString(path, parameters);
    }

    private static WorkbenchCard ToCard(string key, string title, string definition, int? count, string? error) =>
        new(key, title, definition, count, count is null
            ? (string.IsNullOrWhiteSpace(error) ? "This inquiry is unavailable." : error)
            : null);

    private static PoInquiryQuery Clone(PoInquiryQuery query) => new()
    {
        DateFrom = query.DateFrom,
        DateTo = query.DateTo,
        SuppCode = query.SuppCode,
        BuyerCode = query.BuyerCode,
        AsOfDate = query.AsOfDate
    };

    protected sealed record WorkbenchCard(
        string Key,
        string Title,
        string Definition,
        int? Count = null,
        string? UnavailableReason = null);
}
