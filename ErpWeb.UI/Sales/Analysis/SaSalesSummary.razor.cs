using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;

namespace ErpWeb.UI.Sales.Analysis;

/// <summary>
/// Sales Summary (sales-analysis Phase 1) — period sales by one dimension from POSTED invoices, plus
/// the posted INV / CN / DN period chips. Read-only: nothing here changes a document.
/// </summary>
public partial class SaSalesSummary : SaAnalysisPageBase
{
    protected SaSalesSummaryDimension Dimension { get; set; } = SaSalesSummaryDimension.Salesman;

    protected string? SalesmanCode { get; set; }
    protected string? CustCode { get; set; }
    protected string? CustSource { get; set; }
    protected string? CustType { get; set; }
    protected string? CustGroupCode { get; set; }
    protected string? AreaCode { get; set; }
    protected string? IndustryCode { get; set; }
    protected string? ChannelCode { get; set; }

    /// <summary>
    /// Optional branch restriction. It applies to this screen (and the chips), never to attainment —
    /// targets carry no branch, so a branch filter must not change a rep's target (R2).
    /// </summary>
    protected string? BranchCode { get; set; }

    protected SaSalesSummaryResult? Result { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> SalesmanOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CustomerOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> SourceOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> TypeOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> GroupOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> AreaOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> IndustryOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> ChannelOptions { get; set; } = [];

    protected sealed record SaSalesSummaryDimensionOption(SaSalesSummaryDimension Value, string Label);

    protected static IReadOnlyList<SaSalesSummaryDimensionOption> Dimensions { get; } =
    [
        new(SaSalesSummaryDimension.Salesman, "Salesman"),
        new(SaSalesSummaryDimension.Customer, "Customer"),
        new(SaSalesSummaryDimension.Source, "Customer Source (current)"),
        new(SaSalesSummaryDimension.CustType, "Customer type"),
        new(SaSalesSummaryDimension.CustGroup, "Customer group"),
        new(SaSalesSummaryDimension.Area, "Area"),
        new(SaSalesSummaryDimension.Industry, "Industry"),
        new(SaSalesSummaryDimension.Channel, "Channel")
    ];

    protected static string DimensionLabel(SaSalesSummaryDimension dimension) =>
        Dimensions.FirstOrDefault(x => x.Value == dimension)?.Label ?? dimension.ToString();

    protected override async Task OnAnalysisInitializedAsync()
    {
        SalesmanOptions = await CustLookups.ListSalesRepsForAssignmentAsync();
        CustomerOptions = await CustLookups.SearchCustomersAsync(string.Empty, 200);
        SourceOptions = await CustLookups.ListSourcesForAssignmentAsync();
        TypeOptions = await CustLookups.ListTypesForAssignmentAsync();
        GroupOptions = await CustLookups.ListGroupsForAssignmentAsync();
        AreaOptions = await CustLookups.ListAreasForAssignmentAsync();
        IndustryOptions = await CustLookups.ListIndustriesForAssignmentAsync();
        ChannelOptions = await CustLookups.ListChannelsForAssignmentAsync();
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
            var result = await Analysis.GetSalesSummaryAsync(Query);
            if (!result.Succeeded)
            {
                LoadError = result.Message ?? "Unable to run the sales summary.";
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
        Query.Dimension = Dimension;
        Query.SalesmanCode = SalesmanCode;
        Query.CustCode = CustCode;
        Query.CustSource = CustSource;
        Query.CustType = CustType;
        Query.CustGroupCode = CustGroupCode;
        Query.AreaCode = AreaCode;
        Query.IndustryCode = IndustryCode;
        Query.ChannelCode = ChannelCode;
        Query.BranchCode = BranchCode;
    }

    protected string ExportUrl => BuildExportUrl("/sales/analysis/summary/export",
    [
        new("dimension", Dimension.ToString()),
        new("salesmanCode", SalesmanCode),
        new("custCode", CustCode),
        new("custSource", CustSource),
        new("custType", CustType),
        new("custGroupCode", CustGroupCode),
        new("areaCode", AreaCode),
        new("industryCode", IndustryCode),
        new("channelCode", ChannelCode),
        new("branchCode", BranchCode)
    ]);
}
