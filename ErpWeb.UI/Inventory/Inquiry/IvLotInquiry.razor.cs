using System.Collections;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Inquiry;

/// <summary>
/// Lot / Batch Inquiry — the lot passport (origin, dates, QC, ageing) with the piles holding it and its
/// posted movements.
///
/// <para>
/// The movements panel is the transaction inquiry's own row shape and service, so the lot ledger shows
/// the same scope-aware "in"/"out" and the same adjustment reason as every other movement view. The
/// child lists are bounded by one lot, so they are materialised and paged in memory rather than through
/// a second server round trip per page.
/// </para>
/// </summary>
public partial class IvLotInquiry : PageBase
{
    [Inject] private IIvLotInquiryService Lots { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private DxGrid? _grid;

    protected bool IsBootstrapping = true;
    protected bool DetailLoading;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected int TotalCount;

    protected DateTime AsOfDate;

    // Applied state.
    protected string? AppliedICode;
    protected string? AppliedLotNo;
    protected string? AppliedQcStatus;
    protected DateTime? AppliedExpiryFrom;
    protected DateTime? AppliedExpiryTo;
    protected string? AppliedSearchText;
    protected bool AppliedIncludeInactive = true;

    // Draft (popup) state.
    protected string? DraftICode;
    protected string? DraftLotNo;
    protected string? DraftQcStatus;
    protected DateTime? DraftExpiryFrom;
    protected DateTime? DraftExpiryTo;
    protected string? DraftSearchText;
    protected bool DraftIncludeInactive = true;

    // Summary strip.
    protected int SummaryItemCount;
    protected decimal SummaryTotalOnHandQty;
    protected int SummaryExpiredCount;
    protected int SummaryNoExpiryCount;

    // Selected lot detail.
    protected IvLotInquiryRow? SelectedLot;
    protected decimal DetailPileQty;
    protected decimal DetailMovementNetQty;

    protected IvLotInquiryGridDataSource DataSource { get; private set; } = default!;
    protected IvListGridDataSource<IvLotPileRow> PileDataSource { get; private set; } = default!;
    protected IvListGridDataSource<IvTrxHistoryRow> MovementDataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 lot" : $"{TotalCount:N0} lots";

    protected List<GridColumnData> LotColumns()
    {
        var index = 1;
        var columns = new List<GridColumnData>
        {
            new() { Caption = "Item", FieldName = nameof(IvLotInquiryRow.ICode), Width = "120px", VisibleIndex = index++, SortIndex = 0 },
            new() { Caption = "Description", FieldName = nameof(IvLotInquiryRow.IDesc), VisibleIndex = index++ },
            new() { Caption = "Lot no.", FieldName = nameof(IvLotInquiryRow.LotNo), Width = "140px", VisibleIndex = index++ },
            new() { Caption = "Source", FieldName = nameof(IvLotInquiryRow.SourceType), Width = "90px", VisibleIndex = index++ },
            new() { Caption = "Source doc", FieldName = nameof(IvLotInquiryRow.SourceDocNo), Width = "140px", VisibleIndex = index++ },
            new() { Caption = "Supplier", FieldName = nameof(IvLotInquiryRow.SupplierCode), Width = "110px", VisibleIndex = index++ },
            new() { Caption = "Received", FieldName = nameof(IvLotInquiryRow.ReceiptDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = index++ },
            new() { Caption = "Mfg", FieldName = nameof(IvLotInquiryRow.MfgDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = index++ },
            new() { Caption = "Expiry", FieldName = nameof(IvLotInquiryRow.ExpiryDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = index++ },
            new() { Caption = "Days to expiry", FieldName = nameof(IvLotInquiryRow.DaysToExpiry), DataType = "int", Width = "110px", VisibleIndex = index++ },
            new() { Caption = "Age (days)", FieldName = nameof(IvLotInquiryRow.AgeDays), DataType = "int", Width = "100px", VisibleIndex = index++ },
            new() { Caption = "QC", FieldName = nameof(IvLotInquiryRow.QcStatus), Width = "90px", VisibleIndex = index++ },
            new() { Caption = "On hand", FieldName = nameof(IvLotInquiryRow.OnHandQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = index++ },
            new() { Caption = "UOM", FieldName = nameof(IvLotInquiryRow.StdUom), Width = "70px", VisibleIndex = index++ },
            new() { Caption = "Piles", FieldName = nameof(IvLotInquiryRow.PileCount), DataType = "int", Width = "70px", VisibleIndex = index++ },
            new() { Caption = "Active", FieldName = nameof(IvLotInquiryRow.IsActive), DataType = "bool", Width = "80px", VisibleIndex = index++ },
            new() { Caption = "Remarks", FieldName = nameof(IvLotInquiryRow.Remarks), Visible = false, VisibleIndex = index++ }
        };

        columns.AddRange(AuditColumns.For(index));
        return columns;
    }

    protected static List<GridColumnData> PileColumns() =>
    [
        new() { Caption = "Warehouse", FieldName = nameof(IvLotPileRow.WhCode), Width = "110px", VisibleIndex = 1 },
        new() { Caption = "Warehouse name", FieldName = nameof(IvLotPileRow.WhDesc), VisibleIndex = 2 },
        new() { Caption = "Bin", FieldName = nameof(IvLotPileRow.LocCode), Width = "90px", VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(IvLotPileRow.IStatus), Width = "90px", VisibleIndex = 4 },
        new() { Caption = "On hand", FieldName = nameof(IvLotPileRow.StdQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 5 },
        new() { Caption = "UOM", FieldName = nameof(IvLotPileRow.StdUom), Width = "70px", VisibleIndex = 6 },
        new() { Caption = "Last movement", FieldName = nameof(IvLotPileRow.TransDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Expiry", FieldName = nameof(IvLotPileRow.ExpiryDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Ref", FieldName = nameof(IvLotPileRow.RefNo), Width = "130px", VisibleIndex = 9 },
        new() { Caption = "PO", FieldName = nameof(IvLotPileRow.PoNo), Visible = false, VisibleIndex = 10 },
        new() { Caption = "Remarks", FieldName = nameof(IvLotPileRow.Remarks), Visible = false, VisibleIndex = 11 },
        ..AuditColumns.For(12)
    ];

    protected static List<GridColumnData> MovementColumns() =>
    [
        new() { Caption = "Date", FieldName = nameof(IvTrxHistoryRow.TrxDtTime), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "140px", VisibleIndex = 1 },
        new() { Caption = "Type", FieldName = nameof(IvTrxHistoryRow.TrxType), Width = "80px", VisibleIndex = 2 },
        new() { Caption = "Batch", FieldName = nameof(IvTrxHistoryRow.BatchNo), DataType = "int", Width = "80px", VisibleIndex = 3 },
        new() { Caption = "Ref", FieldName = nameof(IvTrxHistoryRow.RefNo), Width = "140px", VisibleIndex = 4 },
        new() { Caption = "Status", FieldName = nameof(IvTrxHistoryRow.IStatus), Width = "90px", VisibleIndex = 5 },
        new() { Caption = "From wh.", FieldName = nameof(IvTrxHistoryRow.FrWarehouse), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "From lot", FieldName = nameof(IvTrxHistoryRow.FrLotNo), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "To wh.", FieldName = nameof(IvTrxHistoryRow.ToWarehouse), Width = "100px", VisibleIndex = 8 },
        new() { Caption = "To lot", FieldName = nameof(IvTrxHistoryRow.ToLotNo), Width = "120px", VisibleIndex = 9 },
        new() { Caption = "In", FieldName = nameof(IvTrxHistoryRow.InQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Out", FieldName = nameof(IvTrxHistoryRow.OutQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Net", FieldName = nameof(IvTrxHistoryRow.NetQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 12 },
        new() { Caption = "UOM", FieldName = nameof(IvTrxHistoryRow.StdUom), Width = "70px", VisibleIndex = 13 },
        new() { Caption = "Reason", FieldName = nameof(IvTrxHistoryRow.Reason), Width = "110px", VisibleIndex = 14 },
        new() { Caption = "Remarks", FieldName = nameof(IvTrxHistoryRow.Remarks), Visible = false, VisibleIndex = 15 },
        ..AuditColumns.For(16)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvLotInquiryGridDataSource(SearchPageAsync);
        PileDataSource = new IvListGridDataSource<IvLotPileRow>();
        MovementDataSource = new IvListGridDataSource<IvTrxHistoryRow>();

        Buttons =
        [
            new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" }
        ];

        AsOfDate = Dates.Today;
        await ReloadGridAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected async Task OnButtonClick(SelectedButtonInfo<IvLotInquiryRow> info)
    {
        if (string.Equals((info.SelectedButton.Text ?? string.Empty).ToUpperInvariant(), "REFRESH",
                StringComparison.Ordinal))
        {
            await ReloadGridAsync();
        }
    }

    protected async Task OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        AppliedICode = item?.ICode;
        await ReloadGridAsync();
    }

    protected Task OnItemCodeChangedAsync(string? code)
    {
        AppliedICode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads the selected lot's piles and movements. The row comes from the grid the user clicked, but
    /// the service re-resolves the tenant and re-checks ACCESS before it reads anything.
    /// </summary>
    protected async Task OnLotSelectedAsync(IvLotInquiryRow? lot)
    {
        SelectedLot = lot;
        DetailLoading = true;
        DetailPileQty = 0m;
        DetailMovementNetQty = 0m;
        PileDataSource.SetRows([]);
        MovementDataSource.SetRows([]);
        await InvokeAsync(StateHasChanged);

        if (lot is not null)
        {
            var result = await Lots.GetDetailAsync(MenuCodes.InventoryLotInquiry, lot);
            if (result.Succeeded && result.Data is not null && result.Data.Lot is not null)
            {
                SelectedLot = result.Data.Lot;
                DetailPileQty = result.Data.PileQty;
                DetailMovementNetQty = result.Data.MovementNetQty;
                PileDataSource.SetRows(result.Data.Piles);
                MovementDataSource.SetRows(result.Data.Movements);
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
        }

        DetailLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    protected void OpenFilterPopup()
    {
        DraftICode = AppliedICode;
        DraftLotNo = AppliedLotNo;
        DraftQcStatus = AppliedQcStatus;
        DraftExpiryFrom = AppliedExpiryFrom;
        DraftExpiryTo = AppliedExpiryTo;
        DraftSearchText = AppliedSearchText;
        DraftIncludeInactive = AppliedIncludeInactive;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedICode = Normalize(DraftICode);
        AppliedLotNo = Normalize(DraftLotNo);
        AppliedQcStatus = Normalize(DraftQcStatus);
        AppliedExpiryFrom = DraftExpiryFrom?.Date;
        AppliedExpiryTo = DraftExpiryTo?.Date;
        AppliedSearchText = Normalize(DraftSearchText);
        AppliedIncludeInactive = DraftIncludeInactive;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftICode = null;
        DraftLotNo = null;
        DraftQcStatus = null;
        DraftExpiryFrom = null;
        DraftExpiryTo = null;
        DraftSearchText = null;
        DraftIncludeInactive = true;

        AppliedICode = null;
        AppliedLotNo = null;
        AppliedQcStatus = null;
        AppliedExpiryFrom = null;
        AppliedExpiryTo = null;
        AppliedSearchText = null;
        AppliedIncludeInactive = true;
        await ResetDetailAsync();
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected void OpenGenealogy()
    {
        if (SelectedLot is not null)
            Navigation.NavigateTo($"/inventory/lot-traceability?lotId={SelectedLot.LotId}");
    }

    private async Task ReloadGridAsync()
    {
        // A different filter set means the selected lot may no longer be in the list, so the detail
        // panels are cleared rather than left showing a lot the user can no longer see.
        await ResetDetailAsync();

        DataSource.UpdateFilters(BuildQuery());
        await RefreshSummaryAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ResetDetailAsync()
    {
        SelectedLot = null;
        DetailPileQty = 0m;
        DetailMovementNetQty = 0m;
        PileDataSource.SetRows([]);
        MovementDataSource.SetRows([]);
        await Task.CompletedTask;
    }

    private IvLotInquiryQuery BuildQuery() =>
        new()
        {
            ICode = AppliedICode,
            LotNo = AppliedLotNo,
            QcStatus = AppliedQcStatus,
            ExpiryFrom = AppliedExpiryFrom,
            ExpiryTo = AppliedExpiryTo,
            SearchText = AppliedSearchText,
            IncludeInactive = AppliedIncludeInactive
        };

    private async Task RefreshSummaryAsync()
    {
        var query = BuildQuery();
        query.Skip = 0;
        query.Take = 1;

        var result = await Lots.GetSummaryAsync(MenuCodes.InventoryLotInquiry, query);
        if (result.Succeeded && result.Data is not null)
        {
            TotalCount = result.Data.TotalRows;
            SummaryItemCount = result.Data.ItemCount;
            SummaryTotalOnHandQty = result.Data.TotalOnHandQty;
            SummaryExpiredCount = result.Data.ExpiredCount;
            SummaryNoExpiryCount = result.Data.NoExpiryCount;
        }
        else
        {
            TotalCount = 0;
            SummaryItemCount = 0;
            SummaryTotalOnHandQty = 0m;
            SummaryExpiredCount = 0;
            SummaryNoExpiryCount = 0;
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
        }
    }

    private async Task<(IReadOnlyList<IvLotInquiryRow> Rows, int TotalCount)> SearchPageAsync(
        IvLotInquiryQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Lots.SearchAsync(MenuCodes.InventoryLotInquiry, query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load lots.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Server-side paging source for the lot list. The filters are rebuilt from the applied state on every
/// reload, so the grid can never page with a stale predicate.
/// </summary>
public sealed class IvLotInquiryGridDataSource : GridCustomDataSource
{
    private readonly Func<IvLotInquiryQuery, CancellationToken, Task<(IReadOnlyList<IvLotInquiryRow> Rows, int TotalCount)>> _loader;
    private IvLotInquiryQuery _filters = new();

    public IvLotInquiryGridDataSource(
        Func<IvLotInquiryQuery, CancellationToken, Task<(IReadOnlyList<IvLotInquiryRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public void UpdateFilters(IvLotInquiryQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(
        GridCustomDataSourceCountOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = 0;
        query.Take = 1;
        var (_, total) = await _loader(query, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(
        GridCustomDataSourceItemsOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = Math.Max(0, options.StartIndex);
        query.Take = options.Count <= 0 ? 50 : options.Count;
        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static IvLotInquiryQuery Clone(IvLotInquiryQuery source) =>
        new()
        {
            ICode = source.ICode,
            LotNo = source.LotNo,
            SearchText = source.SearchText,
            ExpiryFrom = source.ExpiryFrom,
            ExpiryTo = source.ExpiryTo,
            QcStatus = source.QcStatus,
            IncludeInactive = source.IncludeInactive,
            AsOfDate = source.AsOfDate,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}

/// <summary>
/// In-memory paging source for a bounded child list (one lot's piles, one lot's movements). Used where
/// a second server round trip per grid page would buy nothing.
/// </summary>
public sealed class IvListGridDataSource<T> : GridCustomDataSource
{
    private IReadOnlyList<T> _rows = [];

    public void SetRows(IReadOnlyList<T> rows) => _rows = rows;

    public override Task<int> GetItemCountAsync(
        GridCustomDataSourceCountOptions options,
        CancellationToken cancellationToken) =>
        Task.FromResult(_rows.Count);

    public override Task<IList> GetItemsAsync(
        GridCustomDataSourceItemsOptions options,
        CancellationToken cancellationToken)
    {
        var start = Math.Max(0, options.StartIndex);
        var count = options.Count <= 0 ? 50 : options.Count;
        IList page = _rows.Skip(start).Take(count).ToList();
        return Task.FromResult(page);
    }
}
