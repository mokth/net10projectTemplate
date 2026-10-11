using System.Globalization;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.Purchase;

public static class PoProcurementKpiEndpoints
{
    private const int MaxExportRows = 50_000;

    public static IEndpointRouteBuilder MapPoProcurementKpiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/purchase/inquiry/kpi/export", ExportSummaryAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/kpi/suppliers/export", ExportSupplierDeliveryAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportSummaryAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoProcurementKpiService kpis,
        CancellationToken cancellationToken)
    {
        var result = await kpis.GetSummaryAsync(query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var x = result.Data;
        var rows = new List<string[]>
        {
            new[] { "Metric", "Value", "Definition / caveat", "Scope", "Period", "As of" },
            new[] { "Open PO lines", Number(x.OutstandingPoLines), "Latest live revision with positive persisted balance; value omitted across mixed currencies.",
                "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Overdue PO lines", Number(x.OverduePoLines), "Open lines with current-revision ETA before as-of; missing ETA excluded.",
                "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Supplier delivery on-time", x.OnTimePercentage is decimal pct ? $"{pct.ToString("0.00", CultureInfo.InvariantCulture)}%" : "Unavailable",
                "On-time/early completed lines ÷ completed lines; partial lines excluded; current recorded ETA basis.",
                "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Completed delivery sample", x.CompletedDeliveryLines is int completed && x.OnTimeCompletedLines is int onTime
                ? $"{onTime.ToString(CultureInfo.InvariantCulture)}/{completed.ToString(CultureInfo.InvariantCulture)}" : "Unavailable",
                "Numerator / denominator for the on-time metric.", "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Missing ETA lines", Number(x.MissingEtaLines), "Current latest-revision lines without ETA; not counted as overdue or completed OTIF.",
                "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Partial delivery lines", Number(x.PartialDeliveryLines), "Partial lines are excluded from the completed OTIF denominator.",
                "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Received not invoiced", Number(x.ReceivedNotInvoicedLines), "Inquiry count only; not AP/GL liability or inventory valuation.",
                "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Latest posted invoice net unit price", Price(x.LatestNetUnitPrice, x.PriceCurrency, x.PriceUom, x.LatestPriceDate),
                "Selected item and currency; same purchase UOM is required.", "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Previous posted invoice net unit price", Price(x.PreviousNetUnitPrice, x.PriceCurrency, x.PriceUom, x.PreviousPriceDate),
                x.PriceUnavailableReason ?? "Compared with the latest line; no savings claim.", "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) },
            new[] { "Net posted invoice spend", "Unavailable", "Controlled invoice/CN/DN, rollback, tax, exchange-rate and self-billed reconciliation required.",
                "Authorized branch", Period(x.DateFrom, x.DateTo), Date(x.AsOfDate) }
        };
        return Csv(rows, "PoProcurementKpi");
    }

    private static async Task<IResult> ExportSupplierDeliveryAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("Features:ProcurementKpi"))
        {
            return Results.Forbid();
        }

        query.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        query.Skip = 0;
        query.Take = MaxExportRows;
        var result = await inquiry.GetDeliveryPerformanceSummaryAsync(MenuCodes.PurchaseKpi, query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        if (result.Data.TotalCount > MaxExportRows)
        {
            return Results.BadRequest($"Export is limited to {MaxExportRows:N0} suppliers. Refine the filters.");
        }

        var rows = new List<string[]>
        {
            new[] { "Supplier", "Name", "Purchase orders", "PO lines", "Completed", "Partial", "Ordered qty", "Received qty", "On-time lines", "Late lines", "On-time %", "Avg days late", "Max days late" }
        };
        rows.AddRange(result.Data.Rows.Select(x => new[]
        {
            x.SuppCode ?? string.Empty,
            x.SuppName ?? string.Empty,
            x.PurchaseOrderCount.ToString(CultureInfo.InvariantCulture),
            x.PoLinesEvaluated.ToString(CultureInfo.InvariantCulture),
            x.CompletedLines.ToString(CultureInfo.InvariantCulture),
            x.PartialLines.ToString(CultureInfo.InvariantCulture),
            Qty(x.OrderedQty),
            Qty(x.ReceivedQty),
            x.OnTimeLines.ToString(CultureInfo.InvariantCulture),
            x.LateLines.ToString(CultureInfo.InvariantCulture),
            x.OnTimePct is decimal pct ? pct.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty,
            x.AvgDaysLate is decimal avg ? avg.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty,
            x.MaxDaysLate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
        }));
        return Csv(rows, "PoProcurementKpiSuppliers");
    }

    private static IResult ToProblem(IvMasterErrorCode code, string? message) => code switch
    {
        IvMasterErrorCode.AccessDenied => Results.Forbid(),
        IvMasterErrorCode.InvalidScope => Results.BadRequest(message ?? "Invalid company or branch context."),
        _ => Results.BadRequest(message ?? "KPI export failed.")
    };

    private static IResult Csv(List<string[]> rows, string name)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(Escape)));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
        return Results.File(bytes, "text/csv", $"{name}_{DateTime.UtcNow:yyMMddHHmmss}.csv");
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && "=+-@".Contains(text[0]))
        {
            text = $"'{text}";
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string Number(int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "Unavailable";
    private static string Qty(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Price(decimal? value, string? currency, string? uom, DateTime? date) =>
        value is decimal price && !string.IsNullOrWhiteSpace(currency) && !string.IsNullOrWhiteSpace(uom)
            ? $"{price.ToString("0.####", CultureInfo.InvariantCulture)} {currency} / {uom} ({(date is DateTime d ? Date(d) : "date unavailable")})"
            : "Unavailable";
    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Period(DateTime? from, DateTime? to) =>
        $"{(from is DateTime f ? Date(f) : "—")} to {(to is DateTime t ? Date(t) : "—")}";
}
