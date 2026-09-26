using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Analysis;

/// <summary>
/// Sales by Warehouse (sales-analysis Phase 2) — POSTED invoice lines grouped by the line's
/// <c>FrWarehouse</c>.
/// </summary>
public partial class SaSalesByWarehouse : SaAnalysisPageBase
{
    [Inject] protected IIvInventoryLookupService InventoryLookups { get; set; } = default!;

    protected string? Warehouse { get; set; }
    protected string? SalesmanCode { get; set; }
    protected string? CustCode { get; set; }
    protected string? BranchCode { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> WarehouseOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> SalesmanOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CustomerOptions { get; set; } = [];

    protected IReadOnlyList<SaSalesDetailRow> Rows { get; set; } = [];

    protected override async Task OnAnalysisInitializedAsync()
    {
        SalesmanOptions = await CustLookups.ListSalesRepsForAssignmentAsync();
        CustomerOptions = await CustLookups.SearchCustomersAsync(string.Empty, 200);

        var warehouses = await InventoryLookups.ListActiveWarehousesAsync();
        WarehouseOptions = warehouses.Succeeded ? warehouses.Rows : [];
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
                MenuCodes.SalesByWarehouse, Query, SaSalesDetailDimension.Warehouse);
            if (!result.Succeeded)
            {
                LoadError = result.Message ?? "Unable to run the warehouse analysis.";
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
        Query.Warehouse = Warehouse;
        Query.SalesmanCode = SalesmanCode;
        Query.CustCode = CustCode;
        Query.BranchCode = BranchCode;
    }

    protected string ExportUrl => BuildExportUrl("/sales/analysis/by-warehouse/export",
    [
        new("warehouse", Warehouse),
        new("salesmanCode", SalesmanCode),
        new("custCode", CustCode),
        new("branchCode", BranchCode)
    ]);
}
