using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Purchase;

public static class PoMasterRefExportEndpoints
{
    public static IEndpointRouteBuilder MapPoMasterRefExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/purchase/buyers/export", ExportBuyersAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/buying-terms/export", ExportBuyingTermsAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/categories/export", ExportCategoriesAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/authorised/export", ExportAuthorisedAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/items/export", ExportPurItemsAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportBuyersAsync(
        [FromServices] IPoMasterRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportBuyersAsync(cancellationToken);
        return ToFile(result, "PoBuyers", PoMasterRefExportWorkbooks.BuildBuyers);
    }

    private static async Task<IResult> ExportBuyingTermsAsync(
        [FromServices] IPoMasterRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportBuyingTermsAsync(cancellationToken);
        return ToFile(result, "PoBuyingTerms", PoMasterRefExportWorkbooks.BuildBuyingTerms);
    }

    private static async Task<IResult> ExportCategoriesAsync(
        [FromServices] IPoMasterRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportCategoriesAsync(cancellationToken);
        return ToFile(result, "PoCategories", PoMasterRefExportWorkbooks.BuildCategories);
    }

    private static async Task<IResult> ExportAuthorisedAsync(
        [FromServices] IPoMasterRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportAuthorisedAsync(cancellationToken);
        return ToFile(result, "PoAuthorised", PoMasterRefExportWorkbooks.BuildAuthorised);
    }

    private static async Task<IResult> ExportPurItemsAsync(
        [FromServices] IPoMasterRefService masters,
        CancellationToken cancellationToken)
    {
        var result = await masters.ExportPurItemsAsync(cancellationToken);
        return ToFile(result, "PoPurItems", PoMasterRefExportWorkbooks.BuildPurItems);
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
        if (rows.Count > PoMasterRefService.MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {PoMasterRefService.MaxExportRows:N0} rows. Refine filters and try again.");
        }

        var bytes = build(rows);
        var fileName = $"{filePrefix}_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }
}
