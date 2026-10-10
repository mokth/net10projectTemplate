using ErpWeb.Core.Menus;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Pricing;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Sales.Inquiry;

public partial class SaPriceChangeHistoryInquiry : PageBase
{
    [Inject] private ISaPriceChangeHistoryService History { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected SaPriceChangeHistoryQuery Query { get; } = new()
    {
        Take = SaPriceChangeHistoryLimits.DefaultPageSize
    };

    protected IReadOnlyList<SaPriceChangeHistoryRow> Rows { get; private set; } = [];
    protected SaPriceChangeHistoryBatch? SelectedBatch { get; private set; }
    protected bool BatchPopupVisible { get; set; }
    protected bool CanExport { get; private set; }
    protected bool IsLoading { get; private set; }
    protected bool IsBatchLoading { get; private set; }
    protected string? StatusMessage { get; private set; }
    protected int TotalCount { get; private set; }
    protected int CurrentPage { get; private set; } = 1;
    protected int PageSize { get; private set; } = SaPriceChangeHistoryLimits.DefaultPageSize;
    protected int BatchPage { get; private set; } = 1;
    protected int BatchPageSize { get; } = SaPriceChangeHistoryLimits.DefaultPageSize;
    protected int BatchTotalCount { get; private set; }
    protected IReadOnlyList<IvCodeLookupRow> OriginOptions { get; } =
    [
        new() { Code = SalesPriceChangeAuditOrigins.PriceReviewWorkbench, Desc = "Price review workbench" },
        new() { Code = SalesPriceChangeAuditOrigins.ItemMaster, Desc = "Item master" },
        new() { Code = SalesPriceChangeAuditOrigins.PriceListMaster, Desc = "Price list master" },
        new() { Code = SalesPriceChangeAuditOrigins.CustomerItemMaster, Desc = "Customer special master" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> TargetOptions { get; } =
    [
        new() { Code = SaPriceMaintenanceTargets.ItemDefault, Desc = "Item default" },
        new() { Code = SaPriceMaintenanceTargets.PriceList, Desc = "Price list" },
        new() { Code = SaPriceMaintenanceTargets.CustomerItem, Desc = "Customer special" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> ChangeKindOptions { get; } =
    [
        new() { Code = SalesPriceChangeKinds.Create, Desc = "Create" },
        new() { Code = SalesPriceChangeKinds.Update, Desc = "Update" },
        new() { Code = SalesPriceChangeKinds.Delete, Desc = "Delete" },
        new() { Code = SalesPriceChangeKinds.Schedule, Desc = "Schedule" }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        CanExport = await AccessRights.CanAsync(
            MenuCodes.SalesPriceChangeHistory,
            PermissionCodes.Export);
        await LoadAsync();
    }

    protected async Task SearchAsync()
    {
        CurrentPage = 1;
        await LoadAsync();
    }

    protected async Task PreviousPageAsync()
    {
        if (CurrentPage <= 1 || IsLoading)
        {
            return;
        }

        CurrentPage--;
        await LoadAsync();
    }

    protected async Task NextPageAsync()
    {
        if (CurrentPage >= TotalPages || IsLoading)
        {
            return;
        }

        CurrentPage++;
        await LoadAsync();
    }

    protected async Task OpenBatchAsync(SaPriceChangeHistoryRow row)
    {
        if (row.PriceChangeBatchId <= 0 || IsBatchLoading)
        {
            return;
        }

        BatchPage = 1;
        SelectedBatch = null;
        BatchTotalCount = 0;
        BatchPopupVisible = true;
        await LoadBatchAsync(row.PriceChangeBatchId);
    }

    protected async Task PreviousBatchPageAsync()
    {
        if (SelectedBatch is null || BatchPage <= 1 || IsBatchLoading)
        {
            return;
        }

        BatchPage--;
        await LoadBatchAsync(SelectedBatch.PriceChangeBatchId);
    }

    protected async Task NextBatchPageAsync()
    {
        if (SelectedBatch is null || BatchPage >= BatchTotalPages || IsBatchLoading)
        {
            return;
        }

        BatchPage++;
        await LoadBatchAsync(SelectedBatch.PriceChangeBatchId);
    }

    protected void Export()
    {
        if (!CanExport)
        {
            ErrorMessage = "Export permission is required.";
            return;
        }

        Navigation.NavigateTo(BuildExportUrl(), forceLoad: true);
    }

    protected string PageSummary =>
        TotalCount == 0
            ? "No history rows found."
            : $"Page {CurrentPage:N0} of {TotalPages:N0} · {TotalCount:N0} history row(s)";

    protected string BatchPageSummary =>
        BatchTotalCount == 0
            ? "No lines."
            : $"Page {BatchPage:N0} of {BatchTotalPages:N0} · {BatchTotalCount:N0} line(s)";

    protected int TotalPages =>
        Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    protected int BatchTotalPages =>
        Math.Max(1, (int)Math.Ceiling(BatchTotalCount / (double)BatchPageSize));

    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        Query.Skip = (CurrentPage - 1) * PageSize;
        Query.Take = PageSize;
        try
        {
            var result = await History.SearchAsync(Query);
            if (!result.Succeeded || result.Data is null)
            {
                Rows = [];
                TotalCount = 0;
                ErrorMessage = result.Message ?? "Unable to load price change history.";
                return;
            }

            Rows = result.Data.Rows;
            TotalCount = result.Data.TotalCount;
            StatusMessage = PageSummary;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadBatchAsync(long batchId)
    {
        IsBatchLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await History.GetBatchAsync(
                batchId,
                (BatchPage - 1) * BatchPageSize,
                BatchPageSize);
            if (!result.Succeeded || result.Data is null)
            {
                SelectedBatch = null;
                BatchTotalCount = 0;
                ErrorMessage = result.Message ?? "Unable to load the price change batch.";
                return;
            }

            SelectedBatch = result.Data;
            BatchTotalCount = result.Data.TotalLineCount;
        }
        finally
        {
            IsBatchLoading = false;
        }
    }

    private string BuildExportUrl()
    {
        var values = new Dictionary<string, string?>();
        Add(values, "changedDateFromUtc", Query.ChangedDateFromUtc?.ToString("yyyy-MM-dd"));
        Add(values, "changedDateToUtc", Query.ChangedDateToUtc?.ToString("yyyy-MM-dd"));
        Add(values, "effectiveDateFrom", Query.EffectiveDateFrom?.ToString("yyyy-MM-dd"));
        Add(values, "effectiveDateTo", Query.EffectiveDateTo?.ToString("yyyy-MM-dd"));
        Add(values, "origin", Query.Origin);
        Add(values, "targetType", Query.TargetType);
        Add(values, "changeKind", Query.ChangeKind);
        Add(values, "itemCode", Query.ItemCode);
        Add(values, "itemType", Query.ItemType);
        Add(values, "itemClass", Query.ItemClass);
        Add(values, "itemSubClass", Query.ItemSubClass);
        Add(values, "brand", Query.Brand);
        Add(values, "custCode", Query.CustCode);
        Add(values, "custType", Query.CustType);
        Add(values, "custGroup", Query.CustGroup);
        Add(values, "custPriceCode", Query.CustPriceCode);
        Add(values, "changedBy", Query.ChangedBy);
        Add(values, "reasonSearch", Query.ReasonSearch);
        return QueryHelpers.AddQueryString("/sales/inquiry/price-change-history/export", values);
    }

    private static void Add(Dictionary<string, string?> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }
}
