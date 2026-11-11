using ErpWeb.Core.Menus;
using ErpWeb.Core.Quality;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Quality;

public static class QaInquiryExportEndpoints
{
    public static IEndpointRouteBuilder MapQaInquiryExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/quality/inquiry/export", ExportAsync)
            .RequireAuthorization(ErpAuthPolicies.ErpAccess);
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] IQaCaseService cases,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? status,
        [FromQuery] string? sourceType,
        [FromQuery] string? searchText,
        [FromQuery] string? itemCode,
        [FromQuery] string? lotNo,
        [FromQuery] string? poNo,
        [FromQuery] string? workOrderNo,
        [FromQuery] string? supplier,
        [FromQuery] string? inspector,
        [FromQuery] string? reviewer,
        [FromQuery] string? actionOwner,
        [FromQuery] bool overdueOnly = false,
        CancellationToken cancellationToken = default)
    {
        var result = await cases.ExportInquiryAsync(new QaInquiryQuery
        {
            DateFrom = dateFrom,
            DateTo = dateTo,
            Status = status,
            SourceType = sourceType,
            SearchText = searchText,
            ItemCode = itemCode,
            LotNo = lotNo,
            PoNo = poNo,
            WorkOrderNo = workOrderNo,
            Supplier = supplier,
            Inspector = inspector,
            Reviewer = reviewer,
            ActionOwner = actionOwner,
            OverdueOnly = overdueOnly
        }, cancellationToken);
        if (!result.Succeeded || result.FileBytes is null || string.IsNullOrWhiteSpace(result.FileName))
        {
            return result.ErrorMessage == "Not authorized."
                ? Results.Forbid()
                : Results.BadRequest(result.ErrorMessage ?? "Export failed.");
        }

        return Results.File(result.FileBytes, "text/csv", result.FileName);
    }
}
