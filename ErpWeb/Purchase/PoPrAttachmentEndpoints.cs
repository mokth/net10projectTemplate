using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Purchase;

public static class PoPrAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapPoPrAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/purchase/requisitions/attachments")
            .RequireAuthorization();

        group.MapGet("/{docId}", ListAsync);
        group.MapPost("/{docId}", UploadAsync);
        group.MapGet("/{docId}/file", DownloadAsync);
        group.MapDelete("/{docId}/file", DeleteAsync);
        group.MapPost("/cleanup-expired", CleanupExpiredAsync);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string docId,
        [FromServices] IPoPrAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        var result = await attachments.ListAsync(Uri.UnescapeDataString(docId ?? string.Empty), cancellationToken);
        return ToHttpResult(result);
    }

    private static async Task<IResult> UploadAsync(
        string docId,
        HttpRequest request,
        [FromServices] IPoPrAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest("Multipart form data is required.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest("File is required.");
        }

        await using var stream = file.OpenReadStream();
        var result = await attachments.UploadAsync(
            Uri.UnescapeDataString(docId ?? string.Empty),
            file.FileName,
            file.ContentType,
            stream,
            file.Length,
            cancellationToken);
        return ToHttpResult(result);
    }

    private static async Task<IResult> DownloadAsync(
        string docId,
        [FromQuery] string docName,
        [FromServices] IPoPrAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        var result = await attachments.DownloadAsync(
            Uri.UnescapeDataString(docId ?? string.Empty),
            Uri.UnescapeDataString(docName ?? string.Empty),
            cancellationToken);
        if (!result.Succeeded || result.Data.Stream is null)
        {
            return ToHttpResult(result);
        }

        var (stream, fileName, contentType) = result.Data;
        return Results.File(stream, contentType, fileName);
    }

    private static async Task<IResult> DeleteAsync(
        string docId,
        [FromQuery] string docName,
        [FromServices] IPoPrAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        var result = await attachments.DeleteAsync(
            Uri.UnescapeDataString(docId ?? string.Empty),
            Uri.UnescapeDataString(docName ?? string.Empty),
            cancellationToken);
        return ToHttpResult(result);
    }

    private static async Task<IResult> CleanupExpiredAsync(
        [FromServices] IPoPrAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        await attachments.CleanupExpiredDraftsAsync(cancellationToken);
        return Results.Ok(new { message = "Expired PR draft attachments cleaned." });
    }

    private static IResult ToHttpResult<T>(IvMasterOperationResult<T> result)
    {
        if (result.Succeeded)
        {
            return Results.Ok(result.Data);
        }

        return result.ErrorCode switch
        {
            IvMasterErrorCode.AccessDenied => Results.Forbid(),
            IvMasterErrorCode.NotFound => Results.NotFound(new { message = result.Message }),
            IvMasterErrorCode.DuplicateKey => Results.Conflict(new { message = result.Message, errors = result.ValidationErrors }),
            IvMasterErrorCode.InvalidScope => Results.BadRequest(new { message = result.Message }),
            _ => Results.BadRequest(new { message = result.Message, errors = result.ValidationErrors })
        };
    }
}
