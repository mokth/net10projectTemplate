using System.Data;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class InventoryPeriodService : IInventoryPeriodService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<InventoryPeriodService> _logger;

    public InventoryPeriodService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<InventoryPeriodService> logger)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<PeriodOpResult> EnsurePeriodAsync(int fiscalYear, int fiscalMonth, CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Add, ct);
        if (gate is not null) return PeriodOpResult.Fail(InventoryErrorCodes.InvalidCompany, gate);
        if (fiscalMonth is < 1 or > 12)
            return PeriodOpResult.Fail(InventoryErrorCodes.InvalidDocument, "FiscalMonth must be 1–12.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var companyId = _companyContext.CompanyId;
        var existing = await db.InventoryPeriods.FirstOrDefaultAsync(p =>
            p.CompanyId == companyId && p.FiscalYear == fiscalYear && p.FiscalMonth == fiscalMonth, ct);
        if (existing is not null)
            return PeriodOpResult.Ok(existing);

        var start = new DateTime(fiscalYear, fiscalMonth, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var period = new InventoryPeriod
        {
            CompanyId = companyId,
            FiscalYear = fiscalYear,
            FiscalMonth = fiscalMonth,
            StartDate = start,
            EndDate = end,
            IsClosed = false,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50)
        };
        db.InventoryPeriods.Add(period);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(period).State = EntityState.Detached;
            period = await db.InventoryPeriods.SingleAsync(p =>
                p.CompanyId == companyId && p.FiscalYear == fiscalYear && p.FiscalMonth == fiscalMonth, ct);
        }

        return PeriodOpResult.Ok(period);
    }

    public async Task<PeriodOpResult> ListPeriodsAsync(CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, ct);
        if (gate is not null) return PeriodOpResult.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.InventoryPeriods.AsNoTracking()
            .Where(p => p.CompanyId == _companyContext.CompanyId)
            .OrderByDescending(p => p.FiscalYear).ThenByDescending(p => p.FiscalMonth)
            .ToListAsync(ct);
        return PeriodOpResult.Ok(rows);
    }

    public async Task<PeriodOpResult> ClosePeriodAsync(long periodId, string closedBy, CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Close, ct);
        if (gate is not null) return PeriodOpResult.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var period = await LockPeriodAsync(db, periodId, ct);
        if (period is null)
            return PeriodOpResult.Fail(InventoryErrorCodes.InvalidDocument, "Period not found.");
        if (period.CompanyId != _companyContext.CompanyId)
            return PeriodOpResult.Fail(InventoryErrorCodes.InvalidCompany);

        if (period.IsClosed)
        {
            var existing = await db.StockSnapshots.AsNoTracking()
                .Where(s => s.CompanyId == period.CompanyId && s.PeriodId == period.Id)
                .ToListAsync(ct);
            await tx.CommitAsync(ct);
            return PeriodOpResult.Ok(period, existing);
        }

        var priorOpen = await db.InventoryPeriods.AsNoTracking().AnyAsync(p =>
            p.CompanyId == period.CompanyId &&
            !p.IsClosed &&
            (p.FiscalYear < period.FiscalYear ||
             (p.FiscalYear == period.FiscalYear && p.FiscalMonth < period.FiscalMonth)), ct);
        if (priorOpen)
            return PeriodOpResult.Fail(InventoryErrorCodes.InvalidStatus, "Close prior open periods first.");

        var user = InventoryServiceHelper.Truncate(closedBy, 50);
        var asOfRows = await InventoryAsOfCalculator.ComputeAsync(db, period.CompanyId, period.EndDate, ct: ct);

        var stale = await db.StockSnapshots.Where(s => s.PeriodId == period.Id).ToListAsync(ct);
        if (stale.Count > 0) db.StockSnapshots.RemoveRange(stale);

        var snapshots = new List<StockSnapshot>();
        foreach (var row in asOfRows.Where(r => r.Qty != 0 || r.Value != 0))
        {
            var snap = new StockSnapshot
            {
                CompanyId = period.CompanyId,
                BranchId = row.BranchId,
                PeriodId = period.Id,
                WarehouseId = row.WarehouseId,
                ItemVariantId = row.ItemVariantId,
                ClosingQty = row.Qty,
                ClosingCost = row.UnitCost,
                ClosingValue = row.Value,
                SnapshotDate = period.EndDate.Date,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = user
            };
            db.StockSnapshots.Add(snap);
            snapshots.Add(snap);
        }

        period.IsClosed = true;
        period.ClosedBy = user;
        period.ClosedAtUtc = DateTime.UtcNow;
        period.ModifiedAtUtc = DateTime.UtcNow;
        period.ModifiedBy = user;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Closed inventory period {Year}-{Month} with {Count} snapshot rows",
            period.FiscalYear, period.FiscalMonth, snapshots.Count);

        return PeriodOpResult.Ok(period, snapshots);
    }

    public async Task<PeriodOpResult> GetSnapshotsAsync(long periodId, CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, ct);
        if (gate is not null) return PeriodOpResult.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var period = await db.InventoryPeriods.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == periodId && p.CompanyId == _companyContext.CompanyId, ct);
        if (period is null)
            return PeriodOpResult.Fail(InventoryErrorCodes.InvalidDocument, "Period not found.");

        var snaps = await db.StockSnapshots.AsNoTracking()
            .Where(s => s.PeriodId == periodId && s.CompanyId == period.CompanyId)
            .OrderBy(s => s.BranchId).ThenBy(s => s.WarehouseId).ThenBy(s => s.ItemVariantId)
            .ToListAsync(ct);
        return PeriodOpResult.Ok(period, snaps);
    }

    private static async Task<InventoryPeriod?> LockPeriodAsync(AppDbContext db, long periodId, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT Id FROM InventoryPeriod WITH (UPDLOCK, HOLDLOCK) WHERE Id={0};",
                [periodId], ct);
        }

        return await db.InventoryPeriods.FirstOrDefaultAsync(p => p.Id == periodId, ct);
    }

    private async Task<string?> GateAsync(string permission, CancellationToken ct)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, ct);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(
            _accessRights, MenuCodes.InvPeriod, permission, ct);
        return access.Ok ? null : access.Error;
    }
}

public sealed class InventoryAsOfService : IInventoryAsOfService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentUserService _currentUser;

    public InventoryAsOfService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        IAccessRightService accessRights,
        ICurrentUserService currentUser)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _accessRights = accessRights;
        _currentUser = currentUser;
    }

    public async Task<PeriodOpResult> GetAsOfValuationAsync(
        DateTime asOfDate,
        long? branchId = null,
        long? warehouseId = null,
        CancellationToken ct = default)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, ct);
        if (!resolve.Ok) return PeriodOpResult.Fail(InventoryErrorCodes.InvalidCompany, resolve.Error);

        var canAccess = await _accessRights.CanAsync(MenuCodes.InvPeriod, PermissionCodes.Access, ct);
        if (!canAccess) return PeriodOpResult.Fail(InventoryErrorCodes.InvalidCompany, "Not authorized.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var lines = await InventoryAsOfCalculator.ComputeAsync(
            db, _companyContext.CompanyId, asOfDate, branchId, warehouseId, ct);

        var canViewCost = string.Equals(_currentUser.UserLevel, "SYSTEM_ADMIN", StringComparison.OrdinalIgnoreCase)
                          || await _accessRights.CanAsync(MenuCodes.Inventory, PermissionCodes.ViewCost, ct);

        var masked = canViewCost
            ? lines
            : lines.Select(l => new AsOfBalanceDto
            {
                BranchId = l.BranchId,
                WarehouseId = l.WarehouseId,
                ItemVariantId = l.ItemVariantId,
                Qty = l.Qty,
                UnitCost = 0,
                Value = 0
            }).ToList();

        return PeriodOpResult.Ok(new InventoryValuationDto
        {
            AsOfDate = asOfDate.Date,
            TotalQty = masked.Sum(l => l.Qty),
            TotalValue = canViewCost ? masked.Sum(l => l.Value) : 0,
            Lines = masked
        });
    }
}
