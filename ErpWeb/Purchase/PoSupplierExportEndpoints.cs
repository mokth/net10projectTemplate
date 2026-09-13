using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Purchase;

public static class PoSupplierExportEndpoints
{
    public const int MaxExportRows = 50_000;

    public static IEndpointRouteBuilder MapPoSupplierExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/purchase/suppliers/export", ExportAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] IPoSupplierService suppliers,
        [FromServices] IAccessRightService accessRights,
        [FromQuery] string? searchText,
        [FromQuery] bool? isActive,
        [FromQuery] string? suppType,
        [FromQuery] string? categoryCode,
        [FromQuery] string? areaCode,
        [FromQuery] string? sortField,
        [FromQuery] bool sortDescending,
        CancellationToken cancellationToken)
    {
        if (!await accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new PoSupplierListQuery
        {
            SearchText = searchText,
            IsActive = isActive,
            SuppType = suppType,
            CategoryCode = categoryCode,
            AreaCode = areaCode,
            SortField = sortField,
            SortDescending = sortDescending,
            Skip = 0,
            Take = MaxExportRows
        };

        var result = await suppliers.ExportRowsAsync(query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.Message ?? "Export failed.");
        }

        var page = result.Data ?? new PoSupplierListPage();
        if (page.TotalCount > MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {MaxExportRows:N0} rows. Refine filters and try again. Matched: {page.TotalCount:N0}.");
        }

        // Note: ExportRowsAsync returns Take-limited rows; re-search total via Search for over-limit check.
        var countResult = await suppliers.SearchAsync(new PoSupplierListQuery
        {
            SearchText = searchText,
            IsActive = isActive,
            SuppType = suppType,
            CategoryCode = categoryCode,
            AreaCode = areaCode,
            Take = 1
        }, cancellationToken);
        if (countResult.Succeeded && countResult.Data is not null && countResult.Data.TotalCount > MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {MaxExportRows:N0} rows. Refine filters and try again. Matched: {countResult.Data.TotalCount:N0}.");
        }

        var bytes = BuildWorkbook(page.Rows);
        var fileName = $"PoSupplier_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static byte[] BuildWorkbook(IReadOnlyList<PoSupplierListRow> rows)
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
                Name = "Suppliers"
            });

            string[] headers =
            [
                "Supplier Code", "Supplier Name", "Short Name", "Registration No", "Type", "Group", "Sub Group",
                "Area", "City", "State", "Country", "Telephone", "Email", "Currency", "Payment Term",
                "Buying Term", "GL Code", "Active"
            ];

            sheetData.Append(CreateRow(1, headers.Select(CellText).ToArray()));

            uint rowIndex = 2;
            foreach (var r in rows)
            {
                sheetData.Append(CreateRow(rowIndex++,
                [
                    CellText(r.SuppCode),
                    CellText(r.SuppName),
                    CellText(r.SuppShortName),
                    CellText(r.SupplierBrn),
                    CellText(r.SuppType),
                    CellText(r.CategoryCode),
                    CellText(r.CreditorSubGroup),
                    CellText(r.AreaCode),
                    CellText(r.City),
                    CellText(r.State),
                    CellText(r.Country),
                    CellText(r.Tel),
                    CellText(r.Email),
                    CellText(r.Currency),
                    CellText(r.PayCode),
                    CellText(r.BuyingTerm),
                    CellText(r.GlCode),
                    CellText(r.IsActive ? "Y" : "N")
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
