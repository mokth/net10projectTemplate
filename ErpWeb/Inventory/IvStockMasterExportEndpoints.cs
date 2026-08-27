using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

public static class IvStockMasterExportEndpoints
{
    public const int MaxExportRows = 50_000;

    public static IEndpointRouteBuilder MapIvStockMasterExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/items/export", ExportAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] IIvStockMasterService stockMasters,
        [FromServices] IAccessRightService accessRights,
        [FromQuery] string? searchText,
        [FromQuery] bool? isActive,
        [FromQuery] string? iClassCode,
        [FromQuery] string? iSubClassCode,
        [FromQuery] string? iType,
        [FromQuery] string? defWarehouse,
        [FromQuery] string? brand,
        [FromQuery] string? sortField,
        [FromQuery] bool sortDescending,
        CancellationToken cancellationToken)
    {
        if (!await accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new IvStockMasterListQuery
        {
            SearchText = searchText,
            IsActive = isActive,
            IClassCode = iClassCode,
            ISubClassCode = iSubClassCode,
            IType = iType,
            DefWarehouse = defWarehouse,
            Brand = brand,
            SortField = sortField,
            SortDescending = sortDescending,
            Skip = 0,
            Take = MaxExportRows
        };

        var result = await stockMasters.ExportRowsAsync(query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.Message ?? "Export failed.");
        }

        var page = result.Data ?? new IvStockMasterListPage();
        if (page.TotalCount > MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {MaxExportRows:N0} rows. Refine filters and try again. Matched: {page.TotalCount:N0}.");
        }

        var bytes = BuildWorkbook(page.Rows);
        var fileName = $"IvStockMaster_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static byte[] BuildWorkbook(IReadOnlyList<IvStockMasterListRow> rows)
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
                Name = "Items"
            });

            string[] headers =
            [
                "Code", "Description", "Type", "Class", "Subclass", "Brand", "Std UOM",
                "Default Warehouse", "Sell Price", "Purchase Price", "Active",
                "Barcode", "Sell UOM", "Purchase UOM", "Sell GL", "Purchase GL", "Classification"
            ];

            sheetData.Append(CreateRow(1, headers.Select(CellText).ToArray()));

            uint rowIndex = 2;
            foreach (var r in rows)
            {
                sheetData.Append(CreateRow(rowIndex++,
                [
                    CellText(r.ICode),
                    CellText(r.IDesc),
                    CellText(r.IType),
                    CellText(r.IClassCode),
                    CellText(r.ISubClassCode),
                    CellText(r.Brand),
                    CellText(r.StdUom),
                    CellText(r.DefWarehouse),
                    CellNumber(r.SellingPrice),
                    CellNumber(r.PurchasePrice),
                    CellText(r.IsActive ? "Y" : "N"),
                    CellText(r.Barcode),
                    CellText(r.SellingUom),
                    CellText(r.PurUom),
                    CellText(r.SellingGlCode),
                    CellText(r.PurchaseGlCode),
                    CellText(r.Classification)
                ]));
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
