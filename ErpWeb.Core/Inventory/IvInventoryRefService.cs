using System.Data.Common;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class IvInventoryRefService : IIvInventoryRefService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;
    private readonly IIvStockCommonRepository _common;

    public IvInventoryRefService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        IIvStockCommonRepository common)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _common = common;
    }

    // ===================== Warehouse =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<IvWarehouseListRow>>> ListWarehousesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryWarehouse, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<IvWarehouseListRow>(ctx.Error.Value);
        }

        var rows = await _common.ListWarehousesAsync(ctx.CompanyCode!, ctx.BranchCode!, cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<IvWarehouseListRow>>.Ok(
            rows.Select(x => new IvWarehouseListRow
            {
                Code = x.WarehouseCode,
                Desc = x.WarehouseDesc,
                WarehouseType = x.WarehouseType,
                WarehouseRemark = x.WarehouseRemark,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? []
            }).ToList());
    }

    public async Task<IvMasterOperationResult<IvWarehouseEditVm>> GetWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryWarehouse, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvWarehouseEditVm>(ctx.Error.Value);
        }

        var code = (warehouseCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return FailVm<IvWarehouseEditVm>(IvMasterErrorCode.Validation, "Warehouse code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _common.GetWarehouseTrackedAsync(
            db, ctx.CompanyCode!, ctx.BranchCode!, code, cancellationToken);
        if (entity is null)
        {
            return FailVm<IvWarehouseEditVm>(IvMasterErrorCode.NotFound, "Warehouse not found.");
        }

        return IvMasterOperationResult<IvWarehouseEditVm>.Ok(MapWarehouse(entity));
    }

    public async Task<IvMasterOperationResult<IvWarehouseEditVm>> SaveWarehouseAsync(
        IvWarehouseEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<IvWarehouseEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryWarehouse, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvWarehouseEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 20)
        {
            errors["Code"] = "Code must be at most 20 characters.";
        }

        var desc = TruncateOptional(model.Desc, 100);
        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["Desc"] = "Description is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvWarehouseEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<IvWarehouseEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var existing = await _common.GetWarehouseTrackedAsync(
                    db, ctx.CompanyCode!, ctx.BranchCode!, code, cancellationToken);
                if (existing is not null)
                {
                    return FailVm<IvWarehouseEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Warehouse code already exists.",
                        "Code");
                }

                var entity = new IvWarehouse
                {
                    CompanyCode = ctx.CompanyCode!,
                    BranchCode = ctx.BranchCode!,
                    WarehouseCode = code,
                    WarehouseDesc = desc,
                    WarehouseType = TruncateOptional(model.WarehouseType, 20),
                    WarehouseRemark = TruncateOptional(model.WarehouseRemark, 250),
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.IvWarehouses.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<IvWarehouseEditVm>.Ok(MapWarehouse(entity));
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm<IvWarehouseEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "Row version is required for update.");
            }

            var tracked = await _common.GetWarehouseTrackedAsync(
                db, ctx.CompanyCode!, ctx.BranchCode!, code, cancellationToken);
            if (tracked is null)
            {
                return FailVm<IvWarehouseEditVm>(IvMasterErrorCode.NotFound, "Warehouse not found.");
            }

            if (!KeysEqual(tracked.WarehouseCode, code))
            {
                return FailVm<IvWarehouseEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<IvWarehouseEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This warehouse was modified by another user.");
            }

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.WarehouseDesc = desc;
            tracked.WarehouseType = TruncateOptional(model.WarehouseType, 20);
            tracked.WarehouseRemark = TruncateOptional(model.WarehouseRemark, 250);
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<IvWarehouseEditVm>.Ok(MapWarehouse(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<IvWarehouseEditVm>(
                IvMasterErrorCode.Concurrency,
                "This warehouse was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<IvWarehouseEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Warehouse code already exists.",
                "Code");
        }
    }

    public Task<IvMasterOperationResult<object>> SetWarehouseActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetActiveBranchAsync(
            MenuCodes.InventoryWarehouse,
            items,
            isActive,
            async (db, company, branch, codes, ct) =>
                await _common.GetWarehousesTrackedAsync(db, company, branch, codes, ct),
            (e, code) => string.Equals(e.WarehouseCode, code, StringComparison.OrdinalIgnoreCase),
            (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public async Task<DeleteCheckResult> CanDeleteWarehousesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryWarehouse, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeTokenCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await _common.CountWarehouseReferencesBulkAsync(
            db, ctx.CompanyCode!, ctx.BranchCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteWarehousesAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteBranchAsync(
            MenuCodes.InventoryWarehouse,
            items,
            async (db, company, branch, codes, ct) =>
                await _common.GetWarehousesTrackedAsync(db, company, branch, codes, ct),
            (e, code) => string.Equals(e.WarehouseCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, branch, codes, ct) =>
                await _common.CountWarehouseReferencesBulkAsync(db, company, branch, codes, ct),
            (db, entities) => db.IvWarehouses.RemoveRange(entities),
            cancellationToken);

    // ===================== Location =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<IvLocationListRow>>> ListLocationsAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryLocation, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<IvLocationListRow>(ctx.Error.Value);
        }

        var wh = (warehouseCode ?? string.Empty).Trim();
        if (wh.Length == 0)
        {
            return FailList<IvLocationListRow>(IvMasterErrorCode.Validation, "Warehouse is required.");
        }

        var rows = await _common.ListLocationsAsync(ctx.CompanyCode!, ctx.BranchCode!, wh, cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<IvLocationListRow>>.Ok(
            rows.Select(x => new IvLocationListRow
            {
                WarehouseCode = x.WarehouseCode,
                Code = x.LocCode,
                Desc = x.LocDesc,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? []
            }).ToList());
    }

    public async Task<IvMasterOperationResult<IvLocationEditVm>> GetLocationAsync(
        string warehouseCode,
        string locCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryLocation, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvLocationEditVm>(ctx.Error.Value);
        }

        var wh = (warehouseCode ?? string.Empty).Trim();
        var code = (locCode ?? string.Empty).Trim();
        if (wh.Length == 0 || code.Length == 0)
        {
            return FailVm<IvLocationEditVm>(IvMasterErrorCode.Validation, "Warehouse and location code are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _common.GetLocationTrackedAsync(
            db, ctx.CompanyCode!, ctx.BranchCode!, wh, code, cancellationToken);
        if (entity is null)
        {
            return FailVm<IvLocationEditVm>(IvMasterErrorCode.NotFound, "Location not found.");
        }

        return IvMasterOperationResult<IvLocationEditVm>.Ok(MapLocation(entity));
    }

    public async Task<IvMasterOperationResult<IvLocationEditVm>> SaveLocationAsync(
        IvLocationEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<IvLocationEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryLocation, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvLocationEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wh = (model.WarehouseCode ?? string.Empty).Trim();
        var code = (model.Code ?? string.Empty).Trim();
        if (wh.Length == 0)
        {
            errors["WarehouseCode"] = "Warehouse is required.";
        }

        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 10)
        {
            errors["Code"] = "Code must be at most 10 characters.";
        }

        var desc = TruncateOptional(model.Desc, 100);
        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["Desc"] = "Description is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvLocationEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<IvLocationEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var warehouse = await _common.GetWarehouseTrackedAsync(
            db, ctx.CompanyCode!, ctx.BranchCode!, wh, cancellationToken);
        if (warehouse is null)
        {
            return FailVm<IvLocationEditVm>(
                IvMasterErrorCode.Validation,
                "Warehouse not found.",
                "WarehouseCode");
        }

        try
        {
            if (isNew)
            {
                var existing = await _common.GetLocationTrackedAsync(
                    db, ctx.CompanyCode!, ctx.BranchCode!, wh, code, cancellationToken);
                if (existing is not null)
                {
                    return FailVm<IvLocationEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Location code already exists.",
                        "Code");
                }

                var entity = new IvLocation
                {
                    CompanyCode = ctx.CompanyCode!,
                    BranchCode = ctx.BranchCode!,
                    WarehouseCode = wh,
                    LocCode = code,
                    LocDesc = desc,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.IvLocations.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<IvLocationEditVm>.Ok(MapLocation(entity));
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm<IvLocationEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "Row version is required for update.");
            }

            var tracked = await _common.GetLocationTrackedAsync(
                db, ctx.CompanyCode!, ctx.BranchCode!, wh, code, cancellationToken);
            if (tracked is null)
            {
                return FailVm<IvLocationEditVm>(IvMasterErrorCode.NotFound, "Location not found.");
            }

            if (!KeysEqual(tracked.WarehouseCode, wh) || !KeysEqual(tracked.LocCode, code))
            {
                return FailVm<IvLocationEditVm>(
                    IvMasterErrorCode.Validation,
                    "Location code or warehouse cannot be changed.",
                    "Code");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<IvLocationEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This location was modified by another user.");
            }

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.LocDesc = desc;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<IvLocationEditVm>.Ok(MapLocation(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<IvLocationEditVm>(
                IvMasterErrorCode.Concurrency,
                "This location was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<IvLocationEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Location code already exists.",
                "Code");
        }
    }

    public async Task<IvMasterOperationResult<object>> SetLocationActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryLocation, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        if (tokens.Any(t => string.IsNullOrWhiteSpace(t.ParentCode)))
        {
            return FailObj(IvMasterErrorCode.Validation, "Warehouse is required for each location.");
        }

        var keys = tokens
            .Select(t => new IvLocationRefKey(t.ParentCode!.Trim(), t.Code))
            .ToList();
        var now = _dates.Now;
        var user = Truncate(ctx.UserId!, 10);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await _common.GetLocationsTrackedAsync(
                db, ctx.CompanyCode!, ctx.BranchCode!, keys, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e =>
                    string.Equals(e.WarehouseCode, token.ParentCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.LocCode, token.Code, StringComparison.OrdinalIgnoreCase));
                if (entity is null || !RowVersionsEqual(entity.RowVersion, token.RowVersion))
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = token.RowVersion;
                entity.IsActive = isActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<DeleteCheckResult> CanDeleteLocationsAsync(
        IReadOnlyList<IvMasterKeyToken> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryLocation, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var tokens = NormalizeTokens(keys);
        if (tokens.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        var locKeys = tokens
            .Where(t => !string.IsNullOrWhiteSpace(t.ParentCode))
            .Select(t => new IvLocationRefKey(t.ParentCode!.Trim(), t.Code))
            .ToList();
        if (locKeys.Count != tokens.Count)
        {
            return DeleteCheckResult.Blocked("Warehouse is required for each location.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await _common.CountLocationReferencesBulkAsync(
            db, ctx.CompanyCode!, ctx.BranchCode!, locKeys, cancellationToken);
        var displayKeys = locKeys.Select(k => FormatLocationKey(k.WarehouseCode, k.LocCode)).ToList();
        return BuildDeleteCheck(displayKeys, refs);
    }

    public async Task<IvMasterOperationResult<object>> DeleteLocationsAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.InventoryLocation, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        if (tokens.Any(t => string.IsNullOrWhiteSpace(t.ParentCode)))
        {
            return FailObj(IvMasterErrorCode.Validation, "Warehouse is required for each location.");
        }

        var locKeys = tokens
            .Select(t => new IvLocationRefKey(t.ParentCode!.Trim(), t.Code))
            .ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await _common.GetLocationsTrackedAsync(
                db, ctx.CompanyCode!, ctx.BranchCode!, locKeys, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e =>
                    string.Equals(e.WarehouseCode, token.ParentCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.LocCode, token.Code, StringComparison.OrdinalIgnoreCase));
                if (entity is null || !RowVersionsEqual(entity.RowVersion, token.RowVersion))
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = token.RowVersion;
            }

            var displayKeys = locKeys.Select(k => FormatLocationKey(k.WarehouseCode, k.LocCode)).ToList();
            var refs = await _common.CountLocationReferencesBulkAsync(
                db, ctx.CompanyCode!, ctx.BranchCode!, locKeys, cancellationToken);
            var check = BuildDeleteCheck(displayKeys, refs);
            if (!check.CanDelete)
            {
                return IvMasterOperationResult<object>.Fail(
                    IvMasterErrorCode.InUse,
                    check.Message ?? "One or more locations are in use.",
                    deleteCheck: check);
            }

            db.IvLocations.RemoveRange(entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch (DbUpdateException ex) when (IsForeignKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.InUse, "One or more locations are in use.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ===================== Status =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<IvStatusListRow>>> ListStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryStatus, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<IvStatusListRow>(ctx.Error.Value);
        }

        var rows = await _common.ListStatusesAsync(ctx.CompanyCode!, cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<IvStatusListRow>>.Ok(
            rows.Select(x => new IvStatusListRow
            {
                Code = x.IStatus,
                Desc = x.StatusDesc,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? []
            }).ToList());
    }

    public async Task<IvMasterOperationResult<IvStatusEditVm>> GetStatusAsync(
        string iStatus,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryStatus, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvStatusEditVm>(ctx.Error.Value);
        }

        var code = (iStatus ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return FailVm<IvStatusEditVm>(IvMasterErrorCode.Validation, "Status code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _common.GetStatusTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
        if (entity is null)
        {
            return FailVm<IvStatusEditVm>(IvMasterErrorCode.NotFound, "Status not found.");
        }

        return IvMasterOperationResult<IvStatusEditVm>.Ok(MapStatus(entity));
    }

    public async Task<IvMasterOperationResult<IvStatusEditVm>> SaveStatusAsync(
        IvStatusEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<IvStatusEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryStatus, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvStatusEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 10)
        {
            errors["Code"] = "Code must be at most 10 characters.";
        }

        var desc = TruncateOptional(model.Desc, 100);
        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["Desc"] = "Description is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvStatusEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<IvStatusEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var existing = await _common.GetStatusTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
                if (existing is not null)
                {
                    return FailVm<IvStatusEditVm>(
                        IvMasterErrorCode.DuplicateKey, "Status code already exists.", "Code");
                }

                var entity = new IvStatus
                {
                    CompanyCode = ctx.CompanyCode!,
                    IStatus = code,
                    StatusDesc = desc,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.IvStatuses.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<IvStatusEditVm>.Ok(MapStatus(entity));
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm<IvStatusEditVm>(
                    IvMasterErrorCode.Concurrency, "Row version is required for update.");
            }

            var tracked = await _common.GetStatusTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
            if (tracked is null)
            {
                return FailVm<IvStatusEditVm>(IvMasterErrorCode.NotFound, "Status not found.");
            }

            if (!KeysEqual(tracked.IStatus, code))
            {
                return FailVm<IvStatusEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<IvStatusEditVm>(
                    IvMasterErrorCode.Concurrency, "This status was modified by another user.");
            }

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.StatusDesc = desc;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<IvStatusEditVm>.Ok(MapStatus(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<IvStatusEditVm>(
                IvMasterErrorCode.Concurrency, "This status was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<IvStatusEditVm>(
                IvMasterErrorCode.DuplicateKey, "Status code already exists.", "Code");
        }
    }

    public Task<IvMasterOperationResult<object>> SetStatusActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetActiveCompanyAsync(
            MenuCodes.InventoryStatus,
            items,
            isActive,
            async (db, company, codes, ct) => await _common.GetStatusesTrackedAsync(db, company, codes, ct),
            (e, code) => string.Equals(e.IStatus, code, StringComparison.OrdinalIgnoreCase),
            (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public async Task<DeleteCheckResult> CanDeleteStatusesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryStatus, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeTokenCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await _common.CountStatusReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteStatusesAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyAsync(
            MenuCodes.InventoryStatus,
            items,
            async (db, company, codes, ct) => await _common.GetStatusesTrackedAsync(db, company, codes, ct),
            (e, code) => string.Equals(e.IStatus, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, codes, ct) =>
                await _common.CountStatusReferencesBulkAsync(db, company, codes, ct),
            (db, entities) => db.IvStatuses.RemoveRange(entities),
            cancellationToken);

    // ===================== UOM =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<IvUomListRow>>> ListUomsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryUom, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<IvUomListRow>(ctx.Error.Value);
        }

        var rows = await _common.ListUomsAsync(ctx.CompanyCode!, cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<IvUomListRow>>.Ok(
            rows.Select(x => new IvUomListRow
            {
                Code = x.UomCode,
                Desc = x.UomDesc,
                UneceUom = x.UneceUom,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? []
            }).ToList());
    }

    public async Task<IvMasterOperationResult<IvUomEditVm>> GetUomAsync(
        string uomCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryUom, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvUomEditVm>(ctx.Error.Value);
        }

        var code = (uomCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return FailVm<IvUomEditVm>(IvMasterErrorCode.Validation, "UOM code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _common.GetUomTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
        if (entity is null)
        {
            return FailVm<IvUomEditVm>(IvMasterErrorCode.NotFound, "UOM not found.");
        }

        return IvMasterOperationResult<IvUomEditVm>.Ok(MapUom(entity));
    }

    public async Task<IvMasterOperationResult<IvUomEditVm>> SaveUomAsync(
        IvUomEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<IvUomEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryUom, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvUomEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 10)
        {
            errors["Code"] = "Code must be at most 10 characters.";
        }

        var desc = TruncateOptional(model.Desc, 100);
        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["Desc"] = "Description is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvUomEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<IvUomEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var existing = await _common.GetUomTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
                if (existing is not null)
                {
                    return FailVm<IvUomEditVm>(
                        IvMasterErrorCode.DuplicateKey, "UOM code already exists.", "Code");
                }

                var entity = new MsUom
                {
                    CompanyCode = ctx.CompanyCode!,
                    UomCode = code,
                    UomDesc = desc,
                    UneceUom = TruncateOptional(model.UneceUom, 10),
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.MsUoms.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<IvUomEditVm>.Ok(MapUom(entity));
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm<IvUomEditVm>(
                    IvMasterErrorCode.Concurrency, "Row version is required for update.");
            }

            var tracked = await _common.GetUomTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
            if (tracked is null)
            {
                return FailVm<IvUomEditVm>(IvMasterErrorCode.NotFound, "UOM not found.");
            }

            if (!KeysEqual(tracked.UomCode, code))
            {
                return FailVm<IvUomEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<IvUomEditVm>(
                    IvMasterErrorCode.Concurrency, "This UOM was modified by another user.");
            }

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.UomDesc = desc;
            tracked.UneceUom = TruncateOptional(model.UneceUom, 10);
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<IvUomEditVm>.Ok(MapUom(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<IvUomEditVm>(
                IvMasterErrorCode.Concurrency, "This UOM was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<IvUomEditVm>(
                IvMasterErrorCode.DuplicateKey, "UOM code already exists.", "Code");
        }
    }

    public Task<IvMasterOperationResult<object>> SetUomActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetActiveCompanyAsync(
            MenuCodes.InventoryUom,
            items,
            isActive,
            async (db, company, codes, ct) => await _common.GetUomsTrackedAsync(db, company, codes, ct),
            (e, code) => string.Equals(e.UomCode, code, StringComparison.OrdinalIgnoreCase),
            (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public async Task<DeleteCheckResult> CanDeleteUomsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryUom, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeTokenCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await _common.CountUomReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteUomsAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyAsync(
            MenuCodes.InventoryUom,
            items,
            async (db, company, codes, ct) => await _common.GetUomsTrackedAsync(db, company, codes, ct),
            (e, code) => string.Equals(e.UomCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, codes, ct) =>
                await _common.CountUomReferencesBulkAsync(db, company, codes, ct),
            (db, entities) => db.MsUoms.RemoveRange(entities),
            cancellationToken);

    // ===================== Type =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<IvTypeListRow>>> ListTypesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryType, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<IvTypeListRow>(ctx.Error.Value);
        }

        var rows = await _common.ListTypesAsync(ctx.CompanyCode!, cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<IvTypeListRow>>.Ok(
            rows.Select(x => new IvTypeListRow
            {
                Code = x.TypeCode,
                Desc = x.TypeDesc,
                TypeName = x.TypeName,
                KeepStock = x.KeepStock,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? []
            }).ToList());
    }

    public async Task<IvMasterOperationResult<IvTypeEditVm>> GetTypeAsync(
        string typeCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryType, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvTypeEditVm>(ctx.Error.Value);
        }

        var code = (typeCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return FailVm<IvTypeEditVm>(IvMasterErrorCode.Validation, "Type code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _common.GetTypeTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
        if (entity is null)
        {
            return FailVm<IvTypeEditVm>(IvMasterErrorCode.NotFound, "Type not found.");
        }

        return IvMasterOperationResult<IvTypeEditVm>.Ok(MapType(entity));
    }

    public async Task<IvMasterOperationResult<IvTypeEditVm>> SaveTypeAsync(
        IvTypeEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<IvTypeEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryType, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvTypeEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 20)
        {
            errors["Code"] = "Code must be at most 20 characters.";
        }

        var typeName = TruncateOptional(model.TypeName, 100);
        if (string.IsNullOrWhiteSpace(typeName))
        {
            errors["TypeName"] = "Name is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvTypeEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<IvTypeEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var existing = await _common.GetTypeTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
                if (existing is not null)
                {
                    return FailVm<IvTypeEditVm>(
                        IvMasterErrorCode.DuplicateKey, "Type code already exists.", "Code");
                }

                var entity = new IvType
                {
                    CompanyCode = ctx.CompanyCode!,
                    TypeCode = code,
                    TypeName = typeName,
                    TypeDesc = TruncateOptional(model.TypeDesc, 200),
                    KeepStock = model.KeepStock,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.IvTypes.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<IvTypeEditVm>.Ok(MapType(entity));
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm<IvTypeEditVm>(
                    IvMasterErrorCode.Concurrency, "Row version is required for update.");
            }

            var tracked = await _common.GetTypeTrackedAsync(db, ctx.CompanyCode!, code, cancellationToken);
            if (tracked is null)
            {
                return FailVm<IvTypeEditVm>(IvMasterErrorCode.NotFound, "Type not found.");
            }

            if (!KeysEqual(tracked.TypeCode, code))
            {
                return FailVm<IvTypeEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<IvTypeEditVm>(
                    IvMasterErrorCode.Concurrency, "This type was modified by another user.");
            }

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.TypeName = typeName;
            tracked.TypeDesc = TruncateOptional(model.TypeDesc, 200);
            tracked.KeepStock = model.KeepStock;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<IvTypeEditVm>.Ok(MapType(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<IvTypeEditVm>(
                IvMasterErrorCode.Concurrency, "This type was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<IvTypeEditVm>(
                IvMasterErrorCode.DuplicateKey, "Type code already exists.", "Code");
        }
    }

    public Task<IvMasterOperationResult<object>> SetTypeActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetActiveCompanyAsync(
            MenuCodes.InventoryType,
            items,
            isActive,
            async (db, company, codes, ct) => await _common.GetTypesTrackedAsync(db, company, codes, ct),
            (e, code) => string.Equals(e.TypeCode, code, StringComparison.OrdinalIgnoreCase),
            (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public async Task<DeleteCheckResult> CanDeleteTypesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryType, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeTokenCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await _common.CountTypeReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteTypesAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyAsync(
            MenuCodes.InventoryType,
            items,
            async (db, company, codes, ct) => await _common.GetTypesTrackedAsync(db, company, codes, ct),
            (e, code) => string.Equals(e.TypeCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, codes, ct) =>
                await _common.CountTypeReferencesBulkAsync(db, company, codes, ct),
            (db, entities) => db.IvTypes.RemoveRange(entities),
            cancellationToken);

    // ===================== Class =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<IvClassListRow>>> ListClassesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryClass, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<IvClassListRow>(ctx.Error.Value);
        }

        var rows = await _common.ListClassesAsync(ctx.CompanyCode!, cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<IvClassListRow>>.Ok(
            rows.Select(x => new IvClassListRow
            {
                Code = x.IClassCode,
                Desc = x.IDesc,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? []
            }).ToList());
    }

    public async Task<IvMasterOperationResult<IvClassEditVm>> GetClassAsync(
        string iClassCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryClass, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvClassEditVm>(ctx.Error.Value);
        }

        var code = (iClassCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return FailVm<IvClassEditVm>(IvMasterErrorCode.Validation, "Class code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _common.GetClassTrackedAsync(
            db, ctx.CompanyCode!, code, includeSubClasses: true, cancellationToken);
        if (entity is null)
        {
            return FailVm<IvClassEditVm>(IvMasterErrorCode.NotFound, "Class not found.");
        }

        return IvMasterOperationResult<IvClassEditVm>.Ok(MapClass(entity));
    }

    public async Task<IvMasterOperationResult<IvClassEditVm>> SaveClassAsync(
        IvClassEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<IvClassEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryClass, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvClassEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 30)
        {
            errors["Code"] = "Code must be at most 30 characters.";
        }

        var desc = TruncateOptional(model.Desc, 200);
        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["Desc"] = "Description is required.";
        }

        var subClasses = model.SubClasses ?? [];
        var normalizedSubs = new List<(string Code, string? Desc, bool IsActive, byte[]? RowVersion)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < subClasses.Count; i++)
        {
            var sub = subClasses[i];
            var subCode = (sub?.Code ?? string.Empty).Trim();
            if (subCode.Length == 0)
            {
                errors[$"SubClasses[{i}].Code"] = "Subclass code is required.";
                continue;
            }

            if (subCode.Length > 30)
            {
                errors[$"SubClasses[{i}].Code"] = "Subclass code must be at most 30 characters.";
            }

            if (!seen.Add(subCode))
            {
                errors[$"SubClasses[{i}].Code"] = "Duplicate subclass code.";
                continue;
            }

            var subDesc = TruncateOptional(sub?.Desc, 100);
            if (string.IsNullOrWhiteSpace(subDesc))
            {
                errors[$"SubClasses[{i}].Desc"] = "Subclass description is required.";
            }

            normalizedSubs.Add((subCode, subDesc, sub?.IsActive ?? true, sub?.RowVersion));
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvClassEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<IvClassEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            IvClass entity;
            if (isNew)
            {
                var existing = await _common.GetClassTrackedAsync(
                    db, ctx.CompanyCode!, code, includeSubClasses: false, cancellationToken);
                if (existing is not null)
                {
                    return FailVm<IvClassEditVm>(
                        IvMasterErrorCode.DuplicateKey, "Class code already exists.", "Code");
                }

                entity = new IvClass
                {
                    CompanyCode = ctx.CompanyCode!,
                    IClassCode = code,
                    IDesc = desc,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.IvClasses.Add(entity);

                foreach (var sub in normalizedSubs)
                {
                    var subEntity = new IvSubClass
                    {
                        CompanyCode = ctx.CompanyCode!,
                        IClassCode = code,
                        ISubClassCode = sub.Code,
                        ISubClassName = sub.Desc,
                        IsActive = sub.IsActive,
                        CreatedDate = now,
                        CreatedBy = user,
                        ModifiedDate = now,
                        ModifiedBy = user
                    };
                    InventoryLeftoverSite.Apply(subEntity, writeScope);
                    db.IvSubClasses.Add(subEntity);
                }
            }
            else
            {
                if (model.RowVersion is null || model.RowVersion.Length == 0)
                {
                    return FailVm<IvClassEditVm>(
                        IvMasterErrorCode.Concurrency, "Row version is required for update.");
                }

                var tracked = await _common.GetClassTrackedAsync(
                    db, ctx.CompanyCode!, code, includeSubClasses: true, cancellationToken);
                if (tracked is null)
                {
                    return FailVm<IvClassEditVm>(IvMasterErrorCode.NotFound, "Class not found.");
                }

                if (!KeysEqual(tracked.IClassCode, code))
                {
                    return FailVm<IvClassEditVm>(
                        IvMasterErrorCode.Validation,
                        "Code cannot be changed.",
                        "Code");
                }

                if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
                {
                    return FailVm<IvClassEditVm>(
                        IvMasterErrorCode.Concurrency, "This class was modified by another user.");
                }

                db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
                tracked.IDesc = desc;
                tracked.IsActive = model.IsActive;
                tracked.ModifiedDate = now;
                tracked.ModifiedBy = user;
                entity = tracked;

                var incoming = normalizedSubs.ToDictionary(
                    s => s.Code, s => s, StringComparer.OrdinalIgnoreCase);
                var existingSubs = tracked.SubClasses.ToList();
                var toRemove = existingSubs
                    .Where(s => !incoming.ContainsKey(s.ISubClassCode))
                    .ToList();

                if (toRemove.Count > 0)
                {
                    var usage = await _common.CountItemSubclassUsageBulkAsync(
                        db,
                        ctx.CompanyCode!,
                        code,
                        toRemove.Select(s => s.ISubClassCode).ToList(),
                        cancellationToken);
                    var blocked = usage.Where(kv => kv.Value > 0).ToList();
                    if (blocked.Count > 0)
                    {
                        var hits = blocked
                            .Select(kv => new IvMasterReferenceHit
                            {
                                ReferenceType = "Item",
                                Count = kv.Value,
                                Detail = kv.Key
                            })
                            .ToList();
                        return IvMasterOperationResult<IvClassEditVm>.Fail(
                            IvMasterErrorCode.InUse,
                            "One or more subclasses are in use and cannot be removed.",
                            deleteCheck: DeleteCheckResult.Blocked(
                                "One or more subclasses are in use and cannot be removed.",
                                hits));
                    }

                    db.IvSubClasses.RemoveRange(toRemove);
                }

                foreach (var sub in normalizedSubs)
                {
                    var existingSub = existingSubs.FirstOrDefault(s =>
                        string.Equals(s.ISubClassCode, sub.Code, StringComparison.OrdinalIgnoreCase));
                    if (existingSub is null)
                    {
                        var subEntity = new IvSubClass
                        {
                            CompanyCode = ctx.CompanyCode!,
                            IClassCode = code,
                            ISubClassCode = sub.Code,
                            ISubClassName = sub.Desc,
                            IsActive = sub.IsActive,
                            CreatedDate = now,
                            CreatedBy = user,
                            ModifiedDate = now,
                            ModifiedBy = user
                        };
                        InventoryLeftoverSite.Apply(subEntity, writeScope);
                        db.IvSubClasses.Add(subEntity);
                        continue;
                    }

                    if (sub.RowVersion is not null && sub.RowVersion.Length > 0)
                    {
                        if (!RowVersionsEqual(existingSub.RowVersion, sub.RowVersion))
                        {
                            return FailVm<IvClassEditVm>(
                                IvMasterErrorCode.Concurrency,
                                "A subclass was modified by another user.");
                        }

                        db.Entry(existingSub).Property(x => x.RowVersion).OriginalValue = sub.RowVersion;
                    }

                    existingSub.ISubClassName = sub.Desc;
                    existingSub.IsActive = sub.IsActive;
                    existingSub.ModifiedDate = now;
                    existingSub.ModifiedBy = user;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var reloaded = await _common.GetClassTrackedAsync(
                db, ctx.CompanyCode!, code, includeSubClasses: true, cancellationToken);
            return IvMasterOperationResult<IvClassEditVm>.Ok(MapClass(reloaded!));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<IvClassEditVm>(
                IvMasterErrorCode.Concurrency, "This class was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<IvClassEditVm>(
                IvMasterErrorCode.DuplicateKey, "Class or subclass code already exists.", "Code");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task<IvMasterOperationResult<object>> SetClassActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetActiveCompanyAsync(
            MenuCodes.InventoryClass,
            items,
            isActive,
            async (db, company, codes, ct) =>
                await _common.GetClassesTrackedAsync(db, company, codes, includeSubClasses: false, ct),
            (e, code) => string.Equals(e.IClassCode, code, StringComparison.OrdinalIgnoreCase),
            (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public async Task<DeleteCheckResult> CanDeleteClassesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryClass, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeTokenCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await _common.CountClassReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public async Task<IvMasterOperationResult<object>> DeleteClassesAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.InventoryClass, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(t => t.Code).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await _common.GetClassesTrackedAsync(
                db, ctx.CompanyCode!, codes, includeSubClasses: true, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e =>
                    string.Equals(e.IClassCode, token.Code, StringComparison.OrdinalIgnoreCase));
                if (entity is null || !RowVersionsEqual(entity.RowVersion, token.RowVersion))
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = token.RowVersion;
            }

            var refs = await _common.CountClassReferencesBulkAsync(
                db, ctx.CompanyCode!, codes, cancellationToken);
            var check = BuildDeleteCheck(codes, refs);
            if (!check.CanDelete)
            {
                return IvMasterOperationResult<object>.Fail(
                    IvMasterErrorCode.InUse,
                    check.Message ?? "One or more classes are in use.",
                    deleteCheck: check);
            }

            foreach (var entity in entities)
            {
                if (entity.SubClasses.Count > 0)
                {
                    db.IvSubClasses.RemoveRange(entity.SubClasses);
                }
            }

            db.IvClasses.RemoveRange(entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch (DbUpdateException ex) when (IsForeignKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.InUse, "One or more classes are in use.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ===================== Shared helpers =====================

    private async Task<IvMasterOperationResult<object>> SetActiveBranchAsync<TEntity>(
        string menuCode,
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        Func<AppDbContext, string, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TEntity>>> load,
        Func<TEntity, string, bool> match,
        Action<TEntity, bool, DateTime, string> apply,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ctx = await RequireBranchScopeAsync(menuCode, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(t => t.Code).ToList();
        var now = _dates.Now;
        var user = Truncate(ctx.UserId!, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await load(db, ctx.CompanyCode!, ctx.BranchCode!, codes, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e => match(e, token.Code));
                if (entity is null)
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                var entry = db.Entry(entity);
                var current = entry.Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(current, token.RowVersion))
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                entry.Property("RowVersion").OriginalValue = token.RowVersion;
                apply(entity, isActive, now, user);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<IvMasterOperationResult<object>> SetActiveCompanyAsync<TEntity>(
        string menuCode,
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        Func<AppDbContext, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TEntity>>> load,
        Func<TEntity, string, bool> match,
        Action<TEntity, bool, DateTime, string> apply,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(t => t.Code).ToList();
        var now = _dates.Now;
        var user = Truncate(ctx.UserId!, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await load(db, ctx.CompanyCode!, codes, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e => match(e, token.Code));
                if (entity is null)
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                var entry = db.Entry(entity);
                var current = entry.Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(current, token.RowVersion))
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                entry.Property("RowVersion").OriginalValue = token.RowVersion;
                apply(entity, isActive, now, user);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<IvMasterOperationResult<object>> DeleteBranchAsync<TEntity>(
        string menuCode,
        IReadOnlyList<IvMasterKeyToken> items,
        Func<AppDbContext, string, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TEntity>>> load,
        Func<TEntity, string, bool> match,
        Func<AppDbContext, string, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>>> probe,
        Action<AppDbContext, IReadOnlyList<TEntity>> remove,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ctx = await RequireBranchScopeAsync(menuCode, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(t => t.Code).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await load(db, ctx.CompanyCode!, ctx.BranchCode!, codes, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e => match(e, token.Code));
                if (entity is null)
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                var entry = db.Entry(entity);
                var current = entry.Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(current, token.RowVersion))
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                entry.Property("RowVersion").OriginalValue = token.RowVersion;
            }

            var refs = await probe(db, ctx.CompanyCode!, ctx.BranchCode!, codes, cancellationToken);
            var check = BuildDeleteCheck(codes, refs);
            if (!check.CanDelete)
            {
                return IvMasterOperationResult<object>.Fail(
                    IvMasterErrorCode.InUse,
                    check.Message ?? "One or more records are in use.",
                    deleteCheck: check);
            }

            remove(db, entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch (DbUpdateException ex) when (IsForeignKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.InUse, "One or more records are in use.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<IvMasterOperationResult<object>> DeleteCompanyAsync<TEntity>(
        string menuCode,
        IReadOnlyList<IvMasterKeyToken> items,
        Func<AppDbContext, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TEntity>>> load,
        Func<TEntity, string, bool> match,
        Func<AppDbContext, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>>> probe,
        Action<AppDbContext, IReadOnlyList<TEntity>> remove,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(t => t.Code).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await load(db, ctx.CompanyCode!, codes, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e => match(e, token.Code));
                if (entity is null)
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                var entry = db.Entry(entity);
                var current = entry.Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(current, token.RowVersion))
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                entry.Property("RowVersion").OriginalValue = token.RowVersion;
            }

            var refs = await probe(db, ctx.CompanyCode!, codes, cancellationToken);
            var check = BuildDeleteCheck(codes, refs);
            if (!check.CanDelete)
            {
                return IvMasterOperationResult<object>.Fail(
                    IvMasterErrorCode.InUse,
                    check.Message ?? "One or more records are in use.",
                    deleteCheck: check);
            }

            remove(db, entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch (DbUpdateException ex) when (IsForeignKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.InUse, "One or more records are in use.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<UserContext> RequireCompanyScopeAsync(
        string menuCode,
        string permission,
        CancellationToken cancellationToken)
    {
        var ctx = ValidateCompanyContext();
        if (ctx.Error is not null)
        {
            return ctx;
        }

        if (!await _accessRights.CanAsync(menuCode, permission, cancellationToken))
        {
            return UserContext.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        return ctx;
    }

    private async Task<UserContext> RequireBranchScopeAsync(
        string menuCode,
        string permission,
        CancellationToken cancellationToken)
    {
        var ctx = ValidateBranchContext();
        if (ctx.Error is not null)
        {
            return ctx;
        }

        if (!await _accessRights.CanAsync(menuCode, permission, cancellationToken))
        {
            return UserContext.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        return ctx;
    }

    private UserContext ValidateCompanyContext()
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        return UserContext.Ok(scope.CompanyCode, null, scope.UserId);
    }

    private UserContext ValidateBranchContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope, "Invalid branch context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode, scope.UserId);
    }

    private static DeleteCheckResult BuildDeleteCheck(
        IReadOnlyList<string> codes,
        IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>> refs)
    {
        var hits = new List<IvMasterReferenceHit>();
        foreach (var code in codes)
        {
            if (!refs.TryGetValue(code, out var list))
            {
                continue;
            }

            foreach (var hit in list.Where(h => h.Count > 0))
            {
                hits.Add(new IvMasterReferenceHit
                {
                    ReferenceType = hit.ReferenceType,
                    Count = hit.Count,
                    Detail = code
                });
            }
        }

        if (hits.Count == 0)
        {
            return DeleteCheckResult.Ok();
        }

        return DeleteCheckResult.Blocked(
            "One or more selected records are in use.",
            hits);
    }

    private static List<IvMasterKeyToken> NormalizeTokens(IReadOnlyList<IvMasterKeyToken>? items)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        return items
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Code) && x.RowVersion is { Length: > 0 })
            .Select(x => new IvMasterKeyToken
            {
                Code = x.Code.Trim(),
                RowVersion = x.RowVersion,
                ParentCode = string.IsNullOrWhiteSpace(x.ParentCode) ? null : x.ParentCode.Trim()
            })
            .GroupBy(x => $"{x.ParentCode}|{x.Code}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static List<string> NormalizeTokenCodes(IReadOnlyList<string>? codes) =>
        (codes ?? [])
            .Select(c => (c ?? string.Empty).Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool KeysEqual(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool RowVersionsEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null || left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException dbEx)
            {
                var msg = dbEx.Message;
                if (msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("2627") ||
                    msg.Contains("2601"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsForeignKey(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (msg.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("547"))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatLocationKey(string warehouseCode, string locCode) =>
        $"{warehouseCode.Trim()}|{locCode.Trim()}";

    private static string StaleBulkMessage() =>
        "1 selected item was modified by another user. No records were changed. Please reload and try again.";

    private static string MessageFor(IvMasterErrorCode code) => code switch
    {
        IvMasterErrorCode.AccessDenied => "Not authorized.",
        IvMasterErrorCode.InvalidScope => "Invalid company or branch context.",
        _ => "Request failed."
    };

    private static IvMasterOperationResult<IReadOnlyList<T>> FailList<T>(
        IvMasterErrorCode code,
        string? message = null) =>
        IvMasterOperationResult<IReadOnlyList<T>>.Fail(code, message ?? MessageFor(code));

    private static IvMasterOperationResult<T> FailVm<T>(
        IvMasterErrorCode code,
        string? message = null,
        string? field = null)
    {
        IReadOnlyDictionary<string, string>? errors = null;
        if (!string.IsNullOrWhiteSpace(field) && !string.IsNullOrWhiteSpace(message))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [field] = message
            };
        }

        return IvMasterOperationResult<T>.Fail(code, message ?? MessageFor(code), errors);
    }

    private static IvMasterOperationResult<object> FailObj(
        IvMasterErrorCode code,
        string? message = null) =>
        IvMasterOperationResult<object>.Fail(code, message ?? MessageFor(code));

    private static IvWarehouseEditVm MapWarehouse(IvWarehouse x) => new()
    {
        Code = x.WarehouseCode,
        Desc = x.WarehouseDesc,
        WarehouseType = x.WarehouseType,
        WarehouseRemark = x.WarehouseRemark,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static IvLocationEditVm MapLocation(IvLocation x) => new()
    {
        WarehouseCode = x.WarehouseCode,
        Code = x.LocCode,
        Desc = x.LocDesc,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static IvStatusEditVm MapStatus(IvStatus x) => new()
    {
        Code = x.IStatus,
        Desc = x.StatusDesc,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static IvUomEditVm MapUom(MsUom x) => new()
    {
        Code = x.UomCode,
        Desc = x.UomDesc,
        UneceUom = x.UneceUom,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static IvTypeEditVm MapType(IvType x) => new()
    {
        Code = x.TypeCode,
        TypeName = x.TypeName,
        TypeDesc = x.TypeDesc,
        KeepStock = x.KeepStock,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static IvClassEditVm MapClass(IvClass x) => new()
    {
        Code = x.IClassCode,
        Desc = x.IDesc,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy,
        SubClasses = x.SubClasses
            .OrderBy(s => s.ISubClassCode)
            .Select(s => new IvSubClassEditVm
            {
                Code = s.ISubClassCode,
                Desc = s.ISubClassName,
                IsActive = s.IsActive,
                RowVersion = s.RowVersion
            })
            .ToList()
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private readonly record struct UserContext(
        string? CompanyCode,
        string? BranchCode,
        string? UserId,
        IvMasterErrorCode? Error)
    {
        public static UserContext Ok(string companyCode, string? branchCode, string userId) =>
            new(companyCode, branchCode, userId, null);

        public static UserContext Fail(IvMasterErrorCode code, string _) =>
            new(null, null, null, code);
    }
}
