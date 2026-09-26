using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Analysis;

/// <summary>
/// Sales by Category (sales-analysis Phase 2) — POSTED invoice lines grouped by the live item master's
/// class. Items with no master row group under "(unknown)".
/// </summary>
public partial class SaSalesByCategory : SaAnalysisPageBase
{
    [Inject] protected IIvInventoryLookupService InventoryLookups { get; set; } = default!;

    protected string? ClassCode { get; set; }
    protected string? SalesmanCode { get; set; }
    protected string? CustCode { get; set; }
    protected string? BranchCode { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> ClassOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> SalesmanOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CustomerOptions { get; set; } = [];

    protected IReadOnlyList<SaSalesDetailRow> Rows { get; set; } = [];

    protected override async Task OnAnalysisInitializedAsync()
    {
        SalesmanOptions = await CustLookups.ListSalesRepsForAssignmentAsync();
        CustomerOptions = await CustLookups.SearchCustomersAsync(string.Empty, 200);

        var classes = await InventoryLookups.ListActiveClassesAsync();
        ClassOptions = classes.Succeeded ? classes.Rows : [];
    }

    protected async Task OnCustomerSearchChangedAsync(string? value)
    {
        CustomerOptions = await CustLookups.SearchCustomersAsync(value ?? string.Empty, 200);
    }

    protected async Task RunAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            SyncQuery();
            var result = await Analysis.GetSalesDetailAsync(
                MenuCodes.SalesByCategory, Query, SaSalesDetailDimension.Category);
            if (!result.Succeeded)
            {
                LoadError = result.Message ?? "Unable to run the category analysis.";
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
        Query.ClassCode = ClassCode;
        Query.SalesmanCode = SalesmanCode;
        Query.CustCode = CustCode;
        Query.BranchCode = BranchCode;
    }

    protected string ExportUrl => BuildExportUrl("/sales/analysis/by-category/export",
    [
        new("classCode", ClassCode),
        new("salesmanCode", SalesmanCode),
        new("custCode", CustCode),
        new("branchCode", BranchCode)
    ]);
}
