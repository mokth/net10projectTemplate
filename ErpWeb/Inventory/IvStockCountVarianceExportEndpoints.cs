using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

/// <summary>
/// xlsx export for the stock-count variance report. The endpoint checks EXPORT itself and then delegates
/// to <see cref="IIvStockCountService.ExportVarianceRowsAsync"/>, so the export inherits the service's
/// tenant resolution, its ACCESS check on the variance menu and its D17 arithmetic. Company/branch are
/// NEVER accepted from the query string, and the export uses the SAME applied filter object as the grid
/// (never its current page) (D19).
/// </summary>
public static class IvStockCountVarianceExportEndpoints
{
    public static IEndpointRouteBuilder MapIvStockCountVarianceExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/stock-count-variance/export", ExportAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] IIvStockCountService stockCounts,
        [FromServices] IAccessRightService accessRights,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? iCode,
        [FromQuery] string? whCode,
        [FromQuery] string? iClassCode,
        [FromQuery] string? statuses,
        [FromQuery] string? searchText,
        [FromQuery] string? sortField,
        [FromQuery] bool sortDescending = true,
        CancellationToken cancellationToken = default)
    {
        if (!await accessRights.CanAsync(
                MenuCodes.InventoryStockCountVar, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new IvStockCountVarianceQuery
        {
            DateFrom = dateFrom,
            DateTo = dateTo,
            ICode = iCode,
            WhCode = whCode,
            IClassCode = iClassCode,
            IStatuses = Split(statuses),
            SearchText = searchText,
            SortField = sortField,
            SortDescending = sortDescending,
            Skip = 0,
            Take = IvInquiryExportWorkbook.MaxExportRows
        };

        var result = await stockCounts.ExportVarianceRowsAsync(
            MenuCodes.InventoryStockCountVar, query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.ErrorMessage ?? "Export failed.");
        }

        var page = result.VariancePage ?? new IvStockCountVariancePage();
        if (page.TotalCount > IvInquiryExportWorkbook.MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {IvInquiryExportWorkbook.MaxExportRows:N0} rows. Refine the filters and try again. Matched: {page.TotalCount:N0}.");
        }

        var bytes = BuildWorkbook(page.Rows);
        var fileName = $"IvStockCountVariance_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static IReadOnlyList<string> Split(string? values) =>
        (values ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static byte[] BuildWorkbook(IReadOnlyList<IvStockCountVarianceRow> rows)
    {
        var headers = new List<string>
        {
            "Count no.", "Count date", "ADJ batch", "Stale lines", "Line",
            "Item", "Description", "Warehouse", "Bin", "Lot", "Status", "Class", "UOM",
            "System qty", "Physical qty", "Variance", "Direction",
            "Snapshot price", "Variance value", "Counted by", "Counted on",
            "Created", "User ID", "Modified date", "Modified by"
        };

        return IvInquiryExportWorkbook.Build("Count variance", headers, rows, row =>
        [
            IvInquiryExportWorkbook.CellText(row.CountNo),
            IvInquiryExportWorkbook.CellDate(row.CountDate),
            IvInquiryExportWorkbook.CellInt(row.PostedBatchNo),
            IvInquiryExportWorkbook.CellInt(row.PostedStaleLines),
            IvInquiryExportWorkbook.CellInt(row.LineNumber),
            IvInquiryExportWorkbook.CellText(row.ICode),
            IvInquiryExportWorkbook.CellText(row.IDesc),
            IvInquiryExportWorkbook.CellText(row.WHCode),
            IvInquiryExportWorkbook.CellText(row.LocCode),
            IvInquiryExportWorkbook.CellText(row.LotNo),
            IvInquiryExportWorkbook.CellText(row.IStatus),
            IvInquiryExportWorkbook.CellText(row.IClassCode),
            IvInquiryExportWorkbook.CellText(row.StdUom),
            IvInquiryExportWorkbook.CellNumber(row.SystemQty),
            IvInquiryExportWorkbook.CellNumber(row.PhysicalQty),
            IvInquiryExportWorkbook.CellNumber(row.Variance),
            IvInquiryExportWorkbook.CellText(row.Direction),
            IvInquiryExportWorkbook.CellNumber(row.SnapshotUnitPrice),
            IvInquiryExportWorkbook.CellNumber(row.VarianceValue),
            IvInquiryExportWorkbook.CellText(row.CountedBy),
            IvInquiryExportWorkbook.CellDate(row.CountedOn),
            IvInquiryExportWorkbook.CellDate(row.CreatedDate),
            IvInquiryExportWorkbook.CellText(row.CreatedBy),
            IvInquiryExportWorkbook.CellDate(row.ModifiedDate),
            IvInquiryExportWorkbook.CellText(row.ModifiedBy)
        ]);
    }
}
