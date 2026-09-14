using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Admin;

/// <summary>
/// Department + Project reference masters.
///
/// Tenant scope is Company + Branch, enforced with the branch-scope pattern copied from
/// AdSmNumAdminService / IvInventoryRefService. Do not substitute the company-only
/// SaSalesRefService scope here — it would not enforce branch.
///
/// Note on "active": MsDept has an IsActive column; MsProject has no such column, so its
/// active state is derived from Status (ACTIVE vs CLOSED).
/// </summary>
public sealed class MsRefService : IMsRefService
{
    private const int MaxCodeLength = 20;
    private const int MaxNameLength = 100;
    private const int MaxProjNameLength = 150;
    private const int MaxRemarksLength = 500;
    private const int MaxUserIdLength = 20;
    private const int MaxGlCodeLength = 20;
    private const int MaxEmpIdLength = 20;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;

    public MsRefService(
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

    // ===================== Department =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<MsDeptListRow>>> ListDepartmentsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminDept, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<MsDeptListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.MsDepts.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.BranchCode == ctx.BranchCode)
            .OrderBy(x => x.DeptCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<MsDeptListRow>>.Ok(
            rows.Select(MapDeptList).ToList());
    }

    public async Task<IvMasterOperationResult<MsDeptEditVm>> GetDepartmentAsync(
        string code, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminDept, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<MsDeptEditVm>(ctx.Error.Value);
        }

        var deptCode = NormalizeCode(code);
        if (deptCode is null)
        {
            return FailVm<MsDeptEditVm>(IvMasterErrorCode.Validation, "DeptCode is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MsDepts.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode
                    && x.BranchCode == ctx.BranchCode
                    && x.DeptCode == deptCode,
                cancellationToken);

        return entity is null
            ? FailVm<MsDeptEditVm>(IvMasterErrorCode.NotFound, "Department not found.")
            : IvMasterOperationResult<MsDeptEditVm>.Ok(MapDeptEdit(entity));
    }

    public async Task<IvMasterOperationResult<MsDeptEditVm>> SaveDepartmentAsync(
        MsDeptEditVm model, bool isNew, CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<MsDeptEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminDept, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<MsDeptEditVm>(ctx.Error.Value);
        }

        var errors = ValidateDeptModel(model);
        if (!isNew && model.RowVersion is not { Length: > 0 })
        {
            return FailVm<MsDeptEditVm>(
                IvMasterErrorCode.Concurrency, "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<MsDeptEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return FailVm<MsDeptEditVm>(
                IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var deptCode = NormalizeCode(model.Code)!;
        var now = _dates.Now;
        var user = Truncate(write.UserId, MaxUserIdLength);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            if (isNew)
            {
                var exists = await db.MsDepts.AsNoTracking()
                    .AnyAsync(
                        x => x.CompanyCode == write.CompanyCode
                            && x.BranchCode == write.BranchCode
                            && x.DeptCode == deptCode,
                        cancellationToken);
                if (exists)
                {
                    return FailVm<MsDeptEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        $"Department '{deptCode}' already exists.",
                        "Code");
                }

                var entity = new MsDept
                {
                    CompanyCode = write.CompanyCode,
                    BranchCode = write.BranchCode!,
                    DeptCode = deptCode,
                    CreatedDate = now,
                    CreatedBy = user
                };
                ApplyDept(entity, model, now, user);
                EnsureSqliteRowVersion(db, entity);
                db.MsDepts.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<MsDeptEditVm>.Ok(MapDeptEdit(entity));
            }

            var tracked = await db.MsDepts
                .SingleOrDefaultAsync(
                    x => x.CompanyCode == write.CompanyCode
                        && x.BranchCode == write.BranchCode
                        && x.DeptCode == deptCode,
                    cancellationToken);
            if (tracked is null)
            {
                return FailVm<MsDeptEditVm>(IvMasterErrorCode.NotFound, "Department not found.");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<MsDeptEditVm>(
                    IvMasterErrorCode.Concurrency, "This record was modified by another user.");
            }

            db.Entry(tracked).Property(nameof(MsDept.RowVersion)).OriginalValue = model.RowVersion!;
            ApplyDept(tracked, model, now, user);
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<MsDeptEditVm>.Ok(MapDeptEdit(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<MsDeptEditVm>(
                IvMasterErrorCode.Concurrency, "This record was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<MsDeptEditVm>(
                IvMasterErrorCode.DuplicateKey,
                $"Department '{deptCode}' already exists.",
                "Code");
        }
    }

    public async Task<IvMasterOperationResult<object>> SetDepartmentActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminDept, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = _dates.Now;
        var user = Truncate(ctx.UserId, MaxUserIdLength);
        var codes = tokens.Select(x => x.Code).ToList();

        var entities = await db.MsDepts
            .Where(x => x.CompanyCode == ctx.CompanyCode
                && x.BranchCode == ctx.BranchCode
                && codes.Contains(x.DeptCode))
            .ToListAsync(cancellationToken);
        if (entities.Count == 0)
        {
            return FailObj(IvMasterErrorCode.NotFound, "Department not found.");
        }

        foreach (var entity in entities)
        {
            var token = tokens.First(x => CodeEquals(x.Code, entity.DeptCode));
            if (!RowVersionsEqual(entity.RowVersion, token.RowVersion))
            {
                return FailObj(
                    IvMasterErrorCode.Concurrency,
                    "This record was modified by another user.");
            }

            entity.IsActive = isActive;
            entity.ModifiedDate = now;
            entity.ModifiedBy = user;
            db.Entry(entity).Property(nameof(MsDept.RowVersion)).OriginalValue = token.RowVersion;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailObj(
                IvMasterErrorCode.Concurrency, "This record was modified by another user.");
        }

        return IvMasterOperationResult<object>.Ok();
    }

    public async Task<DeleteCheckResult> CanDeleteDepartmentsAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminDept, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await CountDeptReferencesAsync(
            db, ctx.CompanyCode!, ctx.BranchCode, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public async Task<IvMasterOperationResult<object>> DeleteDepartmentsAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminDept, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(x => x.Code).ToList();
        var check = await CanDeleteDepartmentsAsync(codes, cancellationToken);
        if (!check.CanDelete)
        {
            return IvMasterOperationResult<object>.Fail(
                IvMasterErrorCode.InUse, check.Message ?? "Record is in use.", deleteCheck: check);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await db.MsDepts
                .Where(x => x.CompanyCode == ctx.CompanyCode
                    && x.BranchCode == ctx.BranchCode
                    && codes.Contains(x.DeptCode))
                .ToListAsync(cancellationToken);
            if (entities.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailObj(IvMasterErrorCode.NotFound, "Department not found.");
            }

            foreach (var entity in entities)
            {
                var token = tokens.First(x => CodeEquals(x.Code, entity.DeptCode));
                if (!RowVersionsEqual(entity.RowVersion, token.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(
                        IvMasterErrorCode.Concurrency,
                        "This record was modified by another user.");
                }

                db.Entry(entity).Property(nameof(MsDept.RowVersion)).OriginalValue = token.RowVersion;
                db.MsDepts.Remove(entity);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailObj(
                    IvMasterErrorCode.Concurrency, "This record was modified by another user.");
            }

            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ===================== Project =====================

    public async Task<IvMasterOperationResult<IReadOnlyList<MsProjectListRow>>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminProject, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<MsProjectListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.MsProjects.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.BranchCode == ctx.BranchCode)
            .OrderBy(x => x.ProjCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<MsProjectListRow>>.Ok(
            rows.Select(MapProjectList).ToList());
    }

    public async Task<IvMasterOperationResult<MsProjectEditVm>> GetProjectAsync(
        string code, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminProject, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<MsProjectEditVm>(ctx.Error.Value);
        }

        var projCode = NormalizeCode(code);
        if (projCode is null)
        {
            return FailVm<MsProjectEditVm>(IvMasterErrorCode.Validation, "ProjCode is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MsProjects.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode
                    && x.BranchCode == ctx.BranchCode
                    && x.ProjCode == projCode,
                cancellationToken);

        return entity is null
            ? FailVm<MsProjectEditVm>(IvMasterErrorCode.NotFound, "Project not found.")
            : IvMasterOperationResult<MsProjectEditVm>.Ok(MapProjectEdit(entity));
    }

    public async Task<IvMasterOperationResult<MsProjectEditVm>> SaveProjectAsync(
        MsProjectEditVm model, bool isNew, CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<MsProjectEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminProject, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<MsProjectEditVm>(ctx.Error.Value);
        }

        var errors = ValidateProjectModel(model);
        if (!isNew && model.RowVersion is not { Length: > 0 })
        {
            return FailVm<MsProjectEditVm>(
                IvMasterErrorCode.Concurrency, "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<MsProjectEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return FailVm<MsProjectEditVm>(
                IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var projCode = NormalizeCode(model.Code)!;
        var now = _dates.Now;
        var user = Truncate(write.UserId, MaxUserIdLength);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // The default department must resolve when supplied (new or changed). Blank is allowed.
        var deptCode = Null(model.DeptCode);
        if (deptCode is not null)
        {
            var deptOk = await MsRefLookupRules.ExistsActiveAsync(
                db, write.CompanyCode, write.BranchCode, MsRefLookupKind.Department, deptCode, cancellationToken);
            if (!deptOk)
            {
                return FailVm<MsProjectEditVm>(
                    IvMasterErrorCode.Validation,
                    $"Department '{deptCode}' does not exist or is not active.",
                    "DeptCode");
            }
        }

        try
        {
            if (isNew)
            {
                var exists = await db.MsProjects.AsNoTracking()
                    .AnyAsync(
                        x => x.CompanyCode == write.CompanyCode
                            && x.BranchCode == write.BranchCode
                            && x.ProjCode == projCode,
                        cancellationToken);
                if (exists)
                {
                    return FailVm<MsProjectEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        $"Project '{projCode}' already exists.",
                        "Code");
                }

                var entity = new MsProject
                {
                    CompanyCode = write.CompanyCode,
                    BranchCode = write.BranchCode!,
                    ProjCode = projCode,
                    CreatedDate = now,
                    CreatedBy = user
                };
                ApplyProject(entity, model, now, user);
                EnsureSqliteRowVersion(db, entity);
                db.MsProjects.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<MsProjectEditVm>.Ok(MapProjectEdit(entity));
            }

            var tracked = await db.MsProjects
                .SingleOrDefaultAsync(
                    x => x.CompanyCode == write.CompanyCode
                        && x.BranchCode == write.BranchCode
                        && x.ProjCode == projCode,
                    cancellationToken);
            if (tracked is null)
            {
                return FailVm<MsProjectEditVm>(IvMasterErrorCode.NotFound, "Project not found.");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<MsProjectEditVm>(
                    IvMasterErrorCode.Concurrency, "This record was modified by another user.");
            }

            db.Entry(tracked).Property(nameof(MsProject.RowVersion)).OriginalValue = model.RowVersion!;
            ApplyProject(tracked, model, now, user);
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<MsProjectEditVm>.Ok(MapProjectEdit(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<MsProjectEditVm>(
                IvMasterErrorCode.Concurrency, "This record was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<MsProjectEditVm>(
                IvMasterErrorCode.DuplicateKey,
                $"Project '{projCode}' already exists.",
                "Code");
        }
    }

    public async Task<IvMasterOperationResult<object>> SetProjectActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminProject, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = _dates.Now;
        var user = Truncate(ctx.UserId, MaxUserIdLength);
        var codes = tokens.Select(x => x.Code).ToList();

        var entities = await db.MsProjects
            .Where(x => x.CompanyCode == ctx.CompanyCode
                && x.BranchCode == ctx.BranchCode
                && codes.Contains(x.ProjCode))
            .ToListAsync(cancellationToken);
        if (entities.Count == 0)
        {
            return FailObj(IvMasterErrorCode.NotFound, "Project not found.");
        }

        foreach (var entity in entities)
        {
            var token = tokens.First(x => CodeEquals(x.Code, entity.ProjCode));
            if (!RowVersionsEqual(entity.RowVersion, token.RowVersion))
            {
                return FailObj(
                    IvMasterErrorCode.Concurrency,
                    "This record was modified by another user.");
            }

            // MsProject has no IsActive column — active state is derived from Status.
            entity.Status = isActive ? MsProjectStatus.Active : MsProjectStatus.Closed;
            entity.ModifiedDate = now;
            entity.ModifiedBy = user;
            db.Entry(entity).Property(nameof(MsProject.RowVersion)).OriginalValue = token.RowVersion;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailObj(
                IvMasterErrorCode.Concurrency, "This record was modified by another user.");
        }

        return IvMasterOperationResult<object>.Ok();
    }

    public async Task<DeleteCheckResult> CanDeleteProjectsAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminProject, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await CountProjectReferencesAsync(
            db, ctx.CompanyCode!, ctx.BranchCode, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public async Task<IvMasterOperationResult<object>> DeleteProjectsAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminProject, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(x => x.Code).ToList();
        var check = await CanDeleteProjectsAsync(codes, cancellationToken);
        if (!check.CanDelete)
        {
            return IvMasterOperationResult<object>.Fail(
                IvMasterErrorCode.InUse, check.Message ?? "Record is in use.", deleteCheck: check);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await db.MsProjects
                .Where(x => x.CompanyCode == ctx.CompanyCode
                    && x.BranchCode == ctx.BranchCode
                    && codes.Contains(x.ProjCode))
                .ToListAsync(cancellationToken);
            if (entities.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailObj(IvMasterErrorCode.NotFound, "Project not found.");
            }

            foreach (var entity in entities)
            {
                var token = tokens.First(x => CodeEquals(x.Code, entity.ProjCode));
                if (!RowVersionsEqual(entity.RowVersion, token.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(
                        IvMasterErrorCode.Concurrency,
                        "This record was modified by another user.");
                }

                db.Entry(entity).Property(nameof(MsProject.RowVersion)).OriginalValue = token.RowVersion;
                db.MsProjects.Remove(entity);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailObj(
                    IvMasterErrorCode.Concurrency, "This record was modified by another user.");
            }

            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ===================== Master-UI-only lookups =====================

    /// <summary>
    /// Active Department + Project lists for the master UI (Project's default-department
    /// selector). Deliberately NOT used by transaction services — those query the DbSets
    /// directly inside their own GetLookupsAsync.
    /// </summary>
    public async Task<IvMasterOperationResult<MsRefLookupBundle>> ListActiveLookupsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminProject, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<MsRefLookupBundle>.Fail(
                ctx.Error.Value, MessageFor(ctx.Error.Value));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var departments = await db.MsDepts.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.BranchCode == ctx.BranchCode && x.IsActive)
            .OrderBy(x => x.DeptCode)
            .Select(x => new IvCodeLookupRow { Code = x.DeptCode, Desc = x.DeptName })
            .ToListAsync(cancellationToken);

        var projects = await db.MsProjects.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode
                && x.BranchCode == ctx.BranchCode
                && x.Status == MsProjectStatus.Active)
            .OrderBy(x => x.ProjCode)
            .Select(x => new IvCodeLookupRow { Code = x.ProjCode, Desc = x.ProjName })
            .ToListAsync(cancellationToken);

        // Customers follow SaCust's own company scope (SaCust is not branch-scoped).
        var customers = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.IsActive)
            .OrderBy(x => x.CustCode)
            .Select(x => new IvCodeLookupRow { Code = x.CustCode, Desc = x.CustName })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<MsRefLookupBundle>.Ok(new MsRefLookupBundle
        {
            Departments = departments,
            Projects = projects,
            Customers = customers
        });
    }

    // ===================== Reference counting =====================

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountDeptReferencesAsync(
        AppDbContext db,
        string company,
        string? branch,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<IvReferenceCount>>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            map[code] = [];
        }

        // Header-level references (company + branch scoped).
        var saCdn = await GroupCountAsync(
            db.SaCdns.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Dept != null && codes.Contains(x.Dept!)),
            x => x.Dept!, cancellationToken);
        var poPr = await GroupCountAsync(
            db.PoPrs.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DeptCode != null && codes.Contains(x.DeptCode!)),
            x => x.DeptCode!, cancellationToken);
        var poOrder = await GroupCountAsync(
            db.PoOrders.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DeptCode != null && codes.Contains(x.DeptCode!)),
            x => x.DeptCode!, cancellationToken);
        var poInvoice = await GroupCountAsync(
            db.PoInvoices.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Dept != null && codes.Contains(x.Dept!)),
            x => x.Dept!, cancellationToken);
        var poCdn = await GroupCountAsync(
            db.PoCdns.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Dept != null && codes.Contains(x.Dept!)),
            x => x.Dept!, cancellationToken);

        // Line-level / master default references.
        var saDoDetail = await GroupCountAsync(
            db.SaDoDetails.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Dept != null && codes.Contains(x.Dept!)),
            x => x.Dept!, cancellationToken);
        var poPurItem = await GroupCountAsync(
            db.PoPurItems.Where(x => x.CompanyCode == company && x.Dept != null && codes.Contains(x.Dept!)),
            x => x.Dept!, cancellationToken);

        // PoDesc has no BranchCode column — company scope only.
        var poDesc = await GroupCountAsync(
            db.PoDescs.Where(x => x.CompanyCode == company && x.DeptCode != null && codes.Contains(x.DeptCode!)),
            x => x.DeptCode!, cancellationToken);

        // MsProject.DeptCode is the project's default department (cross-master dependency).
        var projectDefault = await GroupCountAsync(
            db.MsProjects.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DeptCode != null && codes.Contains(x.DeptCode!)),
            x => x.DeptCode!, cancellationToken);

        foreach (var (code, count) in saCdn) AddRef(map, code, "Sales CN (header)", count);
        foreach (var (code, count) in saDoDetail) AddRef(map, code, "Sales DO (line)", count);
        foreach (var (code, count) in poPr) AddRef(map, code, "Purchase PR", count);
        foreach (var (code, count) in poOrder) AddRef(map, code, "Purchase Order", count);
        foreach (var (code, count) in poInvoice) AddRef(map, code, "Purchase Invoice", count);
        foreach (var (code, count) in poCdn) AddRef(map, code, "Purchase CN/DN", count);
        foreach (var (code, count) in poPurItem) AddRef(map, code, "Purchase Item (default)", count);
        foreach (var (code, count) in poDesc) AddRef(map, code, "Item Description (default)", count);
        foreach (var (code, count) in projectDefault) AddRef(map, code, "Project (default dept)", count);

        return map.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<IvReferenceCount>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountProjectReferencesAsync(
        AppDbContext db,
        string company,
        string? branch,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<IvReferenceCount>>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            map[code] = [];
        }

        var saSo = await GroupCountAsync(
            db.SaSos.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ProjId != null && codes.Contains(x.ProjId!)),
            x => x.ProjId!, cancellationToken);
        var saDo = await GroupCountAsync(
            db.SaDos.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ProjId != null && codes.Contains(x.ProjId!)),
            x => x.ProjId!, cancellationToken);
        var saCdn = await GroupCountAsync(
            db.SaCdns.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ProjId != null && codes.Contains(x.ProjId!)),
            x => x.ProjId!, cancellationToken);
        var poPr = await GroupCountAsync(
            db.PoPrs.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ProjId != null && codes.Contains(x.ProjId!)),
            x => x.ProjId!, cancellationToken);
        var poOrder = await GroupCountAsync(
            db.PoOrders.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ProjId != null && codes.Contains(x.ProjId!)),
            x => x.ProjId!, cancellationToken);
        var poInvoice = await GroupCountAsync(
            db.PoInvoices.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ProjId != null && codes.Contains(x.ProjId!)),
            x => x.ProjId!, cancellationToken);
        var poCdn = await GroupCountAsync(
            db.PoCdns.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ProjId != null && codes.Contains(x.ProjId!)),
            x => x.ProjId!, cancellationToken);
        var poOrderDetail = await GroupCountAsync(
            db.PoOrderDetails.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ProjId != null && codes.Contains(x.ProjId!)),
            x => x.ProjId!, cancellationToken);

        foreach (var (code, count) in saSo) AddRef(map, code, "Sales Order", count);
        foreach (var (code, count) in saDo) AddRef(map, code, "Sales DO", count);
        foreach (var (code, count) in saCdn) AddRef(map, code, "Sales CN (header)", count);
        foreach (var (code, count) in poPr) AddRef(map, code, "Purchase PR", count);
        foreach (var (code, count) in poOrder) AddRef(map, code, "Purchase Order", count);
        foreach (var (code, count) in poOrderDetail) AddRef(map, code, "Purchase Order (line)", count);
        foreach (var (code, count) in poInvoice) AddRef(map, code, "Purchase Invoice", count);
        foreach (var (code, count) in poCdn) AddRef(map, code, "Purchase CN/DN", count);

        return map.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<IvReferenceCount>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<(string Code, int Count)>> GroupCountAsync<T>(
        IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, string>> selector, CancellationToken ct)
    {
        var rows = await query
            .GroupBy(selector)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.Select(x => (x.Code, x.Count)).ToList();
    }

    private static void AddRef(
        Dictionary<string, List<IvReferenceCount>> map, string code, string referenceType, int count)
    {
        if (count <= 0 || !map.TryGetValue(code, out var list))
        {
            return;
        }

        list.Add(new IvReferenceCount { ReferenceType = referenceType, Count = count });
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

    // ===================== Validation =====================

    private static Dictionary<string, string> ValidateDeptModel(MsDeptEditVm model)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (NormalizeCode(model.Code) is null)
        {
            errors["Code"] = $"DeptCode is required (max {MaxCodeLength}).";
        }

        if (model.Name is { Length: > MaxNameLength })
        {
            errors["Name"] = $"Department name must be at most {MaxNameLength} characters.";
        }

        if (model.ManagerEmpId is { Length: > MaxEmpIdLength })
        {
            errors["ManagerEmpId"] = $"Manager must be at most {MaxEmpIdLength} characters.";
        }

        if (model.GlCode is { Length: > MaxGlCodeLength })
        {
            errors["GlCode"] = $"GL code must be at most {MaxGlCodeLength} characters.";
        }

        if (model.Remarks is { Length: > MaxRemarksLength })
        {
            errors["Remarks"] = $"Remarks must be at most {MaxRemarksLength} characters.";
        }

        return errors;
    }

    private static Dictionary<string, string> ValidateProjectModel(MsProjectEditVm model)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (NormalizeCode(model.Code) is null)
        {
            errors["Code"] = $"ProjCode is required (max {MaxCodeLength}).";
        }

        if (model.Name is { Length: > MaxProjNameLength })
        {
            errors["Name"] = $"Project name must be at most {MaxProjNameLength} characters.";
        }

        if (model.ManagerEmpId is { Length: > MaxEmpIdLength })
        {
            errors["ManagerEmpId"] = $"Manager must be at most {MaxEmpIdLength} characters.";
        }

        if (model.Remarks is { Length: > MaxRemarksLength })
        {
            errors["Remarks"] = $"Remarks must be at most {MaxRemarksLength} characters.";
        }

        if (!MsProjectStatus.IsValid(model.Status))
        {
            errors["Status"] = "Status must be ACTIVE or CLOSED.";
        }

        if (model.BudgetAmnt is < 0)
        {
            errors["BudgetAmnt"] = "Budget cannot be negative.";
        }

        if (model.StartDate is not null && model.EndDate is not null && model.EndDate < model.StartDate)
        {
            errors["EndDate"] = "End date cannot be before the start date.";
        }

        return errors;
    }

    // ===================== Mapping =====================

    private static MsDeptListRow MapDeptList(MsDept x) => new()
    {
        Code = x.DeptCode,
        Name = x.DeptName,
        ManagerEmpId = x.ManagerEmpId,
        GlCode = x.GlCode,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static MsDeptEditVm MapDeptEdit(MsDept x) => new()
    {
        Code = x.DeptCode,
        Name = x.DeptName,
        ManagerEmpId = x.ManagerEmpId,
        GlCode = x.GlCode,
        IsActive = x.IsActive,
        Remarks = x.Remarks,
        RowVersion = x.RowVersion
    };

    private static MsProjectListRow MapProjectList(MsProject x) => new()
    {
        Code = x.ProjCode,
        Name = x.ProjName,
        CustCode = x.CustCode,
        DeptCode = x.DeptCode,
        StartDate = x.StartDate,
        EndDate = x.EndDate,
        Status = x.Status,
        BudgetAmnt = x.BudgetAmnt,
        RowVersion = x.RowVersion ?? []
    };

    private static MsProjectEditVm MapProjectEdit(MsProject x) => new()
    {
        Code = x.ProjCode,
        Name = x.ProjName,
        CustCode = x.CustCode,
        DeptCode = x.DeptCode,
        ManagerEmpId = x.ManagerEmpId,
        StartDate = x.StartDate,
        EndDate = x.EndDate,
        CloseDate = x.CloseDate,
        Status = x.Status,
        BudgetAmnt = x.BudgetAmnt,
        Remarks = x.Remarks,
        RowVersion = x.RowVersion
    };

    private static void ApplyDept(MsDept entity, MsDeptEditVm model, DateTime now, string? user)
    {
        entity.DeptName = Truncate(Null(model.Name), MaxNameLength);
        entity.ManagerEmpId = Truncate(Null(model.ManagerEmpId), MaxEmpIdLength);
        entity.GlCode = Truncate(Null(model.GlCode), MaxGlCodeLength);
        entity.IsActive = model.IsActive;
        entity.Remarks = Truncate(Null(model.Remarks), MaxRemarksLength);
        entity.ModifiedDate = now;
        entity.ModifiedBy = user;
    }

    private static void ApplyProject(MsProject entity, MsProjectEditVm model, DateTime now, string? user)
    {
        entity.ProjName = Truncate(Null(model.Name), MaxProjNameLength);
        entity.CustCode = Null(model.CustCode);
        entity.DeptCode = Null(model.DeptCode);
        entity.ManagerEmpId = Truncate(Null(model.ManagerEmpId), MaxEmpIdLength);
        entity.StartDate = model.StartDate;
        entity.EndDate = model.EndDate;
        entity.CloseDate = model.CloseDate;
        entity.Status = MsProjectStatus.Normalize(model.Status);
        entity.BudgetAmnt = model.BudgetAmnt;
        entity.Remarks = Truncate(Null(model.Remarks), MaxRemarksLength);
        entity.ModifiedDate = now;
        entity.ModifiedBy = user;
    }

    // ===================== Scope / errors =====================

    private async Task<UserContext> RequireBranchScopeAsync(
        string menuCode, string permission, CancellationToken cancellationToken)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope);
        }

        if (!await _accessRights.CanAsync(menuCode, permission, cancellationToken))
        {
            return UserContext.Fail(IvMasterErrorCode.AccessDenied);
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode, scope.UserId);
    }

    private static string MessageFor(IvMasterErrorCode code) => code switch
    {
        IvMasterErrorCode.AccessDenied => "Not authorized.",
        IvMasterErrorCode.InvalidScope => "Invalid company or branch context.",
        IvMasterErrorCode.NotFound => "Record not found.",
        IvMasterErrorCode.Concurrency => "This record was modified by another user.",
        IvMasterErrorCode.DuplicateKey => "A record with the same key already exists.",
        _ => "Request failed."
    };

    private static IvMasterOperationResult<IReadOnlyList<T>> FailList<T>(IvMasterErrorCode code) =>
        IvMasterOperationResult<IReadOnlyList<T>>.Fail(code, MessageFor(code));

    private static IvMasterOperationResult<T> FailVm<T>(
        IvMasterErrorCode code, string? message = null, string? field = null)
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

    private static IvMasterOperationResult<object> FailObj(IvMasterErrorCode code, string? message = null) =>
        IvMasterOperationResult<object>.Fail(code, message ?? MessageFor(code));

    // ===================== Helpers =====================

    private static string? NormalizeCode(string? code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        return trimmed.Length == 0 || trimmed.Length > MaxCodeLength ? null : trimmed.ToUpperInvariant();
    }

    private static List<string> NormalizeCodes(IReadOnlyList<string>? codes) =>
        (codes ?? [])
            .Select(c => (c ?? string.Empty).Trim().ToUpperInvariant())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<IvMasterKeyToken> NormalizeTokens(IReadOnlyList<IvMasterKeyToken>? items) =>
        (items ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Code))
            .Select(x => new IvMasterKeyToken
            {
                Code = x.Code.Trim().ToUpperInvariant(),
                RowVersion = x.RowVersion ?? []
            })
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private static bool CodeEquals(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Null(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= max ? value : value[..max];
    }

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        left is not null && right is not null && left.Length > 0 && left.SequenceEqual(right);

    /// <summary>SQL Server generates rowversion; SQLite (tests) needs a non-empty client token.</summary>
    private static void EnsureSqliteRowVersion(DbContext db, object entity)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var prop = db.Entry(entity).Property("RowVersion");
        if (prop.CurrentValue is not byte[] { Length: > 0 })
        {
            prop.CurrentValue = Guid.NewGuid().ToByteArray();
        }
    }

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) == true;

    private readonly record struct UserContext(
        string? CompanyCode,
        string? BranchCode,
        string? UserId,
        IvMasterErrorCode? Error)
    {
        public static UserContext Ok(string companyCode, string branchCode, string userId) =>
            new(companyCode, branchCode, userId, null);

        public static UserContext Fail(IvMasterErrorCode code) =>
            new(null, null, null, code);
    }
}
