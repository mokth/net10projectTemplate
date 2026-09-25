using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

/// <summary>
/// xlsx export for the Balance-by-Lot inquiry. The endpoint checks EXPORT itself and then delegates to
/// <see cref="IIvBalanceLotService.ExportRowsAsync"/>, so the export inherits the service's tenant
/// resolution, ACCESS check and <c>CanViewPrice</c> masking. Company/branch are NEVER accepted from the
/// query string (SC3).
/// </summary>
public static class IvBalanceLotExportEndpoints
{
    public const int MaxExportRows = 50_000;

    public static IEndpointRouteBuilder MapIvBalanceLotExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/balance-by-lot/export", ExportAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] IIvBalanceLotService balanceLot,
        [FromServices] IAccessRightService accessRights,
        [FromServices] ICurrentUserService currentUser,
        [FromQuery] string? searchText,
        [FromQuery] string? iCode,
        [FromQuery] string? whCode,
        [FromQuery] string? locCode,
        [FromQuery] string? lotNo,
        [FromQuery] string? statuses,
        [FromQuery] decimal? minQty,
        [FromQuery] decimal? maxQty,
        [FromQuery] DateTime? expiryBefore,
        [FromQuery] DateTime? transDateFrom,
        [FromQuery] DateTime? transDateTo,
        [FromQuery] string? sortField,
        [FromQuery] bool sortDescending = false,
        [FromQuery] bool includeZeroQty = true,
        [FromQuery] bool includeInactive = true,
        [FromQuery] bool includeNonStockControl = true,
        CancellationToken cancellationToken = default)
    {
        if (!await accessRights.CanAsync(MenuCodes.InventoryBalanceLot, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new IvBalanceLotQuery
        {
            SearchText = searchText,
            ICode = iCode,
            WhCode = whCode,
            LocCode = locCode,
            LotNo = lotNo,
            IStatuses = SplitStatuses(statuses),
            MinQty = minQty,
            MaxQty = maxQty,
            ExpiryBefore = expiryBefore,
            TransDateFrom = transDateFrom,
            TransDateTo = transDateTo,
            IncludeZeroQty = includeZeroQty,
            IncludeInactive = includeInactive,
            IncludeNonStockControl = includeNonStockControl,
            SortField = sortField,
            SortDescending = sortDescending,
            Skip = 0,
            Take = MaxExportRows
        };

        var result = await balanceLot.ExportRowsAsync(query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.Message ?? "Export failed.");
        }

        var page = result.Data ?? new IvBalanceLotPage();
        if (page.TotalCount > MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {MaxExportRows:N0} rows. Refine filters and try again. Matched: {page.TotalCount:N0}.");
        }

        var bytes = BuildWorkbook(page.Rows, currentUser.CanViewPrice);
        var fileName = $"IvBalanceLot_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static IReadOnlyList<string> SplitStatuses(string? statuses) =>
        (statuses ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static byte[] BuildWorkbook(IReadOnlyList<IvBalanceLotRow> rows, bool includeValue)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Balances"
            });

            var headers = new List<string>
            {
                "Item", "Description", "Warehouse", "Bin", "Lot", "Status", "Qty", "UOM",
                "Expiry", "Last movement", "PO no.", "Ref", "Remarks", "Lot id"
            };

            // SC5 — the value column is omitted ENTIRELY, never emitted blank.
            if (includeValue)
            {
                headers.Add("Est. value");
            }

            sheetData.Append(CreateRow(1, headers.Select(CellText).ToArray()));

            uint rowIndex = 2;
            foreach (var r in rows)
            {
                var cells = new List<Cell>
                {
                    CellText(r.ICode),
                    CellText(r.IDesc),
                    CellText(r.WhCode),
                    CellText(r.LocCode),
                    CellText(r.LotNo),
                    CellText(r.IStatus),
                    CellNumber(r.StdQty),
                    CellText(r.StdUom),
                    CellDate(r.ExpiryDate),
                    CellDate(r.TransDate),
                    CellText(r.PoNo),
                    CellText(r.RefNo),
                    CellText(r.Remarks),
                    CellNumber(r.LotId)
                };

                if (includeValue)
                {
                    cells.Add(CellNumber(r.Value));
                }

                sheetData.Append(CreateRow(rowIndex++, cells.ToArray()));
            }

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static Row CreateRow(uint index, Cell[] cells)
    {
        var row = new Row { RowIndex = index };
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i].CellReference = $"{GetColumnName(i + 1)}{index}";
            row.Append(cells[i]);
        }

        return row;
    }

    private static Cell CellText(string? value) =>
        new()
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value ?? string.Empty))
        };

    private static Cell CellNumber(decimal? value) =>
        value is null
            ? CellText(string.Empty)
            : new Cell
            {
                DataType = CellValues.Number,
                CellValue = new CellValue(value.Value.ToString(CultureInfo.InvariantCulture))
            };

    private static Cell CellNumber(int? value) =>
        value is null
            ? CellText(string.Empty)
            : new Cell
            {
                DataType = CellValues.Number,
                CellValue = new CellValue(value.Value.ToString(CultureInfo.InvariantCulture))
            };

    private static Cell CellDate(DateTime? value) =>
        value is null
            ? CellText(string.Empty)
            : CellText(value.Value.ToString("yyyy-MM-dd"));

    private static string GetColumnName(int columnNumber)
    {
        var name = string.Empty;
        while (columnNumber > 0)
        {
            var remainder = (columnNumber - 1) % 26;
            name = (char)('A' + remainder) + name;
            columnNumber = (columnNumber - 1) / 26;
        }

        return name;
    }
}
