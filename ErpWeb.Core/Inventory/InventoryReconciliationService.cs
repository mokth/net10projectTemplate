using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public enum StockIntegrityKind
{
    BalanceVsLedger = 1,
    LotVsBalance = 2,
    MissingItemCost = 3,
    ValueMismatch = 4
}

public sealed class StockIntegrityIssue
{
    public StockIntegrityKind Kind { get; init; }
    public long? BranchId { get; init; }
    public long? WarehouseId { get; init; }
    public long? LocationId { get; init; }
    public long? ItemVariantId { get; init; }
    public decimal? Expected { get; init; }
    public decimal? Actual { get; init; }
    public string Message { get; init; } = null!;
}

public sealed class RebuildResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int BalanceRows { get; init; }
    public int LotBalanceRows { get; init; }
    public int ItemCostRows { get; init; }

    public static RebuildResult Ok(int balances, int lots, int costs) =>
        new() { Succeeded = true, BalanceRows = balances, LotBalanceRows = lots, ItemCostRows = costs };

    public static RebuildResult Fail(string code, string? message = null) =>
        new() { Succeeded = false, ErrorCode = code, ErrorMessage = message ?? code };
}

public interface IInventoryReconciliationService
{
    Task<IReadOnlyList<StockIntegrityIssue>> FindIssuesAsync(
        long? warehouseId = null,
        CancellationToken ct = default);

    /// <summary>Admin-only: rebuild StockBalance / LotBalance / ItemCost from StockLedger. Audited.</summary>
    Task<RebuildResult> RebuildOperationalBalancesAsync(CancellationToken ct = default);
}

public sealed class InventoryReconciliationService : IInventoryReconciliationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<InventoryReconciliationService> _logger;

    public InventoryReconciliationService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<InventoryReconciliationService> logger)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<IReadOnlyList<StockIntegrityIssue>> FindIssuesAsync(
        long? warehouseId = null,
        CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, ct);
        if (gate is not null)
            return [new StockIntegrityIssue { Kind = StockIntegrityKind.BalanceVsLedger, Message = gate }];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var companyId = _companyContext.CompanyId;
        var branchId = _companyContext.BranchId;
        var issues = new List<StockIntegrityIssue>();

        // 1) Ledger Σ(In-Out) by WH/Loc/Item == StockBalance
        var ledgerQty = await db.StockLedgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BranchId == branchId
                        && (warehouseId == null || l.WarehouseId == warehouseId))
            .GroupBy(l => new { l.WarehouseId, l.LocationId, l.ItemVariantId })
            .Select(g => new
            {
                g.Key.WarehouseId,
                g.Key.LocationId,
                g.Key.ItemVariantId,
                Qty = g.Sum(x => x.QtyInBase - x.QtyOutBase)
            })
            .ToListAsync(ct);

        var balances = await db.StockBalances.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.BranchId == branchId
                        && (warehouseId == null || b.WarehouseId == warehouseId))
            .ToListAsync(ct);

        var balMap = balances.ToDictionary(
            b => (b.WarehouseId, b.LocationId, b.ItemVariantId),
            b => b.QtyOnHand);

        foreach (var row in ledgerQty)
        {
            balMap.TryGetValue((row.WarehouseId, row.LocationId, row.ItemVariantId), out var actual);
            if (actual != row.Qty)
            {
                issues.Add(new StockIntegrityIssue
                {
                    Kind = StockIntegrityKind.BalanceVsLedger,
                    BranchId = branchId,
                    WarehouseId = row.WarehouseId,
                    LocationId = row.LocationId,
                    ItemVariantId = row.ItemVariantId,
                    Expected = row.Qty,
                    Actual = actual,
                    Message = $"StockBalance {actual} != ledger Σ {row.Qty}"
                });
            }
        }

        foreach (var bal in balances.Where(b => b.QtyOnHand != 0))
        {
            if (ledgerQty.All(l =>
                    l.WarehouseId != bal.WarehouseId ||
                    l.LocationId != bal.LocationId ||
                    l.ItemVariantId != bal.ItemVariantId))
            {
                issues.Add(new StockIntegrityIssue
                {
                    Kind = StockIntegrityKind.BalanceVsLedger,
                    BranchId = branchId,
                    WarehouseId = bal.WarehouseId,
                    LocationId = bal.LocationId,
                    ItemVariantId = bal.ItemVariantId,
                    Expected = 0,
                    Actual = bal.QtyOnHand,
                    Message = "StockBalance has qty with no ledger residual."
                });
            }
        }

        // 2) Phase 3: Σ LotBalance == StockBalance for batch items
        var batchVariantIds = await (
            from v in db.ItemVariants.AsNoTracking()
            join i in db.Items.AsNoTracking() on v.ItemId equals i.Id
            where v.CompanyId == companyId && i.IsBatchItem
            select v.Id).ToListAsync(ct);

        foreach (var bal in balances.Where(b => batchVariantIds.Contains(b.ItemVariantId)))
        {
            var lotSum = await (
                from lb in db.LotBalances.AsNoTracking()
                join lot in db.Lots.AsNoTracking() on lb.LotId equals lot.Id
                where lb.CompanyId == companyId && lb.BranchId == branchId
                      && lb.WarehouseId == bal.WarehouseId
                      && lb.LocationId == bal.LocationId
                      && lot.ItemVariantId == bal.ItemVariantId
                select (decimal?)lb.QtyOnHand).SumAsync(ct) ?? 0m;

            if (lotSum != bal.QtyOnHand)
            {
                issues.Add(new StockIntegrityIssue
                {
                    Kind = StockIntegrityKind.LotVsBalance,
                    BranchId = branchId,
                    WarehouseId = bal.WarehouseId,
                    LocationId = bal.LocationId,
                    ItemVariantId = bal.ItemVariantId,
                    Expected = bal.QtyOnHand,
                    Actual = lotSum,
                    Message = $"LotBalance sum {lotSum} != StockBalance {bal.QtyOnHand}"
                });
            }
        }

        // 3) ItemCost exists for every WH/Item with Qty>0
        var whItemQty = balances
            .GroupBy(b => new { b.WarehouseId, b.ItemVariantId })
            .Select(g => new { g.Key.WarehouseId, g.Key.ItemVariantId, Qty = g.Sum(x => x.QtyOnHand) })
            .Where(g => g.Qty > 0)
            .ToList();

        foreach (var g in whItemQty)
        {
            var hasCost = await db.ItemCosts.AsNoTracking().AnyAsync(c =>
                c.CompanyId == companyId && c.BranchId == branchId &&
                c.WarehouseId == g.WarehouseId && c.ItemVariantId == g.ItemVariantId, ct);
            if (!hasCost)
            {
                issues.Add(new StockIntegrityIssue
                {
                    Kind = StockIntegrityKind.MissingItemCost,
                    BranchId = branchId,
                    WarehouseId = g.WarehouseId,
                    ItemVariantId = g.ItemVariantId,
                    Message = "Missing ItemCost for warehouse/item with Qty>0."
                });
            }
        }

        // 4) Value: Σ(Qty × AverageCost) at WH/Item vs ledger residual as-of today
        var asOf = await InventoryAsOfCalculator.ComputeAsync(db, companyId, DateTime.UtcNow.Date, branchId, warehouseId, ct);
        foreach (var line in asOf)
        {
            var opsQty = balances
                .Where(b => b.WarehouseId == line.WarehouseId && b.ItemVariantId == line.ItemVariantId)
                .Sum(b => b.QtyOnHand);
            if (opsQty != line.Qty)
                continue; // balance-vs-ledger already flagged

            var cost = await db.ItemCosts.AsNoTracking().FirstOrDefaultAsync(c =>
                c.CompanyId == companyId && c.BranchId == line.BranchId &&
                c.WarehouseId == line.WarehouseId && c.ItemVariantId == line.ItemVariantId, ct);
            if (cost is null) continue;

            var opsValue = Math.Round(opsQty * cost.AverageCost, 4, MidpointRounding.AwayFromZero);
            if (Math.Abs(opsValue - line.Value) > 0.01m)
            {
                issues.Add(new StockIntegrityIssue
                {
                    Kind = StockIntegrityKind.ValueMismatch,
                    BranchId = line.BranchId,
                    WarehouseId = line.WarehouseId,
                    ItemVariantId = line.ItemVariantId,
                    Expected = line.Value,
                    Actual = opsValue,
                    Message = $"Ledger residual value {line.Value} vs Qty×Avg {opsValue}"
                });
            }
        }

        return issues;
    }

    public async Task<RebuildResult> RebuildOperationalBalancesAsync(CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Close, ct);
        if (gate is not null) return RebuildResult.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var companyId = _companyContext.CompanyId;
        var branchId = _companyContext.BranchId;
        var user = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var utc = DateTime.UtcNow;

        _logger.LogWarning(
            "AUDIT RebuildOperationalBalances started by {User} for Company {CompanyId} Branch {BranchId}",
            user, companyId, branchId);

        var ledgerGroups = await db.StockLedgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BranchId == branchId)
            .GroupBy(l => new { l.WarehouseId, l.LocationId, l.ItemVariantId })
            .Select(g => new
            {
                g.Key.WarehouseId,
                g.Key.LocationId,
                g.Key.ItemVariantId,
                Qty = g.Sum(x => x.QtyInBase - x.QtyOutBase)
            })
            .ToListAsync(ct);

        var existingBalances = await db.StockBalances
            .Where(b => b.CompanyId == companyId && b.BranchId == branchId)
            .ToListAsync(ct);
        db.StockBalances.RemoveRange(existingBalances);

        foreach (var g in ledgerGroups.Where(x => x.Qty != 0))
        {
            db.StockBalances.Add(new StockBalance
            {
                CompanyId = companyId,
                BranchId = branchId,
                WarehouseId = g.WarehouseId,
                LocationId = g.LocationId,
                ItemVariantId = g.ItemVariantId,
                QtyOnHand = g.Qty,
                ReservedQty = 0,
                LastUpdatedAtUtc = utc,
                CreatedAtUtc = utc,
                CreatedBy = user
            });
        }

        var lotGroups = await db.StockLedgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BranchId == branchId && l.LotId != null)
            .GroupBy(l => new { LotId = l.LotId!.Value, l.WarehouseId, l.LocationId })
            .Select(g => new
            {
                g.Key.LotId,
                g.Key.WarehouseId,
                g.Key.LocationId,
                Qty = g.Sum(x => x.QtyInBase - x.QtyOutBase)
            })
            .ToListAsync(ct);

        var existingLots = await db.LotBalances
            .Where(b => b.CompanyId == companyId && b.BranchId == branchId)
            .ToListAsync(ct);
        db.LotBalances.RemoveRange(existingLots);

        foreach (var g in lotGroups.Where(x => x.Qty != 0))
        {
            db.LotBalances.Add(new LotBalance
            {
                CompanyId = companyId,
                BranchId = branchId,
                LotId = g.LotId,
                WarehouseId = g.WarehouseId,
                LocationId = g.LocationId,
                QtyOnHand = g.Qty,
                CreatedAtUtc = utc,
                CreatedBy = user
            });
        }

        // Rebuild ItemCost AverageCost from residual ledger value / qty at WH grain
        var costGroups = await InventoryAsOfCalculator.ComputeAsync(db, companyId, DateTime.UtcNow.Date, branchId, ct: ct);
        var existingCosts = await db.ItemCosts
            .Where(c => c.CompanyId == companyId && c.BranchId == branchId)
            .ToListAsync(ct);
        db.ItemCosts.RemoveRange(existingCosts);

        foreach (var g in costGroups.Where(x => x.Qty != 0))
        {
            db.ItemCosts.Add(new ItemCost
            {
                CompanyId = companyId,
                BranchId = g.BranchId,
                WarehouseId = g.WarehouseId,
                ItemVariantId = g.ItemVariantId,
                AverageCost = g.UnitCost,
                LastCost = g.UnitCost,
                LastUpdatedAtUtc = utc,
                CreatedAtUtc = utc,
                CreatedBy = user
            });
        }

        await db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "AUDIT RebuildOperationalBalances completed by {User}: Balances={Balances}, Lots={Lots}, Costs={Costs}",
            user, ledgerGroups.Count(x => x.Qty != 0), lotGroups.Count(x => x.Qty != 0), costGroups.Count(x => x.Qty != 0));

        return RebuildResult.Ok(
            ledgerGroups.Count(x => x.Qty != 0),
            lotGroups.Count(x => x.Qty != 0),
            costGroups.Count(x => x.Qty != 0));
    }

    private async Task<string?> GateAsync(string permission, CancellationToken ct)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, ct);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(
            _accessRights, MenuCodes.InvReconcile, permission, ct);
        return access.Ok ? null : access.Error;
    }
}
