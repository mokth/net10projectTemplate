using System.Globalization;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Sales;

public static class SaPriceChangeHistoryExportEndpoints
{
    public static IEndpointRouteBuilder MapSaPriceChangeHistoryExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/sales/inquiry/price-change-history/export",
            ExportAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [AsParameters] SaPriceChangeHistoryQuery query,
        [FromServices] ISaPriceChangeHistoryService history,
        CancellationToken cancellationToken)
    {
        var result = await history.ExportAsync(query, cancellationToken);
        if (!result.Succeeded)
        {
            return result.ErrorCode switch
            {
                IvMasterErrorCode.AccessDenied => Results.Forbid(),
                _ => Results.BadRequest(result.Message ?? "Price change history export failed.")
            };
        }

        var rows = new List<string[]>
        {
            new[]
            {
                "Batch", "Changed at", "Effective date", "Origin", "Target", "Change kind",
                "Item", "Description", "UOM", "Customer", "Customer name", "Price list",
                "Old price", "New price", "Difference", "Difference %", "Reason", "Changed by"
            }
        };
        rows.AddRange(result.Data!.Select(x =>
        new[]
        {
            x.BatchReference,
            x.ChangedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            x.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            x.Origin,
            x.TargetType,
            x.ChangeKind,
            x.ItemCode,
            x.ItemDescription ?? string.Empty,
            x.Uom ?? string.Empty,
            x.CustCode ?? string.Empty,
            x.CustomerName ?? string.Empty,
            x.CustPriceCode ?? string.Empty,
            Money(x.OldPrice),
            Money(x.NewPrice),
            Money(x.DifferenceAmount),
            Percent(x.DifferencePercent),
            x.Reason ?? string.Empty,
            x.ChangedBy
        }));

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(Escape)));
        }

        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(builder.ToString()))
            .ToArray();
        return Results.File(
            bytes,
            "text/csv",
            $"SaPriceChangeHistory_{DateTime.Now:yyMMddHHmmss}.csv");
    }

    private static string Money(decimal? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Percent(decimal? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
