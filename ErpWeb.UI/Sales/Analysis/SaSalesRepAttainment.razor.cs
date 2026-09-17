using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;

namespace ErpWeb.UI.Sales.Analysis;

/// <summary>
/// Sales Rep Attainment (sales-analysis Phase 1) — company-wide monthly targets against posted invoice
/// totals. Targets are never prorated, a missing month is zero, and a zero target reads as N/A rather
/// than a divide-by-zero percentage.
/// </summary>
public partial class SaSalesRepAttainment : SaAnalysisPageBase
{
    protected string? SalesmanCode { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> SalesmanOptions { get; set; } = [];
    protected IReadOnlyList<SaSalesRepAttainmentRow> Rows { get; set; } = [];

    protected override async Task OnAnalysisInitializedAsync()
    {
        SalesmanOptions = await CustLookups.ListSalesRepsForAssignmentAsync();
    }

    protected async Task RunAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            SyncQuery();
            var result = await Analysis.GetSalesRepAttainmentAsync(Query);
            if (!result.Succeeded)
            {
                LoadError = result.Message ?? "Unable to run the attainment inquiry.";
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
        Query.SalesmanCode = SalesmanCode;
    }

    /// <summary>Totals for the footer chips. N/A when the aggregated target is zero.</summary>
    protected decimal TotalActual => Rows.Sum(x => x.ActualAmount);

    protected decimal TotalTarget => Rows.Sum(x => x.TargetAmount);

    protected decimal? TotalAttainment =>
        TotalTarget > 0m
            ? Math.Round(TotalActual / TotalTarget * 100m, 2, MidpointRounding.AwayFromZero)
            : null;

    protected string ExportUrl => BuildExportUrl("/sales/analysis/attainment/export",
    [
        new("salesmanCode", SalesmanCode)
    ]);
}
