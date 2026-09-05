using System.Data.Common;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

public sealed class SaSalesRefService : ISaSalesRefService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;

    public SaSalesRefService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
    }

    // ===================== Customer Type =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaCustTypeListRow>>> ListCustTypesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustType, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaCustTypeListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaCustTypes
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.CustTypeCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaCustTypeListRow>>.Ok(
            rows.Select(x => new SaCustTypeListRow
            {
                Code = x.CustTypeCode,
                Desc = x.CustTypeDesc,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? []
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaCustTypeEditVm>> GetCustTypeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustType, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCustTypeEditVm>(ctx.Error.Value);
        }

        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return FailVm<SaCustTypeEditVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaCustTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.CustTypeCode == trimmed,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaCustTypeEditVm>(IvMasterErrorCode.NotFound, "Customer type not found.");
        }

        return IvMasterOperationResult<SaCustTypeEditVm>.Ok(MapCustType(entity));
    }

    public async Task<IvMasterOperationResult<SaCustTypeEditVm>> SaveCustTypeAsync(
        SaCustTypeEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaCustTypeEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustType, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCustTypeEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 40)
        {
            errors["Code"] = "Code must be at most 40 characters.";
        }

        var desc = TruncateOptional(model.Desc, 200);
        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["Desc"] = "Description is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaCustTypeEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaCustTypeEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var exists = await db.SaCustTypes.AnyAsync(
                    x => x.CompanyCode == ctx.CompanyCode && x.CustTypeCode == code,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaCustTypeEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Customer type code already exists.",
                        "Code");
                }

                var entity = new SaCustType
                {
                    CompanyCode = ctx.CompanyCode!,
                    CustTypeCode = code,
                    CustTypeDesc = desc,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaCustTypes.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<SaCustTypeEditVm>.Ok(MapCustType(entity));
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm<SaCustTypeEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "Row version is required for update.");
            }

            var tracked = await db.SaCustTypes.FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.CustTypeCode == code,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm<SaCustTypeEditVm>(IvMasterErrorCode.NotFound, "Customer type not found.");
            }

            if (!KeysEqual(tracked.CustTypeCode, code))
            {
                return FailVm<SaCustTypeEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<SaCustTypeEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This customer type was modified by another user.");
            }

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.CustTypeDesc = desc;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<SaCustTypeEditVm>.Ok(MapCustType(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<SaCustTypeEditVm>(
                IvMasterErrorCode.Concurrency,
                "This customer type was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaCustTypeEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Customer type code already exists.",
                "Code");
        }
    }

    public async Task<IvMasterOperationResult<object>> SetCustTypeActiveAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustType, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeCompanyTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(t => t.Code).ToList();
        var now = _dates.Now;
        var user = Truncate(ctx.UserId!, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await db.SaCustTypes
                .Where(x => x.CompanyCode == ctx.CompanyCode && codes.Contains(x.CustTypeCode))
                .ToListAsync(cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e =>
                    string.Equals(e.CustTypeCode, token.Code, StringComparison.OrdinalIgnoreCase));
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

    public async Task<DeleteCheckResult> CanDeleteCustTypesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustType, PermissionCodes.Delete, cancellationToken);
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
        var refs = await CountCustTypeReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteCustTypesAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyWithRowVersionAsync<SaCustType>(
            MenuCodes.SalesCustType,
            items,
            async (db, company, codes, ct) =>
                await db.SaCustTypes
                    .Where(x => x.CompanyCode == company && codes.Contains(x.CustTypeCode))
                    .ToListAsync(ct),
            (e, code) => string.Equals(e.CustTypeCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, codes, ct) =>
                await CountCustTypeReferencesBulkAsync(db, company, codes, ct),
            (db, entities) => db.SaCustTypes.RemoveRange(entities),
            cancellationToken);

    // ===================== Customer Group =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaCustGroupListRow>>> ListCustGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustGroup, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaCustGroupListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaCustGroups
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.CustGroupCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaCustGroupListRow>>.Ok(
            rows.Select(x => new SaCustGroupListRow
            {
                Code = x.CustGroupCode,
                Desc = x.CustGroupDesc,
                RowVersion = x.RowVersion ?? []
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaCustGroupEditVm>> GetCustGroupAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustGroup, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCustGroupEditVm>(ctx.Error.Value);
        }

        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return FailVm<SaCustGroupEditVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaCustGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.CustGroupCode == trimmed,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaCustGroupEditVm>(IvMasterErrorCode.NotFound, "Customer group not found.");
        }

        return IvMasterOperationResult<SaCustGroupEditVm>.Ok(MapCustGroup(entity));
    }

    public async Task<IvMasterOperationResult<SaCustGroupEditVm>> SaveCustGroupAsync(
        SaCustGroupEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaCustGroupEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustGroup, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCustGroupEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 40)
        {
            errors["Code"] = "Code must be at most 40 characters.";
        }

        var desc = TruncateOptional(model.Desc, 200);
        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["Desc"] = "Description is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaCustGroupEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaCustGroupEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var exists = await db.SaCustGroups.AnyAsync(
                    x => x.CompanyCode == ctx.CompanyCode && x.CustGroupCode == code,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaCustGroupEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Customer group code already exists.",
                        "Code");
                }

                var entity = new SaCustGroup
                {
                    CompanyCode = ctx.CompanyCode!,
                    CustGroupCode = code,
                    CustGroupDesc = desc,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaCustGroups.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<SaCustGroupEditVm>.Ok(MapCustGroup(entity));
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm<SaCustGroupEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "Row version is required for update.");
            }

            var tracked = await db.SaCustGroups.FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.CustGroupCode == code,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm<SaCustGroupEditVm>(IvMasterErrorCode.NotFound, "Customer group not found.");
            }

            if (!KeysEqual(tracked.CustGroupCode, code))
            {
                return FailVm<SaCustGroupEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<SaCustGroupEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This customer group was modified by another user.");
            }

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.CustGroupDesc = desc;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<SaCustGroupEditVm>.Ok(MapCustGroup(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<SaCustGroupEditVm>(
                IvMasterErrorCode.Concurrency,
                "This customer group was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaCustGroupEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Customer group code already exists.",
                "Code");
        }
    }

    public async Task<DeleteCheckResult> CanDeleteCustGroupsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustGroup, PermissionCodes.Delete, cancellationToken);
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
        var refs = await CountCustGroupReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteCustGroupsAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyWithRowVersionAsync<SaCustGroup>(
            MenuCodes.SalesCustGroup,
            items,
            async (db, company, codes, ct) =>
                await db.SaCustGroups
                    .Where(x => x.CompanyCode == company && codes.Contains(x.CustGroupCode))
                    .ToListAsync(ct),
            (e, code) => string.Equals(e.CustGroupCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, codes, ct) =>
                await CountCustGroupReferencesBulkAsync(db, company, codes, ct),
            (db, entities) => db.SaCustGroups.RemoveRange(entities),
            cancellationToken);

    // ===================== Area =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaAreaListRow>>> ListAreasAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesArea, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaAreaListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.IvAreaCodes
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.AreaCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaAreaListRow>>.Ok(
            rows.Select(x => new SaAreaListRow
            {
                Code = x.AreaCode,
                Desc = x.AreaDesc,
                Latitude = x.Latitude,
                Longitude = x.Longitude
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaAreaEditVm>> GetAreaAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesArea, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaAreaEditVm>(ctx.Error.Value);
        }

        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return FailVm<SaAreaEditVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.IvAreaCodes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.AreaCode == trimmed,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaAreaEditVm>(IvMasterErrorCode.NotFound, "Area not found.");
        }

        return IvMasterOperationResult<SaAreaEditVm>.Ok(MapArea(entity));
    }

    public async Task<IvMasterOperationResult<SaAreaEditVm>> SaveAreaAsync(
        SaAreaEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaAreaEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesArea, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaAreaEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 40)
        {
            errors["Code"] = "Code must be at most 40 characters.";
        }

        var desc = TruncateOptional(model.Desc, 200);
        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["Desc"] = "Description is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaAreaEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaAreaEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var exists = await db.IvAreaCodes.AnyAsync(
                    x => x.CompanyCode == ctx.CompanyCode && x.AreaCode == code,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaAreaEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Area code already exists.",
                        "Code");
                }

                var entity = new IvAreaCode
                {
                    CompanyCode = ctx.CompanyCode!,
                    AreaCode = code,
                    AreaDesc = desc,
                    Latitude = TruncateOptional(model.Latitude, 50),
                    Longitude = TruncateOptional(model.Longitude, 50),
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.IvAreaCodes.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<SaAreaEditVm>.Ok(MapArea(entity));
            }

            var tracked = await db.IvAreaCodes.FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.AreaCode == code,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm<SaAreaEditVm>(IvMasterErrorCode.NotFound, "Area not found.");
            }

            if (!KeysEqual(tracked.AreaCode, code))
            {
                return FailVm<SaAreaEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            tracked.AreaDesc = desc;
            tracked.Latitude = TruncateOptional(model.Latitude, 50);
            tracked.Longitude = TruncateOptional(model.Longitude, 50);
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<SaAreaEditVm>.Ok(MapArea(tracked));
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaAreaEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Area code already exists.",
                "Code");
        }
    }

    public async Task<DeleteCheckResult> CanDeleteAreasAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesArea, PermissionCodes.Delete, cancellationToken);
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
        var refs = await CountAreaReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteAreasAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyByCodesAsync<IvAreaCode>(
            MenuCodes.SalesArea,
            codes,
            async (db, company, list, ct) =>
                await db.IvAreaCodes
                    .Where(x => x.CompanyCode == company && list.Contains(x.AreaCode))
                    .ToListAsync(ct),
            (e, code) => string.Equals(e.AreaCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, list, ct) =>
                await CountAreaReferencesBulkAsync(db, company, list, ct),
            (db, entities) => db.IvAreaCodes.RemoveRange(entities),
            cancellationToken);

    // ===================== Country (global) =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaCountryListRow>>> ListCountriesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCountry, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaCountryListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaCountries
            .AsNoTracking()
            .OrderBy(x => x.CountryCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaCountryListRow>>.Ok(
            rows.Select(x => new SaCountryListRow
            {
                Code = x.CountryCode,
                Name = x.CountryName,
                Latitude = x.Latitude,
                Longitude = x.Longitude
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaCountryEditVm>> GetCountryAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCountry, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCountryEditVm>(ctx.Error.Value);
        }

        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return FailVm<SaCountryEditVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaCountries
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CountryCode == trimmed, cancellationToken);
        if (entity is null)
        {
            return FailVm<SaCountryEditVm>(IvMasterErrorCode.NotFound, "Country not found.");
        }

        return IvMasterOperationResult<SaCountryEditVm>.Ok(MapCountry(entity));
    }

    public async Task<IvMasterOperationResult<SaCountryEditVm>> SaveCountryAsync(
        SaCountryEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaCountryEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCountry, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCountryEditVm>(ctx.Error.Value);
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

        var name = TruncateOptional(model.Name, 100);
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["Name"] = "Name is required.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaCountryEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var exists = await db.SaCountries.AnyAsync(x => x.CountryCode == code, cancellationToken);
                if (exists)
                {
                    return FailVm<SaCountryEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Country code already exists.",
                        "Code");
                }

                var entity = new SaCountry
                {
                    CountryCode = code,
                    CountryName = name,
                    Latitude = model.Latitude,
                    Longitude = model.Longitude
                };
                db.SaCountries.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<SaCountryEditVm>.Ok(MapCountry(entity));
            }

            var tracked = await db.SaCountries.FirstOrDefaultAsync(x => x.CountryCode == code, cancellationToken);
            if (tracked is null)
            {
                return FailVm<SaCountryEditVm>(IvMasterErrorCode.NotFound, "Country not found.");
            }

            if (!KeysEqual(tracked.CountryCode, code))
            {
                return FailVm<SaCountryEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            tracked.CountryName = name;
            tracked.Latitude = model.Latitude;
            tracked.Longitude = model.Longitude;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<SaCountryEditVm>.Ok(MapCountry(tracked));
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaCountryEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Country code already exists.",
                "Code");
        }
    }

    public async Task<DeleteCheckResult> CanDeleteCountriesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCountry, PermissionCodes.Delete, cancellationToken);
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
        var refs = await CountCountryReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteCountriesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        DeleteGlobalByCodesAsync<SaCountry>(
            MenuCodes.SalesCountry,
            codes,
            async (db, list, ct) =>
                await db.SaCountries.Where(x => list.Contains(x.CountryCode)).ToListAsync(ct),
            (e, code) => string.Equals(e.CountryCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, list, ct) =>
                await CountCountryReferencesBulkAsync(db, company, list, ct),
            (db, entities) => db.SaCountries.RemoveRange(entities),
            cancellationToken);

    // ===================== Currency =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaCurrencyListRow>>> ListCurrenciesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrency, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaCurrencyListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaCurrencies
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.CurrCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaCurrencyListRow>>.Ok(
            rows.Select(x => new SaCurrencyListRow
            {
                Code = x.CurrCode,
                Desc = x.CurrDesc,
                IsActive = x.IsActive != false
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaCurrencyEditVm>> GetCurrencyAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrency, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCurrencyEditVm>(ctx.Error.Value);
        }

        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return FailVm<SaCurrencyEditVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaCurrencies
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.CurrCode == trimmed,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaCurrencyEditVm>(IvMasterErrorCode.NotFound, "Currency not found.");
        }

        return IvMasterOperationResult<SaCurrencyEditVm>.Ok(MapCurrency(entity));
    }

    public async Task<IvMasterOperationResult<SaCurrencyEditVm>> SaveCurrencyAsync(
        SaCurrencyEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaCurrencyEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrency, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCurrencyEditVm>(ctx.Error.Value);
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
            return IvMasterOperationResult<SaCurrencyEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaCurrencyEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var exists = await db.SaCurrencies.AnyAsync(
                    x => x.CompanyCode == ctx.CompanyCode && x.CurrCode == code,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaCurrencyEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Currency code already exists.",
                        "Code");
                }

                var entity = new SaCurrency
                {
                    CompanyCode = ctx.CompanyCode!,
                    CurrCode = code,
                    CurrDesc = desc,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaCurrencies.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<SaCurrencyEditVm>.Ok(MapCurrency(entity));
            }

            var tracked = await db.SaCurrencies.FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.CurrCode == code,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm<SaCurrencyEditVm>(IvMasterErrorCode.NotFound, "Currency not found.");
            }

            if (!KeysEqual(tracked.CurrCode, code))
            {
                return FailVm<SaCurrencyEditVm>(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    "Code");
            }

            tracked.CurrDesc = desc;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<SaCurrencyEditVm>.Ok(MapCurrency(tracked));
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaCurrencyEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Currency code already exists.",
                "Code");
        }
    }

    public async Task<IvMasterOperationResult<object>> SetCurrencyActiveAsync(
        IReadOnlyList<string> codes,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrency, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var list = NormalizeTokenCodes(codes);
        if (list.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var now = _dates.Now;
        var user = Truncate(ctx.UserId!, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await db.SaCurrencies
                .Where(x => x.CompanyCode == ctx.CompanyCode && list.Contains(x.CurrCode))
                .ToListAsync(cancellationToken);
            if (entities.Count != list.Count)
            {
                return FailObj(IvMasterErrorCode.NotFound, "One or more currencies were not found.");
            }

            foreach (var entity in entities)
            {
                entity.IsActive = isActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<DeleteCheckResult> CanDeleteCurrenciesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrency, PermissionCodes.Delete, cancellationToken);
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
        var refs = await CountCurrencyReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteCurrenciesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyByCodesAsync<SaCurrency>(
            MenuCodes.SalesCurrency,
            codes,
            async (db, company, list, ct) =>
                await db.SaCurrencies
                    .Where(x => x.CompanyCode == company && list.Contains(x.CurrCode))
                    .ToListAsync(ct),
            (e, code) => string.Equals(e.CurrCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, list, ct) =>
                await CountCurrencyReferencesBulkAsync(db, company, list, ct),
            (db, entities) => db.SaCurrencies.RemoveRange(entities),
            cancellationToken);

    // ===================== Payment Term =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaPaymentTermListRow>>> ListPaymentTermsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesPayTerm, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaPaymentTermListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaPaymentTerms
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.PayCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaPaymentTermListRow>>.Ok(
            rows.Select(x => new SaPaymentTermListRow
            {
                Code = x.PayCode,
                Desc = x.PayDesc,
                Days = x.Days,
                IsActive = x.IsActive != false
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaPaymentTermEditVm>> GetPaymentTermAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesPayTerm, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaPaymentTermEditVm>(ctx.Error.Value);
        }

        var normalized = NormalizeMasterCode(code);
        if (normalized.Length == 0)
        {
            return FailVm<SaPaymentTermEditVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaPaymentTerms
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.PayCode == normalized,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaPaymentTermEditVm>(IvMasterErrorCode.NotFound, "Payment term not found.");
        }

        return IvMasterOperationResult<SaPaymentTermEditVm>.Ok(MapPaymentTerm(entity));
    }

    public async Task<IvMasterOperationResult<SaPaymentTermEditVm>> SavePaymentTermAsync(
        SaPaymentTermEditVm model,
        bool isNew,
        string? expectedFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaPaymentTermEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesPayTerm, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaPaymentTermEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = NormalizeMasterCode(model.Code);
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 20)
        {
            errors["Code"] = "Code must be at most 20 characters.";
        }

        var desc = (model.Desc ?? string.Empty).Trim();
        if (desc.Length == 0)
        {
            errors["Desc"] = "Description is required.";
        }
        else if (desc.Length > 100)
        {
            errors["Desc"] = "Description must be at most 100 characters.";
        }

        if (model.Days is null)
        {
            errors["Days"] = "Days is required.";
        }
        else if (model.Days.Value < 0 || model.Days.Value > 9999)
        {
            errors["Days"] = "Days must be between 0 and 9999.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaPaymentTermEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaPaymentTermEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var exists = await db.SaPaymentTerms.AnyAsync(
                    x => x.CompanyCode == ctx.CompanyCode && x.PayCode == code,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaPaymentTermEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Payment term code already exists.",
                        "Code");
                }

                var entity = new SaPaymentTerm
                {
                    CompanyCode = ctx.CompanyCode!,
                    PayCode = code,
                    PayDesc = desc,
                    Days = model.Days,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaPaymentTerms.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<SaPaymentTermEditVm>.Ok(MapPaymentTerm(entity));
            }

            if (string.IsNullOrEmpty(expectedFingerprint))
            {
                return FailVm<SaPaymentTermEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "Concurrency token is missing. Reload and try again.");
            }

            var tracked = await db.SaPaymentTerms.FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.PayCode == code,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm<SaPaymentTermEditVm>(IvMasterErrorCode.NotFound, "Payment term not found.");
            }

            if (SaMasterFingerprint.PaymentTerm(MapPaymentTerm(tracked)) != expectedFingerprint)
            {
                return FailVm<SaPaymentTermEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This record was modified by another user.");
            }

            tracked.PayDesc = desc;
            tracked.Days = model.Days;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<SaPaymentTermEditVm>.Ok(MapPaymentTerm(tracked));
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaPaymentTermEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Payment term code already exists.",
                "Code");
        }
    }

    public async Task<IvMasterOperationResult<object>> SetPaymentTermActiveAsync(
        IReadOnlyList<string> codes,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesPayTerm, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var list = NormalizeMasterCodes(codes);
        if (list.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var now = _dates.Now;
        var user = Truncate(ctx.UserId!, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await db.SaPaymentTerms
                .Where(x => x.CompanyCode == ctx.CompanyCode && list.Contains(x.PayCode))
                .ToListAsync(cancellationToken);
            if (entities.Count != list.Count)
            {
                return FailObj(IvMasterErrorCode.NotFound, "One or more payment terms were not found.");
            }

            foreach (var entity in entities)
            {
                entity.IsActive = isActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<DeleteCheckResult> CanDeletePaymentTermsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesPayTerm, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeMasterCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await CountPaymentTermReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeletePaymentTermsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyByCodesAsync<SaPaymentTerm>(
            MenuCodes.SalesPayTerm,
            NormalizeMasterCodes(codes),
            async (db, company, list, ct) =>
                await db.SaPaymentTerms
                    .Where(x => x.CompanyCode == company && list.Contains(x.PayCode))
                    .ToListAsync(ct),
            (e, code) => string.Equals(e.PayCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, list, ct) =>
                await CountPaymentTermReferencesBulkAsync(db, company, list, ct),
            (db, entities) => db.SaPaymentTerms.RemoveRange(entities),
            cancellationToken);

    // ===================== Sales Rep =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaSalesRepListRow>>> ListSalesRepsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesSalesRep, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaSalesRepListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaSalesReps
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.SrepCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaSalesRepListRow>>.Ok(
            rows.Select(x => new SaSalesRepListRow
            {
                Code = x.SrepCode,
                Name = x.SrepName,
                Tel = x.Tel,
                Email = x.Email,
                CommissionRate = x.CommissionRate,
                IsActive = x.IsActive != false
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaSalesRepEditVm>> GetSalesRepAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesSalesRep, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaSalesRepEditVm>(ctx.Error.Value);
        }

        var normalized = NormalizeMasterCode(code);
        if (normalized.Length == 0)
        {
            return FailVm<SaSalesRepEditVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaSalesReps
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.SrepCode == normalized,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaSalesRepEditVm>(IvMasterErrorCode.NotFound, "Sales rep not found.");
        }

        return IvMasterOperationResult<SaSalesRepEditVm>.Ok(MapSalesRep(entity));
    }

    public async Task<IvMasterOperationResult<SaSalesRepEditVm>> SaveSalesRepAsync(
        SaSalesRepEditVm model,
        bool isNew,
        string? expectedFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaSalesRepEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesSalesRep, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaSalesRepEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = NormalizeMasterCode(model.Code);
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 20)
        {
            errors["Code"] = "Code must be at most 20 characters.";
        }

        var name = (model.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors["Name"] = "Name is required.";
        }
        else if (name.Length > 200)
        {
            errors["Name"] = "Name must be at most 200 characters.";
        }

        ValidateOptionalLength(errors, "Address1", model.Address1, 100);
        ValidateOptionalLength(errors, "Address2", model.Address2, 100);
        ValidateOptionalLength(errors, "Address3", model.Address3, 100);
        ValidateOptionalLength(errors, "City", model.City, 50);
        ValidateOptionalLength(errors, "State", model.State, 50);
        ValidateOptionalLength(errors, "PostalCode", model.PostalCode, 20);
        ValidateOptionalLength(errors, "Country", model.Country, 50);
        ValidateOptionalLength(errors, "Tel", model.Tel, 50);
        ValidateOptionalLength(errors, "Mobile", model.Mobile, 50);
        ValidateOptionalLength(errors, "Email", model.Email, 100);

        if (model.CommissionRate is not null)
        {
            if (model.CommissionRate.Value < 0m || model.CommissionRate.Value > 100m)
            {
                errors["CommissionRate"] = "Commission rate must be between 0 and 100.";
            }
            else if (HasExcessScale(model.CommissionRate.Value, 6))
            {
                errors["CommissionRate"] = "Commission rate must have at most 6 decimal places.";
            }
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaSalesRepEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaSalesRepEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var address1 = TruncateOptional(model.Address1, 100);
        var address2 = TruncateOptional(model.Address2, 100);
        var address3 = TruncateOptional(model.Address3, 100);
        var city = TruncateOptional(model.City, 50);
        var state = TruncateOptional(model.State, 50);
        var postal = TruncateOptional(model.PostalCode, 20);
        var country = TruncateOptional(model.Country, 50);
        var tel = TruncateOptional(model.Tel, 50);
        var mobile = TruncateOptional(model.Mobile, 50);
        var email = TruncateOptional(model.Email, 100);

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var exists = await db.SaSalesReps.AnyAsync(
                    x => x.CompanyCode == ctx.CompanyCode && x.SrepCode == code,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaSalesRepEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Sales rep code already exists.",
                        "Code");
                }

                var entity = new SaSalesRep
                {
                    CompanyCode = ctx.CompanyCode!,
                    SrepCode = code,
                    SrepName = name,
                    Address1 = address1,
                    Address2 = address2,
                    Address3 = address3,
                    City = city,
                    State = state,
                    PostalCode = postal,
                    Country = country,
                    Tel = tel,
                    Mobile = mobile,
                    Email = email,
                    CommissionRate = model.CommissionRate,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaSalesReps.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<SaSalesRepEditVm>.Ok(MapSalesRep(entity));
            }

            if (string.IsNullOrEmpty(expectedFingerprint))
            {
                return FailVm<SaSalesRepEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "Concurrency token is missing. Reload and try again.");
            }

            var tracked = await db.SaSalesReps.FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.SrepCode == code,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm<SaSalesRepEditVm>(IvMasterErrorCode.NotFound, "Sales rep not found.");
            }

            if (SaMasterFingerprint.SalesRep(MapSalesRep(tracked)) != expectedFingerprint)
            {
                return FailVm<SaSalesRepEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This record was modified by another user.");
            }

            tracked.SrepName = name;
            tracked.Address1 = address1;
            tracked.Address2 = address2;
            tracked.Address3 = address3;
            tracked.City = city;
            tracked.State = state;
            tracked.PostalCode = postal;
            tracked.Country = country;
            tracked.Tel = tel;
            tracked.Mobile = mobile;
            tracked.Email = email;
            tracked.CommissionRate = model.CommissionRate;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<SaSalesRepEditVm>.Ok(MapSalesRep(tracked));
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaSalesRepEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Sales rep code already exists.",
                "Code");
        }
    }

    public async Task<IvMasterOperationResult<object>> SetSalesRepActiveAsync(
        IReadOnlyList<string> codes,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesSalesRep, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var list = NormalizeMasterCodes(codes);
        if (list.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var now = _dates.Now;
        var user = Truncate(ctx.UserId!, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await db.SaSalesReps
                .Where(x => x.CompanyCode == ctx.CompanyCode && list.Contains(x.SrepCode))
                .ToListAsync(cancellationToken);
            if (entities.Count != list.Count)
            {
                return FailObj(IvMasterErrorCode.NotFound, "One or more sales reps were not found.");
            }

            foreach (var entity in entities)
            {
                entity.IsActive = isActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<DeleteCheckResult> CanDeleteSalesRepsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesSalesRep, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeMasterCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await CountSalesRepReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteSalesRepsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyByCodesAsync<SaSalesRep>(
            MenuCodes.SalesSalesRep,
            NormalizeMasterCodes(codes),
            async (db, company, list, ct) =>
                await db.SaSalesReps
                    .Where(x => x.CompanyCode == company && list.Contains(x.SrepCode))
                    .ToListAsync(ct),
            (e, code) => string.Equals(e.SrepCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, list, ct) =>
                await CountSalesRepReferencesBulkAsync(db, company, list, ct),
            (db, entities) => db.SaSalesReps.RemoveRange(entities),
            cancellationToken);

    // ===================== Tax Group =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaTaxGroupListRow>>> ListTaxGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesTaxGroup, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaTaxGroupListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaTaxGroups
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.TaxGrCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaTaxGroupListRow>>.Ok(
            rows.Select(x => new SaTaxGroupListRow
            {
                Code = x.TaxGrCode,
                Desc = x.TaxGrDesc,
                Percentage = x.Percentage,
                CompanyCode = x.CompanyCode,
                BranchCode = x.BranchCode,
                LocationCode = x.LocationCode
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaTaxGroupEditVm>> GetTaxGroupAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesTaxGroup, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaTaxGroupEditVm>(ctx.Error.Value);
        }

        var normalized = NormalizeMasterCode(code);
        if (normalized.Length == 0)
        {
            return FailVm<SaTaxGroupEditVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaTaxGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.TaxGrCode == normalized,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaTaxGroupEditVm>(IvMasterErrorCode.NotFound, "Tax group not found.");
        }

        return IvMasterOperationResult<SaTaxGroupEditVm>.Ok(MapTaxGroup(entity));
    }

    public async Task<IvMasterOperationResult<SaTaxGroupEditVm>> SaveTaxGroupAsync(
        SaTaxGroupEditVm model,
        bool isNew,
        string? expectedFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaTaxGroupEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesTaxGroup, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaTaxGroupEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = NormalizeMasterCode(model.Code);
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > 20)
        {
            errors["Code"] = "Code must be at most 20 characters.";
        }

        var desc = (model.Desc ?? string.Empty).Trim();
        if (desc.Length == 0)
        {
            errors["Desc"] = "Description is required.";
        }
        else if (desc.Length > 100)
        {
            errors["Desc"] = "Description must be at most 100 characters.";
        }

        if (model.Percentage < 0m || model.Percentage > 100m)
        {
            errors["Percentage"] = "Percentage must be between 0 and 100.";
        }
        else if (HasExcessScale(model.Percentage, 6))
        {
            errors["Percentage"] = "Percentage must have at most 6 decimal places.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaTaxGroupEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaTaxGroupEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var exists = await db.SaTaxGroups.AnyAsync(
                    x => x.CompanyCode == ctx.CompanyCode && x.TaxGrCode == code,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaTaxGroupEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Tax group code already exists.",
                        "Code");
                }

                var entity = new SaTaxGroup
                {
                    CompanyCode = ctx.CompanyCode!,
                    TaxGrCode = code,
                    TaxGrDesc = desc,
                    Percentage = model.Percentage,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaTaxGroups.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<SaTaxGroupEditVm>.Ok(MapTaxGroup(entity));
            }

            if (string.IsNullOrEmpty(expectedFingerprint))
            {
                return FailVm<SaTaxGroupEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "Concurrency token is missing. Reload and try again.");
            }

            var tracked = await db.SaTaxGroups.FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.TaxGrCode == code,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm<SaTaxGroupEditVm>(IvMasterErrorCode.NotFound, "Tax group not found.");
            }

            if (SaMasterFingerprint.TaxGroup(MapTaxGroup(tracked)) != expectedFingerprint)
            {
                return FailVm<SaTaxGroupEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This record was modified by another user.");
            }

            tracked.TaxGrDesc = desc;
            tracked.Percentage = model.Percentage;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<SaTaxGroupEditVm>.Ok(MapTaxGroup(tracked));
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaTaxGroupEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Tax group code already exists.",
                "Code");
        }
    }

    public async Task<DeleteCheckResult> CanDeleteTaxGroupsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesTaxGroup, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeMasterCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await CountTaxGroupReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteTaxGroupsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyByCodesAsync<SaTaxGroup>(
            MenuCodes.SalesTaxGroup,
            NormalizeMasterCodes(codes),
            async (db, company, list, ct) =>
                await db.SaTaxGroups
                    .Where(x => x.CompanyCode == company && list.Contains(x.TaxGrCode))
                    .ToListAsync(ct),
            (e, code) => string.Equals(e.TaxGrCode, code, StringComparison.OrdinalIgnoreCase),
            async (db, company, list, ct) =>
                await CountTaxGroupReferencesBulkAsync(db, company, list, ct),
            (db, entities) => db.SaTaxGroups.RemoveRange(entities),
            cancellationToken);

    // ===================== Discount Group =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaDisGroupListRow>>> ListDisGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroup, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaDisGroupListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaDisGroups
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.GroupName)
            .ThenBy(x => x.PayCode)
            .Select(x => new SaDisGroupListRow
            {
                GroupName = x.GroupName,
                PayCode = x.PayCode,
                GroupLevel = x.GroupLevel,
                Discount = x.Discount,
                GroupStatus = x.GroupStatus,
                MemberCount = x.Members.Count
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaDisGroupListRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<SaDisGroupEditVm>> GetDisGroupAsync(
        SaDisGroupKey key,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroup, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaDisGroupEditVm>(ctx.Error.Value);
        }

        var normalized = NormalizeDisGroupKey(key);
        if (normalized is null)
        {
            return FailVm<SaDisGroupEditVm>(IvMasterErrorCode.Validation, "Group name and pay code are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaDisGroups
            .AsNoTracking()
            .Include(x => x.Members)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode
                    && x.GroupName == normalized.Value.GroupName
                    && x.PayCode == normalized.Value.PayCode,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaDisGroupEditVm>(IvMasterErrorCode.NotFound, "Discount group not found.");
        }

        return IvMasterOperationResult<SaDisGroupEditVm>.Ok(MapDisGroup(entity));
    }

    public async Task<IvMasterOperationResult<SaDisGroupEditVm>> SaveDisGroupAsync(
        SaDisGroupEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaDisGroupEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroup, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaDisGroupEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupName = (model.GroupName ?? string.Empty).Trim();
        var payCode = (model.PayCode ?? string.Empty).Trim();
        if (groupName.Length == 0)
        {
            errors["GroupName"] = "Group name is required.";
        }
        else if (groupName.Length > 40)
        {
            errors["GroupName"] = "Group name must be at most 40 characters.";
        }

        if (payCode.Length == 0)
        {
            errors["PayCode"] = "Pay code is required.";
        }
        else if (payCode.Length > 40)
        {
            errors["PayCode"] = "Pay code must be at most 40 characters.";
        }

        var members = model.Members ?? [];
        var normalizedMembers = new List<(string CustCode, string CustName)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var custCode = (member?.CustCode ?? string.Empty).Trim();
            if (custCode.Length == 0)
            {
                errors[$"Members[{i}].CustCode"] = "Customer code is required.";
                continue;
            }

            if (custCode.Length > 30)
            {
                errors[$"Members[{i}].CustCode"] = "Customer code must be at most 30 characters.";
            }

            if (!seen.Add(custCode))
            {
                errors[$"Members[{i}].CustCode"] = "Duplicate customer code.";
            }
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaDisGroupEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaDisGroupEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var memberCodes = seen.ToList();
            if (memberCodes.Count > 0)
            {
                var customers = await db.SaCusts
                    .AsNoTracking()
                    .Where(c => c.CompanyCode == ctx.CompanyCode && memberCodes.Contains(c.CustCode))
                    .Select(c => new { c.CustCode, c.CustName })
                    .ToListAsync(cancellationToken);

                if (customers.Count != memberCodes.Count)
                {
                    var missing = memberCodes
                        .Except(customers.Select(c => c.CustCode), StringComparer.OrdinalIgnoreCase)
                        .First();
                    return FailVm<SaDisGroupEditVm>(
                        IvMasterErrorCode.Validation,
                        $"Customer '{missing}' was not found.",
                        "Members");
                }

                normalizedMembers = customers
                    .Select(c => (c.CustCode, c.CustName))
                    .ToList();
            }

            SaDisGroup entity;
            if (isNew)
            {
                var exists = await db.SaDisGroups.AnyAsync(
                    x => x.CompanyCode == ctx.CompanyCode
                        && x.GroupName == groupName
                        && x.PayCode == payCode,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaDisGroupEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Discount group already exists.",
                        "GroupName");
                }

                entity = new SaDisGroup
                {
                    CompanyCode = ctx.CompanyCode!,
                    GroupName = groupName,
                    PayCode = payCode,
                    GroupLevel = model.GroupLevel,
                    Discount = model.Discount,
                    Discount2 = model.Discount2,
                    Discount3 = model.Discount3,
                    DiscountType = TruncateOptional(model.DiscountType, 20),
                    GroupStatus = TruncateOptional(model.GroupStatus, 20),
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaDisGroups.Add(entity);

                foreach (var member in normalizedMembers)
                {
                    db.SaDisCusts.Add(new SaDisCust
                    {
                        CompanyCode = ctx.CompanyCode!,
                        GroupName = groupName,
                        PayCode = payCode,
                        CustCode = member.CustCode,
                        CustName = Truncate(member.CustName, 200)
                    });
                }
            }
            else
            {
                var tracked = await db.SaDisGroups
                    .Include(x => x.Members)
                    .FirstOrDefaultAsync(
                        x => x.CompanyCode == ctx.CompanyCode
                            && x.GroupName == groupName
                            && x.PayCode == payCode,
                        cancellationToken);
                if (tracked is null)
                {
                    return FailVm<SaDisGroupEditVm>(IvMasterErrorCode.NotFound, "Discount group not found.");
                }

                tracked.GroupLevel = model.GroupLevel;
                tracked.Discount = model.Discount;
                tracked.Discount2 = model.Discount2;
                tracked.Discount3 = model.Discount3;
                tracked.DiscountType = TruncateOptional(model.DiscountType, 20);
                tracked.GroupStatus = TruncateOptional(model.GroupStatus, 20);
                tracked.ModifiedDate = now;
                tracked.ModifiedBy = user;
                entity = tracked;

                var incoming = normalizedMembers.ToDictionary(
                    m => m.CustCode, m => m, StringComparer.OrdinalIgnoreCase);
                var existingMembers = tracked.Members.ToList();
                var toRemove = existingMembers
                    .Where(m => !incoming.ContainsKey(m.CustCode))
                    .ToList();
                if (toRemove.Count > 0)
                {
                    db.SaDisCusts.RemoveRange(toRemove);
                }

                foreach (var member in normalizedMembers)
                {
                    var existing = existingMembers.FirstOrDefault(m =>
                        string.Equals(m.CustCode, member.CustCode, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        db.SaDisCusts.Add(new SaDisCust
                        {
                            CompanyCode = ctx.CompanyCode!,
                            GroupName = groupName,
                            PayCode = payCode,
                            CustCode = member.CustCode,
                            CustName = Truncate(member.CustName, 200)
                        });
                    }
                    else
                    {
                        existing.CustName = Truncate(member.CustName, 200);
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var reloaded = await db.SaDisGroups
                .AsNoTracking()
                .Include(x => x.Members)
                .FirstAsync(
                    x => x.CompanyCode == ctx.CompanyCode
                        && x.GroupName == groupName
                        && x.PayCode == payCode,
                    cancellationToken);
            return IvMasterOperationResult<SaDisGroupEditVm>.Ok(MapDisGroup(reloaded));
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<SaDisGroupEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Discount group already exists.",
                "GroupName");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<DeleteCheckResult> CanDeleteDisGroupsAsync(
        IReadOnlyList<SaDisGroupKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroup, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeDisGroupKeys(keys);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await CountDisGroupReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        var displayKeys = list.Select(k => FormatDisGroupKey(k.GroupName, k.PayCode)).ToList();
        return BuildDeleteCheck(displayKeys, refs);
    }

    public async Task<IvMasterOperationResult<object>> DeleteDisGroupsAsync(
        IReadOnlyList<SaDisGroupKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroup, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var list = NormalizeDisGroupKeys(keys);
        if (list.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var displayKeys = list.Select(k => FormatDisGroupKey(k.GroupName, k.PayCode)).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var groupNames = list.Select(k => k.GroupName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var payCodes = list.Select(k => k.PayCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var candidates = await db.SaDisGroups
                .Include(x => x.Members)
                .Where(x => x.CompanyCode == ctx.CompanyCode
                    && groupNames.Contains(x.GroupName)
                    && payCodes.Contains(x.PayCode))
                .ToListAsync(cancellationToken);

            var entities = list
                .Select(k => candidates.FirstOrDefault(e =>
                    string.Equals(e.GroupName, k.GroupName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.PayCode, k.PayCode, StringComparison.OrdinalIgnoreCase)))
                .Where(e => e is not null)
                .Cast<SaDisGroup>()
                .ToList();

            if (entities.Count != list.Count)
            {
                return FailObj(IvMasterErrorCode.NotFound, "One or more discount groups were not found.");
            }

            var refs = await CountDisGroupReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
            var check = BuildDeleteCheck(displayKeys, refs);
            if (!check.CanDelete)
            {
                return IvMasterOperationResult<object>.Fail(
                    IvMasterErrorCode.InUse,
                    check.Message ?? "One or more discount groups are in use.",
                    deleteCheck: check);
            }

            db.SaDisGroups.RemoveRange(entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateException ex) when (IsForeignKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.InUse, "One or more discount groups are in use.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ===================== Currency Rate =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaCurrRateListRow>>> ListCurrRatesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrRate, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaCurrRateListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaCurrRates
            .AsNoTracking()
            .OrderBy(x => x.CurrCode)
            .ThenBy(x => x.StartDate)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaCurrRateListRow>>.Ok(
            rows.Select(x => new SaCurrRateListRow
            {
                CurrCode = x.CurrCode,
                StartDate = x.StartDate.Date,
                EndDate = x.EndDate.Date,
                HomeCurPerUnit = x.HomeCurPerUnit,
                Status = x.Status
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaCurrRateEditVm>> GetCurrRateAsync(
        SaCurrRateKey key,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrRate, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCurrRateEditVm>(ctx.Error.Value);
        }

        var normalized = NormalizeCurrRateKey(key);
        if (normalized is null)
        {
            return FailVm<SaCurrRateEditVm>(IvMasterErrorCode.Validation, "Currency code and dates are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaCurrRates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CurrCode == normalized.Value.CurrCode
                    && x.StartDate == normalized.Value.StartDate
                    && x.EndDate == normalized.Value.EndDate,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaCurrRateEditVm>(IvMasterErrorCode.NotFound, "Currency rate not found.");
        }

        return IvMasterOperationResult<SaCurrRateEditVm>.Ok(MapCurrRate(entity));
    }

    public async Task<IvMasterOperationResult<SaCurrRateEditVm>> SaveCurrRateAsync(
        SaCurrRateEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaCurrRateEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrRate, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaCurrRateEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currCode = (model.CurrCode ?? string.Empty).Trim();
        if (currCode.Length == 0)
        {
            errors["CurrCode"] = "Currency code is required.";
        }
        else if (currCode.Length > 20)
        {
            errors["CurrCode"] = "Currency code must be at most 20 characters.";
        }

        var startDate = model.StartDate.Date;
        var endDate = model.EndDate.Date;
        if (startDate > endDate)
        {
            errors["EndDate"] = "End date must be on or after start date.";
        }

        if (model.HomeCurPerUnit <= 0)
        {
            errors["HomeCurPerUnit"] = "Rate must be greater than zero.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaCurrRateEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaCurrRateEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (IsSqlServer(db))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT TOP 1 1 FROM SaCurrency WITH (UPDLOCK, HOLDLOCK) WHERE CurrCode = {0}",
                    [currCode],
                    cancellationToken);
            }

            if (isNew)
            {
                var exists = await db.SaCurrRates.AnyAsync(
                    x => x.CurrCode == currCode && x.StartDate == startDate && x.EndDate == endDate,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<SaCurrRateEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "Currency rate already exists for this date range.",
                        "CurrCode");
                }
            }
            else
            {
                var tracked = await db.SaCurrRates.FirstOrDefaultAsync(
                    x => x.CurrCode == currCode && x.StartDate == startDate && x.EndDate == endDate,
                    cancellationToken);
                if (tracked is null)
                {
                    return FailVm<SaCurrRateEditVm>(IvMasterErrorCode.NotFound, "Currency rate not found.");
                }
            }

            var overlapping = await db.SaCurrRates
                .Where(x => x.CurrCode == currCode
                    && x.StartDate <= endDate
                    && x.EndDate >= startDate)
                .Where(x => isNew
                    || !(x.CurrCode == currCode && x.StartDate == startDate && x.EndDate == endDate))
                .AnyAsync(cancellationToken);
            if (overlapping)
            {
                return FailVm<SaCurrRateEditVm>(
                    IvMasterErrorCode.Validation,
                    "Date range overlaps an existing rate for this currency.",
                    "StartDate");
            }

            if (isNew)
            {
                var entity = new SaCurrRate
                {
                    CurrCode = currCode,
                    StartDate = startDate,
                    EndDate = endDate,
                    HomeCurPerUnit = model.HomeCurPerUnit,
                    Status = model.Status,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                db.SaCurrRates.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return IvMasterOperationResult<SaCurrRateEditVm>.Ok(MapCurrRate(entity));
            }

            var existing = await db.SaCurrRates.FirstAsync(
                x => x.CurrCode == currCode && x.StartDate == startDate && x.EndDate == endDate,
                cancellationToken);
            existing.HomeCurPerUnit = model.HomeCurPerUnit;
            existing.Status = model.Status;
            existing.ModifiedDate = now;
            existing.ModifiedBy = user;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<SaCurrRateEditVm>.Ok(MapCurrRate(existing));
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<SaCurrRateEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Currency rate already exists for this date range.",
                "CurrCode");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<DeleteCheckResult> CanDeleteCurrRatesAsync(
        IReadOnlyList<SaCurrRateKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrRate, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeCurrRateKeys(keys);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        return DeleteCheckResult.Ok();
    }

    public async Task<IvMasterOperationResult<object>> DeleteCurrRatesAsync(
        IReadOnlyList<SaCurrRateKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCurrRate, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var list = NormalizeCurrRateKeys(keys);
        if (list.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = new List<SaCurrRate>();
            foreach (var key in list)
            {
                var entity = await db.SaCurrRates.FirstOrDefaultAsync(
                    x => x.CurrCode == key.CurrCode
                        && x.StartDate == key.StartDate
                        && x.EndDate == key.EndDate,
                    cancellationToken);
                if (entity is null)
                {
                    return FailObj(IvMasterErrorCode.NotFound, "One or more currency rates were not found.");
                }

                entities.Add(entity);
            }

            db.SaCurrRates.RemoveRange(entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ===================== Shared delete helpers =====================

    private async Task<IvMasterOperationResult<object>> DeleteCompanyWithRowVersionAsync<TEntity>(
        string menuCode,
        IReadOnlyList<SaCompanyMasterKeyToken> items,
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

        var tokens = NormalizeCompanyTokens(items);
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

    private async Task<IvMasterOperationResult<object>> DeleteCompanyByCodesAsync<TEntity>(
        string menuCode,
        IReadOnlyList<string> codes,
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

        var list = NormalizeTokenCodes(codes);
        if (list.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await load(db, ctx.CompanyCode!, list, cancellationToken);
            if (entities.Count != list.Count)
            {
                return FailObj(IvMasterErrorCode.NotFound, "One or more records were not found.");
            }

            var refs = await probe(db, ctx.CompanyCode!, list, cancellationToken);
            var check = BuildDeleteCheck(list, refs);
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

    private async Task<IvMasterOperationResult<object>> DeleteGlobalByCodesAsync<TEntity>(
        string menuCode,
        IReadOnlyList<string> codes,
        Func<AppDbContext, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TEntity>>> load,
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

        var list = NormalizeTokenCodes(codes);
        if (list.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await load(db, list, cancellationToken);
            if (entities.Count != list.Count)
            {
                return FailObj(IvMasterErrorCode.NotFound, "One or more records were not found.");
            }

            var refs = await probe(db, ctx.CompanyCode!, list, cancellationToken);
            var check = BuildDeleteCheck(list, refs);
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

    // ===================== Reference counts =====================

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountCustTypeReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);
        var rows = await db.SaCusts
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode && c.CustType != null && codes.Contains(c.CustType))
            .GroupBy(c => c.CustType!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            AddRef(map, row.Code, "Customer", row.Count);
        }

        return FreezeMap(map);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountCustGroupReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);
        var rows = await db.SaCusts
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode && c.CustGroupCode != null && codes.Contains(c.CustGroupCode))
            .GroupBy(c => c.CustGroupCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            AddRef(map, row.Code, "Customer", row.Count);
        }

        return FreezeMap(map);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountAreaReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);
        var rows = await db.SaCusts
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode && c.AreaCode != null && codes.Contains(c.AreaCode))
            .GroupBy(c => c.AreaCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            AddRef(map, row.Code, "Customer", row.Count);
        }

        return FreezeMap(map);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountCountryReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);
        foreach (var code in codes)
        {
            var count = await db.SaCusts
                .AsNoTracking()
                .CountAsync(
                    c => c.CompanyCode == companyCode &&
                        (c.Country == code || c.ShipCountry == code || c.InvCountry == code),
                    cancellationToken);
            if (count > 0)
            {
                AddRef(map, code, "Customer", count);
            }
        }

        return FreezeMap(map);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountCurrencyReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);

        var custRows = await db.SaCusts
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode && c.Currency != null && codes.Contains(c.Currency))
            .GroupBy(c => c.Currency!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in custRows)
        {
            AddRef(map, row.Code, "Customer", row.Count);
        }

        var rateRows = await db.SaCurrRates
            .AsNoTracking()
            .Where(r => codes.Contains(r.CurrCode))
            .GroupBy(r => r.CurrCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in rateRows)
        {
            AddRef(map, row.Code, "CurrencyRate", row.Count);
        }

        return FreezeMap(map);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountPaymentTermReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);

        var custRows = await db.SaCusts
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode && c.PayCode != null && codes.Contains(c.PayCode))
            .GroupBy(c => c.PayCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in custRows)
        {
            AddRef(map, row.Code, "Customer", row.Count);
        }

        var invoiceRows = await db.SaInvoices
            .AsNoTracking()
            .Where(i => i.CompanyCode == companyCode && i.PayCode != null && codes.Contains(i.PayCode))
            .GroupBy(i => i.PayCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in invoiceRows)
        {
            AddRef(map, row.Code, "Invoice", row.Count);
        }

        var disGroupRows = await db.SaDisGroups
            .AsNoTracking()
            .Where(d => d.CompanyCode == companyCode && codes.Contains(d.PayCode))
            .GroupBy(d => d.PayCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in disGroupRows)
        {
            AddRef(map, row.Code, "DiscountGroup", row.Count);
        }

        return FreezeMap(map);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountSalesRepReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);

        var custRows = await db.SaCusts
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode && c.SalesmanCode != null && codes.Contains(c.SalesmanCode))
            .GroupBy(c => c.SalesmanCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in custRows)
        {
            AddRef(map, row.Code, "Customer", row.Count);
        }

        var invoiceRows = await db.SaInvoices
            .AsNoTracking()
            .Where(i => i.CompanyCode == companyCode && i.SalesmanCode != null && codes.Contains(i.SalesmanCode))
            .GroupBy(i => i.SalesmanCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in invoiceRows)
        {
            AddRef(map, row.Code, "Invoice", row.Count);
        }

        return FreezeMap(map);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountTaxGroupReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);

        var custRows = await db.SaCusts
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode && c.TaxGrCode != null && codes.Contains(c.TaxGrCode))
            .GroupBy(c => c.TaxGrCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in custRows)
        {
            AddRef(map, row.Code, "Customer", row.Count);
        }

        var invoiceRows = await db.SaInvoices
            .AsNoTracking()
            .Where(i => i.CompanyCode == companyCode && i.TaxGrCode != null && codes.Contains(i.TaxGrCode))
            .GroupBy(i => i.TaxGrCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in invoiceRows)
        {
            AddRef(map, row.Code, "Invoice", row.Count);
        }

        var detailRows = await db.SaInvoiceDetails
            .AsNoTracking()
            .Where(d => d.CompanyCode == companyCode && d.TaxGrCode != null && codes.Contains(d.TaxGrCode))
            .GroupBy(d => d.TaxGrCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in detailRows)
        {
            AddRef(map, row.Code, "InvoiceDetail", row.Count);
        }

        return FreezeMap(map);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountDisGroupReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<SaDisGroupKey> keys,
        CancellationToken cancellationToken)
    {
        var displayKeys = keys.Select(k => FormatDisGroupKey(k.GroupName, k.PayCode)).ToList();
        var map = InitRefMap(displayKeys);

        foreach (var key in keys)
        {
            var displayKey = FormatDisGroupKey(key.GroupName, key.PayCode);
            var custCount = await db.SaCusts
                .AsNoTracking()
                .CountAsync(
                    c => c.CompanyCode == companyCode && c.GroupDiscount == key.GroupName,
                    cancellationToken);
            if (custCount > 0)
            {
                AddRef(map, displayKey, "Customer", custCount);
            }

            var memberCount = await db.SaDisCusts
                .AsNoTracking()
                .CountAsync(
                    m => m.CompanyCode == companyCode
                        && m.GroupName == key.GroupName
                        && m.PayCode == key.PayCode,
                    cancellationToken);
            if (memberCount > 0)
            {
                AddRef(map, displayKey, "DiscountGroupMember", memberCount);
            }
        }

        return FreezeMap(map);
    }

    private static Dictionary<string, List<IvReferenceCount>> InitRefMap(IEnumerable<string> codes) =>
        codes.ToDictionary(c => c, _ => new List<IvReferenceCount>(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>> FreezeMap(
        Dictionary<string, List<IvReferenceCount>> map) =>
        map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IvReferenceCount>)kv.Value, StringComparer.OrdinalIgnoreCase);

    private static void AddRef(Dictionary<string, List<IvReferenceCount>> map, string code, string referenceType, int count)
    {
        if (count <= 0 || !map.TryGetValue(code, out var list))
        {
            return;
        }

        list.Add(new IvReferenceCount { ReferenceType = referenceType, Count = count });
    }

    // ===================== Shared helpers =====================

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

    private UserContext ValidateCompanyContext()
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.UserId);
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

    private static List<SaCompanyMasterKeyToken> NormalizeCompanyTokens(IReadOnlyList<SaCompanyMasterKeyToken>? items)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        return items
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Code) && x.RowVersion is { Length: > 0 })
            .Select(x => new SaCompanyMasterKeyToken
            {
                Code = x.Code.Trim(),
                RowVersion = x.RowVersion
            })
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static List<string> NormalizeTokenCodes(IReadOnlyList<string>? codes) =>
        (codes ?? [])
            .Select(c => (c ?? string.Empty).Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeMasterCode(string? code) =>
        (code ?? string.Empty).Trim().ToUpperInvariant();

    private static List<string> NormalizeMasterCodes(IReadOnlyList<string>? codes) =>
        (codes ?? [])
            .Select(NormalizeMasterCode)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool HasExcessScale(decimal value, int scale) =>
        value != decimal.Round(value, scale, MidpointRounding.AwayFromZero);

    private static void ValidateOptionalLength(
        Dictionary<string, string> errors,
        string field,
        string? value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Trim().Length > maxLength)
        {
            errors[field] = $"{field} must be at most {maxLength} characters.";
        }
    }

    private static List<SaDisGroupKey> NormalizeDisGroupKeys(IReadOnlyList<SaDisGroupKey>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return [];
        }

        return keys
            .Select(NormalizeDisGroupKey)
            .Where(k => k is not null)
            .Select(k => new SaDisGroupKey { GroupName = k!.Value.GroupName, PayCode = k.Value.PayCode })
            .GroupBy(k => FormatDisGroupKey(k.GroupName, k.PayCode), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static (string GroupName, string PayCode)? NormalizeDisGroupKey(SaDisGroupKey? key)
    {
        if (key is null)
        {
            return null;
        }

        var groupName = (key.GroupName ?? string.Empty).Trim();
        var payCode = (key.PayCode ?? string.Empty).Trim();
        if (groupName.Length == 0 || payCode.Length == 0)
        {
            return null;
        }

        return (groupName, payCode);
    }

    private static List<SaCurrRateKey> NormalizeCurrRateKeys(IReadOnlyList<SaCurrRateKey>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return [];
        }

        return keys
            .Select(NormalizeCurrRateKey)
            .Where(k => k is not null)
            .Select(k => new SaCurrRateKey
            {
                CurrCode = k!.Value.CurrCode,
                StartDate = k.Value.StartDate,
                EndDate = k.Value.EndDate
            })
            .GroupBy(
                k => $"{k.CurrCode}|{k.StartDate:yyyy-MM-dd}|{k.EndDate:yyyy-MM-dd}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static (string CurrCode, DateTime StartDate, DateTime EndDate)? NormalizeCurrRateKey(SaCurrRateKey? key)
    {
        if (key is null)
        {
            return null;
        }

        var currCode = (key.CurrCode ?? string.Empty).Trim();
        if (currCode.Length == 0)
        {
            return null;
        }

        return (currCode, key.StartDate.Date, key.EndDate.Date);
    }

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

    private static string FormatDisGroupKey(string groupName, string payCode) =>
        $"{groupName.Trim()}|{payCode.Trim()}";

    private static string StaleBulkMessage() =>
        "1 selected item was modified by another user. No records were changed. Please reload and try again.";

    private static string MessageFor(IvMasterErrorCode code) => code switch
    {
        IvMasterErrorCode.AccessDenied => "Not authorized.",
        IvMasterErrorCode.InvalidScope => "Invalid company context.",
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

    private static SaCustTypeEditVm MapCustType(SaCustType x) => new()
    {
        Code = x.CustTypeCode,
        Desc = x.CustTypeDesc,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion
    };

    private static SaCustGroupEditVm MapCustGroup(SaCustGroup x) => new()
    {
        Code = x.CustGroupCode,
        Desc = x.CustGroupDesc,
        RowVersion = x.RowVersion
    };

    private static SaAreaEditVm MapArea(IvAreaCode x) => new()
    {
        Code = x.AreaCode,
        Desc = x.AreaDesc,
        Latitude = x.Latitude,
        Longitude = x.Longitude
    };

    private static SaCountryEditVm MapCountry(SaCountry x) => new()
    {
        Code = x.CountryCode,
        Name = x.CountryName,
        Latitude = x.Latitude,
        Longitude = x.Longitude
    };

    private static SaCurrencyEditVm MapCurrency(SaCurrency x) => new()
    {
        Code = x.CurrCode,
        Desc = x.CurrDesc,
        IsActive = x.IsActive != false
    };

    private static SaPaymentTermEditVm MapPaymentTerm(SaPaymentTerm x) => new()
    {
        Code = x.PayCode,
        Desc = x.PayDesc,
        Days = x.Days,
        IsActive = x.IsActive != false
    };

    private static SaSalesRepEditVm MapSalesRep(SaSalesRep x) => new()
    {
        Code = x.SrepCode,
        Name = x.SrepName,
        Address1 = x.Address1,
        Address2 = x.Address2,
        Address3 = x.Address3,
        City = x.City,
        State = x.State,
        PostalCode = x.PostalCode,
        Country = x.Country,
        Tel = x.Tel,
        Mobile = x.Mobile,
        Email = x.Email,
        CommissionRate = x.CommissionRate,
        IsActive = x.IsActive != false
    };

    private static SaTaxGroupEditVm MapTaxGroup(SaTaxGroup x) => new()
    {
        Code = x.TaxGrCode,
        Desc = x.TaxGrDesc,
        Percentage = x.Percentage,
        CompanyCode = x.CompanyCode,
        BranchCode = x.BranchCode,
        LocationCode = x.LocationCode
    };

    private static SaDisGroupEditVm MapDisGroup(SaDisGroup x) => new()
    {
        GroupName = x.GroupName,
        PayCode = x.PayCode,
        GroupLevel = x.GroupLevel,
        Discount = x.Discount,
        Discount2 = x.Discount2,
        Discount3 = x.Discount3,
        DiscountType = x.DiscountType,
        GroupStatus = x.GroupStatus,
        Members = x.Members
            .OrderBy(m => m.CustCode)
            .Select(m => new SaDisGroupMemberVm
            {
                CustCode = m.CustCode,
                CustName = m.CustName
            })
            .ToList()
    };

    private static SaCurrRateEditVm MapCurrRate(SaCurrRate x) => new()
    {
        CurrCode = x.CurrCode,
        StartDate = x.StartDate.Date,
        EndDate = x.EndDate.Date,
        HomeCurPerUnit = x.HomeCurPerUnit,
        Status = x.Status
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

    private static bool IsSqlServer(AppDbContext db) =>
        (db.Database.ProviderName ?? string.Empty).Contains("SqlServer", StringComparison.OrdinalIgnoreCase);

    private readonly record struct UserContext(
        string? CompanyCode,
        string? UserId,
        IvMasterErrorCode? Error)
    {
        public static UserContext Ok(string companyCode, string userId) =>
            new(companyCode, userId, null);

        public static UserContext Fail(IvMasterErrorCode code, string _) =>
            new(null, null, code);
    }
}
