using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// OpenXML workbooks for Purchase reference master exports.
/// </summary>
public static class PoMasterRefExportWorkbooks
{
    public static readonly string[] BuyerHeaders = ["Code", "Name", "Desc", "Active"];
    public static readonly string[] BuyingTermHeaders = ["Code", "Description", "Active"];
    public static readonly string[] CategoryHeaders = ["Code", "Description", "Active"];
    public static readonly string[] AuthorisedHeaders = ["Code", "Name", "Email", "MobileNo", "Active"];

    public static readonly string[] PurItemHeaders =
    [
        "Id", "Item Code", "Description", "Category", "Vendor", "Vendor Name",
        "Currency", "Unit Price", "MOQ", "Status"
    ];

    public static byte[] BuildBuyers(IReadOnlyList<PoBuyerListRow> rows) =>
        Build("Buyers", BuyerHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Name),
            CellText(r.Desc),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildBuyingTerms(IReadOnlyList<PoBuyingTermListRow> rows) =>
        Build("BuyingTerms", BuyingTermHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Description),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildCategories(IReadOnlyList<PoCategoryListRow> rows) =>
        Build("Categories", CategoryHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Description),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildAuthorised(IReadOnlyList<PoAuthorisedListRow> rows) =>
        Build("Authorised", AuthorisedHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Name),
            CellText(r.Email),
            CellText(r.MobileNo),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildPurItems(IReadOnlyList<PoPurItemListRow> rows) =>
        Build("PurchaseItems", PurItemHeaders, rows.Select(r => new[]
        {
            CellText(r.Id.ToString(CultureInfo.InvariantCulture)),
            CellText(r.ICode),
            CellText(r.IDesc),
            CellText(r.Category),
            CellText(r.Vendor),
            CellText(r.VendName),
            CellText(r.Currency),
            CellText(r.UnitPrice?.ToString(CultureInfo.InvariantCulture)),
            CellText(r.Moq.ToString(CultureInfo.InvariantCulture)),
            CellText(r.Status)
        }).ToList());

    public static IReadOnlyList<string> ReadHeaderRow(byte[] workbookBytes)
    {
        using var stream = new MemoryStream(workbookBytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheetData = document.WorkbookPart!.WorksheetParts.First().Worksheet.GetFirstChild<SheetData>()!;
        var header = sheetData.Elements<Row>().First();
        return header.Elements<Cell>()
            .Select(c => c.InlineString?.Text?.Text ?? string.Empty)
            .ToList();
    }

    public static IReadOnlyList<IReadOnlyList<string>> ReadDataRows(byte[] workbookBytes)
    {
        using var stream = new MemoryStream(workbookBytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheetData = document.WorkbookPart!.WorksheetParts.First().Worksheet.GetFirstChild<SheetData>()!;
        return sheetData.Elements<Row>()
            .Skip(1)
            .Select(row => (IReadOnlyList<string>)row.Elements<Cell>()
                .Select(c => c.InlineString?.Text?.Text ?? string.Empty)
                .ToList())
            .ToList();
    }

    private static byte[] Build(string sheetName, string[] headers, List<Cell[]> dataRows)
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
                Name = sheetName
            });

            sheetData.Append(CreateRow(1, headers.Select(CellText).ToArray()));
            uint rowIndex = 2;
            foreach (var cells in dataRows)
            {
                sheetData.Append(CreateRow(rowIndex++, cells));
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
