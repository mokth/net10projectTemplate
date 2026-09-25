using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Owns the on-hand-derived compositions that are NEW to the inquiry suite — alerts, stock summary and
/// lot inquiry. Kept separate from <see cref="IIvStockCommonRepository"/> on purpose (D8): that class is
/// ~1,900 lines and must stay byte-stable so the shipped Balance-by-Lot feature carries zero merge risk.
///
/// <para>
/// <b>Ownership rule.</b> Every <c>IvTrxHistory</c> read belongs to
/// <see cref="IIvStockHistoryRepository"/>; this class may COMPOSE those reads
/// (<see cref="IvStockHistoryRepository.LastMovementByItem"/>) but never opens the table itself. That is
/// what keeps "when did this item last move" a single definition.
/// </para>
///
/// <para>
/// <b>Aggregation rule.</b> Counts, sums and group-by projections are evaluated in SQL over the same
/// predicate the grid uses, never by materialising rows — except the lot inquiry's pile panel, which is
/// bounded by one lot.
/// </para>
/// </summary>
public interface IIvStockInquiryRepository
{
    /// <summary>
    /// Live <c>SUM(IvBalLoc.StdQty)</c> over a stock slice, or <c>null</c> when the live figure would
    /// not mean anything (the slice pins no item, so it would sum the whole branch).
    ///
    /// <para>
    /// Both sides of any comparison must be aggregated on the same slice key (<see cref="IvStockSliceKey"/>),
    /// which is why this takes the same wildcard-slice type the history reads use.
    /// </para>
    /// </summary>
    Task<decimal?> SumOnHandForScopeAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryScope scope,
        CancellationToken cancellationToken = default);

    // ── Phase 2 — stock alerts ────────────────────────────────────────────────────────────────────

    /// <summary>Paged alert rows for one rule. The rule changes the whole predicate, not a column.</summary>
    Task<(IReadOnlyList<IvStockAlertRow> Rows, int TotalCount)> SearchStockAlertsAsync(
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Match count for the same predicate as <see cref="SearchStockAlertsAsync"/>.</summary>
    Task<int> CountStockAlertsAsync(
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Aggregate over the SAME predicate as the grid, computed in SQL.</summary>
    Task<IvStockAlertSummary> SummariseStockAlertsAsync(
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Export rows (no Skip, capped by <c>query.Take</c>) using the same composition.</summary>
    Task<IReadOnlyList<IvStockAlertRow>> ListStockAlertsForExportAsync(
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default);

    // ── Phase 2 — lot inquiry ─────────────────────────────────────────────────────────────────────

    /// <summary>Paged lot passports with their on-hand aggregate.</summary>
    Task<(IReadOnlyList<IvLotInquiryRow> Rows, int TotalCount)> SearchLotInquiryAsync(
        string companyCode,
        string branchCode,
        IvLotInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Aggregate over the SAME predicate as the lot grid, computed in SQL.</summary>
    Task<IvLotInquirySummary> SummariseLotInquiryAsync(
        string companyCode,
        string branchCode,
        IvLotInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The piles holding one lot — the child panel of the lot passport. Unpaged: one lot has a handful of
    /// piles by construction, and <c>IX_IvBalLoc_LotId</c> serves the seek.
    /// </summary>
    Task<IReadOnlyList<IvLotPileRow>> ListLotPilesAsync(
        string companyCode,
        string branchCode,
        int lotId,
        CancellationToken cancellationToken = default);

    // ── Phase 2 — stock summary ───────────────────────────────────────────────────────────────────

    /// <summary>Paged group rows for the selected grouping mode. Grouping happens in SQL.</summary>
    Task<(IReadOnlyList<IvStockSummaryRow> Rows, int TotalCount)> SearchStockSummaryAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Group count for the same predicate and grouping mode as <see cref="SearchStockSummaryAsync"/>.</summary>
    Task<int> CountStockSummaryAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate over the SAME predicate as the grid. Because grouping only partitions the piles, the
    /// quantity/value/pile measures are computed over the flat slice and are identical for every mode.
    /// </summary>
    Task<IvStockSummarySummary> SummariseStockSummaryAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Export rows (no Skip, capped by <c>query.Take</c>) using the same grouping.</summary>
    Task<IReadOnlyList<IvStockSummaryRow>> ListStockSummaryForExportAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IIvStockInquiryRepository"/>
public sealed class IvStockInquiryRepository : IIvStockInquiryRepository
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int MaxExportRows = 1_000_000;

    /// <summary>Reported as the threshold basis when no warehouse is pinned — the D3 item-level basis.</summary>
    public const string AllWarehousesBasis = "All warehouses";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public IvStockInquiryRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<decimal?> SumOnHandForScopeAsync(
        string companyCode,
        string branchCode,
        IvTrxHistoryScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Without an item the sum would cover the whole branch — a number nobody asked for.
        if (scope.IsEmpty || scope.ICode is null)
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        IQueryable<IvBalLoc> piles = db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ICode == scope.ICode);

        if (scope.WhCode is not null)
        {
            var wh = scope.WhCode;
            piles = piles.Where(x => x.WhCode == wh);
        }

        if (scope.LocCode is not null)
        {
            var loc = scope.LocCode;
            piles = piles.Where(x => x.LocCode == loc);
        }

        if (scope.LotNo is not null)
        {
            var lot = scope.LotNo;
            piles = piles.Where(x => x.LotNo == lot);
        }

        if (scope.IStatus is not null)
        {
            var status = scope.IStatus;
            piles = piles.Where(x => x.IStatus == status);
        }

        return await piles.SumAsync(x => x.StdQty, cancellationToken);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  Phase 2 — stock alerts
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<(IReadOnlyList<IvStockAlertRow> Rows, int TotalCount)> SearchStockAlertsAsync(
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var alerts = BuildStockAlertQuery(db, companyCode, branchCode, query);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? DefaultPageSize : query.Take, 1, MaxPageSize);

        var total = await alerts.CountAsync(cancellationToken);
        var rows = await ApplyAlertSort(alerts, query).Skip(skip).Take(take).ToListAsync(cancellationToken);

        return (rows, total);
    }

    public async Task<int> CountStockAlertsAsync(
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildStockAlertQuery(db, companyCode, branchCode, query).CountAsync(cancellationToken);
    }

    public async Task<IvStockAlertSummary> SummariseStockAlertsAsync(
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var alerts = BuildStockAlertQuery(db, companyCode, branchCode, query);

        var totalRows = await alerts.CountAsync(cancellationToken);
        var itemCount = await alerts.Select(x => x.ICode).Distinct().CountAsync(cancellationToken);

        // OnHand is a projected column, so the sum wraps the projection in a subquery — still one round
        // trip and still SQL-side, never a materialised row set.
        var totalOnHand = await alerts.SumAsync(x => x.OnHand, cancellationToken);

        return new IvStockAlertSummary
        {
            TotalRows = totalRows,
            ItemCount = itemCount,
            TotalOnHand = totalOnHand
        };
    }

    public async Task<IReadOnlyList<IvStockAlertRow>> ListStockAlertsForExportAsync(
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var alerts = BuildStockAlertQuery(db, companyCode, branchCode, query);
        var take = Math.Clamp(query.Take <= 0 ? DefaultPageSize : query.Take, 1, MaxExportRows);

        return await ApplyAlertSort(alerts, query).Take(take).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The ONE dispatcher every alert caller goes through, so the grid, the count, the summary and the
    /// export can never disagree about what a rule means.
    /// </summary>
    private static IQueryable<IvStockAlertRow> BuildStockAlertQuery(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvStockAlertQuery query)
    {
        var rule = IvStockAlertRules.Normalize(query.Rule);
        return IvStockAlertRules.IsLotRule(rule)
            ? BuildAlertExpiryQuery(db, companyCode, branchCode, query, rule)
            : BuildAlertItemQuery(db, companyCode, branchCode, query, rule);
    }

    /// <summary>
    /// The item-master-driven alert rows (<c>LOW</c>, <c>OVER</c>, <c>SLOW</c>, <c>DEAD</c>,
    /// <c>NEVER_MOVED</c>).
    ///
    /// <para>
    /// The driver is <c>IvStockMasters</c> LEFT JOIN the aggregated on-hand, and that direction is
    /// load-bearing: an item that has never had a pile must still alert on <c>LOW</c>, which a
    /// balance-driven query cannot see.
    /// </para>
    /// </summary>
    private static IQueryable<IvStockAlertRow> BuildAlertItemQuery(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        string rule)
    {
        var asOf = query.AsOfDate.Date;
        var basis = ThresholdBasis(query);
        var slice = BuildAlertItemSlice(db, companyCode, branchCode, query);

        if (rule == IvStockAlertRules.Over)
        {
            // MaxStock must be configured AND non-zero: a 0 ceiling can never fire because negative
            // stock is impossible, and NULL must not silently become 0.
            return slice
                .Where(x => x.Sm.MaxStock != null && x.Sm.MaxStock.Value != 0m && x.OnHand > x.Sm.MaxStock.Value)
                .Select(x => new IvStockAlertRow
                {
                    ICode = x.Sm.ICode,
                    IDesc = x.Sm.IDesc,
                    IClassCode = x.Sm.IClassCode,
                    StdUom = x.Sm.StdUom,
                    MinStock = x.Sm.MinStock,
                    MaxStock = x.Sm.MaxStock,
                    OnHand = x.OnHand,
                    // Nullable arithmetic: the WHERE already guaranteed non-null, and this keeps the
                    // projection free of a forcing accessor the compiler cannot verify.
                    Variance = x.OnHand - x.Sm.MaxStock,
                    LastMovement = x.LastMovement,
                    ThresholdBasis = basis,
                    CreatedDate = x.Sm.CreatedDate,
                    CreatedBy = x.Sm.CreatedBy,
                    ModifiedDate = x.Sm.ModifiedDate,
                    ModifiedBy = x.Sm.ModifiedBy
                });
        }

        if (rule == IvStockAlertRules.Low)
        {
            return slice
                .Where(x => x.Sm.MinStock != null && x.Sm.MinStock.Value != 0m && x.OnHand < x.Sm.MinStock.Value)
                .Select(x => new IvStockAlertRow
                {
                    ICode = x.Sm.ICode,
                    IDesc = x.Sm.IDesc,
                    IClassCode = x.Sm.IClassCode,
                    StdUom = x.Sm.StdUom,
                    MinStock = x.Sm.MinStock,
                    MaxStock = x.Sm.MaxStock,
                    OnHand = x.OnHand,
                    Variance = x.OnHand - x.Sm.MinStock,
                    LastMovement = x.LastMovement,
                    ThresholdBasis = basis,
                    CreatedDate = x.Sm.CreatedDate,
                    CreatedBy = x.Sm.CreatedBy,
                    ModifiedDate = x.Sm.ModifiedDate,
                    ModifiedBy = x.Sm.ModifiedBy
                });
        }

        if (rule == IvStockAlertRules.NeverMoved)
        {
            // Opening / imported stock: on hand, and no posted movement at all. NOT dead stock — nothing
            // ever moved, so nothing can have gone stale.
            return slice
                .Where(x => x.OnHand > 0m && x.LastMovement == null)
                .Select(x => new IvStockAlertRow
                {
                    ICode = x.Sm.ICode,
                    IDesc = x.Sm.IDesc,
                    IClassCode = x.Sm.IClassCode,
                    StdUom = x.Sm.StdUom,
                    MinStock = x.Sm.MinStock,
                    MaxStock = x.Sm.MaxStock,
                    OnHand = x.OnHand,
                    LastMovement = x.LastMovement,
                    ThresholdBasis = basis,
                    CreatedDate = x.Sm.CreatedDate,
                    CreatedBy = x.Sm.CreatedBy,
                    ModifiedDate = x.Sm.ModifiedDate,
                    ModifiedBy = x.Sm.ModifiedBy
                });
        }

        // SLOW / DEAD share one shape and differ only in the horizon. The bound is a C# date compared
        // against the stored timestamp — never a .Date() call on the column.
        var days = rule == IvStockAlertRules.Dead
            ? Math.Max(1, query.DeadDays)
            : Math.Max(1, query.SlowDays);
        var bound = asOf.AddDays(-days);

        return slice
            .Where(x => x.OnHand > 0m && x.LastMovement != null && x.LastMovement < bound)
            .Select(x => new IvStockAlertRow
            {
                ICode = x.Sm.ICode,
                IDesc = x.Sm.IDesc,
                IClassCode = x.Sm.IClassCode,
                StdUom = x.Sm.StdUom,
                MinStock = x.Sm.MinStock,
                MaxStock = x.Sm.MaxStock,
                OnHand = x.OnHand,
                LastMovement = x.LastMovement,
                ThresholdBasis = basis,
                CreatedDate = x.Sm.CreatedDate,
                CreatedBy = x.Sm.CreatedBy,
                ModifiedDate = x.Sm.ModifiedDate,
                ModifiedBy = x.Sm.ModifiedBy
            });
    }

    /// <summary>
    /// The item-driven slice: <c>IvStockMasters</c> with its on-hand and its last movement as correlated
    /// scalar subqueries.
    ///
    /// <para>
    /// <b>Why subqueries and not LEFT JOINs.</b> A joined aggregate has to be coalesced with a
    /// conditional, and EF Core inlines that conditional into the consuming predicate — producing
    /// <c>a &amp;&amp; b ? 0 : c &lt; d</c>, which cannot be translated. <c>Sum</c> over a
    /// <c>decimal</c> sequence already yields 0 for no rows, and <c>Max</c> over a nullable sequence
    /// yields <c>null</c> for no rows, so neither needs a conditional at all.
    /// </para>
    /// </summary>
    private static IQueryable<IvStockAlertItemSlice> BuildAlertItemSlice(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvStockAlertQuery query)
    {
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        IQueryable<IvBalLoc> piles = db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        if (!string.IsNullOrWhiteSpace(query.WhCode))
        {
            var wh = query.WhCode.Trim();
            piles = piles.Where(x => x.WhCode == wh);
        }

        // Captured in a local on purpose: the movement source must be a queryable the expression tree can
        // compose over. A helper call made inside the projection is an unexpandable method call.
        var movements = IvStockHistoryRepository.MovementsForBranch(db, company, branch);

        IQueryable<IvStockAlertItemSlice> slice =
            from sm in db.IvStockMasters.AsNoTracking()
            where sm.CompanyCode == company
            select new IvStockAlertItemSlice
            {
                Sm = sm,
                OnHand = piles.Where(x => x.ICode == sm.ICode).Sum(x => x.StdQty),
                LastMovement = movements.Where(x => x.ICode == sm.ICode).Max(x => (DateTime?)x.TrxDtTime)
            };

        // Inclusion toggles — true means "do not restrict", false (the default) applies the predicate.
        if (!query.IncludeInactive)
        {
            slice = slice.Where(x => x.Sm.IsActive);
        }

        if (!query.IncludeNonStockControl)
        {
            slice = slice.Where(x => x.Sm.StockControl);
        }

        if (!string.IsNullOrWhiteSpace(query.ICode))
        {
            var code = query.ICode.Trim();
            slice = slice.Where(x => x.Sm.ICode == code);
        }

        if (!string.IsNullOrWhiteSpace(query.IClassCode))
        {
            var cls = query.IClassCode.Trim();
            slice = slice.Where(x => x.Sm.IClassCode == cls);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            slice = slice.Where(x =>
                x.Sm.ICode.Contains(term)
                || (x.Sm.IDesc != null && x.Sm.IDesc.Contains(term)));
        }

        return slice;
    }

    /// <summary>
    /// The lot-driven alert rows (<c>EXPIRING</c>, <c>EXPIRED</c>).
    ///
    /// <para>
    /// The driver is <c>IvLot</c> — non-lot items have no <c>IvLot</c> row and are explicitly out. The
    /// row is one <b>lot per warehouse holding it</b>, because "which lot expires, where is it and how
    /// much is there" is the actionable set; a lot with no pile left in scope is not an alert, since
    /// there is nothing to act on.
    /// </para>
    ///
    /// <para>
    /// The boundary: a lot expiring <b>today is not expired</b>, and a lot expiring exactly
    /// <c>ExpiryDays</c> away IS expiring. Both are computed from the C# <c>AsOfDate</c> parameter with
    /// no <c>.Date</c> call on the column (D14).
    /// </para>
    /// </summary>
    private static IQueryable<IvStockAlertRow> BuildAlertExpiryQuery(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvStockAlertQuery query,
        string rule)
    {
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var asOf = query.AsOfDate.Date;
        var horizon = asOf.AddDays(Math.Max(1, query.ExpiryDays));
        var basis = ThresholdBasis(query);
        var restrictInactive = !query.IncludeInactive;
        var restrictNonStockControl = !query.IncludeNonStockControl;

        IQueryable<IvBalLoc> piles = db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.LotId != null);

        if (!string.IsNullOrWhiteSpace(query.WhCode))
        {
            var wh = query.WhCode.Trim();
            piles = piles.Where(x => x.WhCode == wh);
        }

        var pilesByLot = piles
            .GroupBy(x => new { x.LotId, x.WhCode })
            .Select(g => new IvLotWarehouseQty
            {
                LotId = g.Key.LotId!.Value,
                WhCode = g.Key.WhCode,
                Qty = g.Sum(x => x.StdQty)
            });

        var slice =
            from lot in db.IvLots.AsNoTracking()
            where lot.CompanyCode == company && lot.ExpiryDate != null
            join p in pilesByLot on lot.Id equals p.LotId
            join sm in db.IvStockMasters.AsNoTracking()
                on new { lot.CompanyCode, lot.ICode } equals new { sm.CompanyCode, sm.ICode }
            where !restrictInactive || sm.IsActive
            where !restrictNonStockControl || sm.StockControl
            select new { Lot = lot, Pile = p, Sm = sm };

        if (!string.IsNullOrWhiteSpace(query.ICode))
        {
            var code = query.ICode.Trim();
            slice = slice.Where(x => x.Lot.ICode == code);
        }

        if (!string.IsNullOrWhiteSpace(query.IClassCode))
        {
            var cls = query.IClassCode.Trim();
            slice = slice.Where(x => x.Sm.IClassCode == cls);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            slice = slice.Where(x =>
                x.Lot.ICode.Contains(term)
                || x.Lot.LotNo.Contains(term)
                || (x.Sm.IDesc != null && x.Sm.IDesc.Contains(term)));
        }

        slice = rule == IvStockAlertRules.Expired
            // Already-expired stock is the control exception and is never dropped.
            ? slice.Where(x => x.Lot.ExpiryDate < asOf)
            : slice.Where(x => x.Lot.ExpiryDate >= asOf && x.Lot.ExpiryDate <= horizon);

        return slice.Select(x => new IvStockAlertRow
        {
            ICode = x.Lot.ICode,
            IDesc = x.Sm.IDesc,
            IClassCode = x.Sm.IClassCode,
            StdUom = x.Sm.StdUom,
            MinStock = x.Sm.MinStock,
            MaxStock = x.Sm.MaxStock,
            OnHand = x.Pile.Qty,
            WhCode = x.Pile.WhCode,
            LotNo = x.Lot.LotNo,
            ExpiryDate = x.Lot.ExpiryDate,
            ThresholdBasis = basis,
            CreatedDate = x.Lot.CreatedDate,
            CreatedBy = x.Lot.CreatedBy,
            ModifiedDate = x.Lot.ModifiedDate,
            ModifiedBy = x.Lot.ModifiedBy
        });
    }

    private static string ThresholdBasis(IvStockAlertQuery query) =>
        string.IsNullOrWhiteSpace(query.WhCode) ? AllWarehousesBasis : query.WhCode.Trim();

    /// <summary>
    /// Rule-aware default order, and a whitelist for explicit sorts. A field that does not exist for the
    /// selected rule can never reach the expression tree — it falls back to the rule's own default order.
    /// Every branch ends in a secondary item-code key so the order is TOTAL and paging is safe.
    /// </summary>
    private static IOrderedQueryable<IvStockAlertRow> ApplyAlertSort(
        IQueryable<IvStockAlertRow> alerts,
        IvStockAlertQuery query)
    {
        var rule = IvStockAlertRules.Normalize(query.Rule);
        var field = (query.SortField ?? string.Empty).Trim();
        var desc = query.SortDescending;

        if (!IvStockAlertSortFields.Allowed.Contains(field))
        {
            // Most actionable first: the biggest shortfall/overage, the stalest movement, the nearest
            // expiry — then the item code so the order is total and stable.
            return rule switch
            {
                IvStockAlertRules.Low => alerts.OrderBy(x => x.OnHand).ThenBy(x => x.ICode),
                IvStockAlertRules.Over => alerts.OrderByDescending(x => x.Variance).ThenBy(x => x.ICode),
                IvStockAlertRules.Expiring or IvStockAlertRules.Expired =>
                    alerts.OrderBy(x => x.ExpiryDate).ThenBy(x => x.ICode).ThenBy(x => x.LotNo),
                _ => alerts.OrderBy(x => x.LastMovement).ThenBy(x => x.ICode)
            };
        }

        return field switch
        {
            nameof(IvStockAlertRow.ICode) => desc
                ? alerts.OrderByDescending(x => x.ICode)
                : alerts.OrderBy(x => x.ICode),
            nameof(IvStockAlertRow.IDesc) => desc
                ? alerts.OrderByDescending(x => x.IDesc)
                : alerts.OrderBy(x => x.IDesc),
            nameof(IvStockAlertRow.IClassCode) => desc
                ? alerts.OrderByDescending(x => x.IClassCode)
                : alerts.OrderBy(x => x.IClassCode),
            nameof(IvStockAlertRow.OnHand) => desc
                ? alerts.OrderByDescending(x => x.OnHand)
                : alerts.OrderBy(x => x.OnHand),
            nameof(IvStockAlertRow.MinStock) => desc
                ? alerts.OrderByDescending(x => x.MinStock)
                : alerts.OrderBy(x => x.MinStock),
            nameof(IvStockAlertRow.MaxStock) => desc
                ? alerts.OrderByDescending(x => x.MaxStock)
                : alerts.OrderBy(x => x.MaxStock),
            nameof(IvStockAlertRow.LastMovement) => desc
                ? alerts.OrderByDescending(x => x.LastMovement)
                : alerts.OrderBy(x => x.LastMovement),
            nameof(IvStockAlertRow.ExpiryDate) => desc
                ? alerts.OrderByDescending(x => x.ExpiryDate)
                : alerts.OrderBy(x => x.ExpiryDate),
            nameof(IvStockAlertRow.LotNo) => desc
                ? alerts.OrderByDescending(x => x.LotNo)
                : alerts.OrderBy(x => x.LotNo),
            nameof(IvStockAlertRow.WhCode) => desc
                ? alerts.OrderByDescending(x => x.WhCode)
                : alerts.OrderBy(x => x.WhCode),
            _ => alerts.OrderBy(x => x.ICode)
        };
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  Phase 2 — lot inquiry
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<(IReadOnlyList<IvLotInquiryRow> Rows, int TotalCount)> SearchLotInquiryAsync(
        string companyCode,
        string branchCode,
        IvLotInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildLotSlice(db, companyCode, branchCode, query);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? DefaultPageSize : query.Take, 1, MaxPageSize);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await ApplyLotSort(slice, query).Skip(skip).Take(take)
            .Select(x => new IvLotInquiryRow
            {
                LotId = x.Lot.Id,
                ICode = x.Lot.ICode,
                IDesc = x.Sm != null ? x.Sm.IDesc : null,
                LotNo = x.Lot.LotNo,
                SourceType = x.Lot.SourceType,
                SourceDocNo = x.Lot.SourceDocNo,
                SupplierCode = x.Lot.SupplierCode,
                ReceiptDate = x.Lot.ReceiptDate,
                MfgDate = x.Lot.MfgDate,
                ExpiryDate = x.Lot.ExpiryDate,
                QcStatus = x.Lot.QcStatus,
                Remarks = x.Lot.Remarks,
                IsActive = x.Lot.IsActive,
                OnHandQty = x.OnHandQty,
                PileCount = x.PileCount,
                StdUom = x.Sm != null ? x.Sm.StdUom : null,
                CreatedDate = x.Lot.CreatedDate,
                CreatedBy = x.Lot.CreatedBy,
                ModifiedDate = x.Lot.ModifiedDate,
                ModifiedBy = x.Lot.ModifiedBy
            })
            .ToListAsync(cancellationToken);

        return (rows, total);
    }

    public async Task<IvLotInquirySummary> SummariseLotInquiryAsync(
        string companyCode,
        string branchCode,
        IvLotInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildLotSlice(db, companyCode, branchCode, query);
        var asOf = query.AsOfDate.Date;

        return new IvLotInquirySummary
        {
            TotalRows = await slice.CountAsync(cancellationToken),
            ItemCount = await slice.Select(x => x.Lot.ICode).Distinct().CountAsync(cancellationToken),
            ExpiredCount = await slice.CountAsync(
                x => x.Lot.ExpiryDate != null && x.Lot.ExpiryDate < asOf, cancellationToken),
            NoExpiryCount = await slice.CountAsync(x => x.Lot.ExpiryDate == null, cancellationToken),
            TotalOnHandQty = await slice.SumAsync(x => x.OnHandQty, cancellationToken)
        };
    }

    public async Task<IReadOnlyList<IvLotPileRow>> ListLotPilesAsync(
        string companyCode,
        string branchCode,
        int lotId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        if (lotId <= 0)
        {
            return [];
        }

        return await (
            from bal in db.IvBalLocs.AsNoTracking()
            where bal.CompanyCode == company && bal.BranchCode == branch && bal.LotId == lotId
            join wh in db.IvWarehouses.AsNoTracking()
                on new { bal.CompanyCode, bal.BranchCode, WhCode = bal.WhCode }
                equals new { wh.CompanyCode, wh.BranchCode, WhCode = wh.WarehouseCode }
                into whs
            from wh in whs.DefaultIfEmpty()
            join lot in db.IvLots.AsNoTracking()
                on bal.LotId equals lot.Id
                into lots
            from lot in lots.DefaultIfEmpty()
            orderby bal.WhCode, bal.LocCode, bal.IStatus, bal.Id
            select new IvLotPileRow
            {
                Id = bal.Id,
                WhCode = bal.WhCode,
                WhDesc = wh != null ? wh.WarehouseDesc : null,
                LocCode = bal.LocCode,
                IStatus = bal.IStatus,
                StdQty = bal.StdQty,
                StdUom = bal.StdUom,
                TransDate = bal.TransDate,
                ExpiryDate = lot != null ? lot.ExpiryDate : null,
                RefNo = bal.RefNo,
                PoNo = bal.PoNo,
                Remarks = bal.Remarks,
                CreatedDate = bal.CreatedDate,
                CreatedBy = bal.CreatedBy,
                ModifiedDate = bal.ModifiedDate
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The lot slice: <c>IvLot</c> LEFT JOIN its item master, with the on-hand quantity and pile count as
    /// correlated scalar subqueries over the branch's piles.
    ///
    /// <para>
    /// A lot with no stock left still appears (its history is the audit trail), and the item master is a
    /// LEFT JOIN so a lot whose item row is missing is not silently dropped from the listing.
    /// </para>
    /// </summary>
    private static IQueryable<IvLotSlice> BuildLotSlice(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvLotInquiryQuery query)
    {
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        IQueryable<IvBalLoc> piles = db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.LotId != null);

        IQueryable<IvLotSlice> slice =
            from lot in db.IvLots.AsNoTracking()
            where lot.CompanyCode == company
            join sm in db.IvStockMasters.AsNoTracking()
                on new { lot.CompanyCode, lot.ICode } equals new { sm.CompanyCode, sm.ICode }
                into sms
            from sm in sms.DefaultIfEmpty()
            select new IvLotSlice
            {
                Lot = lot,
                Sm = sm,
                // Sum over a decimal sequence is 0 for no rows and Count is 0 — no conditional needed,
                // which is what keeps these usable in a WHERE/ORDER BY alongside the row columns.
                OnHandQty = piles.Where(x => x.LotId == lot.Id).Sum(x => x.StdQty),
                PileCount = piles.Count(x => x.LotId == lot.Id)
            };

        if (!query.IncludeInactive)
        {
            slice = slice.Where(x => x.Lot.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.ICode))
        {
            var code = query.ICode.Trim();
            slice = slice.Where(x => x.Lot.ICode == code);
        }

        if (!string.IsNullOrWhiteSpace(query.LotNo))
        {
            var lotNo = query.LotNo.Trim();
            slice = slice.Where(x => x.Lot.LotNo.Contains(lotNo));
        }

        if (!string.IsNullOrWhiteSpace(query.QcStatus))
        {
            var qc = query.QcStatus.Trim();
            slice = slice.Where(x => x.Lot.QcStatus == qc);
        }

        // Half-open: >= from.Date and < to.Date.AddDays(1). No .Date on the column.
        if (query.ExpiryFrom is DateTime from)
        {
            var fromBound = from.Date;
            slice = slice.Where(x => x.Lot.ExpiryDate != null && x.Lot.ExpiryDate >= fromBound);
        }

        if (query.ExpiryTo is DateTime to)
        {
            var toBound = to.Date.AddDays(1);
            slice = slice.Where(x => x.Lot.ExpiryDate != null && x.Lot.ExpiryDate < toBound);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            slice = slice.Where(x =>
                x.Lot.ICode.Contains(term)
                || x.Lot.LotNo.Contains(term)
                || (x.Sm != null && x.Sm.IDesc != null && x.Sm.IDesc.Contains(term))
                || (x.Lot.SourceDocNo != null && x.Lot.SourceDocNo.Contains(term))
                || (x.Lot.SupplierCode != null && x.Lot.SupplierCode.Contains(term)));
        }

        return slice;
    }

    private static IOrderedQueryable<IvLotSlice> ApplyLotSort(IQueryable<IvLotSlice> slice, IvLotInquiryQuery query)
    {
        var field = (query.SortField ?? string.Empty).Trim();
        if (!IvLotInquirySortFields.Allowed.Contains(field))
        {
            // Default: most recently received first, then item + lot so the order is total.
            return slice
                .OrderByDescending(x => x.Lot.ReceiptDate)
                .ThenBy(x => x.Lot.ICode)
                .ThenBy(x => x.Lot.LotNo);
        }

        return field switch
        {
            nameof(IvLotInquiryRow.ICode) => query.SortDescending
                ? slice.OrderByDescending(x => x.Lot.ICode).ThenBy(x => x.Lot.LotNo)
                : slice.OrderBy(x => x.Lot.ICode).ThenBy(x => x.Lot.LotNo),
            nameof(IvLotInquiryRow.IDesc) => query.SortDescending
                ? slice.OrderByDescending(x => x.Sm != null ? x.Sm.IDesc : null).ThenBy(x => x.Lot.ICode)
                : slice.OrderBy(x => x.Sm != null ? x.Sm.IDesc : null).ThenBy(x => x.Lot.ICode),
            nameof(IvLotInquiryRow.LotNo) => query.SortDescending
                ? slice.OrderByDescending(x => x.Lot.LotNo)
                : slice.OrderBy(x => x.Lot.LotNo),
            nameof(IvLotInquiryRow.ReceiptDate) => query.SortDescending
                ? slice.OrderByDescending(x => x.Lot.ReceiptDate).ThenBy(x => x.Lot.ICode)
                : slice.OrderBy(x => x.Lot.ReceiptDate).ThenBy(x => x.Lot.ICode),
            nameof(IvLotInquiryRow.ExpiryDate) => query.SortDescending
                ? slice.OrderByDescending(x => x.Lot.ExpiryDate).ThenBy(x => x.Lot.ICode)
                : slice.OrderBy(x => x.Lot.ExpiryDate).ThenBy(x => x.Lot.ICode),
            nameof(IvLotInquiryRow.QcStatus) => query.SortDescending
                ? slice.OrderByDescending(x => x.Lot.QcStatus).ThenBy(x => x.Lot.ICode)
                : slice.OrderBy(x => x.Lot.QcStatus).ThenBy(x => x.Lot.ICode),
            nameof(IvLotInquiryRow.SourceDocNo) => query.SortDescending
                ? slice.OrderByDescending(x => x.Lot.SourceDocNo).ThenBy(x => x.Lot.ICode)
                : slice.OrderBy(x => x.Lot.SourceDocNo).ThenBy(x => x.Lot.ICode),
            nameof(IvLotInquiryRow.OnHandQty) => query.SortDescending
                ? slice.OrderByDescending(x => x.OnHandQty).ThenBy(x => x.Lot.ICode)
                : slice.OrderBy(x => x.OnHandQty).ThenBy(x => x.Lot.ICode),
            _ => slice.OrderByDescending(x => x.Lot.ReceiptDate).ThenBy(x => x.Lot.ICode)
        };
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  Phase 2 — stock summary
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<(IReadOnlyList<IvStockSummaryRow> Rows, int TotalCount)> SearchStockSummaryAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var groups = BuildStockSummaryGroupQuery(db, companyCode, branchCode, query);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? DefaultPageSize : query.Take, 1, MaxPageSize);

        var total = await groups.CountAsync(cancellationToken);
        var rows = await ApplySummarySort(groups, query).Skip(skip).Take(take).ToListAsync(cancellationToken);

        return (rows, total);
    }

    public async Task<int> CountStockSummaryAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildStockSummaryGroupQuery(db, companyCode, branchCode, query)
            .CountAsync(cancellationToken);
    }

    public async Task<IvStockSummarySummary> SummariseStockSummaryAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var groupCount = await BuildStockSummaryGroupQuery(db, companyCode, branchCode, query)
            .CountAsync(cancellationToken);

        // Grouping only partitions the piles, so these measures are mode-independent and are computed
        // once over the FLAT slice — still SQL-side, never a materialised row set.
        var flat = BuildStockSummarySlice(db, companyCode, branchCode, query);

        var itemCount = await flat.Select(x => x.ICode).Distinct().CountAsync(cancellationToken);
        var pileCount = await flat.CountAsync(cancellationToken);
        var zeroQtyPileCount = await flat.CountAsync(x => x.StdQty == 0m, cancellationToken);
        var totalQty = await flat.SumAsync(x => x.StdQty, cancellationToken);

        // The locked estimate formula (D5/D13): bal.UnitPrice ?? item.PurchasePrice ?? 0, inlined
        // because a plain C# helper cannot appear inside an expression tree.
        var totalValue = await flat.SumAsync(
            x => x.StdQty * (x.UnitPrice ?? x.PurchasePrice ?? 0m),
            cancellationToken);

        return new IvStockSummarySummary
        {
            GroupCount = groupCount,
            ItemCount = itemCount,
            PileCount = pileCount,
            ZeroQtyPileCount = zeroQtyPileCount,
            TotalQty = totalQty,
            TotalValue = totalValue
        };
    }

    public async Task<IReadOnlyList<IvStockSummaryRow>> ListStockSummaryForExportAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var groups = BuildStockSummaryGroupQuery(db, companyCode, branchCode, query);
        var take = Math.Clamp(query.Take <= 0 ? DefaultPageSize : query.Take, 1, MaxExportRows);

        return await ApplySummarySort(groups, query).Take(take).ToListAsync(cancellationToken);
    }

    /// <summary>Dispatches to the grouping branch the query asked for. All four are SQL-side GROUP BY.</summary>
    private static IQueryable<IvStockSummaryRow> BuildStockSummaryGroupQuery(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query)
    {
        var slice = BuildStockSummarySlice(db, companyCode, branchCode, query);

        return IvStockSummaryGroupBys.Normalize(query.GroupBy) switch
        {
            IvStockSummaryGroupBys.Warehouse =>
                slice.GroupBy(x => new { x.WhCode, x.WhDesc })
                    .Select(g => new IvStockSummaryRow
                    {
                        WhCode = g.Key.WhCode,
                        WhDesc = g.Key.WhDesc,
                        TotalQty = g.Sum(x => x.StdQty),
                        ItemCount = g.Select(x => x.ICode).Distinct().Count(),
                        PileCount = g.Count(),
                        ZeroQtyPileCount = g.Count(x => x.StdQty == 0m),
                        EstValue = g.Sum(x => x.StdQty * (x.UnitPrice ?? x.PurchasePrice ?? 0m))
                    }),

            IvStockSummaryGroupBys.Class =>
                slice.GroupBy(x => new { x.IClassCode, x.IClassDesc })
                    .Select(g => new IvStockSummaryRow
                    {
                        IClassCode = g.Key.IClassCode,
                        IClassDesc = g.Key.IClassDesc,
                        TotalQty = g.Sum(x => x.StdQty),
                        ItemCount = g.Select(x => x.ICode).Distinct().Count(),
                        PileCount = g.Count(),
                        ZeroQtyPileCount = g.Count(x => x.StdQty == 0m),
                        EstValue = g.Sum(x => x.StdQty * (x.UnitPrice ?? x.PurchasePrice ?? 0m))
                    }),

            IvStockSummaryGroupBys.ItemWarehouse =>
                slice.GroupBy(x => new { x.ICode, x.IDesc, x.StdUom, x.WhCode })
                    .Select(g => new IvStockSummaryRow
                    {
                        ICode = g.Key.ICode,
                        IDesc = g.Key.IDesc,
                        StdUom = g.Key.StdUom,
                        WhCode = g.Key.WhCode,
                        TotalQty = g.Sum(x => x.StdQty),
                        ItemCount = 1,
                        PileCount = g.Count(),
                        ZeroQtyPileCount = g.Count(x => x.StdQty == 0m),
                        EstValue = g.Sum(x => x.StdQty * (x.UnitPrice ?? x.PurchasePrice ?? 0m))
                    }),

            // Item (the default).
            _ =>
                slice.GroupBy(x => new { x.ICode, x.IDesc, x.IClassCode, x.StdUom })
                    .Select(g => new IvStockSummaryRow
                    {
                        ICode = g.Key.ICode,
                        IDesc = g.Key.IDesc,
                        IClassCode = g.Key.IClassCode,
                        StdUom = g.Key.StdUom,
                        TotalQty = g.Sum(x => x.StdQty),
                        ItemCount = 1,
                        PileCount = g.Count(),
                        ZeroQtyPileCount = g.Count(x => x.StdQty == 0m),
                        EstValue = g.Sum(x => x.StdQty * (x.UnitPrice ?? x.PurchasePrice ?? 0m))
                    })
        };
    }

    /// <summary>
    /// The flat balance slice behind every grouping: <c>IvBalLoc</c> INNER JOIN the item master (a pile
    /// whose item row is gone is not a summary row anyone can act on) LEFT JOIN the warehouse and the item
    /// class.
    ///
    /// <para>
    /// <b>Flat scalars, not entity references, and that is load-bearing.</b> The first implementation
    /// projected a slice carrying <c>IvBalLoc</c>/<c>IvStockMaster</c>/<c>IvWarehouse</c>/<c>IvClass</c>
    /// references through four <c>DefaultIfEmpty</c> joins and then grouped it; EF Core cannot translate a
    /// <c>GROUP BY</c> over that TransparentIdentifier chain. A projection of plain columns also removes
    /// every conditional from the grouping keys, because a LEFT JOIN already yields <c>NULL</c>.
    /// </para>
    ///
    /// <para>
    /// Filters follow the <see cref="IvBalanceLotQuery"/> inclusion convention, and the tenant predicate is
    /// the same one the shipped balance inquiry applies.
    /// </para>
    /// </summary>
    private static IQueryable<IvStockSummaryFlat> BuildStockSummarySlice(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query)
    {
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        IQueryable<IvStockSummaryFlat> slice =
            from bal in db.IvBalLocs.AsNoTracking()
            join sm in db.IvStockMasters.AsNoTracking()
                on new { bal.CompanyCode, bal.ICode } equals new { sm.CompanyCode, sm.ICode }
            join wh in db.IvWarehouses.AsNoTracking()
                on new { bal.CompanyCode, bal.BranchCode, WhCode = bal.WhCode }
                equals new { wh.CompanyCode, wh.BranchCode, WhCode = wh.WarehouseCode }
                into whs
            from wh in whs.DefaultIfEmpty()
            join cls in db.IvClasses.AsNoTracking()
                on new { bal.CompanyCode, IClassCode = sm.IClassCode }
                equals new { cls.CompanyCode, cls.IClassCode }
                into clss
            from cls in clss.DefaultIfEmpty()
            where bal.CompanyCode == company && bal.BranchCode == branch
            select new IvStockSummaryFlat
            {
                ICode = bal.ICode,
                IDesc = sm.IDesc,
                StdUom = sm.StdUom,
                IClassCode = sm.IClassCode,
                IClassDesc = cls.IDesc,
                WhCode = bal.WhCode,
                WhDesc = wh.WarehouseDesc,
                StdQty = bal.StdQty,
                UnitPrice = bal.UnitPrice,
                PurchasePrice = sm.PurchasePrice,
                IStatus = bal.IStatus,
                IsActive = sm.IsActive,
                StockControl = sm.StockControl
            };

        if (!query.IncludeZeroQty)
        {
            slice = slice.Where(x => x.StdQty > 0m);
        }

        if (!query.IncludeInactive)
        {
            slice = slice.Where(x => x.IsActive);
        }

        if (!query.IncludeNonStockControl)
        {
            slice = slice.Where(x => x.StockControl);
        }

        if (!string.IsNullOrWhiteSpace(query.ICode))
        {
            var code = query.ICode.Trim();
            slice = slice.Where(x => x.ICode == code);
        }

        if (!string.IsNullOrWhiteSpace(query.WhCode))
        {
            var wh = query.WhCode.Trim();
            slice = slice.Where(x => x.WhCode == wh);
        }

        if (!string.IsNullOrWhiteSpace(query.IClassCode))
        {
            var cls = query.IClassCode.Trim();
            slice = slice.Where(x => x.IClassCode == cls);
        }

        var statuses = CleanStatuses(query.IStatuses);
        if (statuses.Count > 0)
        {
            // The status lives on the pile, which the flat row no longer carries — so it is applied on the
            // pre-projection source and matched by the pile's own columns.
            slice = slice.Where(x => statuses.Contains(x.IStatus));
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            slice = slice.Where(x =>
                x.ICode.Contains(term)
                || (x.IDesc != null && x.IDesc.Contains(term))
                || x.WhCode.Contains(term));
        }

        return slice;
    }

    /// <summary>
    /// A grouped query can only be ordered by its own grouping keys and aggregates, so the legal sort
    /// fields are PER GROUPING MODE. A field outside that set falls back to the mode's default order
    /// instead of reaching the expression tree — ordering by an ungrouped column is invalid SQL and EF
    /// (correctly) refuses to translate it.
    /// </summary>
    private static IOrderedQueryable<IvStockSummaryRow> ApplySummarySort(
        IQueryable<IvStockSummaryRow> groups,
        IvStockSummaryQuery query)
    {
        var mode = IvStockSummaryGroupBys.Normalize(query.GroupBy);
        var field = (query.SortField ?? string.Empty).Trim();
        var descending = query.SortDescending;

        if (!LegalSummarySortFields(mode).Contains(field))
        {
            return DefaultSummaryOrder(groups, mode);
        }

        return field switch
        {
            nameof(IvStockSummaryRow.ICode) => descending
                ? groups.OrderByDescending(x => x.ICode)
                : groups.OrderBy(x => x.ICode),
            nameof(IvStockSummaryRow.IDesc) => descending
                ? groups.OrderByDescending(x => x.IDesc)
                : groups.OrderBy(x => x.IDesc),
            nameof(IvStockSummaryRow.StdUom) => descending
                ? groups.OrderByDescending(x => x.StdUom)
                : groups.OrderBy(x => x.StdUom),
            nameof(IvStockSummaryRow.WhCode) => descending
                ? groups.OrderByDescending(x => x.WhCode)
                : groups.OrderBy(x => x.WhCode),
            nameof(IvStockSummaryRow.WhDesc) => descending
                ? groups.OrderByDescending(x => x.WhDesc)
                : groups.OrderBy(x => x.WhDesc),
            nameof(IvStockSummaryRow.IClassCode) => descending
                ? groups.OrderByDescending(x => x.IClassCode)
                : groups.OrderBy(x => x.IClassCode),
            nameof(IvStockSummaryRow.IClassDesc) => descending
                ? groups.OrderByDescending(x => x.IClassDesc)
                : groups.OrderBy(x => x.IClassDesc),
            nameof(IvStockSummaryRow.TotalQty) => descending
                ? groups.OrderByDescending(x => x.TotalQty)
                : groups.OrderBy(x => x.TotalQty),
            nameof(IvStockSummaryRow.ItemCount) => descending
                ? groups.OrderByDescending(x => x.ItemCount)
                : groups.OrderBy(x => x.ItemCount),
            nameof(IvStockSummaryRow.PileCount) => descending
                ? groups.OrderByDescending(x => x.PileCount)
                : groups.OrderBy(x => x.PileCount),
            nameof(IvStockSummaryRow.ZeroQtyPileCount) => descending
                ? groups.OrderByDescending(x => x.ZeroQtyPileCount)
                : groups.OrderBy(x => x.ZeroQtyPileCount),
            nameof(IvStockSummaryRow.EstValue) => descending
                ? groups.OrderByDescending(x => x.EstValue)
                : groups.OrderBy(x => x.EstValue),
            _ => DefaultSummaryOrder(groups, mode)
        };
    }

    /// <summary>
    /// The columns a grouped query for this mode is ALLOWED to order by: its grouping keys plus its
    /// aggregates. Deriving it from the mode (rather than from one shared whitelist) is what stops a
    /// "warehouse" sort being requested on an Item group, which no index or plan could satisfy.
    /// </summary>
    private static HashSet<string> LegalSummarySortFields(string mode) => mode switch
    {
        IvStockSummaryGroupBys.Warehouse => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(IvStockSummaryRow.WhCode),
            nameof(IvStockSummaryRow.WhDesc),
            nameof(IvStockSummaryRow.TotalQty),
            nameof(IvStockSummaryRow.ItemCount),
            nameof(IvStockSummaryRow.PileCount),
            nameof(IvStockSummaryRow.ZeroQtyPileCount),
            nameof(IvStockSummaryRow.EstValue)
        },
        IvStockSummaryGroupBys.Class => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(IvStockSummaryRow.IClassCode),
            nameof(IvStockSummaryRow.IClassDesc),
            nameof(IvStockSummaryRow.TotalQty),
            nameof(IvStockSummaryRow.ItemCount),
            nameof(IvStockSummaryRow.PileCount),
            nameof(IvStockSummaryRow.ZeroQtyPileCount),
            nameof(IvStockSummaryRow.EstValue)
        },
        IvStockSummaryGroupBys.ItemWarehouse => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(IvStockSummaryRow.ICode),
            nameof(IvStockSummaryRow.IDesc),
            nameof(IvStockSummaryRow.StdUom),
            nameof(IvStockSummaryRow.WhCode),
            nameof(IvStockSummaryRow.TotalQty),
            nameof(IvStockSummaryRow.PileCount),
            nameof(IvStockSummaryRow.ZeroQtyPileCount),
            nameof(IvStockSummaryRow.EstValue)
        },
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(IvStockSummaryRow.ICode),
            nameof(IvStockSummaryRow.IDesc),
            nameof(IvStockSummaryRow.IClassCode),
            nameof(IvStockSummaryRow.StdUom),
            nameof(IvStockSummaryRow.TotalQty),
            nameof(IvStockSummaryRow.PileCount),
            nameof(IvStockSummaryRow.ZeroQtyPileCount),
            nameof(IvStockSummaryRow.EstValue)
        }
    };

    /// <summary>
    /// The default order per grouping mode. It must be TOTAL and built only from that mode's grouping
    /// keys: a grouped query with a non-deterministic order cannot be paged safely, and ordering by an
    /// ungrouped column is not valid SQL.
    /// </summary>
    private static IOrderedQueryable<IvStockSummaryRow> DefaultSummaryOrder(
        IQueryable<IvStockSummaryRow> groups,
        string? groupBy) =>
        IvStockSummaryGroupBys.Normalize(groupBy) switch
        {
            IvStockSummaryGroupBys.Warehouse => groups.OrderBy(x => x.WhCode),
            IvStockSummaryGroupBys.Class => groups.OrderBy(x => x.IClassCode),
            IvStockSummaryGroupBys.ItemWarehouse => groups.OrderBy(x => x.ICode).ThenBy(x => x.WhCode),
            _ => groups.OrderBy(x => x.ICode)
        };

    private static List<string> CleanStatuses(IReadOnlyList<string>? statuses) =>
        (statuses ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

// ── Internal slice / aggregate shapes (query compositions, never materialised DTOs) ─────────────────

/// <summary>One lot's aggregated on-hand in one warehouse (the expiry-alert row grain).</summary>
public sealed class IvLotWarehouseQty
{
    public int LotId { get; set; }
    public string WhCode { get; set; } = string.Empty;
    public decimal Qty { get; set; }
}

/// <summary>
/// Item-master-driven alert slice. The driver is the <c>IvStockMaster</c> row; the two measures are
/// correlated scalar subqueries, so an item with no pile still produces a row with <c>OnHand = 0</c>.
/// </summary>
public sealed class IvStockAlertItemSlice
{
    public IvStockMaster Sm { get; set; } = null!;
    public decimal OnHand { get; set; }
    public DateTime? LastMovement { get; set; }
}

/// <summary>Lot-driven slice for the lot inquiry: the lot, its item master and its on-hand aggregate.</summary>
public sealed class IvLotSlice
{
    public IvLot Lot { get; set; } = null!;
    public IvStockMaster? Sm { get; set; }
    public decimal OnHandQty { get; set; }
    public int PileCount { get; set; }
}

/// <summary>
/// Flat balance row for the stock summary. Deliberately carries NO entity references: a
/// <c>GROUP BY</c> over the TransparentIdentifier chain produced by <c>DefaultIfEmpty</c> joins cannot be
/// translated by EF Core, and a LEFT JOIN already yields <c>NULL</c>, so no grouping key needs a
/// conditional.
/// </summary>
public sealed class IvStockSummaryFlat
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? StdUom { get; set; }
    public string? IClassCode { get; set; }
    public string? IClassDesc { get; set; }
    public string WhCode { get; set; } = string.Empty;
    public string? WhDesc { get; set; }
    public string IStatus { get; set; } = string.Empty;
    public decimal StdQty { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? PurchasePrice { get; set; }
    public bool IsActive { get; set; }
    public bool StockControl { get; set; }
}
