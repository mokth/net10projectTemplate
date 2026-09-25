using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

/// <summary>
/// xlsx export for the stock card — this is where "stock movement as a report" is actually fulfilled.
/// The endpoint re-runs the SAME service call the page uses, so the exported row count equals the
/// grid's <c>TotalCount</c>, the ledger keeps its chronological order (the running balance depends on
/// it), and the export can never reflect a different scope from the one on screen.
/// </summary>
public static class IvStockCardExportEndpoints
{
    public const int MaxExportRows = 50_000;

    public static IEndpointRouteBuilder MapIvStockCardExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/stock-card/export", ExportAsync)
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
        [FromQuery] string? statuses,
        [FromQuery] DateTime? trxDateFrom,
        [FromQuery] DateTime? trxDateTo,
        CancellationToken cancellationToken = default)
    {
        if (!await accessRights.CanAsync(MenuCodes.InventoryStockCard, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new IvTrxHistoryQuery
        {
            ICode = iCode,
            WhCode = whCode,
            LocCode = locCode,
            LotNo = lotNo,
            IStatuses = (statuses ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            TrxDateFrom = trxDateFrom,
            TrxDateTo = trxDateTo
        };

        var result = await trxHistory.GetStockCardAsync(MenuCodes.InventoryStockCard, query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.Message ?? "Export failed.");
        }

        var card = result.Data ?? new IvStockCardPage();
        if (card.MatchingCount > MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {MaxExportRows:N0} rows. Refine filters and try again. Matched: {card.MatchingCount:N0}.");
        }

        var bytes = BuildWorkbook(
            card,
            await accessRights.CanAsync(MenuCodes.InventoryStockCard, PermissionCodes.ViewPrice, cancellationToken));
        var fileName = $"IvStockCard_{iCode}_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static byte[] BuildWorkbook(IvStockCardPage card, bool includeValue)
    {
        var headers = new List<string>
        {
            "Date", "Type", "Batch", "Line", "Ref", "Status",
            "From warehouse", "From bin", "From lot", "To warehouse", "To bin", "To lot",
            "In", "Out", "Net", "Running", "UOM", "Reason", "Remarks"
        };

        // Omitted entirely, never blanked, when price is not visible (D19).
        if (includeValue)
        {
            headers.Add("Est. value");
        }

        return IvInquiryExportWorkbook.Build("Stock card", headers, card.Rows, row =>
        {
            var cells = new List<DocumentFormat.OpenXml.Spreadsheet.Cell>
            {
                IvInquiryExportWorkbook.CellDateTime(row.TrxDtTime),
                IvInquiryExportWorkbook.CellText(row.TrxType),
                IvInquiryExportWorkbook.CellInt(row.BatchNo),
                IvInquiryExportWorkbook.CellInt(row.TrxLineNo),
                IvInquiryExportWorkbook.CellText(row.RefNo),
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
                IvInquiryExportWorkbook.CellNumber(row.RunningQty),
                IvInquiryExportWorkbook.CellText(row.StdUom),
                IvInquiryExportWorkbook.CellText(row.Reason),
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
