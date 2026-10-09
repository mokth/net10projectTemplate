using ErpWeb.Core.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

public static class IvStockMasterImageEndpoints
{
    public static IEndpointRouteBuilder MapIvStockMasterImageEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/item-image", ReadAsync)
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        [FromQuery] string? iCode,
        HttpResponse response,
        [FromServices] IIvStockMasterImageService imageService,
        CancellationToken cancellationToken)
    {
        var result = await imageService.OpenReadAsync(iCode ?? string.Empty, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            return result.ErrorCode switch
            {
                IvMasterErrorCode.AccessDenied => Results.Forbid(),
                IvMasterErrorCode.InvalidScope or IvMasterErrorCode.Validation => Results.BadRequest(),
                IvMasterErrorCode.NotFound => Results.NotFound(),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        }

        response.Headers.CacheControl = "private, no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";

        return Results.File(result.Data.Stream, result.Data.ContentType);
    }
}
