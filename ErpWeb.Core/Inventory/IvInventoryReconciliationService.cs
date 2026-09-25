using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class IvInventoryReconcileFinding
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int? BalLocId { get; init; }
    public int? HistoryId { get; init; }
    public string? Slice { get; init; }
    public decimal? BalLocQty { get; init; }
    public decimal? HistoryNetQty { get; init; }
}

public sealed class IvInventoryReconcileResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public bool HasIntegrityErrors => Findings.Count > 0;
    public string Status => HasIntegrityErrors
        ? "INVENTORY DATA INTEGRITY ERROR"
        : "OK";
    public IReadOnlyList<IvInventoryReconcileFinding> Findings { get; init; } = [];

    public static IvInventoryReconcileResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };

    public static IvInventoryReconcileResult Ok(IReadOnlyList<IvInventoryReconcileFinding> findings) =>
        new() { Succeeded = true, Findings = findings };
}

/// <summary>
/// Diagnostic only. OpeningQty=0 is valid for empty test DBs — do not enable as production
/// stock-audit until an opening-balance baseline exists.
///
/// <para>
/// ACCESS-gated on <c>INV_RECONCILIATION</c>: the findings name piles and history rows, so reading them
/// is a right of its own and must not be reachable from another inventory screen.
/// </para>
/// </summary>
public interface IIvInventoryReconciliationService
{
    /// <summary>Reconcile under the reconciliation screen's own grant.</summary>
    Task<IvInventoryReconcileResult> ReconcileAsync(
        string? iCode = null,
        string? whCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconcile under the caller's own menu. The period-close workflow reuses this same diagnostic
    /// under <c>INV_PERIOD_CLOSE</c> as its blocking precondition, so a close cannot borrow the
    /// reconciliation screen's grant (and vice versa).
    /// </summary>
    Task<IvInventoryReconcileResult> ReconcileAsync(
        string menuCode,
        string? iCode,
        string? whCode,
        CancellationToken cancellationToken = default);
}

public sealed class IvInventoryReconciliationService : IIvInventoryReconciliationService
{
    /// <summary>
    /// Menus this service is allowed to serve — the reconciliation screen and the period-close
    /// workflow (which uses the same diagnostic as its blocking precondition).
    /// </summary>
    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.InventoryReconciliation,
        MenuCodes.InventoryPeriodClose
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public IvInventoryReconciliationService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<IvInventoryReconcileResult> ReconcileAsync(
        string? iCode = null,
        string? whCode = null,
        CancellationToken cancellationToken = default)
        => await ReconcileAsync(MenuCodes.InventoryReconciliation, iCode, whCode, cancellationToken);

    public async Task<IvInventoryReconcileResult> ReconcileAsync(
        string menuCode,
        string? iCode,
        string? whCode,
        CancellationToken cancellationToken = default)
    {
        // Tenant first (fail closed), then ACCESS on the CALLER's menu — the same order every other
        // inquiry service uses, so a caller can never learn of another company's data by being
        // denied the permission.
        var context = await IvInquiryScopeResolver.ResolveAsync(
            _tenant, _accessRights, menuCode, KnownMenus, cancellationToken);
        if (!context.Succeeded)
        {
            return IvInventoryReconcileResult.Fail(context.Error!);
        }

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var itemFilter = string.IsNullOrWhiteSpace(iCode) ? null : iCode.Trim();
        var whFilter = string.IsNullOrWhiteSpace(whCode) ? null : whCode.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var findings = new List<IvInventoryReconcileFinding>();

        var balQuery = db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);
        if (itemFilter is not null)
        {
            balQuery = balQuery.Where(x => x.ICode == itemFilter);
        }

        if (whFilter is not null)
        {
            balQuery = balQuery.Where(x => x.WhCode == whFilter);
        }

        var balances = await balQuery.ToListAsync(cancellationToken);

        // Duplicate slices (legacy)
        foreach (var dup in balances
                     .GroupBy(x => IvStockSliceKey.Create(
                         x.CompanyCode, x.BranchCode, x.ICode, x.WhCode, x.LocCode, x.LotNo, x.IStatus))
                     .Where(g => g.Count() > 1))
        {
            findings.Add(new IvInventoryReconcileFinding
            {
                Code = "DUPLICATE_SLICE",
                Message = $"Duplicate balance rows for slice {dup.Key}.",
                Slice = dup.Key.ToString()
            });
        }

        var historyQuery = db.IvTrxHistories.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);
        if (itemFilter is not null)
        {
            historyQuery = historyQuery.Where(x => x.ICode == itemFilter);
        }

        var histories = await historyQuery.ToListAsync(cancellationToken);

        // The orphan check asks whether a pile EXISTS, so it is resolved against the WHOLE branch — never
        // against the filtered set. Resolving it against the filtered set would report every transfer
        // that came from another warehouse (or another item) as "orphaned history": a false positive
        // manufactured by asking a narrower question than the one the check means.
        var branchBalLocIds = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var balIds = branchBalLocIds.ToHashSet();

        // Orphan history: stock-controlled (has BalLoc FK) but missing BalLoc
        foreach (var h in histories)
        {
            if (h.ToBalLocId is int toId && !balIds.Contains(toId)
                && (itemFilter is null || string.Equals(h.ICode, itemFilter, StringComparison.OrdinalIgnoreCase))
                && (whFilter is null || string.Equals(h.ToWarehouse, whFilter, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new IvInventoryReconcileFinding
                {
                    Code = "ORPHAN_HISTORY",
                    Message = $"History Id {h.Id} ToBalLocId {toId} is missing.",
                    HistoryId = h.Id,
                    BalLocId = toId
                });
            }

            if (h.FromBalLocId is int fromId && !balIds.Contains(fromId)
                && (itemFilter is null || string.Equals(h.ICode, itemFilter, StringComparison.OrdinalIgnoreCase))
                && (whFilter is null || string.Equals(h.FrWarehouse, whFilter, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new IvInventoryReconcileFinding
                {
                    Code = "ORPHAN_HISTORY",
                    Message = $"History Id {h.Id} FromBalLocId {fromId} is missing.",
                    HistoryId = h.Id,
                    BalLocId = fromId
                });
            }
        }

        // Non-stock history (null BalLoc FKs) is valid — ignore for on-hand.
        //
        // D18: BOTH sides are aggregated on the same 7-part slice key, never row-by-row. Per-BalLoc
        // comparison manufactures false discrepancies the moment a legacy database holds two balance
        // rows for one slice (a defect this service also reports as DUPLICATE_SLICE), because each row
        // would be measured against the whole of that slice's history. The slice a history leg belongs
        // to is resolved through the BalLoc it points at, so a leg can never be attributed to a slice
        // by string inference.
        var sliceById = new Dictionary<int, IvStockSliceKey>();
        foreach (var bal in balances)
        {
            sliceById[bal.Id] = IvStockSliceKey.Create(
                bal.CompanyCode, bal.BranchCode, bal.ICode, bal.WhCode, bal.LocCode, bal.LotNo, bal.IStatus);
        }

        var balQtyBySlice = new Dictionary<IvStockSliceKey, decimal>();
        var balIdsBySlice = new Dictionary<IvStockSliceKey, List<int>>();
        foreach (var bal in balances)
        {
            var key = sliceById[bal.Id];
            balQtyBySlice[key] = balQtyBySlice.GetValueOrDefault(key) + bal.StdQty;
            if (!balIdsBySlice.TryGetValue(key, out var ids))
            {
                ids = [];
                balIdsBySlice[key] = ids;
            }

            ids.Add(bal.Id);
        }

        var netBySlice = new Dictionary<IvStockSliceKey, decimal>();
        foreach (var h in histories)
        {
            if (h.ToBalLocId is int toId && sliceById.TryGetValue(toId, out var toSlice))
            {
                netBySlice[toSlice] = netBySlice.GetValueOrDefault(toSlice) + (h.ToStdQty ?? 0m);
            }

            if (h.FromBalLocId is int fromId && sliceById.TryGetValue(fromId, out var fromSlice))
            {
                netBySlice[fromSlice] = netBySlice.GetValueOrDefault(fromSlice) - (h.FrStdQty ?? 0m);
            }
        }

        // Ordered by slice so a run's findings are stable and diffable between runs.
        foreach (var (slice, balQty) in balQtyBySlice.OrderBy(x => x.Key))
        {
            var net = netBySlice.GetValueOrDefault(slice);
            var std = IvQty.Round(balQty);
            var netRounded = IvQty.Round(net);
            var balanceIds = balIdsBySlice[slice];

            if (std != netRounded)
            {
                findings.Add(new IvInventoryReconcileFinding
                {
                    Code = "MISMATCH",
                    Message = balanceIds.Count == 1
                        ? $"BalLoc {balanceIds[0]} StdQty {std} != history net {netRounded}."
                        : $"Slice {slice} has {balanceIds.Count} balance rows totalling {std}, which != history net {netRounded}.",
                    BalLocId = balanceIds.Min(),
                    BalLocQty = std,
                    HistoryNetQty = netRounded,
                    Slice = slice.ToString()
                });
            }

            if (!netBySlice.ContainsKey(slice) && std != 0m)
            {
                findings.Add(new IvInventoryReconcileFinding
                {
                    Code = "UNEXPECTED_BALANCE",
                    Message = $"BalLoc {balanceIds.Min()} has qty {std} with no posted stock history (OpeningQty assumed 0).",
                    BalLocId = balanceIds.Min(),
                    BalLocQty = std,
                    HistoryNetQty = 0m,
                    Slice = slice.ToString()
                });
            }
        }

        // ── Stock-count document vs. its ADJ batch ────────────────────────────────────────────────
        // Two rollback routes exist on purpose (the count screen and the Stock Adjustment list), and
        // D8 says that is safe BECAUSE divergence is detected. This is that detector.
        var countHeaders = await db.IvStockCountHdrs
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new
            {
                x.Id,
                x.CountNo,
                x.Status,
                x.PostedBatchNo
            })
            .ToListAsync(cancellationToken);

        var countIds = countHeaders.Select(x => x.Id).ToList();
        var countLines = countIds.Count == 0
            ? []
            : await db.IvStockCountLines
                .AsNoTracking()
                .Where(l => countIds.Contains(l.StockCountId))
                .Select(l => new { l.StockCountId, l.PhysicalQty, l.SystemQty })
                .ToListAsync(cancellationToken);

        var linesByCount = countLines
            .GroupBy(l => l.StockCountId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var countedBatchNos = countHeaders
            .Where(x => x.PostedBatchNo is not null)
            .Select(x => x.PostedBatchNo!.Value)
            .Distinct()
            .ToList();

        var batchStatuses = countedBatchNos.Count == 0
            ? new Dictionary<int, string>()
            : await db.IvTrxBatches
                .AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                            && countedBatchNos.Contains(x.BatchNo))
                .ToDictionaryAsync(x => x.BatchNo, x => x.BatchStatus, cancellationToken);

        foreach (var header in countHeaders)
        {
            var batchStatus = header.PostedBatchNo is int linkedBatchNo
                ? batchStatuses.GetValueOrDefault(linkedBatchNo)
                : null;

            if (string.Equals(header.Status, IvStockCountStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                if (header.PostedBatchNo is int postedBatchNo)
                {
                    if (!string.Equals(batchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new IvInventoryReconcileFinding
                        {
                            Code = "STOCK_COUNT_BATCH_NOT_POSTED",
                            Message = $"Stock count {header.CountNo} is POSTED but its batch {postedBatchNo} "
                                + $"is {batchStatus ?? "missing"} (its adjustment may have been rolled back "
                                + "outside the count screen — run Recover)."
                        });
                    }
                }
                else
                {
                    // The all-zero-variance post is legitimate and must never be flagged, so the test is
                    // the EVIDENCE: a counted line whose count differed from the snapshot implies a
                    // variance existed, which means a batch should have been produced.
                    var varianceLines = linesByCount
                        .GetValueOrDefault(header.Id, [])
                        .Count(l => l.PhysicalQty is decimal physical
                                    && IvQty.Round(physical) != IvQty.Round(l.SystemQty));

                    if (varianceLines > 0)
                    {
                        findings.Add(new IvInventoryReconcileFinding
                        {
                            Code = "STOCK_COUNT_UNPOSTED_VARIANCE",
                            Message = $"Stock count {header.CountNo} is POSTED with no batch, but {varianceLines} "
                                + "counted line(s) differ from the snapshot taken at Generate. No adjustment was posted."
                        });
                    }
                }
            }
            else if (string.Equals(header.Status, IvStockCountStatuses.RolledBack, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(batchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new IvInventoryReconcileFinding
                {
                    Code = "STOCK_COUNT_BATCH_STILL_POSTED",
                    Message = $"Stock count {header.CountNo} is ROLLED_BACK but its batch {header.PostedBatchNo} "
                        + "is still POSTED."
                });
            }
        }

        return IvInventoryReconcileResult.Ok(findings);
    }
}
