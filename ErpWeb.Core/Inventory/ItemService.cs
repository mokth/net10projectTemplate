using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class ItemService : IItemService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<ItemService> _logger;

    public ItemService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<ItemService> logger)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<InventoryOpResult<Item>> GetAsync(CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, cancellationToken);
        if (gate is not null) return InventoryOpResult<Item>.Fail(gate);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Items.AsNoTracking()
            .Where(x => x.CompanyId == _companyContext.CompanyId)
            .OrderBy(x => x.ItemCode)
            .ToListAsync(cancellationToken);
        return InventoryOpResult<Item>.Ok(rows);
    }

    public async Task<InventoryOpResult<Item>> AddAsync(Item item, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Add, cancellationToken);
        if (gate is not null) return InventoryOpResult<Item>.Fail(gate);

        var code = (item.ItemCode ?? string.Empty).Trim().ToUpperInvariant();
        var desc = (item.ItemDescription ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code)) return InventoryOpResult<Item>.Fail("Item code is required.");
        if (string.IsNullOrWhiteSpace(desc)) return InventoryOpResult<Item>.Fail("Item description is required.");
        if (item.BaseUOMId <= 0) return InventoryOpResult<Item>.Fail("Base UOM is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var uomOk = await db.UOMs.AnyAsync(
            u => u.Id == item.BaseUOMId && u.CompanyId == _companyContext.CompanyId && u.IsActive,
            cancellationToken);
        if (!uomOk) return InventoryOpResult<Item>.Fail("Base UOM was not found.");

        if (await db.Items.AnyAsync(x => x.CompanyId == _companyContext.CompanyId && x.ItemCode == code, cancellationToken))
            return InventoryOpResult<Item>.Fail("Item code already exists.");

        if (await db.ItemVariants.AnyAsync(x => x.CompanyId == _companyContext.CompanyId && x.SKU == code, cancellationToken))
            return InventoryOpResult<Item>.Fail("Default SKU already exists.");

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var stamp = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var now = DateTime.UtcNow;
        var entity = new Item
        {
            CompanyId = _companyContext.CompanyId,
            ItemCode = code,
            ItemDescription = desc,
            BaseUOMId = item.BaseUOMId,
            IsStockItem = item.IsStockItem,
            IsBatchItem = item.IsBatchItem,
            CostingMethod = CostingMethod.MOVING_AVG,
            MinStockQty = item.MinStockQty,
            MaxStockQty = item.MaxStockQty,
            ReorderQty = item.ReorderQty,
            TaxCode = string.IsNullOrWhiteSpace(item.TaxCode) ? null : item.TaxCode.Trim(),
            IsActive = item.IsActive,
            CreatedAtUtc = now,
            CreatedBy = stamp
        };
        db.Items.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        db.ItemVariants.Add(new ItemVariant
        {
            CompanyId = _companyContext.CompanyId,
            ItemId = entity.Id,
            SKU = code,
            VariantDescription = desc,
            IsDefault = true,
            IsActive = true,
            CreatedAtUtc = now,
            CreatedBy = stamp
        });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("Item created {ItemCode} CompanyId={CompanyId}", code, entity.CompanyId);
        return InventoryOpResult<Item>.Ok(entity);
    }

    public async Task<InventoryOpResult<Item>> UpdateAsync(Item item, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Edit, cancellationToken);
        if (gate is not null) return InventoryOpResult<Item>.Fail(gate);

        var desc = (item.ItemDescription ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(desc)) return InventoryOpResult<Item>.Fail("Item description is required.");
        if (item.BaseUOMId <= 0) return InventoryOpResult<Item>.Fail("Base UOM is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Items.FirstOrDefaultAsync(
            x => x.Id == item.Id && x.CompanyId == _companyContext.CompanyId, cancellationToken);
        if (entity is null) return InventoryOpResult<Item>.Fail("Item was not found.");

        var uomOk = await db.UOMs.AnyAsync(
            u => u.Id == item.BaseUOMId && u.CompanyId == _companyContext.CompanyId && u.IsActive,
            cancellationToken);
        if (!uomOk) return InventoryOpResult<Item>.Fail("Base UOM was not found.");

        entity.ItemDescription = desc;
        entity.BaseUOMId = item.BaseUOMId;
        entity.IsStockItem = item.IsStockItem;
        entity.IsBatchItem = item.IsBatchItem;
        entity.MinStockQty = item.MinStockQty;
        entity.MaxStockQty = item.MaxStockQty;
        entity.ReorderQty = item.ReorderQty;
        entity.TaxCode = string.IsNullOrWhiteSpace(item.TaxCode) ? null : item.TaxCode.Trim();
        entity.IsActive = item.IsActive;
        entity.ModifiedAtUtc = DateTime.UtcNow;
        entity.ModifiedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<Item>.Ok(entity);
    }

    public async Task<InventoryOpResult<Item>> DeleteAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Delete, cancellationToken);
        if (gate is not null) return InventoryOpResult<Item>.Fail(gate);
        if (ids.Count == 0) return InventoryOpResult<Item>.Fail("No item selected.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Items.Where(x => ids.Contains(x.Id) && x.CompanyId == _companyContext.CompanyId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return InventoryOpResult<Item>.Fail("Item was not found.");

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

        var variants = await db.ItemVariants.Where(v => ids.Contains(v.ItemId)).ToListAsync(cancellationToken);
        foreach (var v in variants)
        {
            v.IsDeleted = true;
            v.DeletedAtUtc = now;
            v.DeletedBy = stamp;
            v.IsActive = false;
            v.ModifiedAtUtc = now;
            v.ModifiedBy = stamp;
        }

        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<Item>.Ok();
    }

    public async Task<InventoryOpResult<ItemVariant>> GetVariantsAsync(
        long itemId,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, cancellationToken);
        if (gate is not null) return InventoryOpResult<ItemVariant>.Fail(gate);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ItemVariants.AsNoTracking()
            .Where(x => x.CompanyId == _companyContext.CompanyId && x.ItemId == itemId)
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.SKU)
            .ToListAsync(cancellationToken);
        return InventoryOpResult<ItemVariant>.Ok(rows);
    }

    public async Task<InventoryOpResult<UOMConversion>> GetConversionsAsync(
        long itemId,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Access, cancellationToken);
        if (gate is not null) return InventoryOpResult<UOMConversion>.Fail(gate);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.UOMConversions.AsNoTracking()
            .Where(x => x.CompanyId == _companyContext.CompanyId && x.ItemId == itemId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return InventoryOpResult<UOMConversion>.Ok(rows);
    }

    public async Task<InventoryOpResult<UOMConversion>> AddConversionAsync(
        UOMConversion conversion,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Edit, cancellationToken);
        if (gate is not null) return InventoryOpResult<UOMConversion>.Fail(gate);

        if (conversion.ConversionRate <= 0)
            return InventoryOpResult<UOMConversion>.Fail("Conversion rate must be greater than zero.");
        if (conversion.FromUOMId == conversion.ToUOMId)
            return InventoryOpResult<UOMConversion>.Fail("From UOM and To UOM must differ.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var itemOk = await db.Items.AnyAsync(
            i => i.Id == conversion.ItemId && i.CompanyId == _companyContext.CompanyId, cancellationToken);
        if (!itemOk) return InventoryOpResult<UOMConversion>.Fail("Item was not found.");

        var uomIds = new[] { conversion.FromUOMId, conversion.ToUOMId };
        var uomCount = await db.UOMs.CountAsync(
            u => uomIds.Contains(u.Id) && u.CompanyId == _companyContext.CompanyId, cancellationToken);
        if (uomCount != 2) return InventoryOpResult<UOMConversion>.Fail("UOM was not found.");

        var exists = await db.UOMConversions.AnyAsync(
            x => x.CompanyId == _companyContext.CompanyId
                 && x.ItemId == conversion.ItemId
                 && x.FromUOMId == conversion.FromUOMId
                 && x.ToUOMId == conversion.ToUOMId,
            cancellationToken);
        if (exists) return InventoryOpResult<UOMConversion>.Fail("Conversion already exists.");

        var entity = new UOMConversion
        {
            CompanyId = _companyContext.CompanyId,
            ItemId = conversion.ItemId,
            FromUOMId = conversion.FromUOMId,
            ToUOMId = conversion.ToUOMId,
            ConversionRate = conversion.ConversionRate,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = InventoryServiceHelper.Truncate(_currentUser.UserId, 50)
        };
        db.UOMConversions.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<UOMConversion>.Ok(entity);
    }

    public async Task<InventoryOpResult<UOMConversion>> DeleteConversionsAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(PermissionCodes.Edit, cancellationToken);
        if (gate is not null) return InventoryOpResult<UOMConversion>.Fail(gate);
        if (ids.Count == 0) return InventoryOpResult<UOMConversion>.Fail("No conversion selected.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.UOMConversions
            .Where(x => ids.Contains(x.Id) && x.CompanyId == _companyContext.CompanyId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return InventoryOpResult<UOMConversion>.Fail("Conversion was not found.");

        var stamp = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.IsDeleted = true;
            row.DeletedAtUtc = now;
            row.DeletedBy = stamp;
            row.ModifiedAtUtc = now;
            row.ModifiedBy = stamp;
        }
        await db.SaveChangesAsync(cancellationToken);
        return InventoryOpResult<UOMConversion>.Ok();
    }

    private async Task<string?> GateAsync(string permission, CancellationToken cancellationToken)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, cancellationToken);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(
            _accessRights, MenuCodes.InvItems, permission, cancellationToken);
        return access.Ok ? null : access.Error;
    }
}
