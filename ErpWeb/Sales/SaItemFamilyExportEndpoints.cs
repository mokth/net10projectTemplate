using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Sales;

/// <summary>
/// Workbook downloads for the sales item family. Same shape as <c>SaMasterRefExportEndpoints</c>:
/// the service enforces EXPORT (and VIEW_PRICE for the price columns) — the endpoint never widens access.
/// </summary>
public static class SaItemFamilyExportEndpoints
{
    public static IEndpointRouteBuilder MapSaItemFamilyExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sales/price-groups/export", ExportCustPriceGroupsAsync).RequireAuthorization();
        endpoints.MapGet("/sales/customer-prices/export", ExportCustPricesAsync).RequireAuthorization();
        endpoints.MapGet("/sales/customer-items/export", ExportItemCustsAsync).RequireAuthorization();
        endpoints.MapGet("/sales/item-discounts/export", ExportDisGroupItemsAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportCustPriceGroupsAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportCustPriceGroupsAsync(cancellationToken);
        return ToFile(result, "IvCustPriceGroups", SaMasterRefExportWorkbooks.BuildCustPriceGroups);
    }

    private static async Task<IResult> ExportCustPricesAsync(
        [FromServices] ISaSalesRefService masters,
        [FromQuery] string? custPriceCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(custPriceCode))
        {
            return Results.BadRequest("A price group code is required.");
        }

        var result = await masters.ExportCustPricesAsync(custPriceCode, cancellationToken);
        if (!result.Succeeded)
        {
            return ToHttpError(result);
        }

        var rows = result.Data ?? [];
        if (rows.Count > SaSalesRefService.MaxExportRows)
        {
            return Results.BadRequest($"Export is limited to {SaSalesRefService.MaxExportRows:N0} rows.");
        }

        return Results.File(
            SaMasterRefExportWorkbooks.BuildCustPrices(custPriceCode.Trim().ToUpperInvariant(), rows),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"IvCustPrices_{DateTime.Now:yyMMddHHmmss}.xlsx");
    }

    private static async Task<IResult> ExportItemCustsAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportItemCustsAsync(cancellationToken);
        return ToFile(result, "SaItemCust", SaMasterRefExportWorkbooks.BuildItemCusts);
    }

    private static async Task<IResult> ExportDisGroupItemsAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportDisGroupItemsAsync(cancellationToken);
        return ToFile(result, "SaDisGroupItem", SaMasterRefExportWorkbooks.BuildDisGroupItems);
    }

    private static IResult ToHttpError<T>(IvMasterOperationResult<T> result) =>
        result.ErrorCode switch
        {
            IvMasterErrorCode.AccessDenied => Results.Forbid(),
            IvMasterErrorCode.InvalidScope => Results.BadRequest(result.Message ?? "Invalid company context."),
            _ => Results.BadRequest(result.Message ?? "Export failed.")
        };

    private static IResult ToFile<TRow>(
        IvMasterOperationResult<IReadOnlyList<TRow>> result,
        string filePrefix,
        Func<IReadOnlyList<TRow>, byte[]> build)
    {
        if (!result.Succeeded)
        {
            return ToHttpError(result);
        }

        var rows = result.Data ?? [];
        if (rows.Count > SaSalesRefService.MaxExportRows)
        {
            return Results.BadRequest($"Export is limited to {SaSalesRefService.MaxExportRows:N0} rows.");
        }

        var bytes = build(rows);
        var fileName = $"{filePrefix}_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }
}
