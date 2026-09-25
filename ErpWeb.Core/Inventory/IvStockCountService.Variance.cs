using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// The stock-count variance report — the second half of <see cref="IvStockCountService"/>
/// (plan-inventoryInquirySuite Phase 3, item 14).
///
/// <para>
/// It reads <c>IvStockCountHdr</c> + <c>IvStockCountLine</c> — the evidence the count document already
/// froze at Generate and Post — so it adds no data of its own. <b>POSTED sheets only</b>: a DRAFT or
/// COUNTED sheet has no evidence, and a ROLLED_BACK sheet's adjustment is no longer in the ledger, so
/// including either would print a variance figure beside stock that no longer exists.
/// </para>
///
/// <para>
/// <b>The staleness caveat is the point of the page (D4).</b> <c>SystemQty</c> is Generate-time
/// evidence, not the live quantity at post — the posting engine re-reads the locked balance and
/// computes its own delta. So this is a <em>sheet</em> variance report, and the header's
/// <c>PostedStaleLines</c> travels onto every row as the disclosure.
/// </para>
///
/// <para>
/// <b>The accuracy formula is locked (D17).</b> Line accuracy is
/// <c>ExactMatchLines / CountedLines × 100</c>, with <c>CountedLines = 0</c> giving <b>null</b>
/// (rendered "—"), never 0 and never 100. A <c>1 − ABS(variance)/ABS(systemQty)</c> ratio is forbidden:
/// <c>SystemQty = 0</c> is common in a count and that formula divides by zero.
/// </para>
/// </summary>
public sealed partial class IvStockCountService
{
    /// <summary>
    /// The variance report is its OWN screen with its own grant: a user may be allowed to read count
    /// evidence without being allowed to create, count, post or roll back a sheet. That is why the menu
    /// code is a parameter on every variance member instead of a constant.
    /// </summary>
    private static readonly HashSet<string> VarianceMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.InventoryStockCountVar
    };

    private const int DefaultVariancePageSize = 50;
    private const int MaxVariancePageSize = 100;

    /// <summary>A hard ceiling for the export path; the endpoint's own cap is lower and refuses by name.</summary>
    private const int MaxVarianceExportRows = 1_000_000;

    public async Task<IvStockCountOperationResult> SearchVarianceAsync(
        string menuCode,
        IvStockCountVarianceQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveVarianceAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvStockCountOperationResult.Fail(context.Error!);
        }

        var prepared = query ?? new IvStockCountVarianceQuery();
        var skip = Math.Max(0, prepared.Skip);
        var take = Math.Clamp(
            prepared.Take <= 0 ? DefaultVariancePageSize : prepared.Take, 1, MaxVariancePageSize);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildVarianceSlice(db, context.CompanyCode!, context.BranchCode!, prepared);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await ApplyVarianceSort(slice, prepared)
            .Skip(skip)
            .Take(take)
            .Select(x => new IvStockCountVarianceRow
            {
                HeaderId = x.Header.Id,
                CountNo = x.Header.CountNo,
                CountDate = x.Header.CountDate,
                PostedBatchNo = x.Header.PostedBatchNo,
                PostedStaleLines = x.Header.PostedStaleLines,
                LineId = x.Line.Id,
                LineNumber = x.Line.LineNumber,
                BalLocId = x.Line.BalLocId,
                ICode = x.Line.ICode,
                IDesc = x.Line.IDesc,
                WHCode = x.Line.WHCode,
                LocCode = x.Line.LocCode,
                LotNo = x.Line.LotNo,
                IStatus = x.Line.IStatus,
                IClassCode = x.Line.IClassCode,
                StdUom = x.Line.StdUom,
                SystemQty = x.Line.SystemQty,
                PhysicalQty = x.Line.PhysicalQty,
                SnapshotUnitPrice = x.Line.SnapshotUnitPrice,
                RecountCount = x.Line.RecountCount,
                CountedBy = x.Line.CountedBy,
                CountedOn = x.Line.CountedOn,
                CreatedDate = x.Line.Header.CreatedDate,
                CreatedBy = x.Line.Header.CreatedBy,
                ModifiedDate = x.Line.Header.ModifiedDate,
                ModifiedBy = x.Line.Header.ModifiedBy
            })
            .ToListAsync(cancellationToken);

        DecorateVarianceRows(rows);

        return IvStockCountOperationResult.OkVariance(new IvStockCountVariancePage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvStockCountOperationResult> GetVarianceSummaryAsync(
        string menuCode,
        IvStockCountVarianceQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveVarianceAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvStockCountOperationResult.Fail(context.Error!);
        }

        var prepared = query ?? new IvStockCountVarianceQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildVarianceSlice(db, context.CompanyCode!, context.BranchCode!, prepared);

        var summary = await SummariseVarianceAsync(slice, cancellationToken);
        return IvStockCountOperationResult.OkVarianceSummary(summary);
    }

    public async Task<IvStockCountOperationResult> ExportVarianceRowsAsync(
        string menuCode,
        IvStockCountVarianceQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveVarianceAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvStockCountOperationResult.Fail(context.Error!);
        }

        var prepared = query ?? new IvStockCountVarianceQuery();
        var take = Math.Clamp(prepared.Take <= 0 ? DefaultVariancePageSize : prepared.Take, 1, MaxVarianceExportRows);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildVarianceSlice(db, context.CompanyCode!, context.BranchCode!, prepared);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await ApplyVarianceSort(slice, prepared)
            .Take(take)
            .Select(x => new IvStockCountVarianceRow
            {
                HeaderId = x.Header.Id,
                CountNo = x.Header.CountNo,
                CountDate = x.Header.CountDate,
                PostedBatchNo = x.Header.PostedBatchNo,
                PostedStaleLines = x.Header.PostedStaleLines,
                LineId = x.Line.Id,
                LineNumber = x.Line.LineNumber,
                BalLocId = x.Line.BalLocId,
                ICode = x.Line.ICode,
                IDesc = x.Line.IDesc,
                WHCode = x.Line.WHCode,
                LocCode = x.Line.LocCode,
                LotNo = x.Line.LotNo,
                IStatus = x.Line.IStatus,
                IClassCode = x.Line.IClassCode,
                StdUom = x.Line.StdUom,
                SystemQty = x.Line.SystemQty,
                PhysicalQty = x.Line.PhysicalQty,
                SnapshotUnitPrice = x.Line.SnapshotUnitPrice,
                RecountCount = x.Line.RecountCount,
                CountedBy = x.Line.CountedBy,
                CountedOn = x.Line.CountedOn,
                CreatedDate = x.Line.Header.CreatedDate,
                CreatedBy = x.Line.Header.CreatedBy,
                ModifiedDate = x.Line.Header.ModifiedDate,
                ModifiedBy = x.Line.Header.ModifiedBy
            })
            .ToListAsync(cancellationToken);

        DecorateVarianceRows(rows);

        return IvStockCountOperationResult.OkVariance(new IvStockCountVariancePage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    // ── Composition ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ONE variance composition. POSTED sheets only, and every filter is optional.
    ///
    /// <para>
    /// Date filters are half-open (<c>&gt;= from.Date</c> and <c>&lt; to.Date.AddDays(1)</c>) with no
    /// <c>.Date</c> call on a database column, exactly like the rest of the inquiry suite.
    /// </para>
    /// </summary>
    private static IQueryable<IvStockCountVarianceSlice> BuildVarianceSlice(
        AppDbContext db,
        string company,
        string branch,
        IvStockCountVarianceQuery query)
    {
        IQueryable<IvStockCountVarianceSlice> slice =
            from h in db.IvStockCountHdrs.AsNoTracking()
            join l in db.IvStockCountLines.AsNoTracking() on h.Id equals l.StockCountId
            where h.CompanyCode == company
                  && h.BranchCode == branch
                  && h.Status == IvStockCountStatuses.Posted
            select new IvStockCountVarianceSlice { Header = h, Line = l };

        if (query.DateFrom is DateTime from)
        {
            var fromBound = from.Date;
            slice = slice.Where(x => x.Header.CountDate >= fromBound);
        }

        if (query.DateTo is DateTime to)
        {
            var toBound = to.Date.AddDays(1);
            slice = slice.Where(x => x.Header.CountDate < toBound);
        }

        if (!string.IsNullOrWhiteSpace(query.ICode))
        {
            var code = query.ICode.Trim();
            slice = slice.Where(x => x.Line.ICode == code);
        }

        if (!string.IsNullOrWhiteSpace(query.WhCode))
        {
            var wh = query.WhCode.Trim();
            slice = slice.Where(x => x.Line.WHCode == wh);
        }

        if (!string.IsNullOrWhiteSpace(query.IClassCode))
        {
            var cls = query.IClassCode.Trim();
            slice = slice.Where(x => x.Line.IClassCode == cls);
        }

        var statuses = (query.IStatuses ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (statuses.Count > 0)
        {
            slice = slice.Where(x => statuses.Contains(x.Line.IStatus));
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            slice = slice.Where(x =>
                x.Header.CountNo.Contains(term)
                || x.Line.ICode.Contains(term)
                || (x.Line.IDesc != null && x.Line.IDesc.Contains(term)));
        }

        return slice;
    }

    /// <summary>
    /// SQL-side aggregates over the same predicate as the grid. The accuracy numerator and denominator
    /// are counted in SQL and the percentage is derived in memory — a division in SQL would have to
    /// guard the zero denominator twice, and <c>CountedLines = 0</c> must yield <b>null</b>, not 0.
    /// </summary>
    private static async Task<IvStockCountVarianceSummary> SummariseVarianceAsync(
        IQueryable<IvStockCountVarianceSlice> slice,
        CancellationToken cancellationToken)
    {
        var sheetCount = await slice.Select(x => x.Header.Id).Distinct().CountAsync(cancellationToken);
        var lineCount = await slice.CountAsync(cancellationToken);

        var counted = slice.Where(x => x.Line.PhysicalQty != null);
        var countedLines = await counted.CountAsync(cancellationToken);

        // Both columns are stored at the quantity scale (SystemQty is rounded at Generate, PhysicalQty on
        // save), so this equality IS the rounded comparison — and it keeps the summary's numerator
        // provably in step with the row's own Variance == 0.
        var exactMatchLines = await counted
            .CountAsync(x => x.Line.PhysicalQty == x.Line.SystemQty, cancellationToken);

        var increaseLines = await counted
            .CountAsync(x => x.Line.SystemQty < x.Line.PhysicalQty, cancellationToken);
        var decreaseLines = await counted
            .CountAsync(x => x.Line.SystemQty > x.Line.PhysicalQty, cancellationToken);

        var netVarianceQty = await counted
            .SumAsync(x => x.Line.SystemQty - x.Line.PhysicalQty!.Value, cancellationToken);

        // The absolute total is built from the two one-sided sums rather than Math.Abs: Math.Abs has no
        // SQL translation on this provider, and filtering by the comparison keeps both sides positive.
        var increaseQty = await counted
            .Where(x => x.Line.SystemQty < x.Line.PhysicalQty)
            .SumAsync(x => x.Line.PhysicalQty!.Value - x.Line.SystemQty, cancellationToken);
        var decreaseQty = await counted
            .Where(x => x.Line.SystemQty > x.Line.PhysicalQty)
            .SumAsync(x => x.Line.SystemQty - x.Line.PhysicalQty!.Value, cancellationToken);
        var absVarianceQty = increaseQty + decreaseQty;

        // A line with no snapshot price cannot contribute a value; the sum is NULL when nothing priced.
        // Counted rather than probed with Any(): Any() on an IQueryable is a second, blocking round trip.
        var pricedLines = counted.Where(x => x.Line.SnapshotUnitPrice != null);
        var pricedLineCount = await pricedLines.CountAsync(cancellationToken);
        var varianceValue = pricedLineCount == 0
            ? (decimal?)null
            : IvQty.Round(await pricedLines.SumAsync(
                x => (x.Line.SystemQty - x.Line.PhysicalQty!.Value) * x.Line.SnapshotUnitPrice!.Value,
                cancellationToken));

        // Per-SHEET disclosure: distinct the header first, or a 40-line sheet would report its stale
        // count 40 times.
        var staleBySheet = await slice
            .Select(x => new { x.Header.Id, x.Header.PostedStaleLines })
            .Distinct()
            .ToListAsync(cancellationToken);

        return new IvStockCountVarianceSummary
        {
            SheetCount = sheetCount,
            LineCount = lineCount,
            CountedLines = countedLines,
            ExactMatchLines = exactMatchLines,
            IncreaseLines = increaseLines,
            DecreaseLines = decreaseLines,
            PostedStaleLines = staleBySheet.Sum(x => x.PostedStaleLines ?? 0),
            NetVarianceQty = IvQty.Round(netVarianceQty),
            AbsVarianceQty = IvQty.Round(absVarianceQty),
            VarianceValue = varianceValue
        };
    }

    /// <summary>
    /// Fills the four service-owned row columns. All four are <c>IvQty.Round</c>-based, matching the count
    /// screen's own preview so the two cannot disagree about which direction "positive" points.
    /// </summary>
    private static void DecorateVarianceRows(IReadOnlyList<IvStockCountVarianceRow> rows)
    {
        foreach (var row in rows)
        {
            row.IsCounted = row.PhysicalQty is not null;

            if (row.PhysicalQty is not decimal physical)
            {
                // Not counted: no variance exists. The line is excluded from the accuracy denominator
                // rather than counted as agreement (D17).
                row.Direction = IvStockCountVarianceDirections.NotCounted;
                continue;
            }

            var variance = IvQty.Round(row.SystemQty) - IvQty.Round(physical);
            row.Variance = variance;
            row.Direction = variance > 0m
                ? IvStockCountVarianceDirections.Decrease
                : variance < 0m
                    ? IvStockCountVarianceDirections.Increase
                    : IvStockCountVarianceDirections.None;

            row.VarianceValue = row.SnapshotUnitPrice is decimal price
                ? IvQty.Round(variance * price)
                : null;
        }
    }

    private static IOrderedQueryable<IvStockCountVarianceSlice> ApplyVarianceSort(
        IQueryable<IvStockCountVarianceSlice> slice,
        IvStockCountVarianceQuery query)
    {
        var field = (query.SortField ?? string.Empty).Trim();
        if (!IvStockCountVarianceSortFields.Allowed.Contains(field))
        {
            // Default: newest sheet first, then the sheet's own line order, so the order is TOTAL.
            return slice
                .OrderByDescending(x => x.Header.CountDate)
                .ThenByDescending(x => x.Header.CountNo)
                .ThenBy(x => x.Line.LineNumber);
        }

        return field switch
        {
            nameof(IvStockCountVarianceRow.CountDate) => query.SortDescending
                ? slice.OrderByDescending(x => x.Header.CountDate).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Header.CountDate).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.CountNo) => query.SortDescending
                ? slice.OrderByDescending(x => x.Header.CountNo).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Header.CountNo).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.ICode) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.ICode).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.ICode).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.IDesc) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.IDesc).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.IDesc).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.WHCode) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.WHCode).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.WHCode).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.LocCode) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.LocCode).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.LocCode).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.LotNo) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.LotNo).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.LotNo).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.IStatus) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.IStatus).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.IStatus).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.SystemQty) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.SystemQty).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.SystemQty).ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.PhysicalQty) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.PhysicalQty).ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.PhysicalQty).ThenBy(x => x.Line.LineNumber),
            // The signed difference is not stored, so its sort key is derived — as NULLABLE arithmetic,
            // never a conditional, so an uncounted line simply sorts as NULL and no conditional can be
            // inlined into the ORDER BY.
            nameof(IvStockCountVarianceRow.Variance) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.SystemQty - x.Line.PhysicalQty)
                    .ThenBy(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.SystemQty - x.Line.PhysicalQty)
                    .ThenBy(x => x.Line.LineNumber),
            nameof(IvStockCountVarianceRow.LineNumber) => query.SortDescending
                ? slice.OrderByDescending(x => x.Line.LineNumber)
                : slice.OrderBy(x => x.Line.LineNumber),
            _ => slice
                .OrderByDescending(x => x.Header.CountDate)
                .ThenByDescending(x => x.Header.CountNo)
                .ThenBy(x => x.Line.LineNumber)
        };
    }

    /// <summary>
    /// Tenant scope first (fail closed), then ACCESS on the caller's OWN variance menu — the same order
    /// and the same two messages every other inquiry screen uses.
    /// </summary>
    private Task<IvInquiryScopeContext> ResolveVarianceAsync(
        string menuCode,
        CancellationToken cancellationToken) =>
        IvInquiryScopeResolver.ResolveAsync(
            _tenant, _accessRights, menuCode, VarianceMenus, cancellationToken);
}

/// <summary>
/// Internal query shape for the variance report: one count line with its owning header. Both are
/// entity references, which is safe here because the projection is a final <c>Select</c> over real
/// columns — no <c>GROUP BY</c> and no conditional aggregating them.
/// </summary>
public sealed class IvStockCountVarianceSlice
{
    public IvStockCountHdr Header { get; set; } = null!;
    public IvStockCountLine Line { get; set; } = null!;
}
