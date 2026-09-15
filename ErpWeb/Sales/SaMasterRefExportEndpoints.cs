using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Sales;

/// <summary>
/// Server-side export endpoints for the flat sales reference masters. The EXPORT permission is
/// checked inside the service (the server execution point), not by hiding the toolbar button (D-15).
/// </summary>
public static class SaMasterRefExportEndpoints
{
    public static IEndpointRouteBuilder MapSaMasterRefExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sales/customer-sub-groups/export", ExportCustSubGroupsAsync).RequireAuthorization();
        endpoints.MapGet("/sales/ship-vias/export", ExportShipViasAsync).RequireAuthorization();
        endpoints.MapGet("/sales/so-types/export", ExportSoTypesAsync).RequireAuthorization();
        endpoints.MapGet("/sales/comments/export", ExportCommentsAsync).RequireAuthorization();
        endpoints.MapGet("/sales/shipping-lead-time/export", ExportShippingLeadTimesAsync).RequireAuthorization();
        endpoints.MapGet("/sales/lmw/export", ExportLmwsAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportCustSubGroupsAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportCustSubGroupsAsync(cancellationToken);
        return ToFile(result, "SaCustSubGroups", SaMasterRefExportWorkbooks.BuildCustSubGroups);
    }

    private static async Task<IResult> ExportShipViasAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportShipViasAsync(cancellationToken);
        return ToFile(result, "SaShipVias", SaMasterRefExportWorkbooks.BuildShipVias);
    }

    private static async Task<IResult> ExportSoTypesAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportSoTypesAsync(cancellationToken);
        return ToFile(result, "SaSOTypes", SaMasterRefExportWorkbooks.BuildSoTypes);
    }

    private static async Task<IResult> ExportCommentsAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportCommentsAsync(cancellationToken);
        return ToFile(result, "SaComments", SaMasterRefExportWorkbooks.BuildComments);
    }

    private static async Task<IResult> ExportShippingLeadTimesAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportShippingLeadTimesAsync(cancellationToken);
        return ToFile(result, "SaShippingLeadTimes", SaMasterRefExportWorkbooks.BuildShippingLeadTimes);
    }

    private static async Task<IResult> ExportLmwsAsync(
        [FromServices] ISaSalesRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportLmwsAsync(cancellationToken);
        return ToFile(result, "SaLMW", SaMasterRefExportWorkbooks.BuildLmws);
    }

    private static IResult ToFile<TRow>(
        IvMasterOperationResult<IReadOnlyList<TRow>> result,
        string filePrefix,
        Func<IReadOnlyList<TRow>, byte[]> build)
    {
        if (!result.Succeeded)
        {
            return result.ErrorCode switch
            {
                IvMasterErrorCode.AccessDenied => Results.Forbid(),
                IvMasterErrorCode.InvalidScope => Results.BadRequest(result.Message ?? "Invalid company context."),
                _ => Results.BadRequest(result.Message ?? "Export failed.")
            };
        }

        var rows = result.Data ?? [];
        if (rows.Count > SaSalesRefService.MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {SaSalesRefService.MaxExportRows:N0} rows.");
        }

        var bytes = build(rows);
        var fileName = $"{filePrefix}_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }
}
