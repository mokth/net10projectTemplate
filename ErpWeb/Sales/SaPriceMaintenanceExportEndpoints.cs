using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Sales;

public static class SaPriceMaintenanceExportEndpoints
{
    public static IEndpointRouteBuilder MapSaPriceMaintenanceExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/sales/pricing/review/export",
            ExportAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [AsParameters] SaPriceReviewQuery query,
        [FromServices] ISaPriceMaintenanceService maintenance,
        CancellationToken cancellationToken,
        [FromQuery] string? adjustmentMethod,
        [FromQuery] decimal adjustmentValue,
        [FromQuery] int decimalPlaces = 2,
        [FromQuery] string? roundingMode = null)
    {
        var result = await maintenance.BuildReviewWorkbookAsync(
            new SaPriceReviewExportRequest
            {
                Query = query,
                AdjustmentMethod = adjustmentMethod ?? SaPriceAdjustmentMethods.SetPrice,
                AdjustmentValue = adjustmentValue,
                DecimalPlaces = decimalPlaces,
                RoundingMode = roundingMode ?? SaPriceRoundingModes.Normal
            },
            cancellationToken);

        if (!result.Succeeded)
        {
            return result.ErrorCode switch
            {
                IvMasterErrorCode.AccessDenied => Results.Forbid(),
                _ => Results.BadRequest(result.Message ?? "Price review export failed.")
            };
        }

        return Results.File(
            result.Data!,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SaPriceReview_{DateTime.UtcNow:yyMMddHHmmss}.xlsx");
    }
}
