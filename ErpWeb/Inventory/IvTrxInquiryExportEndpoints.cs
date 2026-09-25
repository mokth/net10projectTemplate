using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

/// <summary>
/// xlsx export for the transaction inquiry. The endpoint checks EXPORT itself and then delegates to
/// <see cref="IIvTrxHistoryService.ExportRowsAsync"/>, so the export inherits the service's tenant
/// resolution, ACCESS check and <c>VIEW_PRICE</c> masking. Company/branch are NEVER accepted from the
/// query string, and the export uses the SAME applied filter object as the grid (never its page).
/// </summary>
public static class IvTrxInquiryExportEndpoints
{
    public const int MaxExportRows = IvInquiryExportWorkbook.MaxExportRows;

    public static IEndpointRouteBuilder MapIvTrxInquiryExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/trx-inquiry/export", ExportAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] IIvTrxHistoryService trxHistory,
        [FromServices] IAccessRightService accessRights,
        [FromQuery] string? iCode,
        [FromQuery] string? whCode,
        [FromQuery] string? locCode,
        [FromQuery] string? lotNo,
        [FromQuery] string? trxTypes,
        [FromQuery] int? batchNo,
        [FromQuery] string? refNo,
        [FromQuery] string? documentNo,
        [FromQuery] string? statuses,
        [FromQuery] DateTime? trxDateFrom,
        [FromQuery] DateTime? trxDateTo,
        [FromQuery] string? searchText,
        [FromQuery] string? sortField,
        [FromQuery] bool sortDescending = false,
        CancellationToken cancellationToken = default)
    {
        if (!await accessRights.CanAsync(MenuCodes.InventoryTrxInquiry, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new IvTrxHistoryQuery
        {
            ICode = iCode,
            WhCode = whCode,
            LocCode = locCode,
            LotNo = lotNo,
            TrxTypes = Split(trxTypes),
            BatchNo = batchNo,
            RefNo = refNo,
            DocumentNo = documentNo,
            IStatuses = Split(statuses),
            TrxDateFrom = trxDateFrom,
            TrxDateTo = trxDateTo,
            SearchText = searchText,
            SortField = sortField,
            SortDescending = sortDescending,
            Skip = 0,
            Take = MaxExportRows
        };

        var result = await trxHistory.ExportRowsAsync(MenuCodes.InventoryTrxInquiry, query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.Message ?? "Export failed.");
        }

        var page = result.Data ?? new IvTrxHistoryPage();
        if (page.TotalCount > MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {MaxExportRows:N0} rows. Refine filters and try again. Matched: {page.TotalCount:N0}.");
        }

        var bytes = BuildWorkbook(
            page.Rows,
            await accessRights.CanAsync(MenuCodes.InventoryTrxInquiry, PermissionCodes.ViewPrice, cancellationToken));
        var fileName = $"IvTrxInquiry_{DateTime.Now:yyMMddHHmmss}.xlsx";
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

    private static byte[] BuildWorkbook(IReadOnlyList<IvTrxHistoryRow> rows, bool includeValue)
    {
        var headers = new List<string>
        {
            "Date", "Type", "Batch", "Line", "Ref", "Item", "Description", "Status",
            "From warehouse", "From bin", "From lot", "To warehouse", "To bin", "To lot",
            "In", "Out", "Net", "UOM", "Reason",
            "DO", "INV", "SO", "PO", "Remarks"
        };

        // The value column is omitted ENTIRELY when the caller may not see money, never emitted blank.
        if (includeValue)
        {
            headers.Add("Est. value");
        }

        return IvInquiryExportWorkbook.Build("Movements", headers, rows, row =>
        {
            var cells = new List<DocumentFormat.OpenXml.Spreadsheet.Cell>
            {
                IvInquiryExportWorkbook.CellDateTime(row.TrxDtTime),
                IvInquiryExportWorkbook.CellText(row.TrxType),
                IvInquiryExportWorkbook.CellInt(row.BatchNo),
                IvInquiryExportWorkbook.CellInt(row.TrxLineNo),
                IvInquiryExportWorkbook.CellText(row.RefNo),
                IvInquiryExportWorkbook.CellText(row.ICode),
                IvInquiryExportWorkbook.CellText(row.IDesc),
                IvInquiryExportWorkbook.CellText(row.IStatus),
                IvInquiryExportWorkbook.CellText(row.FrWarehouse),
                IvInquiryExportWorkbook.CellText(row.FrLocation),
                IvInquiryExportWorkbook.CellText(row.FrLotNo),
                IvInquiryExportWorkbook.CellText(row.ToWarehouse),
                IvInquiryExportWorkbook.CellText(row.ToLocation),
                IvInquiryExportWorkbook.CellText(row.ToLotNo),
                IvInquiryExportWorkbook.CellNumber(row.InQty),
                IvInquiryExportWorkbook.CellNumber(row.OutQty),
                IvInquiryExportWorkbook.CellNumber(row.NetQty),
                IvInquiryExportWorkbook.CellText(row.StdUom),
                IvInquiryExportWorkbook.CellText(row.Reason),
                IvInquiryExportWorkbook.CellText(row.DoNo),
                IvInquiryExportWorkbook.CellText(row.InvNo),
                IvInquiryExportWorkbook.CellText(row.SoNo),
                IvInquiryExportWorkbook.CellText(row.PoNo),
                IvInquiryExportWorkbook.CellText(row.Remarks)
            };

            if (includeValue)
            {
                cells.Add(IvInquiryExportWorkbook.CellNumber(row.EstValue));
            }

            return cells;
        });
    }
}
