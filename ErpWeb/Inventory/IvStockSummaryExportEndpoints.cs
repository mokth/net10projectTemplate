using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

/// <summary>
/// xlsx export for the stock summary. The endpoint checks EXPORT itself and then delegates to
/// <see cref="IIvStockSummaryService.ExportRowsAsync"/>, so the export inherits the service's tenant
/// resolution, ACCESS check and <c>VIEW_PRICE</c> masking. Company/branch are NEVER accepted from the
/// query string, and the export uses the SAME applied grouping and filter object as the grid (D19).
///
/// <para>
/// The quantity column follows the screen's D16 rule: it is present with a plain "Total qty" caption for
/// Item/Item × Warehouse, and for Warehouse/Class it appears only when the caller opted in — captioned
/// "Total qty (mixed Std UOM)". An export must not hand out a number the screen deliberately withheld.
/// </para>
/// </summary>
public static class IvStockSummaryExportEndpoints
{
    public static IEndpointRouteBuilder MapIvStockSummaryExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/stock-summary/export", ExportAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        [FromServices] IIvStockSummaryService summaries,
        [FromServices] IAccessRightService accessRights,
        [FromQuery] string? groupBy,
        [FromQuery] string? iCode,
        [FromQuery] string? whCode,
        [FromQuery] string? iClassCode,
        [FromQuery] string? statuses,
        [FromQuery] bool includeZeroQty = true,
        [FromQuery] bool includeInactive = true,
        [FromQuery] bool includeNonStockControl = true,
        [FromQuery] bool showMixedQty = false,
        [FromQuery] string? sortField = null,
        [FromQuery] bool sortDescending = false,
        CancellationToken cancellationToken = default)
    {
        if (!await accessRights.CanAsync(MenuCodes.InventoryStockSummary, PermissionCodes.Export, cancellationToken))
        {
            return Results.Forbid();
        }

        var query = new IvStockSummaryQuery
        {
            GroupBy = IvStockSummaryGroupBys.Normalize(groupBy),
            ICode = iCode,
            WhCode = whCode,
            IClassCode = iClassCode,
            IStatuses = Split(statuses),
            IncludeZeroQty = includeZeroQty,
            IncludeInactive = includeInactive,
            IncludeNonStockControl = includeNonStockControl,
            SortField = sortField,
            SortDescending = sortDescending,
            Skip = 0,
            Take = IvInquiryExportWorkbook.MaxExportRows
        };

        var result = await summaries.ExportRowsAsync(MenuCodes.InventoryStockSummary, query, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.BadRequest(result.Message ?? "Export failed.");
        }

        var page = result.Data ?? new IvStockSummaryPage();
        if (page.TotalCount > IvInquiryExportWorkbook.MaxExportRows)
        {
            return Results.BadRequest(
                $"Export is limited to {IvInquiryExportWorkbook.MaxExportRows:N0} rows. Refine the filters and try again. Matched: {page.TotalCount:N0}.");
        }

        // The value column is omitted ENTIRELY when the caller may not see money (D11 Option B): the grid
        // never showed it, so the file must not either.
        var includeValue = await accessRights.CanAsync(
            MenuCodes.InventoryStockSummary, PermissionCodes.ViewPrice, cancellationToken);

        var bytes = BuildWorkbook(page.Rows, query.GroupBy, includeValue, showMixedQty);
        var fileName = $"IvStockSummary_{query.GroupBy}_{DateTime.Now:yyMMddHHmmss}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static IReadOnlyList<string> Split(string? values) =>
        (values ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The workbook mirrors the grid's column set for the selected grouping mode (D16).</summary>
    private static byte[] BuildWorkbook(
        IReadOnlyList<IvStockSummaryRow> rows,
        string groupBy,
        bool includeValue,
        bool showMixedQty)
    {
        var mode = IvStockSummaryGroupBys.Normalize(groupBy);
        var byItem = IvStockSummaryGroupBys.HasSingleUomPerGroup(mode);
        var byWarehouse = mode == IvStockSummaryGroupBys.Warehouse;
        var byClass = mode == IvStockSummaryGroupBys.Class;

        var headers = new List<string>();
        if (byWarehouse)
        {
            headers.Add("Warehouse");
            headers.Add("Warehouse name");
        }
        else if (byClass)
        {
            headers.Add("Class");
            headers.Add("Class description");
        }
        else
        {
            headers.Add("Item");
            headers.Add("Description");
            headers.Add(mode == IvStockSummaryGroupBys.ItemWarehouse ? "Warehouse" : "Class");
            headers.Add("UOM");
        }

        // A mixed-unit total is only emitted when the caller asked for it on the screen.
        var includeQty = byItem || showMixedQty;
        if (includeQty)
        {
            headers.Add(byItem ? "Total qty" : "Total qty (mixed Std UOM)");
        }

        if (!byItem)
        {
            headers.Add("Items");
        }

        headers.Add("Piles");
        headers.Add("Zero-qty piles");

        if (includeValue)
        {
            headers.Add("Est. value");
        }

        return IvInquiryExportWorkbook.Build(
            SheetNameFor(mode),
            headers,
            rows,
            row =>
            {
                var cells = new List<DocumentFormat.OpenXml.Spreadsheet.Cell>();
                if (byWarehouse)
                {
                    cells.Add(IvInquiryExportWorkbook.CellText(row.WhCode));
                    cells.Add(IvInquiryExportWorkbook.CellText(row.WhDesc));
                }
                else if (byClass)
                {
                    cells.Add(IvInquiryExportWorkbook.CellText(row.IClassCode));
                    cells.Add(IvInquiryExportWorkbook.CellText(row.IClassDesc));
                }
                else
                {
                    cells.Add(IvInquiryExportWorkbook.CellText(row.ICode));
                    cells.Add(IvInquiryExportWorkbook.CellText(row.IDesc));
                    cells.Add(mode == IvStockSummaryGroupBys.ItemWarehouse
                        ? IvInquiryExportWorkbook.CellText(row.WhCode)
                        : IvInquiryExportWorkbook.CellText(row.IClassCode));
                    cells.Add(IvInquiryExportWorkbook.CellText(row.StdUom));
                }

                if (includeQty)
                {
                    cells.Add(IvInquiryExportWorkbook.CellNumber(row.TotalQty));
                }

                if (!byItem)
                {
                    cells.Add(IvInquiryExportWorkbook.CellInt(row.ItemCount));
                }

                cells.Add(IvInquiryExportWorkbook.CellInt(row.PileCount));
                cells.Add(IvInquiryExportWorkbook.CellInt(row.ZeroQtyPileCount));

                if (includeValue)
                {
                    cells.Add(IvInquiryExportWorkbook.CellNumber(row.EstValue));
                }

                return cells;
            });
    }

    /// <summary>An xlsx sheet name: at most 31 characters and no <c>: \ / ? * [ ]</c>.</summary>
    private static string SheetNameFor(string groupBy)
    {
        var name = IvStockSummaryGroupBys.Describe(groupBy).Replace("×", "x", StringComparison.Ordinal);
        return name.Length <= 31 ? name : name[..31];
    }
}
