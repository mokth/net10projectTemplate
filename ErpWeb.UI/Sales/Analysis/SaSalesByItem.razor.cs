using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Analysis;

/// <summary>
/// Sales by Item (sales-analysis Phase 2) — POSTED invoice lines aggregated by item code. Read-only.
/// </summary>
public partial class SaSalesByItem : SaAnalysisPageBase
{
    [Inject] protected IIvInventoryLookupService InventoryLookups { get; set; } = default!;

    protected string? ItemCode { get; set; }
    protected string? SalesmanCode { get; set; }
    protected string? CustCode { get; set; }
    protected string? BranchCode { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> SalesmanOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CustomerOptions { get; set; } = [];

    protected IReadOnlyList<SaSalesDetailRow> Rows { get; set; } = [];

    protected override async Task OnAnalysisInitializedAsync()
    {
        SalesmanOptions = await CustLookups.ListSalesRepsForAssignmentAsync();
        CustomerOptions = await CustLookups.SearchCustomersAsync(string.Empty, 200);
    }

    protected async Task OnCustomerSearchChangedAsync(string? value)
    {
        CustomerOptions = await CustLookups.SearchCustomersAsync(value ?? string.Empty, 200);
    }

    protected async Task OnItemCodeChangedAsync(string? code)
    {
        ItemCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        await Task.CompletedTask;
    }

    protected Task OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        ItemCode = item?.ICode;
        return Task.CompletedTask;
    }

    protected async Task RunAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            SyncQuery();
            var result = await Analysis.GetSalesDetailAsync(
                MenuCodes.SalesByItem, Query, SaSalesDetailDimension.Item);
            if (!result.Succeeded)
            {
                LoadError = result.Message ?? "Unable to run the item analysis.";
                Rows = [];
                return;
            }

            Rows = result.Data ?? [];
            HasRun = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SyncQuery()
    {
        Query.ItemCode = ItemCode;
        Query.SalesmanCode = SalesmanCode;
        Query.CustCode = CustCode;
        Query.BranchCode = BranchCode;
    }

    protected string ExportUrl => BuildExportUrl("/sales/analysis/by-item/export",
    [
        new("itemCode", ItemCode),
        new("salesmanCode", SalesmanCode),
        new("custCode", CustCode),
        new("branchCode", BranchCode)
    ]);
}
