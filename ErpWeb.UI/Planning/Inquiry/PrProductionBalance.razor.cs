using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Planning.Inquiry;

public partial class PrProductionBalance : PageBase, IDisposable
{
    [Inject] private IProductionBalanceInquiryService Balance { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;

    protected bool IsBootstrapping = true;
    protected bool DetailLoading;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;

    protected string? AppliedKind;
    protected string? AppliedWorkOrderNo;
    protected string? AppliedItemCode;
    protected bool AppliedIncludeZeroQty;

    protected string DraftKind = string.Empty;
    protected string DraftWorkOrderNo = string.Empty;
    protected string DraftItemCode = string.Empty;
    protected bool DraftIncludeZeroQty;

    protected ProductionBalanceLotRow? SelectedLot;
    protected ProductionBalanceGridDataSource DataSource { get; private set; } = default!;
    protected InMemoryGridDataSource<ProductionBalanceLotMovementRow> MovementDataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 lot" : $"{TotalCount:N0} lots";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedKind)
        || !string.IsNullOrWhiteSpace(AppliedWorkOrderNo)
        || !string.IsNullOrWhiteSpace(AppliedItemCode)
        || AppliedIncludeZeroQty;

    protected IReadOnlyList<FilterOption> KindOptions { get; } =
    [
        new(string.Empty, "All kinds"),
        new(ProductionBalLotKinds.MaterialIn, "MATERIAL_IN"),
        new(ProductionBalLotKinds.Wip, "WIP")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Kind", FieldName = nameof(ProductionBalanceLotRow.Kind), Width = "110px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Item", FieldName = nameof(ProductionBalanceLotRow.ItemCode), Width = "120px", VisibleIndex = 2 },
        new() { Caption = "Description", FieldName = nameof(ProductionBalanceLotRow.Description), VisibleIndex = 3 },
        new() { Caption = "Lot", FieldName = nameof(ProductionBalanceLotRow.LotNo), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "Work Order", FieldName = nameof(ProductionBalanceLotRow.WorkOrderNo), Width = "130px", VisibleIndex = 5 },
        new() { Caption = "Work Centre", FieldName = nameof(ProductionBalanceLotRow.WorkCentreCode), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Process", FieldName = nameof(ProductionBalanceLotRow.ProcessCode), Width = "100px", VisibleIndex = 7 },
        new() { Caption = "Qty", FieldName = nameof(ProductionBalanceLotRow.Qty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 8 },
        new() { Caption = "UOM", FieldName = nameof(ProductionBalanceLotRow.Uom), Width = "70px", VisibleIndex = 9 },
        new() { Caption = "Warehouse", FieldName = nameof(ProductionBalanceLotRow.WarehouseCode), Width = "110px", VisibleIndex = 10 },
        new() { Caption = "Bin", FieldName = nameof(ProductionBalanceLotRow.LocationCode), Width = "90px", VisibleIndex = 11 },
        new() { Caption = "Net received (base)", FieldName = nameof(ProductionBalanceLotRow.NetReceivedBaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "140px" },
        new() { Caption = "Pending FG (base)", FieldName = nameof(ProductionBalanceLotRow.PendingReceiptBaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "140px" },
        new() { Caption = "Base UOM", FieldName = nameof(ProductionBalanceLotRow.BaseUom), Width = "80px" },
        new() { Caption = "Last movement", FieldName = nameof(ProductionBalanceLotRow.LastMovementDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "120px", VisibleIndex = 12 }
    ];

    protected List<GridColumnData> MovementColumns { get; } =
    [
        new() { Caption = "Date", FieldName = nameof(ProductionBalanceLotMovementRow.MovementDate), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "140px", VisibleIndex = 1 },
        new() { Caption = "Type", FieldName = nameof(ProductionBalanceLotMovementRow.MovementType), Width = "140px", VisibleIndex = 2 },
        new() { Caption = "Qty", FieldName = nameof(ProductionBalanceLotMovementRow.Qty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 3 },
        new() { Caption = "UOM", FieldName = nameof(ProductionBalanceLotMovementRow.Uom), Width = "70px", VisibleIndex = 4 },
        new() { Caption = "Signed base", FieldName = nameof(ProductionBalanceLotMovementRow.SignedBaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Base UOM", FieldName = nameof(ProductionBalanceLotMovementRow.BaseUom), Width = "80px", VisibleIndex = 6 },
        new() { Caption = "Doc type", FieldName = nameof(ProductionBalanceLotMovementRow.DocumentType), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Doc no.", FieldName = nameof(ProductionBalanceLotMovementRow.DocumentNo), Width = "130px", VisibleIndex = 8 },
        new() { Caption = "Created", FieldName = nameof(ProductionBalanceLotMovementRow.CreatedDate), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "140px", VisibleIndex = 9 }
    ];

    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new ProductionBalanceGridDataSource(LoadPageAsync);
        MovementDataSource = new InMemoryGridDataSource<ProductionBalanceLotMovementRow>();
        Buttons = [new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" }];
        SyncFilters();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;

    protected async Task OnButtonClick(SelectedButtonInfo<ProductionBalanceLotRow> info)
    {
        if (string.Equals(info.SelectedButton.Text, "REFRESH", StringComparison.OrdinalIgnoreCase))
            await ReloadAsync();
    }

    protected async Task OnLotSelectedAsync(ProductionBalanceLotRow? lot)
    {
        SelectedLot = lot;
        DetailLoading = true;
        MovementDataSource.SetRows([]);
        await InvokeAsync(StateHasChanged);

        if (lot is not null)
        {
            var result = await Balance.GetMovementsAsync(lot.Uid);
            if (result.Succeeded && result.Data is not null)
                MovementDataSource.SetRows(result.Data);
            else
                ErrorMessage = result.Message ?? "Unable to load lot movements.";
        }

        DetailLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    protected void OpenFilterPopup()
    {
        DraftKind = AppliedKind ?? string.Empty;
        DraftWorkOrderNo = AppliedWorkOrderNo ?? string.Empty;
        DraftItemCode = AppliedItemCode ?? string.Empty;
        DraftIncludeZeroQty = AppliedIncludeZeroQty;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedKind = NullIfEmpty(DraftKind);
        AppliedWorkOrderNo = NullIfEmpty(DraftWorkOrderNo);
        AppliedItemCode = NullIfEmpty(DraftItemCode);
        AppliedIncludeZeroQty = DraftIncludeZeroQty;
        FilterPopupVisible = false;
        SelectedLot = null;
        MovementDataSource.SetRows([]);
        await ReloadAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftKind = DraftWorkOrderNo = DraftItemCode = string.Empty;
        DraftIncludeZeroQty = false;
        await ApplyFiltersAsync();
    }

    protected Task OnSearchTextChanged(string value)
    {
        SearchText = value ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version == _searchVersion)
                await InvokeAsync(ReloadAsync);
        };
        _searchDebounce.Start();
        return Task.CompletedTask;
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private async Task ReloadAsync()
    {
        SyncFilters();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncFilters() => DataSource.UpdateFilters(new ProductionBalanceLotQuery
    {
        SearchText = NullIfEmpty(SearchText),
        Kind = AppliedKind,
        WorkOrderNo = AppliedWorkOrderNo,
        ItemCode = AppliedItemCode,
        IncludeZeroQty = AppliedIncludeZeroQty
    });

    private async Task<(IReadOnlyList<ProductionBalanceLotRow>, int)> LoadPageAsync(
        ProductionBalanceLotQuery query,
        CancellationToken ct)
    {
        var result = await Balance.SearchAsync(query, ct);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load production balances.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    protected sealed record FilterOption(string Key, string Name);
}

public sealed class ProductionBalanceGridDataSource : GridCustomDataSource
{
    private readonly Func<ProductionBalanceLotQuery, CancellationToken, Task<(IReadOnlyList<ProductionBalanceLotRow>, int)>> _loader;
    private ProductionBalanceLotQuery _filters = new();

    public ProductionBalanceGridDataSource(
        Func<ProductionBalanceLotQuery, CancellationToken, Task<(IReadOnlyList<ProductionBalanceLotRow>, int)>> loader)
        => _loader = loader;

    public void UpdateFilters(ProductionBalanceLotQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken ct)
    {
        var q = Clone(_filters);
        q.Skip = 0;
        q.Take = 1;
        var (_, count) = await _loader(q, ct);
        return count;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken ct)
    {
        var q = Clone(_filters);
        q.Skip = Math.Max(0, options.StartIndex);
        q.Take = Math.Clamp(options.Count <= 0 ? 50 : options.Count, 1, 100);
        var (rows, _) = await _loader(q, ct);
        return rows.ToList();
    }

    private static ProductionBalanceLotQuery Clone(ProductionBalanceLotQuery x) => new()
    {
        SearchText = x.SearchText,
        Kind = x.Kind,
        WorkOrderNo = x.WorkOrderNo,
        ItemCode = x.ItemCode,
        IncludeZeroQty = x.IncludeZeroQty,
        Skip = x.Skip,
        Take = x.Take
    };
}

public sealed class InMemoryGridDataSource<T> : GridCustomDataSource
{
    private IReadOnlyList<T> _rows = [];

    public void SetRows(IReadOnlyList<T> rows) => _rows = rows;

    public override Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.Count);

    public override Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken cancellationToken)
    {
        var start = Math.Max(0, options.StartIndex);
        var count = options.Count <= 0 ? 50 : options.Count;
        IList page = _rows.Skip(start).Take(count).ToList();
        return Task.FromResult(page);
    }
}
