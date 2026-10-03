using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Owns every read over <c>dbo.IvTrxHistory</c> — the transaction inquiry, the stock card and (later)
/// the slow/dead movement aggregate.
///
/// <para>
/// One private composition (<see cref="BuildTrxHistoryQuery"/>) feeds four thin callers, exactly like
/// <c>IvStockCommonRepository.BuildBalanceLotQuery</c>. Aggregates are computed in SQL as
/// <c>SUM(to-leg) - SUM(from-leg)</c> over two leg-filtered queries, so no conditional expression ever
/// reaches the expression tree and the summary provably cannot depend on paging.
/// </para>
///
/// <para>
/// <b>Scope-aware quantities.</b> A movement line has a from-leg and a to-leg. When the caller pins a
/// stock slice (item + warehouse + bin + lot + status), only the leg that lands inside it counts:
/// a transfer out of the slice contributes to <c>OutQty</c>, never to <c>InQty</c>. When no slice is
/// pinned every leg counts, so <c>Net = ToStdQty - FrStdQty</c> — the plain movement net.
/// </para>
/// </summary>
public interface IIvStockHistoryRepository
{
    /// <summary>Paged movement rows. Server-side sort + paging, clamped to 100 rows per page.</summary>
    Task<(IReadOnlyList<IvTrxHistoryRow> Rows, int TotalCount)> SearchTrxHistoryPagedAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Match count for the same predicate as <see cref="SearchTrxHistoryPagedAsync"/>.</summary>
    Task<int> CountTrxHistoryAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Period aggregate over the SAME predicate as the grid.</summary>
    Task<IvTrxHistorySummary> SummariseTrxHistoryAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Export rows (no Skip, capped by <c>query.Take</c>) using the same composition.</summary>
    Task<IReadOnlyList<IvTrxHistoryRow>> ListTrxHistoryForExportAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Net quantity of every in-scope movement strictly before <paramref name="beforeExclusive"/> —
    /// the Stock Card's opening balance (D12). The query's own date range is deliberately ignored:
    /// the opening is everything that happened before the period began.
    /// </summary>
    Task<decimal> SumScopeOpeningQtyAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        DateTime beforeExclusive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The period ledger in CHRONOLOGICAL order (<c>TrxDtTime, BatchNo, TrxLineNo, Id</c>) — a running
    /// balance is only meaningful in that order, so this method does not honour the sort whitelist.
    /// Capped at <see cref="IvStockHistoryRepository.MaxStockCardRows"/> + 1 so the caller can tell
    /// whether the cap was hit.
    /// </summary>
    Task<(IReadOnlyList<IvTrxHistoryRow> Rows, int TotalCount)> ListTrxHistoryLedgerAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IIvStockHistoryRepository"/>
public sealed class IvStockHistoryRepository : IIvStockHistoryRepository
{
    /// <summary>
    /// Hard cap on the Stock Card ledger. A running balance cannot be aggregated in SQL without a
    /// window function, so the period's rows are materialised; the cap keeps that bounded and the page
    /// tells the user to narrow the date range instead of silently showing a partial ledger.
    /// </summary>
    public const int MaxStockCardRows = 20_000;

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public IvStockHistoryRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<(IReadOnlyList<IvTrxHistoryRow> Rows, int TotalCount)> SearchTrxHistoryPagedAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = IvTrxHistoryScope.FromQuery(query);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? DefaultPageSize : query.Take, 1, MaxPageSize);
        var slice = ApplyScope(BuildTrxHistoryQuery(db, companyCode, branchCode, query, includeDateRange: true), scope);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await Project(ApplySort(slice, query.SortField, query.SortDescending).Skip(skip).Take(take))
            .ToListAsync(cancellationToken);

        return (rows, total);
    }

    public async Task<int> CountTrxHistoryAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = IvTrxHistoryScope.FromQuery(query);
        var slice = ApplyScope(BuildTrxHistoryQuery(db, companyCode, branchCode, query, includeDateRange: true), scope);

        return await slice.CountAsync(cancellationToken);
    }

    public async Task<IvTrxHistorySummary> SummariseTrxHistoryAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = IvTrxHistoryScope.FromQuery(query);
        var anyLeg = ApplyScope(BuildTrxHistoryQuery(db, companyCode, branchCode, query, includeDateRange: true), scope);

        var totalRows = await anyLeg.CountAsync(cancellationToken);
        var batchCount = await anyLeg.Select(x => x.Trx.BatchNo).Distinct().CountAsync(cancellationToken);

        var inQuery = ToLegInScope(anyLeg, scope);
        var outQuery = FromLegInScope(anyLeg, scope);
        var inQty = await inQuery.SumAsync(x => x.Trx.ToStdQty ?? 0m, cancellationToken);
        var outQty = await outQuery.SumAsync(x => x.Trx.FrStdQty ?? 0m, cancellationToken);

        var adjustNet = await SumNetAsync(
            anyLeg.Where(x => x.Trx.TrxType == IvTrxHistoryTypes.StockAdjustment), scope, cancellationToken);

        // The locked value formula (D13): history.UnitPrice ?? item.PurchasePrice ?? 0. Inlined on
        // purpose — a plain C# helper cannot appear inside an expression tree (it will not translate).
        // Cost / CostPrice / AsNowCost are legacy, sparse and deliberately excluded.
        var inValue = await inQuery.SumAsync(
            x => (x.Trx.ToStdQty ?? 0m)
                 * (x.Trx.UnitPrice ?? (x.Sm != null ? x.Sm.PurchasePrice : (decimal?)null) ?? 0m),
            cancellationToken);
        var outValue = await outQuery.SumAsync(
            x => (x.Trx.FrStdQty ?? 0m)
                 * (x.Trx.UnitPrice ?? (x.Sm != null ? x.Sm.PurchasePrice : (decimal?)null) ?? 0m),
            cancellationToken);

        return new IvTrxHistorySummary
        {
            TotalRows = totalRows,
            BatchCount = batchCount,
            InQty = inQty,
            OutQty = outQty,
            AdjustNetQty = adjustNet,
            TotalValue = inValue - outValue
        };
    }

    public async Task<IReadOnlyList<IvTrxHistoryRow>> ListTrxHistoryForExportAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = IvTrxHistoryScope.FromQuery(query);
        var take = Math.Clamp(query.Take <= 0 ? DefaultPageSize : query.Take, 1, 1_000_000);
        var slice = ApplyScope(BuildTrxHistoryQuery(db, companyCode, branchCode, query, includeDateRange: true), scope);

        return await Project(ApplySort(slice, query.SortField, query.SortDescending).Take(take))
            .ToListAsync(cancellationToken);
    }

    public async Task<decimal> SumScopeOpeningQtyAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        DateTime beforeExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = IvTrxHistoryScope.FromQuery(query);

        // The opening ignores the query's own period; it is everything strictly before the period start.
        var before = BuildTrxHistoryQuery(db, companyCode, branchCode, query, includeDateRange: false)
            .Where(x => x.Trx.TrxDtTime < beforeExclusive);

        return await SumNetAsync(before, scope, cancellationToken);
    }

    public async Task<(IReadOnlyList<IvTrxHistoryRow> Rows, int TotalCount)> ListTrxHistoryLedgerAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = IvTrxHistoryScope.FromQuery(query);
        var slice = ApplyScope(BuildTrxHistoryQuery(db, companyCode, branchCode, query, includeDateRange: true), scope);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await Project(
                slice.OrderBy(x => x.Trx.TrxDtTime)
                    .ThenBy(x => x.Trx.BatchNo)
                    .ThenBy(x => x.Trx.TrxLineNo)
                    .ThenBy(x => x.Trx.Id)
                    .Take(MaxStockCardRows + 1))
            .ToListAsync(cancellationToken);

        return (rows, total);
    }

    // ── Shared composition (one definition, four thin callers) ───────────────────────────────────

    private static IQueryable<IvTrxHistorySlice> BuildTrxHistoryQuery(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvTrxHistoryQuery query,
        bool includeDateRange)
    {
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        IQueryable<IvTrxHistorySlice> slice =
            from trx in db.IvTrxHistories.AsNoTracking()
            join sm in db.IvStockMasters.AsNoTracking()
                on new { trx.CompanyCode, trx.ICode } equals new { sm.CompanyCode, sm.ICode }
                into sms
            from sm in sms.DefaultIfEmpty()
            where trx.CompanyCode == company && trx.BranchCode == branch
            select new IvTrxHistorySlice
            {
                Trx = trx,
                Sm = sm
            };

        if (!string.IsNullOrWhiteSpace(query.ICode))
        {
            var code = query.ICode.Trim();
            slice = slice.Where(x => x.Trx.ICode == code);
        }

        var trxTypes = (query.TrxTypes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct()
            .ToList();
        if (trxTypes.Count > 0)
        {
            slice = slice.Where(x => trxTypes.Contains(x.Trx.TrxType));
        }

        if (query.BatchNo is int batchNo)
        {
            slice = slice.Where(x => x.Trx.BatchNo == batchNo);
        }

        if (!string.IsNullOrWhiteSpace(query.RefNo))
        {
            var refNo = query.RefNo.Trim();
            slice = slice.Where(x => x.Trx.RefNo != null && x.Trx.RefNo.Contains(refNo));
        }

        if (!string.IsNullOrWhiteSpace(query.DocumentNo))
        {
            var docNo = query.DocumentNo.Trim();
            slice = slice.Where(x =>
                (x.Trx.DoNo != null && x.Trx.DoNo.Contains(docNo))
                || (x.Trx.InvNo != null && x.Trx.InvNo.Contains(docNo))
                || (x.Trx.SoNo != null && x.Trx.SoNo.Contains(docNo))
                || (x.Trx.PoNo != null && x.Trx.PoNo.Contains(docNo)));
        }

        var statuses = (query.IStatuses ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct()
            .ToList();
        if (statuses.Count > 0)
        {
            slice = slice.Where(x => x.Trx.IStatus != null && statuses.Contains(x.Trx.IStatus));
        }

        if (includeDateRange)
        {
            // Half-open: >= from.Date and < to.Date.AddDays(1). Never .Date on a database column.
            if (query.TrxDateFrom is DateTime from)
            {
                var fromBound = from.Date;
                slice = slice.Where(x => x.Trx.TrxDtTime >= fromBound);
            }

            if (query.TrxDateTo is DateTime to)
            {
                var toBound = to.Date.AddDays(1);
                slice = slice.Where(x => x.Trx.TrxDtTime < toBound);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            slice = slice.Where(x =>
                x.Trx.ICode.Contains(term)
                || (x.Trx.IDesc != null && x.Trx.IDesc.Contains(term))
                || (x.Trx.ProdCode != null && x.Trx.ProdCode.Contains(term))
                || (x.Trx.RefNo != null && x.Trx.RefNo.Contains(term))
                || (x.Trx.Remarks != null && x.Trx.Remarks.Contains(term)));
        }

        return slice;
    }

    /// <summary>
    /// Rows touching the slice: either leg matching is enough. Null tests are hoisted into captured
    /// booleans so the predicate is a plain SQL OR — a parameter compared to NULL never matches.
    /// </summary>
    private static IQueryable<IvTrxHistorySlice> ApplyScope(
        IQueryable<IvTrxHistorySlice> slice,
        IvTrxHistoryScope scope)
    {
        if (scope.IsEmpty)
        {
            return slice;
        }

        var hasCode = scope.ICode is not null;
        var code = scope.ICode ?? string.Empty;
        var hasWh = scope.WhCode is not null;
        var wh = scope.WhCode ?? string.Empty;
        var hasLoc = scope.LocCode is not null;
        var loc = scope.LocCode ?? string.Empty;
        var hasLot = scope.LotNo is not null;
        var lot = scope.LotNo ?? string.Empty;
        var hasStatus = scope.IStatus is not null;
        var status = scope.IStatus ?? string.Empty;

        return slice.Where(x =>
            (!hasCode || x.Trx.ICode == code)
            && (!hasWh || x.Trx.FrWarehouse == wh || x.Trx.ToWarehouse == wh)
            && (!hasLoc || x.Trx.FrLocation == loc || x.Trx.ToLocation == loc)
            && (!hasLot || x.Trx.FrLotNo == lot || x.Trx.ToLotNo == lot)
            && (!hasStatus || x.Trx.IStatus == status));
    }

    /// <summary>Narrows to rows whose <b>receiving</b> leg lands inside the slice.</summary>
    private static IQueryable<IvTrxHistorySlice> ToLegInScope(
        IQueryable<IvTrxHistorySlice> slice,
        IvTrxHistoryScope scope)
    {
        if (scope.ICode is not null)
        {
            var code = scope.ICode;
            slice = slice.Where(x => x.Trx.ICode == code);
        }

        if (scope.WhCode is not null)
        {
            var wh = scope.WhCode;
            slice = slice.Where(x => x.Trx.ToWarehouse == wh);
        }

        if (scope.LocCode is not null)
        {
            var loc = scope.LocCode;
            slice = slice.Where(x => x.Trx.ToLocation == loc);
        }

        if (scope.LotNo is not null)
        {
            var lot = scope.LotNo;
            slice = slice.Where(x => x.Trx.ToLotNo == lot);
        }

        if (scope.IStatus is not null)
        {
            var status = scope.IStatus;
            slice = slice.Where(x => x.Trx.IStatus == status);
        }

        return slice;
    }

    /// <summary>Narrows to rows whose <b>issuing</b> leg lands inside the slice.</summary>
    private static IQueryable<IvTrxHistorySlice> FromLegInScope(
        IQueryable<IvTrxHistorySlice> slice,
        IvTrxHistoryScope scope)
    {
        if (scope.ICode is not null)
        {
            var code = scope.ICode;
            slice = slice.Where(x => x.Trx.ICode == code);
        }

        if (scope.WhCode is not null)
        {
            var wh = scope.WhCode;
            slice = slice.Where(x => x.Trx.FrWarehouse == wh);
        }

        if (scope.LocCode is not null)
        {
            var loc = scope.LocCode;
            slice = slice.Where(x => x.Trx.FrLocation == loc);
        }

        if (scope.LotNo is not null)
        {
            var lot = scope.LotNo;
            slice = slice.Where(x => x.Trx.FrLotNo == lot);
        }

        if (scope.IStatus is not null)
        {
            var status = scope.IStatus;
            slice = slice.Where(x => x.Trx.IStatus == status);
        }

        return slice;
    }

    private static async Task<decimal> SumNetAsync(
        IQueryable<IvTrxHistorySlice> slice,
        IvTrxHistoryScope scope,
        CancellationToken cancellationToken)
    {
        var inQty = await ToLegInScope(slice, scope).SumAsync(x => x.Trx.ToStdQty ?? 0m, cancellationToken);
        var outQty = await FromLegInScope(slice, scope).SumAsync(x => x.Trx.FrStdQty ?? 0m, cancellationToken);
        return inQty - outQty;
    }

    private static IQueryable<IvTrxHistoryRow> Project(IQueryable<IvTrxHistorySlice> slice) =>
        slice.Select(x => new IvTrxHistoryRow
        {
            Id = x.Trx.Id,
            BatchNo = x.Trx.BatchNo,
            TrxLineNo = x.Trx.TrxLineNo,
            TrxDtTime = x.Trx.TrxDtTime,
            TrxType = x.Trx.TrxType,
            BatchStatus = x.Trx.BatchStatus,
            RefNo = x.Trx.RefNo,
            ICode = x.Trx.ICode,
            IDesc = x.Trx.IDesc ?? (x.Sm != null ? x.Sm.IDesc : null),
            ProdCode = x.Trx.ProdCode,
            FrWarehouse = x.Trx.FrWarehouse,
            FrLocation = x.Trx.FrLocation,
            FrLotNo = x.Trx.FrLotNo,
            FrStdQty = x.Trx.FrStdQty,
            FrStdUom = x.Trx.FrStdUom,
            ToWarehouse = x.Trx.ToWarehouse,
            ToLocation = x.Trx.ToLocation,
            ToLotNo = x.Trx.ToLotNo,
            ToStdQty = x.Trx.ToStdQty,
            ToStdUom = x.Trx.ToStdUom,
            IStatus = x.Trx.IStatus,
            DoNo = x.Trx.DoNo,
            InvNo = x.Trx.InvNo,
            SoNo = x.Trx.SoNo,
            PoNo = x.Trx.PoNo,
            Remarks = x.Trx.Remarks,
            UnitPrice = x.Trx.UnitPrice ?? (x.Sm != null ? x.Sm.PurchasePrice : (decimal?)null),
            CreatedDate = x.Trx.CreatedDate,
            CreatedBy = x.Trx.CreatedBy,
            ModifiedDate = x.Trx.ModifiedDate,
            ModifiedBy = null
        });

    private static IOrderedQueryable<IvTrxHistorySlice> ApplySort(
        IQueryable<IvTrxHistorySlice> slice,
        string? sortField,
        bool sortDescending)
    {
        var field = (sortField ?? string.Empty).Trim();
        if (!IvTrxHistorySortFields.Allowed.Contains(field))
        {
            // Default order: newest movement first.
            return slice
                .OrderByDescending(x => x.Trx.TrxDtTime)
                .ThenByDescending(x => x.Trx.Id);
        }

        // InQty/OutQty are not stored columns, so their sort keys are derived from the raw legs.
        return field switch
        {
            nameof(IvTrxHistoryRow.InQty) => sortDescending
                ? slice.OrderByDescending(x => x.Trx.ToStdQty ?? 0m)
                : slice.OrderBy(x => x.Trx.ToStdQty ?? 0m),
            nameof(IvTrxHistoryRow.OutQty) => sortDescending
                ? slice.OrderByDescending(x => x.Trx.FrStdQty ?? 0m)
                : slice.OrderBy(x => x.Trx.FrStdQty ?? 0m),
            nameof(IvTrxHistoryRow.NetQty) => sortDescending
                ? slice.OrderByDescending(x => (x.Trx.ToStdQty ?? 0m) - (x.Trx.FrStdQty ?? 0m))
                : slice.OrderBy(x => (x.Trx.ToStdQty ?? 0m) - (x.Trx.FrStdQty ?? 0m)),
            _ => OrderByField(slice, field, sortDescending)
        };
    }

    private static IOrderedQueryable<IvTrxHistorySlice> OrderByField(
        IQueryable<IvTrxHistorySlice> slice,
        string field,
        bool descending) =>
        field switch
        {
            nameof(IvTrxHistoryRow.TrxDtTime) => descending
                ? slice.OrderByDescending(x => x.Trx.TrxDtTime)
                : slice.OrderBy(x => x.Trx.TrxDtTime),
            nameof(IvTrxHistoryRow.TrxType) => descending
                ? slice.OrderByDescending(x => x.Trx.TrxType)
                : slice.OrderBy(x => x.Trx.TrxType),
            nameof(IvTrxHistoryRow.BatchNo) => descending
                ? slice.OrderByDescending(x => x.Trx.BatchNo)
                : slice.OrderBy(x => x.Trx.BatchNo),
            nameof(IvTrxHistoryRow.RefNo) => descending
                ? slice.OrderByDescending(x => x.Trx.RefNo)
                : slice.OrderBy(x => x.Trx.RefNo),
            nameof(IvTrxHistoryRow.ICode) => descending
                ? slice.OrderByDescending(x => x.Trx.ICode)
                : slice.OrderBy(x => x.Trx.ICode),
            nameof(IvTrxHistoryRow.IDesc) => descending
                ? slice.OrderByDescending(x => x.Trx.IDesc ?? (x.Sm != null ? x.Sm.IDesc : null))
                : slice.OrderBy(x => x.Trx.IDesc ?? (x.Sm != null ? x.Sm.IDesc : null)),
            nameof(IvTrxHistoryRow.IStatus) => descending
                ? slice.OrderByDescending(x => x.Trx.IStatus)
                : slice.OrderBy(x => x.Trx.IStatus),
            _ => slice.OrderByDescending(x => x.Trx.TrxDtTime)
        };

    // ── Movement-ageing compositions, shared with the stock-alert rules (Phase 2) ─────────────────

    /// <summary>
    /// The branch's posted movements as a COMPOSABLE source. Every <c>IvTrxHistory</c> read is defined
    /// here (D8) — callers compose predicates and aggregates over it; they never open the table
    /// themselves.
    ///
    /// <para>
    /// <b>Last movement</b> = <c>MAX(TrxDtTime)</c> over this source, which is what the stock-alert rules
    /// aggregate. <b>Never <c>IvBalLoc.TransDate</c></b> as a movement basis: that column is the last
    /// movement date of a <em>pile</em>, it is overwritten on every posting, and a back-dated post can
    /// move it backwards — so it cannot answer "when did this item last move".
    /// </para>
    ///
    /// <para>
    /// It is exposed as an <see cref="IQueryable{T}"/> so a caller can CAPTURE it in a local and compose
    /// a correlated subquery over it. Capturing matters: a helper call written directly inside a
    /// projection becomes an unexpandable method call in the expression tree, and EF Core then refuses to
    /// translate it.
    /// </para>
    /// </summary>
    public static IQueryable<IvTrxHistory> MovementsForBranch(
        AppDbContext db,
        string companyCode,
        string branchCode)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        var activeEpochs = db.StockLedgerEpochs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.Status == ErpWeb.Model.Entities.StockLedger.StockLedgerEpochStatuses.Active);
        return db.IvTrxHistories.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && ((!activeEpochs.Any() && x.LedgerVersion == null)
                    || (x.LedgerVersion == 2
                        && activeEpochs.Any(e => e.Id == x.LedgerEpochId)
                        && db.StockPostings.Any(p => p.Id == x.StockPostingId
                            && p.CompanyCode == company && p.BranchCode == branch
                            && p.SealedAtUtc != null))));
    }
}
