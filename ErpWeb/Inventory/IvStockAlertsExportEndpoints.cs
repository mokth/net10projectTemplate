using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

/// <summary>
/// xlsx export for the stock-alert inquiry. The endpoint checks EXPORT itself and then delegates to
/// <see cref="IIvStockAlertService.ExportRowsAsync"/>, so the export inherits the service's tenant
/// resolution, its ACCESS check and its rule normalization — the exported definition of "dead stock"
/// cannot drift from the grid's. Company/branch are NEVER accepted from the query string, and the export
/// uses the SAME applied rule and filter object as the grid (never its current page) (D19).
/// </summary>
public static class IvStockAlertsExportEndpoints
{
    public static IEndpointRouteBuilder MapIvStockAlertsExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/stock-alerts/export", ExportAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] IIvStockAlertService alerts,
        [FromServices] IAccessRightService accessRights,
        [FromQuery] string? rule,
        [FromQuery] string? iCode,
        [FromQuery] string? whCode,
        [FromQuery] string? iClassCode,
        [FromQuery] int? slowDays,
        [FromQuery] int? deadDays,
        [FromQuery] int? expiryDays,
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool includeNonStockControl = false,
        [FromQuery] string? sortField = null,
        [FromQuery] bool sortDescending = false,
        CancellationToken cancellationToken = default)
    {
        if (!await accessRights.CanAsync(MenuCodes.InventoryStockAlerts, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new IvStockAlertQuery
        {
            Rule = IvStockAlertRules.Normalize(rule),
            ICode = iCode,
            WhCode = whCode,
            IClassCode = iClassCode,
            SlowDays = slowDays ?? 90,
            DeadDays = deadDays ?? 180,
            ExpiryDays = expiryDays ?? 30,
            IncludeInactive = includeInactive,
            IncludeNonStockControl = includeNonStockControl,
            SortField = sortField,
            SortDescending = sortDescending,
            Skip = 0,
            Take = IvInquiryExportWorkbook.MaxExportRows
        };

        var result = await alerts.ExportRowsAsync(MenuCodes.InventoryStockAlerts, query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.Message ?? "Export failed.");
        }

        var page = result.Data ?? new IvStockAlertPage();
        if (page.TotalCount > IvInquiryExportWorkbook.MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {IvInquiryExportWorkbook.MaxExportRows:N0} rows. Refine the filters and try again. Matched: {page.TotalCount:N0}.");
        }

        var bytes = BuildWorkbook(page.Rows, query.Rule);
        var fileName = $"IvStockAlerts_{query.Rule}_{page.AsOfDate:yyyyMMdd}_{DateTime.Now:HHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    /// <summary>
    /// The workbook mirrors the grid: the rule decides which column groups exist, so an exported
    /// "expired lots" sheet never carries a blank Min-stock column that looks like missing data.
    /// </summary>
    private static byte[] BuildWorkbook(IReadOnlyList<IvStockAlertRow> rows, string rule)
    {
        var isThreshold = rule is IvStockAlertRules.Low or IvStockAlertRules.Over;
        var isMovement = IvStockAlertRules.IsMovementRule(rule);
        var isExpiry = IvStockAlertRules.IsLotRule(rule);

        var headers = new List<string> { "Item", "Description", "Class", "Std UOM" };
        if (isThreshold)
        {
            headers.Add("Min stock");
            headers.Add("Max stock");
        }

        headers.Add("On hand");

        if (isThreshold)
        {
            headers.Add("Variance");
        }

        if (isMovement)
        {
            headers.Add("Last movement");
            headers.Add("Days since movement");
        }

        if (isExpiry)
        {
            headers.Add("Warehouse");
            headers.Add("Lot");
            headers.Add("Expiry");
            headers.Add("Days to expiry");
        }

        headers.Add("Threshold basis");
        headers.Add("Created");
        headers.Add("User ID");
        headers.Add("Modified date");
        headers.Add("Modified by");

        return IvInquiryExportWorkbook.Build(
            SheetNameFor(rule),
            headers,
            rows,
            row =>
            {
                var cells = new List<DocumentFormat.OpenXml.Spreadsheet.Cell>
                {
                    IvInquiryExportWorkbook.CellText(row.ICode),
                    IvInquiryExportWorkbook.CellText(row.IDesc),
                    IvInquiryExportWorkbook.CellText(row.IClassCode),
                    IvInquiryExportWorkbook.CellText(row.StdUom)
                };

                if (isThreshold)
                {
                    cells.Add(IvInquiryExportWorkbook.CellNumber(row.MinStock));
                    cells.Add(IvInquiryExportWorkbook.CellNumber(row.MaxStock));
                }

                cells.Add(IvInquiryExportWorkbook.CellNumber(row.OnHand));

                if (isThreshold)
                {
                    cells.Add(IvInquiryExportWorkbook.CellNumber(row.Variance));
                }

                if (isMovement)
                {
                    cells.Add(IvInquiryExportWorkbook.CellDate(row.LastMovement));
                    cells.Add(IvInquiryExportWorkbook.CellInt(row.DaysSinceMovement));
                }

                if (isExpiry)
                {
                    cells.Add(IvInquiryExportWorkbook.CellText(row.WhCode));
                    cells.Add(IvInquiryExportWorkbook.CellText(row.LotNo));
                    cells.Add(IvInquiryExportWorkbook.CellDate(row.ExpiryDate));
                    cells.Add(IvInquiryExportWorkbook.CellInt(row.DaysToExpiry));
                }

                cells.Add(IvInquiryExportWorkbook.CellText(row.ThresholdBasis));
                cells.Add(IvInquiryExportWorkbook.CellDate(row.CreatedDate));
                cells.Add(IvInquiryExportWorkbook.CellText(row.CreatedBy));
                cells.Add(IvInquiryExportWorkbook.CellDate(row.ModifiedDate));
                cells.Add(IvInquiryExportWorkbook.CellText(row.ModifiedBy));

                return cells;
            });
    }

    /// <summary>An xlsx sheet name: at most 31 characters and no <c>: \ / ? * [ ]</c>.</summary>
    private static string SheetNameFor(string rule)
    {
        var name = IvStockAlertRules.Describe(rule);
        return name.Length <= 31 ? name : name[..31];
    }
}
