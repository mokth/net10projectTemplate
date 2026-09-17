using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;

namespace ErpWeb.UI.Sales.Analysis;

/// <summary>
/// Quotation Conversion (sales-analysis Phase 1) — win/loss buckets, win rate and lost-reason rollup
/// for CURRENT quotation revisions. Won is the state the converter writes
/// (<c>CLOSED</c> + <c>CONVERTED</c>); amounts are the header total, never a re-totalled line set.
/// </summary>
public partial class SaQtConversionAnalysis : SaAnalysisPageBase
{
    protected string? SalesmanCode { get; set; }
    protected bool GroupBySalesRep { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> SalesmanOptions { get; set; } = [];
    protected SaQtConversionResult? Result { get; set; }

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
            var result = await Analysis.GetQtConversionAsync(Query);
            if (!result.Succeeded)
            {
                LoadError = result.Message ?? "Unable to run the quotation conversion inquiry.";
                Result = null;
                return;
            }

            Result = result.Data;
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
        Query.GroupQtBySalesRep = GroupBySalesRep;
    }

    protected string ExportUrl => BuildExportUrl("/sales/analysis/qt-conversion/export",
    [
        new("salesmanCode", SalesmanCode),
        new("groupQtBySalesRep", GroupBySalesRep ? "true" : null)
    ]);
}
