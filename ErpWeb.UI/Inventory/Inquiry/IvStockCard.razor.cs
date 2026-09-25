using System.Collections;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Inventory.Inquiry;

/// <summary>
/// Stock Card / Stock Movement — the chronological ledger for one stock slice, with an opening
/// balance computed over every earlier movement and a running balance over the period (D12).
///
/// <para>
/// The running column is computed over <em>exactly</em> the key the row filter uses, so it is only a
/// single physical pile's balance when the scope pins warehouse, bin, lot AND status. Anything wider
/// is captioned "all bins/lots in scope" rather than quietly presenting a sum as a pile balance.
/// </para>
/// </summary>
public partial class IvStockCard : PageBase, IDisposable
{
    [Inject] private IIvTrxHistoryService TrxHistory { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;

    private DxGrid? _grid;

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected int TotalCount;

    protected bool CanExport;
    protected bool CanViewValue;

    // The item is the card's subject, so it lives in the toolbar rather than the filter popup.
    protected string? AppliedICode;

    // Applied scope + period.
    protected string? AppliedWhCode;
    protected string? AppliedLocCode;
    protected string? AppliedLotNo;
    protected IReadOnlyList<string> AppliedStatuses { get; set; } = [];
    protected DateTime? AppliedTrxDateFrom;
    protected DateTime? AppliedTrxDateTo;

    // Draft (popup) scope + period.
    protected string? DraftWhCode;
    protected string? DraftLocCode;
    protected string? DraftLotNo;
    protected IEnumerable<string> DraftStatuses { get; set; } = [];
    protected DateTime? DraftTrxDateFrom;
    protected DateTime? DraftTrxDateTo;

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Statuses { get; set; } = [];

    // Card figures.
    protected decimal OpeningQty;
    protected decimal CardInQty;
    protected decimal CardOutQty;
    protected decimal CardNetQty;
    protected decimal CardAdjustNetQty;
    protected decimal LedgerClosingQty;
    protected decimal? LiveQty;
    protected decimal? DifferenceQty;
    protected decimal? CardTotalValue;
    protected bool Truncated;
    protected int MatchingCount;

    protected IvStockCardDataSource DataSource { get; private set; } = default!;

    protected bool HasItem => !string.IsNullOrWhiteSpace(AppliedICode);

    /// <summary>
    /// True only when the scope is one physical pile — all five slice parts pinned. Anything less
    /// makes the running column a sum across piles, which the caption must say out loud (D12).
    /// </summary>
    protected bool IsSinglePile =>
        HasItem
        && !string.IsNullOrWhiteSpace(AppliedWhCode)
        && !string.IsNullOrWhiteSpace(AppliedLocCode)
        && !string.IsNullOrWhiteSpace(AppliedLotNo)
        && AppliedStatuses.Count == 1;

    protected string RunningCaption => IsSinglePile ? "Running" : "Running (all bins/lots in scope)";

    protected string TotalCountLabel => TotalCount == 1 ? "1 line" : $"{TotalCount:N0} lines";

    protected bool HasDifference => DifferenceQty is decimal d && d != 0m;

    protected List<GridColumnData> Columns() =>
    [
        new() { Caption = "Date", FieldName = nameof(IvTrxHistoryRow.TrxDtTime), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "140px", VisibleIndex = 1 },
        new() { Caption = "Type", FieldName = nameof(IvTrxHistoryRow.TrxType), Width = "80px", VisibleIndex = 2 },
        new() { Caption = "Batch", FieldName = nameof(IvTrxHistoryRow.BatchNo), DataType = "int", Width = "80px", VisibleIndex = 3 },
        new() { Caption = "Ref", FieldName = nameof(IvTrxHistoryRow.RefNo), Width = "140px", VisibleIndex = 4 },
        new() { Caption = "Warehouse", FieldName = nameof(IvTrxHistoryRow.ToWarehouse), Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Bin", FieldName = nameof(IvTrxHistoryRow.ToLocation), Width = "80px", VisibleIndex = 6 },
        new() { Caption = "Lot", FieldName = nameof(IvTrxHistoryRow.ToLotNo), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Status", FieldName = nameof(IvTrxHistoryRow.IStatus), Width = "90px", VisibleIndex = 8 },
        new() { Caption = "In", FieldName = nameof(IvTrxHistoryRow.InQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 9 },
        new() { Caption = "Out", FieldName = nameof(IvTrxHistoryRow.OutQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Net", FieldName = nameof(IvTrxHistoryRow.NetQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 11 },
        new() { Caption = RunningCaption, FieldName = nameof(IvTrxHistoryRow.RunningQty), DataType = "decimal", DisplayFormat = "n4", Width = "130px", VisibleIndex = 12 },
        new() { Caption = "UOM", FieldName = nameof(IvTrxHistoryRow.StdUom), Width = "70px", VisibleIndex = 13 },
        new() { Caption = "Reason", FieldName = nameof(IvTrxHistoryRow.Reason), Width = "110px", VisibleIndex = 14 },
        // The money column is omitted, never blanked, when the caller may not see value (D11 Option B).
        new() { Caption = "Est. value", FieldName = nameof(IvTrxHistoryRow.EstValue), DataType = "decimal", DisplayFormat = "n4", Width = "120px", Visible = CanViewValue, VisibleIndex = 15 },
        new() { Caption = "Unit price", FieldName = nameof(IvTrxHistoryRow.UnitPrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", Visible = false, VisibleIndex = 16 },
        new() { Caption = "From warehouse", FieldName = nameof(IvTrxHistoryRow.FrWarehouse), Width = "110px", Visible = false, VisibleIndex = 17 },
        new() { Caption = "From bin", FieldName = nameof(IvTrxHistoryRow.FrLocation), Width = "90px", Visible = false, VisibleIndex = 18 },
        new() { Caption = "From lot", FieldName = nameof(IvTrxHistoryRow.FrLotNo), Width = "110px", Visible = false, VisibleIndex = 19 },
        new() { Caption = "Line", FieldName = nameof(IvTrxHistoryRow.TrxLineNo), DataType = "int", Width = "70px", Visible = false, VisibleIndex = 20 },
        new() { Caption = "Remarks", FieldName = nameof(IvTrxHistoryRow.Remarks), Visible = false, VisibleIndex = 21 },
        ..AuditColumns.For(startVisibleIndex: 22)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvStockCardDataSource();

        CanExport = await AccessRights.CanAsync(MenuCodes.InventoryStockCard, PermissionCodes.Export);
        CanViewValue = await AccessRights.CanAsync(MenuCodes.InventoryStockCard, PermissionCodes.ViewPrice);

        Buttons =
        [
            new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
            new() { Text = "EXPORT", IConClass = "fa-solid fa-file-excel", Style = "primary", Enabled = CanExport }
        ];

        // A sane, company-local default period. The service requires a start date because the opening
        // balance is "everything before it"; the screen pre-fills rather than demanding input.
        AppliedTrxDateFrom = Dates.Today.AddMonths(-1);
        AppliedTrxDateTo = Dates.Today;

        await LoadLookupsAsync();
        await ReloadCardAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected async Task OnButtonClick(SelectedButtonInfo<IvTrxHistoryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadCardAsync();
                break;
            case "EXPORT":
                await OnExportAsync();
                break;
        }
    }

    protected async Task OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        AppliedICode = item?.ICode;
        await ReloadCardAsync();
    }

    protected Task OnItemCodeChangedAsync(string? code)
    {
        AppliedICode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        return Task.CompletedTask;
    }

    protected void OpenFilterPopup()
    {
        DraftWhCode = AppliedWhCode;
        DraftLocCode = AppliedLocCode;
        DraftLotNo = AppliedLotNo;
        DraftStatuses = AppliedStatuses;
        DraftTrxDateFrom = AppliedTrxDateFrom;
        DraftTrxDateTo = AppliedTrxDateTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedWhCode = Normalize(DraftWhCode);
        AppliedLocCode = Normalize(DraftLocCode);
        AppliedLotNo = Normalize(DraftLotNo);
        AppliedStatuses = [.. DraftStatuses
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        AppliedTrxDateFrom = DraftTrxDateFrom?.Date;
        AppliedTrxDateTo = DraftTrxDateTo?.Date;
        FilterPopupVisible = false;
        await ReloadCardAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftWhCode = null;
        DraftLocCode = null;
        DraftLotNo = null;
        DraftStatuses = [];
        DraftTrxDateFrom = Dates.Today.AddMonths(-1);
        DraftTrxDateTo = Dates.Today;

        AppliedWhCode = null;
        AppliedLocCode = null;
        AppliedLotNo = null;
        AppliedStatuses = [];
        AppliedTrxDateFrom = Dates.Today.AddMonths(-1);
        AppliedTrxDateTo = Dates.Today;
        FilterPopupVisible = false;
        await ReloadCardAsync();
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    public void Dispose()
    {
        // No timers: the card is driven by explicit reloads.
    }

    private async Task ReloadCardAsync()
    {
        if (!HasItem)
        {
            ResetCard();
            DataSource.SetRows([]);
            _grid?.Reload();
            await InvokeAsync(StateHasChanged);
            return;
        }

        var result = await TrxHistory.GetStockCardAsync(MenuCodes.InventoryStockCard, BuildQuery());
        if (!result.Succeeded || result.Data is null)
        {
            ResetCard();
            DataSource.SetRows([]);
            ErrorMessage = result.Message ?? "Unable to build the stock card.";
            _grid?.Reload();
            await InvokeAsync(StateHasChanged);
            return;
        }

        var card = result.Data;
        TotalCount = card.TotalCount;
        MatchingCount = card.MatchingCount;
        OpeningQty = card.OpeningQty;
        CardInQty = card.InQty;
        CardOutQty = card.OutQty;
        CardNetQty = card.NetQty;
        CardAdjustNetQty = card.AdjustNetQty;
        LedgerClosingQty = card.LedgerClosingQty;
        LiveQty = card.LiveQty;
        DifferenceQty = card.DifferenceQty;
        CardTotalValue = card.TotalValue;
        Truncated = card.Truncated;

        DataSource.SetRows(card.Rows);
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void ResetCard()
    {
        TotalCount = 0;
        MatchingCount = 0;
        OpeningQty = 0m;
        CardInQty = 0m;
        CardOutQty = 0m;
        CardNetQty = 0m;
        CardAdjustNetQty = 0m;
        LedgerClosingQty = 0m;
        LiveQty = null;
        DifferenceQty = null;
        CardTotalValue = null;
        Truncated = false;
    }

    private IvTrxHistoryQuery BuildQuery() =>
        new()
        {
            ICode = AppliedICode,
            WhCode = AppliedWhCode,
            LocCode = AppliedLocCode,
            LotNo = AppliedLotNo,
            IStatuses = AppliedStatuses,
            TrxDateFrom = AppliedTrxDateFrom,
            TrxDateTo = AppliedTrxDateTo
        };

    private async Task LoadLookupsAsync()
    {
        var warehouses = await Lookups.ListActiveWarehousesAsync();
        var statuses = await Lookups.ListActiveStatusesAsync();

        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];
        Statuses = statuses.Succeeded ? statuses.Rows : [];

        if (!warehouses.Succeeded || !statuses.Succeeded)
        {
            ErrorMessage = warehouses.ErrorMessage ?? statuses.ErrorMessage ?? "Unable to load filter lookups.";
        }
    }

    private async Task OnExportAsync()
    {
        if (!CanExport)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (!HasItem)
        {
            StatusMessage = "Select an item first.";
            return;
        }

        var url = QueryHelpers.AddQueryString("/inventory/stock-card/export", BuildQueryDictionary());
        Navigation.NavigateTo(url, forceLoad: true);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The applied scope and period — never the grid's current page. Company and branch are absent on
    /// purpose: the endpoint resolves them server-side (D19).
    /// </summary>
    private Dictionary<string, string?> BuildQueryDictionary()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["iCode"] = AppliedICode
        };

        if (!string.IsNullOrWhiteSpace(AppliedWhCode))
        {
            dict["whCode"] = AppliedWhCode;
        }

        if (!string.IsNullOrWhiteSpace(AppliedLocCode))
        {
            dict["locCode"] = AppliedLocCode;
        }

        if (!string.IsNullOrWhiteSpace(AppliedLotNo))
        {
            dict["lotNo"] = AppliedLotNo;
        }

        if (AppliedStatuses.Count > 0)
        {
            dict["statuses"] = string.Join(',', AppliedStatuses);
        }

        if (AppliedTrxDateFrom is DateTime from)
        {
            dict["trxDateFrom"] = from.ToString("yyyy-MM-dd");
        }

        if (AppliedTrxDateTo is DateTime to)
        {
            dict["trxDateTo"] = to.ToString("yyyy-MM-dd");
        }

        return dict;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The stock card's ledger is materialised once per reload and paged in memory, because a running
/// balance cannot be aggregated in SQL without a window function. The grid therefore serves slices of
/// that one ordered list; a sort request is deliberately IGNORED, because re-ordering the rows would
/// make the running column meaningless.
/// </summary>
public sealed class IvStockCardDataSource : GridCustomDataSource
{
    private IReadOnlyList<IvTrxHistoryRow> _rows = [];

    public void SetRows(IReadOnlyList<IvTrxHistoryRow> rows) => _rows = rows;

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
