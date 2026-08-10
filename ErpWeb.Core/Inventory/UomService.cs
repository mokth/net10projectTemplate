using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class UomService : IUomService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<UomService> _logger;

    public UomService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<UomService> logger)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<InventoryOpResult<UOM>> GetAsync(CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, cancellationToken);
        if (gate is not null) return InventoryOpResult<UOM>.Fail(gate);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.UOMs.AsNoTracking()
            .Where(x => x.CompanyId == _companyContext.CompanyId)
            .OrderBy(x => x.UOMCode)
            .ToListAsync(cancellationToken);
        return InventoryOpResult<UOM>.Ok(rows);
    }

    public async Task<InventoryOpResult<UOM>> AddAsync(UOM uom, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Add, cancellationToken);
        if (gate is not null) return InventoryOpResult<UOM>.Fail(gate);

        var code = (uom.UOMCode ?? string.Empty).Trim().ToUpperInvariant();
        var name = (uom.UOMName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code)) return InventoryOpResult<UOM>.Fail("UOM code is required.");
        if (string.IsNullOrWhiteSpace(name)) return InventoryOpResult<UOM>.Fail("UOM name is required.");
        if (uom.DecimalPlaces is < 0 or > 6) return InventoryOpResult<UOM>.Fail("Decimal places must be 0–6.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.UOMs.AnyAsync(x => x.CompanyId == _companyContext.CompanyId && x.UOMCode == code, cancellationToken))
            return InventoryOpResult<UOM>.Fail("UOM code already exists.");

        var entity = new UOM
        {
            CompanyId = _companyContext.CompanyId,
            UOMCode = code,
            UOMName = name,
            DecimalPlaces = uom.DecimalPlaces,
            IsActive = uom.IsActive,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50)
        };
        db.UOMs.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("UOM created {UOMCode} CompanyId={CompanyId}", code, entity.CompanyId);
        return InventoryOpResult<UOM>.Ok(entity);
    }

    public async Task<InventoryOpResult<UOM>> UpdateAsync(UOM uom, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Edit, cancellationToken);
        if (gate is not null) return InventoryOpResult<UOM>.Fail(gate);

        var name = (uom.UOMName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return InventoryOpResult<UOM>.Fail("UOM name is required.");
        if (uom.DecimalPlaces is < 0 or > 6) return InventoryOpResult<UOM>.Fail("Decimal places must be 0–6.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.UOMs.FirstOrDefaultAsync(
            x => x.Id == uom.Id && x.CompanyId == _companyContext.CompanyId, cancellationToken);
        if (entity is null) return InventoryOpResult<UOM>.Fail("UOM was not found.");

        entity.UOMName = name;
        entity.DecimalPlaces = uom.DecimalPlaces;
        entity.IsActive = uom.IsActive;
        entity.ModifiedAtUtc = DateTime.UtcNow;
        entity.ModifiedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<UOM>.Ok(entity);
    }

    public async Task<InventoryOpResult<UOM>> DeleteAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Delete, cancellationToken);
        if (gate is not null) return InventoryOpResult<UOM>.Fail(gate);
        if (ids.Count == 0) return InventoryOpResult<UOM>.Fail("No UOM selected.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.UOMs.Where(x => ids.Contains(x.Id) && x.CompanyId == _companyContext.CompanyId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return InventoryOpResult<UOM>.Fail("UOM was not found.");

        var inUse = await db.Items.AnyAsync(
            i => rows.Select(r => r.Id).Contains(i.BaseUOMId), cancellationToken);
        if (inUse) return InventoryOpResult<UOM>.Fail("UOM is used by one or more items.");

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
        return InventoryOpResult<UOM>.Ok();
    }

    private async Task<string?> GateAsync(string permission, CancellationToken cancellationToken)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, cancellationToken);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(
            _accessRights, MenuCodes.InvUom, permission, cancellationToken);
        return access.Ok ? null : access.Error;
    }
}
