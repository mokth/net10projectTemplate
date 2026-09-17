using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Sales.Analysis;

/// <summary>
/// Shared plumbing for the read-only Sales Analysis screens (sales-analysis Phase 1).
///
/// Every screen defaults to the current calendar month, keeps its filter state in the query object and
/// exports through a server endpoint that calls the <b>same</b> <see cref="ISaSalesAnalysisService"/>
/// method as the grid — so the exported file and the screen can never disagree (R9).
/// </summary>
public abstract class SaAnalysisPageBase : PageBase
{
    [Inject] protected ISaSalesAnalysisService Analysis { get; set; } = default!;
    [Inject] protected ISaCustLookupService CustLookups { get; set; } = default!;
    [Inject] protected ICurrentDateService Dates { get; set; } = default!;

    protected DateTime? DateFrom { get; set; }
    protected DateTime? DateTo { get; set; }

    protected bool IsLoading { get; set; }
    protected string? LoadError { get; set; }

    /// <summary>False until the operator runs the inquiry, so an empty grid is never mistaken for "no sales".</summary>
    protected bool HasRun { get; set; }

    /// <summary>
    /// The one query the grid renders from. It is also what the export URL is built from, which is how
    /// R9 is enforced structurally rather than by convention.
    /// </summary>
    protected SaSalesAnalysisQuery Query { get; } = new();

    protected override async Task OnPageInitializedAsync()
    {
        var today = Dates.Now.Date;
        DateFrom ??= new DateTime(today.Year, today.Month, 1);
        DateTo ??= today;
        SyncQueryDates();

        await OnAnalysisInitializedAsync();
    }

    protected virtual Task OnAnalysisInitializedAsync() => Task.CompletedTask;

    protected void SyncQueryDates()
    {
        Query.DateFrom = DateFrom;
        Query.DateTo = DateTo;
    }

    /// <summary>ISO date format matches what the export endpoints bind from the query string.</summary>
    protected string? DateFromIso => DateFrom?.ToString("yyyy-MM-dd");

    protected string? DateToIso => DateTo?.ToString("yyyy-MM-dd");

    protected void DismissError() => LoadError = null;

    /// <summary>
    /// Builds the export URL from the SAME query object the grid uses, so no filter can be dropped or
    /// re-interpreted on the way to the file.
    /// </summary>
    protected string BuildExportUrl(string path, IEnumerable<KeyValuePair<string, string?>> extras)
    {
        SyncQueryDates();

        var parameters = new Dictionary<string, string?>();
        AddIfSet(parameters, "dateFrom", DateFromIso);
        AddIfSet(parameters, "dateTo", DateToIso);

        foreach (var pair in extras)
        {
            AddIfSet(parameters, pair.Key, pair.Value);
        }

        return QueryHelpers.AddQueryString(path, parameters);
    }

    private static void AddIfSet(Dictionary<string, string?> parameters, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters[key] = value;
        }
    }

    /// <summary>Text for a null/blank grouping key or sales-rep code, matching the service's own label.</summary>
    protected static string OrBlank(string? value) => string.IsNullOrWhiteSpace(value) ? "(blank)" : value!;

    protected static string PercentText(decimal? value) => value is decimal v ? $"{v:0.##}%" : "N/A";

    protected static string MoneyText(decimal value) => value.ToString("N2");
}
