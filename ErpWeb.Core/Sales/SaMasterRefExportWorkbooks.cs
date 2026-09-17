using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ErpWeb.Core.Sales;

/// <summary>
/// OpenXML workbooks for the flat sales reference master exports (D-15). Same shape as
/// <c>PoMasterRefExportWorkbooks</c> and the legacy <c>ListViewControl.ExportGrid</c>.
/// </summary>
public static class SaMasterRefExportWorkbooks
{
    public static readonly string[] CustSubGroupHeaders = ["Code", "Description"];
    public static readonly string[] ShipViaHeaders = ["Code", "Description", "Active"];
    public static readonly string[] SoTypeHeaders = ["Code", "Description", "Active"];
    public static readonly string[] CommentHeaders = ["Comment ID", "Comment", "Active"];
    public static readonly string[] ShippingLeadTimeHeaders = ["Code", "Description", "Days", "Type", "Active"];

    public static readonly string[] LmwHeaders =
    [
        "Licence No", "Customer Code", "Customer Name", "Licence ID", "Licence Type",
        "Licence Start", "Licence End", "System Start", "System End",
        "Name", "IC", "Position"
    ];

    public static byte[] BuildCustSubGroups(IReadOnlyList<SaCustSubGroupListRow> rows) =>
        Build("CustomerSubGroups", CustSubGroupHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Desc)
        }).ToList());

    public static byte[] BuildShipVias(IReadOnlyList<SaShipViaListRow> rows) =>
        Build("ShipVia", ShipViaHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Desc),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildSoTypes(IReadOnlyList<SaSOTypeListRow> rows) =>
        Build("SOTypes", SoTypeHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Desc),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildComments(IReadOnlyList<SaCommentListRow> rows) =>
        Build("Comments", CommentHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Comment),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildShippingLeadTimes(IReadOnlyList<SaShippingLeadTimeListRow> rows) =>
        Build("ShippingLeadTime", ShippingLeadTimeHeaders, rows.Select(r => new[]
        {
            CellText(r.Code),
            CellText(r.Desc),
            CellText(r.Days?.ToString(CultureInfo.InvariantCulture)),
            CellText(r.Type),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildLmws(IReadOnlyList<SaLMWListRow> rows) =>
        Build("LMW", LmwHeaders, rows.Select(r => new[]
        {
            CellText(r.LicenseNo),
            CellText(r.CustCode),
            CellText(r.CustName),
            CellText(r.LicenseID),
            CellText(r.LicenseType),
            CellText(r.LicenseStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CellText(r.LicenseEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CellText(r.SystemStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CellText(r.SystemEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CellText(r.Name),
            CellText(r.IC),
            CellText(r.Position)
        }).ToList());

    // ---- Sales item family (plans/sales-item-family-v2-plan.md §11.1, §24) ----
    // VIEW_PRICE is enforced inside the service, so an unauthorized caller is handed rows whose price
    // columns are already empty; the workbook below only writes what it was given.

    public static readonly string[] CustPriceGroupHeaders = ["Code", "Description", "Items", "Active"];

    // Phase 3: a price list may hold SEVERAL rows for one item + UOM (quantity tiers and promotions),
    // so the band, the validity window and the currency are part of what identifies a line. Without them
    // the export produced indistinguishable duplicate rows and silently dropped the data that tells them
    // apart. Currency BLANK means the company base currency, never "unknown".
    public static readonly string[] CustPriceHeaders =
    [
        "Price Group", "Item", "Description", "UOM", "Min Qty", "Max Qty",
        "Valid From", "Valid To", "Currency", "Selling Price", "Pack Size"
    ];

    public static readonly string[] ItemCustHeaders =
        ["Customer", "Item", "Description", "Customer Item Code", "UOM", "MOQ", "Unit Price", "Currency", "Status"];

    public static readonly string[] DisGroupItemHeaders =
    [
        "Item", "Description", "Class", "Qty From", "Qty To", "From", "To",
        "Discount 1", "Type 1", "Discount 2", "Type 2", "Effect Price"
    ];

    public static byte[] BuildCustPriceGroups(IReadOnlyList<IvCustPriceGroupListRow> rows) =>
        Build("CustPriceGroups", CustPriceGroupHeaders, rows.Select(r => new[]
        {
            CellText(r.CustPriceCode),
            CellText(r.CustPriceDesc),
            CellText(r.LineCount.ToString(CultureInfo.InvariantCulture)),
            CellText(r.IsActive ? "Y" : "N")
        }).ToList());

    public static byte[] BuildCustPrices(string custPriceCode, IReadOnlyList<IvCustPriceListRow> rows) =>
        Build("CustPrices", CustPriceHeaders, rows.Select(r => new[]
        {
            CellText(custPriceCode),
            CellText(r.ICode),
            CellText(r.IDesc),
            CellText(r.UOM),
            CellText(r.MinQty?.ToString(CultureInfo.InvariantCulture)),
            CellText(r.MaxQty?.ToString(CultureInfo.InvariantCulture)),
            CellText(r.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CellText(r.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CellText(r.CurrencyCode),
            CellText(Money(r.SellingPrice)),
            CellText(Money(r.SellPackSize))
        }).ToList());

    public static byte[] BuildItemCusts(IReadOnlyList<SaItemCustListRow> rows) =>
        Build("ItemCust", ItemCustHeaders, rows.Select(r => new[]
        {
            CellText(r.CustCode),
            CellText(r.ICode),
            CellText(r.IDesc),
            CellText(r.CustICode),
            CellText(r.SellingUOM),
            CellText(r.MOQ.ToString(CultureInfo.InvariantCulture)),
            CellText(Money(r.UnitPrice)),
            CellText(r.Currency),
            CellText(r.Status)
        }).ToList());

    public static byte[] BuildDisGroupItems(IReadOnlyList<SaDisGroupItemListRow> rows) =>
        Build("DisGroupItem", DisGroupItemHeaders, rows.Select(r => new[]
        {
            CellText(r.ICode),
            CellText(r.IDesc),
            CellText(r.IClass),
            CellText(Money(r.QtyFr)),
            CellText(Money(r.QtyTo)),
            CellText(r.DateFr.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CellText(r.DateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CellText(Money(r.Discount)),
            CellText(r.DiscountType),
            CellText(Money(r.Discount1)),
            CellText(r.DiscountType1),
            CellText(r.EffectPrice)
        }).ToList());

    /// <summary>Money and quantities are written with the invariant culture so Excel never re-reads them
    /// through a locale-specific decimal separator.</summary>
    private static string Money(decimal? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;

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
