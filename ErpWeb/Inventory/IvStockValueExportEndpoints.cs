using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Inventory;

/// <summary>
/// xlsx export for **Inventory Valuation**. The endpoint checks EXPORT itself and then delegates to
/// <see cref="IIvStockSummaryService.ExportRowsAsync"/> under the VALUE menu code, so the export inherits
/// the same authoritative ledger composition as the screen but its own ACCESS / <c>VIEW_PRICE</c> grant.
/// Company and branch are NEVER accepted from the query string (D19).
///
/// <para>
/// The value columns are omitted **entirely** when the caller may not see price, and the quantity column
/// follows the screen's D16 rule: present as "Total qty" for Item groups, and for Warehouse/Class only
/// when the caller opted in — captioned "Total qty (mixed Std UOM)". An export must never hand out a
/// figure the screen deliberately withheld.
/// </para>
/// </summary>
public static class IvStockValueExportEndpoints
{
    public static IEndpointRouteBuilder MapIvStockValueExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/valuation/export", ExportAsync)
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
        if (!await accessRights.CanAsync(MenuCodes.InventoryStockValue, PermissionCodes.Export, cancellationToken))
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

        var result = await summaries.ExportRowsAsync(MenuCodes.InventoryStockValue, query, cancellationToken);
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

        var includeValue = await accessRights.CanAsync(
            MenuCodes.InventoryStockValue, PermissionCodes.ViewPrice, cancellationToken);

        var bytes = BuildWorkbook(page.Rows, query.GroupBy, includeValue, showMixedQty);
        var fileName = $"IvStockValue_{query.GroupBy}_{DateTime.Now:yyMMddHHmmss}.xlsx";
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
            headers.Add("UOM");
        }

        var includeQty = byItem || showMixedQty;
        if (includeQty)
        {
            headers.Add(byItem ? "Total qty" : "Total qty (mixed Std UOM)");
        }

        headers.Add("Piles");
        headers.Add("Cost method");
        headers.Add("Valuation status");

        if (includeValue)
        {
            headers.Add("Inventory value");
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
                    cells.Add(IvInquiryExportWorkbook.CellText(row.StdUom));
                }

                if (includeQty)
                {
                    cells.Add(IvInquiryExportWorkbook.CellNumber(row.TotalQty));
                }

                cells.Add(IvInquiryExportWorkbook.CellInt(row.PileCount));

                cells.Add(IvInquiryExportWorkbook.CellText(row.CostMethod));
                cells.Add(IvInquiryExportWorkbook.CellText(row.ValuationStatus));

                if (includeValue)
                {
                    cells.Add(IvInquiryExportWorkbook.CellNumber(row.InventoryValue));
                }

                return cells;
            });
    }

    /// <summary>An xlsx sheet name: at most 31 characters and no <c>: \ / ? * [ ]</c>.</summary>
    private static string SheetNameFor(string groupBy)
    {
        // The multiplication sign is not safe in a sheet name on every reader.
        var name = IvStockSummaryGroupBys.Describe(groupBy).Replace("×", "x", StringComparison.Ordinal);
        return name.Length <= 31 ? name : name[..31];
    }
}
