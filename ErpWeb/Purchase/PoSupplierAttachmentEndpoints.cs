using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Purchase;

public static class PoSupplierAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapPoSupplierAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Catch-all {*suppCode} must be the final path segment (codes contain '/').
        var group = endpoints.MapGroup("/purchase/suppliers")
            .RequireAuthorization();

        group.MapGet("/attachments/{*suppCode}", ListAsync);
        group.MapPost("/attachments/{*suppCode}", UploadAsync);
        group.MapGet("/attachment/{*suppCode}", DownloadAsync);
        group.MapDelete("/attachment/{*suppCode}", DeleteAsync);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string suppCode,
        [FromServices] IPoSupplierAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        var code = DecodeSuppCode(suppCode);
        var result = await attachments.ListAsync(code, cancellationToken);
        return ToHttpResult(result);
    }

    private static async Task<IResult> UploadAsync(
        string suppCode,
        HttpRequest request,
        [FromServices] IPoSupplierAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        var code = DecodeSuppCode(suppCode);
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
            code,
            file.FileName,
            file.ContentType,
            stream,
            file.Length,
            cancellationToken);
        return ToHttpResult(result);
    }

    private static async Task<IResult> DownloadAsync(
        string suppCode,
        [FromQuery] string docName,
        [FromServices] IPoSupplierAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        var code = DecodeSuppCode(suppCode);
        var name = Uri.UnescapeDataString(docName ?? string.Empty);
        var result = await attachments.DownloadAsync(code, name, cancellationToken);
        if (!result.Succeeded || result.Data.Stream is null)
        {
            return ToHttpResult(result);
        }

        var (stream, fileName, contentType) = result.Data;
        return Results.File(stream, contentType, fileName);
    }

    private static async Task<IResult> DeleteAsync(
        string suppCode,
        [FromQuery] string docName,
        [FromServices] IPoSupplierAttachmentService attachments,
        CancellationToken cancellationToken)
    {
        var code = DecodeSuppCode(suppCode);
        var name = Uri.UnescapeDataString(docName ?? string.Empty);
        var result = await attachments.DeleteAsync(code, name, cancellationToken);
        return ToHttpResult(result);
    }

    private static string DecodeSuppCode(string? suppCode)
    {
        var raw = (suppCode ?? string.Empty).Trim().TrimEnd('/');
        return Uri.UnescapeDataString(raw);
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
