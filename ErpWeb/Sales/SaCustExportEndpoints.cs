using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Sales;

public static class SaCustExportEndpoints
{
    public const int MaxExportRows = 50_000;

    public static IEndpointRouteBuilder MapSaCustExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sales/customers/export", ExportAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] ISaCustService customers,
        [FromServices] IAccessRightService accessRights,
        [FromQuery] string? searchText,
        [FromQuery] bool? isActive,
        [FromQuery] string? custType,
        [FromQuery] string? custGroupCode,
        [FromQuery] string? salesmanCode,
        [FromQuery] string? areaCode,
        [FromQuery] string? sortField,
        [FromQuery] bool sortDescending,
        CancellationToken cancellationToken)
    {
        if (!await accessRights.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new SaCustListQuery
        {
            SearchText = searchText,
            IsActive = isActive,
            CustType = custType,
            CustGroupCode = custGroupCode,
            SalesmanCode = salesmanCode,
            AreaCode = areaCode,
            SortField = sortField,
            SortDescending = sortDescending,
            Skip = 0,
            Take = MaxExportRows
        };

        var result = await customers.ExportRowsAsync(query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.Message ?? "Export failed.");
        }

        var page = result.Data ?? new SaCustListPage();
        if (page.TotalCount > MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {MaxExportRows:N0} rows. Refine filters and try again. Matched: {page.TotalCount:N0}.");
        }

        var bytes = BuildWorkbook(page.Rows);
        var fileName = $"SaCust_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static byte[] BuildWorkbook(IReadOnlyList<SaCustListRow> rows)
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
                Name = "Customers"
            });

            string[] headers =
            [
                "Code", "Name", "Short Name", "Type", "Group", "Salesman", "Area",
                "City", "Tel", "Pay Term", "Currency", "Credit Limit", "Active"
            ];

            sheetData.Append(CreateRow(1, headers.Select(CellText).ToArray()));

            uint rowIndex = 2;
            foreach (var r in rows)
            {
                sheetData.Append(CreateRow(rowIndex++,
                [
                    CellText(r.CustCode),
                    CellText(r.CustName),
                    CellText(r.CustShortName),
                    CellText(r.CustType),
                    CellText(r.CustGroupCode),
                    CellText(r.SalesmanCode),
                    CellText(r.AreaCode),
                    CellText(r.City),
                    CellText(r.Tel),
                    CellText(r.PayCode),
                    CellText(r.Currency),
                    CellNumber(r.CreditLimit),
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
