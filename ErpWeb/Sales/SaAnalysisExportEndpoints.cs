using System.Globalization;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Sales;

/// <summary>
/// CSV export for the three Sales Analysis screens (sales-analysis Phase 1).
///
/// Each handler calls the SAME <see cref="ISaSalesAnalysisService"/> method the grid calls, with the
/// same query object, so a downloaded file can never disagree with what is on screen (R9). The
/// permission check also lives in the service, so an export can never widen access (R7).
/// </summary>
public static class SaAnalysisExportEndpoints
{
    public static IEndpointRouteBuilder MapSaAnalysisExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sales/analysis/summary/export", ExportSummaryAsync).RequireAuthorization();
        endpoints.MapGet("/sales/analysis/attainment/export", ExportAttainmentAsync).RequireAuthorization();
        endpoints.MapGet("/sales/analysis/qt-conversion/export", ExportQtConversionAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportSummaryAsync(
        [AsParameters] SaSalesAnalysisQuery query,
        [FromServices] ISaSalesAnalysisService analysis,
        CancellationToken cancellationToken)
    {
        var result = await analysis.GetSalesSummaryAsync(query, cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var data = result.Data!;
        var rows = new List<string[]>
        {
            new[] { "Key", "Description", "Invoice count", "Invoice total", "Gross amount", "Tax amount", "Share %" }
        };

        rows.AddRange(data.Rows.Select(x => new[]
        {
            x.Key,
            x.Description ?? string.Empty,
            x.InvoiceCount.ToString(CultureInfo.InvariantCulture),
            Money(x.InvoiceTotal),
            Money(x.GrossAmount),
            Money(x.TaxAmount),
            Percent(x.SharePercent)
        }));

        rows.Add(
        [
            "TOTAL", string.Empty,
            data.Totals.InvoiceCount.ToString(CultureInfo.InvariantCulture),
            Money(data.Totals.InvoiceTotal),
            Money(data.Totals.GrossAmount),
            Money(data.Totals.TaxAmount),
            "100"
        ]);

        // Period chips are posted INV / CN / DN over the same range. CN and DN are stored positive and
        // netted once here (R3) — the file must show the same Net Sales Amount the screen shows.
        rows.Add([]);
        rows.Add(["Period", "Count", "Amount", string.Empty, string.Empty, string.Empty, string.Empty]);
        rows.Add(["Posted invoices", data.Totals.InvoiceCount.ToString(CultureInfo.InvariantCulture), Money(data.Totals.InvoiceTotal), string.Empty, string.Empty, string.Empty, string.Empty]);
        rows.Add(["Posted credit notes", data.Totals.CreditNoteCount.ToString(CultureInfo.InvariantCulture), Money(data.Totals.CreditNoteTotal), string.Empty, string.Empty, string.Empty, string.Empty]);
        rows.Add(["Posted debit notes", data.Totals.DebitNoteCount.ToString(CultureInfo.InvariantCulture), Money(data.Totals.DebitNoteTotal), string.Empty, string.Empty, string.Empty, string.Empty]);
        rows.Add(["Net sales amount", string.Empty, Money(data.Totals.NetSalesAmount), string.Empty, string.Empty, string.Empty, string.Empty]);

        return Csv(rows, "SaSalesSummary");
    }

    private static async Task<IResult> ExportAttainmentAsync(
        [AsParameters] SaSalesAnalysisQuery query,
        [FromServices] ISaSalesAnalysisService analysis,
        CancellationToken cancellationToken)
    {
        var result = await analysis.GetSalesRepAttainmentAsync(query, cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var rows = new List<string[]>
        {
            new[] { "Sales rep", "Name", "Actual", "Target", "Attainment %", "Months with target" }
        };

        rows.AddRange((result.Data ?? []).Select(x => new[]
        {
            x.Code,
            x.Name ?? string.Empty,
            Money(x.ActualAmount),
            Money(x.TargetAmount),
            // R2/M2: no target means N/A — never a divide-by-zero "0".
            x.AttainmentPercent is decimal percent ? Percent(percent) : "N/A",
            x.MonthsWithTarget.ToString(CultureInfo.InvariantCulture)
        }));

        return Csv(rows, "SaSalesRepAttainment");
    }

    private static async Task<IResult> ExportQtConversionAsync(
        [AsParameters] SaSalesAnalysisQuery query,
        [FromServices] ISaSalesAnalysisService analysis,
        CancellationToken cancellationToken)
    {
        var result = await analysis.GetQtConversionAsync(query, cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var data = result.Data!;
        var rows = new List<string[]>
        {
            new[] { "Bucket", "Count", "Amount" }
        };

        rows.Add(Bucket("Total", data.Total));
        rows.Add(Bucket("Open", data.Open));
        rows.Add(Bucket("Won", data.Won));
        rows.Add(Bucket("Lost", data.Lost));
        rows.Add(Bucket("Expired", data.Expired));
        rows.Add(Bucket("Cancelled", data.Cancelled));
        rows.Add(["Win rate %", string.Empty, data.WinRatePercent is decimal rate ? Percent(rate) : "N/A"]);

        rows.Add([]);
        rows.Add(["Lost reason", "Count", "Amount"]);
        rows.AddRange(data.LostReasons.Select(x => new[]
        {
            x.Reason,
            x.Count.ToString(CultureInfo.InvariantCulture),
            Money(x.Amount)
        }));

        if (data.BySalesRep.Count > 0)
        {
            rows.Add([]);
            rows.Add(["Sales rep", "Total count", "Total amount", "Won count", "Won amount", "Lost count", "Lost amount", "Win rate %"]);
            rows.AddRange(data.BySalesRep.Select(x => new[]
            {
                x.SalesRep,
                x.Total.Count.ToString(CultureInfo.InvariantCulture),
                Money(x.Total.Amount),
                x.Won.Count.ToString(CultureInfo.InvariantCulture),
                Money(x.Won.Amount),
                x.Lost.Count.ToString(CultureInfo.InvariantCulture),
                Money(x.Lost.Amount),
                x.WinRatePercent is decimal repRate ? Percent(repRate) : "N/A"
            }));
        }

        return Csv(rows, "SaQtConversion");
    }

    private static string[] Bucket(string name, SaQtConversionBucket bucket) =>
        [name, bucket.Count.ToString(CultureInfo.InvariantCulture), Money(bucket.Amount)];

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static IResult ToProblem(IvMasterErrorCode code, string? message) =>
        code switch
        {
            IvMasterErrorCode.AccessDenied => Results.Forbid(),
            IvMasterErrorCode.InvalidScope =>
                Results.BadRequest(message ?? "Invalid company context."),
            _ => Results.BadRequest(message ?? "Export failed.")
        };

    private static IResult Csv(List<string[]> rows, string filePrefix)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(Escape)));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
        return Results.File(
            bytes,
            "text/csv",
            $"{filePrefix}_{DateTime.Now:yyMMddHHmmss}.csv");
    }

    /// <summary>
    /// RFC 4180 escaping: quote a field when it holds a comma, quote or newline, and double any embedded
    /// quote. Grouping keys are customer-controlled text, so this is not optional.
    /// </summary>
    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
