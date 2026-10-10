using System.Globalization;
using ClosedXML.Excel;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;

namespace ErpWeb.Core.Sales;

public sealed partial class SaPriceMaintenanceService
{
    private static readonly string[] ReviewWorkbookHeaders =
    [
        "ReviewRowKey", "Target", "Price List", "Customer", "Item", "Description",
        "UOM", "MOQ", "Min Qty", "Max Qty", "Currency", "Valid From", "Valid To",
        "Current Price", "New Price", "Selected"
    ];

    public async Task<IvMasterOperationResult<byte[]>> BuildReviewWorkbookAsync(
        SaPriceReviewExportRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new SaPriceReviewExportRequest();
        var read = await CheckReadAsync(cancellationToken);
        if (read.ErrorCode is not null)
        {
            return FailWorkbook(read.ErrorCode.Value, read.Message!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesPriceMaintenance, PermissionCodes.Export, cancellationToken))
        {
            return FailWorkbook(IvMasterErrorCode.AccessDenied, "Export permission is required.");
        }

        request.Query ??= new SaPriceReviewQuery();
        request.Query.TargetType = NormalizeToken(request.Query.TargetType);
        request.Query.Skip = 0;
        request.Query.Take = SaPriceMaintenanceLimits.MaxReviewRows;

        var page = await SearchAsync(request.Query, cancellationToken);
        if (!page.Succeeded || page.Data is null)
        {
            return FailWorkbook(page.ErrorCode, page.Message ?? "Unable to build the review workbook.");
        }

        if (page.Data.TotalCount > SaPriceReviewWorkbookLimits.MaxRows)
        {
            return FailWorkbook(
                IvMasterErrorCode.Validation,
                $"Export is limited to {SaPriceReviewWorkbookLimits.MaxRows:N0} rows. Refine the filters first.");
        }

        var staged = page.Data.Rows.ToList();
        foreach (var row in staged)
        {
            var calculation = SaPriceAdjustmentCalculator.Calculate(
                row.CurrentPrice,
                request.AdjustmentMethod,
                request.AdjustmentValue,
                request.DecimalPlaces,
                request.RoundingMode);
            row.ProposedPrice = calculation.NewPrice;
            row.Warning = calculation.Error;
            row.Status = calculation.Succeeded
                ? SaPriceReviewStatuses.Ready
                : SaPriceReviewStatuses.Blocked;
        }

        try
        {
            using var workbook = new XLWorkbook();
            var meta = workbook.Worksheets.Add("Meta");
            WriteMeta(meta, request.Query.TargetType, request.Query.ReviewAsOf);

            var prices = workbook.Worksheets.Add("Prices");
            WritePrices(prices, staged);

            using var output = new MemoryStream();
            workbook.SaveAs(output);
            return IvMasterOperationResult<byte[]>.Ok(output.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailWorkbook(IvMasterErrorCode.Validation, $"Unable to build the review workbook: {ex.Message}");
        }
    }

    public async Task<IvMasterOperationResult<SaPriceImportPreview>> ParseImportAsync(
        Stream workbook,
        SaPriceImportContext context,
        CancellationToken cancellationToken = default)
    {
        var read = await CheckReadAsync(cancellationToken);
        if (read.ErrorCode is not null)
        {
            return FailImport(read.ErrorCode.Value, read.Message!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesPriceMaintenance, PermissionCodes.Import, cancellationToken))
        {
            return FailImport(IvMasterErrorCode.AccessDenied, "Import permission is required.");
        }

        if (workbook is null)
        {
            return FailImport(IvMasterErrorCode.Validation, "An .xlsx workbook is required.");
        }

        if (context is null || !SaPriceMaintenanceTargets.IsSupported(NormalizeToken(context.TargetType)))
        {
            return FailImport(IvMasterErrorCode.Validation, "A supported staged review target is required.");
        }

        if (context.StagedRows.Count > SaPriceReviewWorkbookLimits.MaxRows)
        {
            return FailImport(
                IvMasterErrorCode.Validation,
                $"The staged review cannot exceed {SaPriceReviewWorkbookLimits.MaxRows:N0} rows.");
        }

        try
        {
            if (workbook.CanSeek)
            {
                workbook.Position = 0;
            }

            if (workbook.Length > SaPriceReviewWorkbookLimits.MaxFileBytes)
            {
                return FailImport(
                    IvMasterErrorCode.Validation,
                    $"Workbook exceeds the {SaPriceReviewWorkbookLimits.MaxFileBytes / (1024 * 1024):N0} MB limit.");
            }

            using var book = new XLWorkbook(workbook);
            var templateVersion = ReadMeta(book, "TemplateVersion");
            var workbookTarget = NormalizeToken(ReadMeta(book, "TargetType"));
            var expectedTarget = NormalizeToken(context.TargetType);
            if (!string.Equals(templateVersion, SaPriceReviewWorkbookLimits.TemplateVersion, StringComparison.Ordinal))
            {
                return FailImport(IvMasterErrorCode.Validation, "The workbook template version is not supported.");
            }

            if (!string.Equals(workbookTarget, expectedTarget, StringComparison.Ordinal))
            {
                return FailImport(IvMasterErrorCode.Validation, "The workbook target does not match the current staged review.");
            }

            var sheet = book.Worksheets.FirstOrDefault(x => string.Equals(x.Name, "Prices", StringComparison.OrdinalIgnoreCase));
            if (sheet is null)
            {
                return FailImport(IvMasterErrorCode.Validation, "The workbook is missing the Prices sheet.");
            }

            var header = sheet.FirstRowUsed();
            if (header is null)
            {
                return FailImport(IvMasterErrorCode.Validation, "The Prices sheet is missing its header row.");
            }

            var headerCells = header.CellsUsed().ToList();
            var duplicateHeaders = headerCells
                .GroupBy(x => x.GetString().Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1)
                .Select(x => string.IsNullOrWhiteSpace(x.Key) ? "(blank)" : x.Key)
                .ToList();
            if (duplicateHeaders.Count > 0)
            {
                return FailImport(IvMasterErrorCode.Validation, $"Duplicate or blank workbook headers: {string.Join(", ", duplicateHeaders)}.");
            }

            var columns = headerCells.ToDictionary(
                x => x.GetString().Trim(),
                x => x.Address.ColumnNumber,
                StringComparer.OrdinalIgnoreCase);
            var missing = ReviewWorkbookHeaders
                .Where(x => !columns.ContainsKey(x))
                .ToList();
            if (missing.Count > 0)
            {
                return FailImport(IvMasterErrorCode.Validation, $"The workbook is missing required columns: {string.Join(", ", missing)}.");
            }

            var stagedByKey = context.StagedRows
                .Where(x => !string.IsNullOrWhiteSpace(x.ReviewRowKey))
                .ToDictionary(x => x.ReviewRowKey, StringComparer.Ordinal);
            var importRows = new List<SaPriceImportRow>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var dataRows = sheet.RowsUsed().Skip(1).Where(x => !x.IsEmpty()).ToList();
            if (dataRows.Count > SaPriceReviewWorkbookLimits.MaxRows)
            {
                return FailImport(
                    IvMasterErrorCode.Validation,
                    $"The workbook cannot contain more than {SaPriceReviewWorkbookLimits.MaxRows:N0} rows.");
            }

            foreach (var row in dataRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowNumber = row.RowNumber();
                var errors = new List<string>();
                var keyCell = row.Cell(columns["ReviewRowKey"]);
                SaPriceReviewRow? staged = null;
                if (keyCell.HasFormula)
                {
                    errors.Add("ReviewRowKey cannot contain a formula.");
                }

                var key = CellText(keyCell);
                if (string.IsNullOrWhiteSpace(key))
                {
                    errors.Add("ReviewRowKey is required.");
                }
                else if (!seenKeys.Add(key))
                {
                    errors.Add("ReviewRowKey is duplicated in the workbook.");
                }
                else if (!stagedByKey.TryGetValue(key, out staged))
                {
                    errors.Add("ReviewRowKey is not present in the current staged review.");
                }

                var selected = false;
                var selectedCell = row.Cell(columns["Selected"]);
                if (selectedCell.HasFormula)
                {
                    errors.Add("Selected cannot contain a formula.");
                }
                else if (!TryReadBoolean(selectedCell, out selected))
                {
                    errors.Add("Selected must be Yes, No, true, false, 1, or 0.");
                }

                decimal? newPrice = null;
                var newPriceCell = row.Cell(columns["New Price"]);
                if (newPriceCell.HasFormula)
                {
                    errors.Add("New Price cannot contain a formula.");
                }
                else if (!TryReadNullableDecimal(newPriceCell, out newPrice))
                {
                    errors.Add("New Price must be a decimal literal or blank.");
                }
                else if (newPrice is < 0m)
                {
                    errors.Add("New Price cannot be negative.");
                }

                if (staged is not null)
                {
                    ValidateIdentityCell(errors, row.Cell(columns["Target"]), staged.TargetType, "Target");
                    ValidateIdentityCell(errors, row.Cell(columns["Price List"]), staged.CustPriceCode, "Price List");
                    ValidateIdentityCell(errors, row.Cell(columns["Customer"]), staged.CustCode, "Customer");
                    ValidateIdentityCell(errors, row.Cell(columns["Item"]), staged.ItemCode, "Item");
                    ValidateIdentityCell(errors, row.Cell(columns["Description"]), staged.ItemDescription, "Description");
                    ValidateIdentityCell(errors, row.Cell(columns["UOM"]), staged.Uom, "UOM");
                    ValidateNullableInt(errors, row.Cell(columns["MOQ"]), staged.Moq, "MOQ");
                    ValidateNullableDecimal(errors, row.Cell(columns["Min Qty"]), staged.MinQty, "Min Qty");
                    ValidateNullableDecimal(errors, row.Cell(columns["Max Qty"]), staged.MaxQty, "Max Qty");
                    ValidateIdentityCell(errors, row.Cell(columns["Currency"]), staged.CurrencyCode, "Currency");
                    ValidateNullableDate(errors, row.Cell(columns["Valid From"]), staged.ValidFrom, "Valid From");
                    ValidateNullableDate(errors, row.Cell(columns["Valid To"]), staged.ValidTo, "Valid To");
                    ValidateNullableDecimal(errors, row.Cell(columns["Current Price"]), staged.CurrentPrice, "Current Price");
                }

                importRows.Add(new SaPriceImportRow
                {
                    RowNumber = rowNumber,
                    ReviewRowKey = key,
                    NewPrice = newPrice,
                    Selected = selected,
                    Error = errors.Count == 0 ? null : string.Join(" ", errors)
                });
            }

            if (importRows.Count == 0)
            {
                return FailImport(IvMasterErrorCode.Validation, "The Prices sheet contains no data rows.");
            }

            var updates = importRows.Where(x => x.Error is null).ToList();
            return IvMasterOperationResult<SaPriceImportPreview>.Ok(new SaPriceImportPreview
            {
                IsValid = updates.Count == importRows.Count,
                TemplateVersion = templateVersion,
                TargetType = workbookTarget,
                AcceptedRowCount = updates.Count,
                Rows = importRows,
                Updates = updates
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailImport(IvMasterErrorCode.Validation, $"Unable to parse the review workbook: {ex.Message}");
        }
    }

    private static void WriteMeta(IXLWorksheet sheet, string targetType, DateTime? reviewAsOf)
    {
        sheet.Cell(1, 1).Value = "Field";
        sheet.Cell(1, 2).Value = "Value";
        sheet.Cell(2, 1).Value = "TemplateVersion";
        sheet.Cell(2, 2).Value = SaPriceReviewWorkbookLimits.TemplateVersion;
        sheet.Cell(3, 1).Value = "TargetType";
        sheet.Cell(3, 2).Value = targetType;
        sheet.Cell(4, 1).Value = "ReviewAsOf";
        sheet.Cell(4, 2).Value = reviewAsOf?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        sheet.Cell(5, 1).Value = "ExportedAtUtc";
        sheet.Cell(5, 2).Value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Columns().AdjustToContents(12, 36);
    }

    private static void WritePrices(IXLWorksheet sheet, IReadOnlyList<SaPriceReviewRow> rows)
    {
        for (var i = 0; i < ReviewWorkbookHeaders.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = ReviewWorkbookHeaders[i];
        }

        var rowNumber = 2;
        foreach (var row in rows)
        {
            var col = 1;
            sheet.Cell(rowNumber, col++).Value = row.ReviewRowKey;
            sheet.Cell(rowNumber, col++).Value = row.TargetType;
            sheet.Cell(rowNumber, col++).Value = row.CustPriceCode ?? string.Empty;
            sheet.Cell(rowNumber, col++).Value = row.CustCode ?? string.Empty;
            sheet.Cell(rowNumber, col++).Value = row.ItemCode;
            sheet.Cell(rowNumber, col++).Value = row.ItemDescription ?? string.Empty;
            sheet.Cell(rowNumber, col++).Value = row.Uom ?? string.Empty;
            SetNullableNumber(sheet.Cell(rowNumber, col++), row.Moq);
            SetNullableNumber(sheet.Cell(rowNumber, col++), row.MinQty);
            SetNullableNumber(sheet.Cell(rowNumber, col++), row.MaxQty);
            sheet.Cell(rowNumber, col++).Value = row.CurrencyCode ?? string.Empty;
            SetNullableDate(sheet.Cell(rowNumber, col++), row.ValidFrom);
            SetNullableDate(sheet.Cell(rowNumber, col++), row.ValidTo);
            SetNullableNumber(sheet.Cell(rowNumber, col++), row.CurrentPrice);
            SetNullableNumber(sheet.Cell(rowNumber, col++), row.ProposedPrice);
            sheet.Cell(rowNumber, col).Value = row.Selected ? "Yes" : "No";
            rowNumber++;
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(1, rows.Count + 1), ReviewWorkbookHeaders.Length).SetAutoFilter();
        sheet.Columns().AdjustToContents(10, 32);
    }

    private static void SetNullableNumber(IXLCell cell, decimal? value)
    {
        if (value.HasValue)
        {
            cell.Value = value.Value;
            return;
        }

        cell.Value = string.Empty;
    }

    private static void SetNullableNumber(IXLCell cell, int? value)
    {
        if (value.HasValue)
        {
            cell.Value = value.Value;
            return;
        }

        cell.Value = string.Empty;
    }

    private static void SetNullableDate(IXLCell cell, DateTime? value)
    {
        if (value.HasValue)
        {
            cell.Value = value.Value.Date;
            cell.Style.DateFormat.Format = "yyyy-mm-dd";
            return;
        }

        cell.Value = string.Empty;
    }

    private static string? ReadMeta(XLWorkbook book, string field)
    {
        var sheet = book.Worksheets.FirstOrDefault(x => string.Equals(x.Name, "Meta", StringComparison.OrdinalIgnoreCase));
        if (sheet is null)
        {
            return null;
        }

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            if (string.Equals(CellText(row.Cell(1)), field, StringComparison.OrdinalIgnoreCase))
            {
                return CellText(row.Cell(2));
            }
        }

        return null;
    }

    private static void ValidateIdentityCell(
        ICollection<string> errors,
        IXLCell cell,
        string? expected,
        string field)
    {
        if (cell.HasFormula)
        {
            errors.Add($"{field} cannot contain a formula.");
            return;
        }

        var actual = CellText(cell);
        var expectedText = expected?.Trim() ?? string.Empty;
        if (!string.Equals(actual, expectedText, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{field} does not match the staged review.");
        }
    }

    private static void ValidateNullableDecimal(
        ICollection<string> errors,
        IXLCell cell,
        decimal? expected,
        string field)
    {
        if (cell.HasFormula)
        {
            errors.Add($"{field} cannot contain a formula.");
            return;
        }

        if (!TryReadNullableDecimal(cell, out var actual) || actual != expected)
        {
            errors.Add($"{field} does not match the staged review.");
        }
    }

    private static void ValidateNullableInt(
        ICollection<string> errors,
        IXLCell cell,
        int? expected,
        string field)
    {
        if (cell.HasFormula)
        {
            errors.Add($"{field} cannot contain a formula.");
            return;
        }

        if (!TryReadNullableInt(cell, out var actual) || actual != expected)
        {
            errors.Add($"{field} does not match the staged review.");
        }
    }

    private static void ValidateNullableDate(
        ICollection<string> errors,
        IXLCell cell,
        DateTime? expected,
        string field)
    {
        if (cell.HasFormula)
        {
            errors.Add($"{field} cannot contain a formula.");
            return;
        }

        if (!TryReadNullableDate(cell, out var actual)
            || actual?.Date != expected?.Date)
        {
            errors.Add($"{field} does not match the staged review.");
        }
    }

    private static bool TryReadNullableDecimal(IXLCell cell, out decimal? value)
    {
        value = null;
        if (cell.IsEmpty())
        {
            return true;
        }

        var text = CellText(cell);
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryReadNullableInt(IXLCell cell, out int? value)
    {
        value = null;
        if (cell.IsEmpty() || string.IsNullOrWhiteSpace(CellText(cell)))
        {
            return true;
        }

        var text = CellText(cell);
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryReadNullableDate(IXLCell cell, out DateTime? value)
    {
        value = null;
        if (cell.IsEmpty() || string.IsNullOrWhiteSpace(CellText(cell)))
        {
            return true;
        }

        if (cell.DataType == XLDataType.DateTime)
        {
            value = cell.GetDateTime().Date;
            return true;
        }

        var text = CellText(cell);
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            value = parsed.Date;
            return true;
        }

        return false;
    }

    private static bool TryReadBoolean(IXLCell cell, out bool value)
    {
        value = false;
        if (cell.IsEmpty())
        {
            return false;
        }

        var text = CellText(cell);
        if (bool.TryParse(text, out value))
        {
            return true;
        }

        if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "y", StringComparison.OrdinalIgnoreCase)
            || text == "1")
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "n", StringComparison.OrdinalIgnoreCase)
            || text == "0")
        {
            value = false;
            return true;
        }

        return false;
    }

    private static string CellText(IXLCell cell) =>
        cell.GetFormattedString().Trim();

    private static IvMasterOperationResult<byte[]> FailWorkbook(IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<byte[]>.Fail(code, message);

    private static IvMasterOperationResult<SaPriceImportPreview> FailImport(
        IvMasterErrorCode code,
        string message) =>
        IvMasterOperationResult<SaPriceImportPreview>.Fail(code, message);
}
