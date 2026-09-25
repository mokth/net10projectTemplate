using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ErpWeb.Inventory;

/// <summary>
/// The one xlsx writer every inventory-inquiry export uses.
///
/// <para>
/// The export contract is common to every endpoint that ships one (D19): the same applied query as the
/// grid, no paging, server-side tenant resolution, <c>EXPORT</c> re-checked inside the handler, money
/// columns omitted (never blanked) when price is not visible, a 50 000-row refusal, and a row count
/// equal to the grid's <c>TotalCount</c>. Sharing the OpenXML plumbing is how the "value omitted"
/// decision stays one decision instead of one per endpoint.
/// </para>
/// </summary>
internal static class IvInquiryExportWorkbook
{
    /// <summary>
    /// The one row ceiling every inquiry export refuses past (never truncates). It lives here, with the
    /// workbook helper, so the "50 000" in the export contract is one number and not one per endpoint.
    /// </summary>
    public const int MaxExportRows = 50_000;

    /// <summary>Builds a single-sheet workbook from a header list and a per-row cell projection.</summary>
    public static byte[] Build<T>(
        string sheetName,
        IReadOnlyList<string> headers,
        IEnumerable<T> rows,
        Func<T, IReadOnlyList<Cell>> project)
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
            foreach (var row in rows)
            {
                sheetData.Append(CreateRow(rowIndex++, project(row).ToArray()));
            }

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    public static Cell CellText(string? value) =>
        new()
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value ?? string.Empty))
        };

    public static Cell CellNumber(decimal? value) =>
        value is null
            ? CellText(string.Empty)
            : new Cell
            {
                DataType = CellValues.Number,
                CellValue = new CellValue(value.Value.ToString(CultureInfo.InvariantCulture))
            };

    public static Cell CellInt(int? value) => CellNumber(value);

    public static Cell CellDate(DateTime? value) =>
        value is null
            ? CellText(string.Empty)
            : CellText(value.Value.ToString("yyyy-MM-dd"));

    public static Cell CellDateTime(DateTime? value) =>
        value is null
            ? CellText(string.Empty)
            : CellText(value.Value.ToString("yyyy-MM-dd HH:mm"));

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
