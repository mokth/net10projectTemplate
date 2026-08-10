using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class WarehouseService : IWarehouseService
{
    public const string DefaultLocationCode = "MAIN";
    public const string DefaultLocationName = "Main";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<WarehouseService> _logger;

    public WarehouseService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<WarehouseService> logger)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<InventoryOpResult<Warehouse>> GetAsync(CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, cancellationToken);
        if (gate is not null) return InventoryOpResult<Warehouse>.Fail(gate);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Warehouses.AsNoTracking()
            .Where(x => x.CompanyId == _companyContext.CompanyId && x.BranchId == _companyContext.BranchId)
            .OrderBy(x => x.WarehouseCode)
            .ToListAsync(cancellationToken);
        return InventoryOpResult<Warehouse>.Ok(rows);
    }

    public async Task<InventoryOpResult<Warehouse>> AddAsync(
        Warehouse warehouse,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Add, cancellationToken);
        if (gate is not null) return InventoryOpResult<Warehouse>.Fail(gate);

        var code = (warehouse.WarehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        var name = (warehouse.WarehouseName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code)) return InventoryOpResult<Warehouse>.Fail("Warehouse code is required.");
        if (string.IsNullOrWhiteSpace(name)) return InventoryOpResult<Warehouse>.Fail("Warehouse name is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Warehouses.AnyAsync(
            x => x.CompanyId == _companyContext.CompanyId
                 && x.BranchId == _companyContext.BranchId
                 && x.WarehouseCode == code,
            cancellationToken);
        if (exists) return InventoryOpResult<Warehouse>.Fail("Warehouse code already exists.");

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var stamp = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var now = DateTime.UtcNow;
        var entity = new Warehouse
        {
            CompanyId = _companyContext.CompanyId,
            BranchId = _companyContext.BranchId,
            WarehouseCode = code,
            WarehouseName = name,
            IsActive = warehouse.IsActive,
            CreatedAtUtc = now,
            CreatedBy = stamp
        };
        db.Warehouses.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        db.WarehouseLocations.Add(new WarehouseLocation
        {
            CompanyId = _companyContext.CompanyId,
            BranchId = _companyContext.BranchId,
            WarehouseId = entity.Id,
            LocationCode = DefaultLocationCode,
            LocationName = DefaultLocationName,
            IsActive = true,
            CreatedAtUtc = now,
            CreatedBy = stamp
        });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Warehouse created {WarehouseCode} with MAIN location. CompanyId={CompanyId} BranchId={BranchId}",
            code, entity.CompanyId, entity.BranchId);
        return InventoryOpResult<Warehouse>.Ok(entity);
    }

    public async Task<InventoryOpResult<Warehouse>> UpdateAsync(
        Warehouse warehouse,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Edit, cancellationToken);
        if (gate is not null) return InventoryOpResult<Warehouse>.Fail(gate);

        var name = (warehouse.WarehouseName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return InventoryOpResult<Warehouse>.Fail("Warehouse name is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Warehouses.FirstOrDefaultAsync(
            x => x.Id == warehouse.Id
                 && x.CompanyId == _companyContext.CompanyId
                 && x.BranchId == _companyContext.BranchId,
            cancellationToken);
        if (entity is null) return InventoryOpResult<Warehouse>.Fail("Warehouse was not found.");

        entity.WarehouseName = name;
        entity.IsActive = warehouse.IsActive;
        entity.ModifiedAtUtc = DateTime.UtcNow;
        entity.ModifiedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<Warehouse>.Ok(entity);
    }

    public async Task<InventoryOpResult<Warehouse>> DeleteAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Delete, cancellationToken);
        if (gate is not null) return InventoryOpResult<Warehouse>.Fail(gate);
        if (ids.Count == 0) return InventoryOpResult<Warehouse>.Fail("No warehouse selected.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Warehouses
            .Where(x => ids.Contains(x.Id)
                        && x.CompanyId == _companyContext.CompanyId
                        && x.BranchId == _companyContext.BranchId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return InventoryOpResult<Warehouse>.Fail("Warehouse was not found.");

        var stamp = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.IsDeleted = true;
            row.DeletedAtUtc = now;
            row.DeletedBy = stamp;
            row.IsActive = false;
            row.ModifiedAtUtc = now;
            row.ModifiedBy = stamp;
        }

        var locations = await db.WarehouseLocations
            .Where(l => ids.Contains(l.WarehouseId))
            .ToListAsync(cancellationToken);
        foreach (var loc in locations)
        {
            loc.IsDeleted = true;
            loc.DeletedAtUtc = now;
            loc.DeletedBy = stamp;
            loc.IsActive = false;
            loc.ModifiedAtUtc = now;
            loc.ModifiedBy = stamp;
        }

        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<Warehouse>.Ok();
    }

    private async Task<string?> GateAsync(string permission, CancellationToken cancellationToken)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, cancellationToken);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(
            _accessRights, MenuCodes.InvWarehouses, permission, cancellationToken);
        return access.Ok ? null : access.Error;
    }
}

public sealed class WarehouseLocationService : IWarehouseLocationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;

    public WarehouseLocationService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
    }

    public async Task<InventoryOpResult<WarehouseLocation>> GetAsync(
        long? warehouseId = null,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, cancellationToken);
        if (gate is not null) return InventoryOpResult<WarehouseLocation>.Fail(gate);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.WarehouseLocations.AsNoTracking()
            .Where(x => x.CompanyId == _companyContext.CompanyId && x.BranchId == _companyContext.BranchId);
        if (warehouseId is > 0)
        {
            query = query.Where(x => x.WarehouseId == warehouseId);
        }

        var rows = await query.OrderBy(x => x.WarehouseId).ThenBy(x => x.LocationCode).ToListAsync(cancellationToken);
        return InventoryOpResult<WarehouseLocation>.Ok(rows);
    }

    public async Task<InventoryOpResult<WarehouseLocation>> AddAsync(
        WarehouseLocation location,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Add, cancellationToken);
        if (gate is not null) return InventoryOpResult<WarehouseLocation>.Fail(gate);

        var code = (location.LocationCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code)) return InventoryOpResult<WarehouseLocation>.Fail("Location code is required.");
        if (location.WarehouseId <= 0) return InventoryOpResult<WarehouseLocation>.Fail("Warehouse is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var whOk = await db.Warehouses.AnyAsync(
            w => w.Id == location.WarehouseId
                 && w.CompanyId == _companyContext.CompanyId
                 && w.BranchId == _companyContext.BranchId,
            cancellationToken);
        if (!whOk) return InventoryOpResult<WarehouseLocation>.Fail("Warehouse was not found.");

        var exists = await db.WarehouseLocations.AnyAsync(
            x => x.CompanyId == _companyContext.CompanyId
                 && x.BranchId == _companyContext.BranchId
                 && x.WarehouseId == location.WarehouseId
                 && x.LocationCode == code,
            cancellationToken);
        if (exists) return InventoryOpResult<WarehouseLocation>.Fail("Location code already exists.");

        var entity = new WarehouseLocation
        {
            CompanyId = _companyContext.CompanyId,
            BranchId = _companyContext.BranchId,
            WarehouseId = location.WarehouseId,
            LocationCode = code,
            LocationName = string.IsNullOrWhiteSpace(location.LocationName)
                ? code
                : location.LocationName.Trim(),
            IsActive = location.IsActive,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50)
        };
        db.WarehouseLocations.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<WarehouseLocation>.Ok(entity);
    }

    public async Task<InventoryOpResult<WarehouseLocation>> UpdateAsync(
        WarehouseLocation location,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Edit, cancellationToken);
        if (gate is not null) return InventoryOpResult<WarehouseLocation>.Fail(gate);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.WarehouseLocations.FirstOrDefaultAsync(
            x => x.Id == location.Id
                 && x.CompanyId == _companyContext.CompanyId
                 && x.BranchId == _companyContext.BranchId,
            cancellationToken);
        if (entity is null) return InventoryOpResult<WarehouseLocation>.Fail("Location was not found.");

        if (!location.IsActive && entity.IsActive)
        {
            var activeCount = await db.WarehouseLocations.CountAsync(
                x => x.WarehouseId == entity.WarehouseId && x.IsActive,
                cancellationToken);
            if (activeCount <= 1)
                return InventoryOpResult<WarehouseLocation>.Fail("Cannot deactivate the last active location.");
        }

        entity.LocationName = string.IsNullOrWhiteSpace(location.LocationName)
            ? entity.LocationCode
            : location.LocationName.Trim();
        entity.IsActive = location.IsActive;
        entity.ModifiedAtUtc = DateTime.UtcNow;
        entity.ModifiedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<WarehouseLocation>.Ok(entity);
    }

    public async Task<InventoryOpResult<WarehouseLocation>> DeleteAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Delete, cancellationToken);
        if (gate is not null) return InventoryOpResult<WarehouseLocation>.Fail(gate);
        if (ids.Count == 0) return InventoryOpResult<WarehouseLocation>.Fail("No location selected.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WarehouseLocations
            .Where(x => ids.Contains(x.Id)
                        && x.CompanyId == _companyContext.CompanyId
                        && x.BranchId == _companyContext.BranchId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return InventoryOpResult<WarehouseLocation>.Fail("Location was not found.");

        foreach (var group in rows.GroupBy(r => r.WarehouseId))
        {
            var activeRemaining = await db.WarehouseLocations.CountAsync(
                x => x.WarehouseId == group.Key
                     && x.IsActive
                     && !ids.Contains(x.Id),
                cancellationToken);
            if (activeRemaining == 0)
                return InventoryOpResult<WarehouseLocation>.Fail("Cannot delete/deactivate the last active location on a warehouse.");
        }

        var stamp = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.IsDeleted = true;
            row.DeletedAtUtc = now;
            row.DeletedBy = stamp;
            row.IsActive = false;
            row.ModifiedAtUtc = now;
            row.ModifiedBy = stamp;
        }
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<WarehouseLocation>.Ok();
    }

    private async Task<string?> GateAsync(string permission, CancellationToken cancellationToken)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, cancellationToken);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(
            _accessRights, MenuCodes.InvLocations, permission, cancellationToken);
        return access.Ok ? null : access.Error;
    }
}

public sealed class ReasonCodeService : IReasonCodeService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;

    public ReasonCodeService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
    }

    public async Task<InventoryOpResult<ReasonCode>> GetAsync(CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, cancellationToken);
        if (gate is not null) return InventoryOpResult<ReasonCode>.Fail(gate);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ReasonCodes.AsNoTracking()
            .Where(x => x.CompanyId == _companyContext.CompanyId)
            .OrderBy(x => x.ReasonCodeValue)
            .ToListAsync(cancellationToken);
        return InventoryOpResult<ReasonCode>.Ok(rows);
    }

    public async Task<InventoryOpResult<ReasonCode>> AddAsync(
        ReasonCode reason,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Add, cancellationToken);
        if (gate is not null) return InventoryOpResult<ReasonCode>.Fail(gate);

        var code = (reason.ReasonCodeValue ?? string.Empty).Trim().ToUpperInvariant();
        var name = (reason.ReasonName ?? string.Empty).Trim();
        var applies = (reason.AppliesTo ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code)) return InventoryOpResult<ReasonCode>.Fail("Reason code is required.");
        if (string.IsNullOrWhiteSpace(name)) return InventoryOpResult<ReasonCode>.Fail("Reason name is required.");
        if (string.IsNullOrWhiteSpace(applies)) return InventoryOpResult<ReasonCode>.Fail("Applies To is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.ReasonCodes.AnyAsync(
                x => x.CompanyId == _companyContext.CompanyId && x.ReasonCodeValue == code, cancellationToken))
            return InventoryOpResult<ReasonCode>.Fail("Reason code already exists.");

        var entity = new ReasonCode
        {
            CompanyId = _companyContext.CompanyId,
            ReasonCodeValue = code,
            ReasonName = name,
            AppliesTo = applies,
            IsActive = reason.IsActive,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50)
        };
        db.ReasonCodes.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<ReasonCode>.Ok(entity);
    }

    public async Task<InventoryOpResult<ReasonCode>> UpdateAsync(
        ReasonCode reason,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Edit, cancellationToken);
        if (gate is not null) return InventoryOpResult<ReasonCode>.Fail(gate);

        var name = (reason.ReasonName ?? string.Empty).Trim();
        var applies = (reason.AppliesTo ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name)) return InventoryOpResult<ReasonCode>.Fail("Reason name is required.");
        if (string.IsNullOrWhiteSpace(applies)) return InventoryOpResult<ReasonCode>.Fail("Applies To is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ReasonCodes.FirstOrDefaultAsync(
            x => x.Id == reason.Id && x.CompanyId == _companyContext.CompanyId, cancellationToken);
        if (entity is null) return InventoryOpResult<ReasonCode>.Fail("Reason code was not found.");

        entity.ReasonName = name;
        entity.AppliesTo = applies;
        entity.IsActive = reason.IsActive;
        entity.ModifiedAtUtc = DateTime.UtcNow;
        entity.ModifiedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<ReasonCode>.Ok(entity);
    }

    public async Task<InventoryOpResult<ReasonCode>> DeleteAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Delete, cancellationToken);
        if (gate is not null) return InventoryOpResult<ReasonCode>.Fail(gate);
        if (ids.Count == 0) return InventoryOpResult<ReasonCode>.Fail("No reason code selected.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ReasonCodes
            .Where(x => ids.Contains(x.Id) && x.CompanyId == _companyContext.CompanyId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return InventoryOpResult<ReasonCode>.Fail("Reason code was not found.");

        var stamp = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.IsDeleted = true;
            row.DeletedAtUtc = now;
            row.DeletedBy = stamp;
            row.IsActive = false;
            row.ModifiedAtUtc = now;
            row.ModifiedBy = stamp;
        }
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<ReasonCode>.Ok();
    }

    private async Task<string?> GateAsync(string permission, CancellationToken cancellationToken)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, cancellationToken);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(
            _accessRights, MenuCodes.InvReasonCodes, permission, cancellationToken);
        return access.Ok ? null : access.Error;
    }
}
