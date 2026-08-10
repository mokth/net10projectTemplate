using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Security;

public sealed class BranchService : IBranchService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<BranchService> _logger;

    public BranchService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<BranchService> logger)
    {
        _dbFactory = dbFactory;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<BranchOperationResult> GetBranchesAsync(CancellationToken cancellationToken = default)
    {
        var context = await ValidateContextAsync(cancellationToken);
        if (context.Error is not null)
        {
            return BranchOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminBranch, PermissionCodes.Access, cancellationToken))
        {
            return BranchOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var branches = await db.Branches
            .AsNoTracking()
            .Where(b => b.CompanyId == context.CompanyId)
            .OrderBy(b => b.BranchCode)
            .ToListAsync(cancellationToken);

        return BranchOperationResult.Ok(branches);
    }

    public async Task<BranchOperationResult> AddBranchAsync(
        Branch branch,
        CancellationToken cancellationToken = default)
    {
        var context = await ValidateContextAsync(cancellationToken);
        if (context.Error is not null)
        {
            return BranchOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminBranch, PermissionCodes.Add, cancellationToken))
        {
            return BranchOperationResult.Fail("Not authorized.");
        }

        var code = (branch.BranchCode ?? string.Empty).Trim().ToUpperInvariant();
        var name = (branch.BranchName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return BranchOperationResult.Fail("Branch code is required.");
        }

        if (code.Length > 5)
        {
            return BranchOperationResult.Fail("Branch code must be at most 5 characters.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return BranchOperationResult.Fail("Branch name is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Branches.AnyAsync(
            b => b.CompanyId == context.CompanyId && b.BranchCode == code,
            cancellationToken);
        if (exists)
        {
            return BranchOperationResult.Fail("Branch code already exists.");
        }

        var entity = new Branch
        {
            CompanyId = context.CompanyId!.Value,
            BranchCode = code,
            BranchName = name,
            IsActive = branch.IsActive,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = Truncate(_currentUser.UserId, 50)
        };
        db.Branches.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Branch created. UserId={UserId} CompanyId={CompanyId} BranchCode={BranchCode}",
            _currentUser.UserId,
            entity.CompanyId,
            entity.BranchCode);
        return BranchOperationResult.Ok(entity);
    }

    public async Task<BranchOperationResult> UpdateBranchAsync(
        Branch branch,
        CancellationToken cancellationToken = default)
    {
        var context = await ValidateContextAsync(cancellationToken);
        if (context.Error is not null)
        {
            return BranchOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminBranch, PermissionCodes.Edit, cancellationToken))
        {
            return BranchOperationResult.Fail("Not authorized.");
        }

        var name = (branch.BranchName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BranchOperationResult.Fail("Branch name is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Branches.FirstOrDefaultAsync(
            b => b.Id == branch.Id && b.CompanyId == context.CompanyId,
            cancellationToken);
        if (entity is null)
        {
            return BranchOperationResult.Fail("Branch was not found.");
        }

        entity.BranchName = name;
        entity.IsActive = branch.IsActive;
        entity.ModifiedAtUtc = DateTime.UtcNow;
        entity.ModifiedBy = Truncate(_currentUser.UserId, 50);
        await db.SaveChangesAsync(cancellationToken);
        return BranchOperationResult.Ok(entity);
    }

    public async Task<BranchOperationResult> DeleteBranchesAsync(
        IReadOnlyCollection<long> branchIds,
        CancellationToken cancellationToken = default)
    {
        var context = await ValidateContextAsync(cancellationToken);
        if (context.Error is not null)
        {
            return BranchOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminBranch, PermissionCodes.Delete, cancellationToken))
        {
            return BranchOperationResult.Fail("Not authorized.");
        }

        if (branchIds.Count == 0)
        {
            return BranchOperationResult.Fail("No branches selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entities = await db.Branches
            .Where(b => branchIds.Contains(b.Id) && b.CompanyId == context.CompanyId)
            .ToListAsync(cancellationToken);
        if (entities.Count == 0)
        {
            return BranchOperationResult.Fail("Branch was not found.");
        }

        if (entities.Any(b => string.Equals(b.BranchCode, "HQ", StringComparison.OrdinalIgnoreCase)))
        {
            return BranchOperationResult.Fail("The HQ branch cannot be deleted.");
        }

        var stamp = Truncate(_currentUser.UserId, 50);
        var now = DateTime.UtcNow;
        foreach (var entity in entities)
        {
            entity.IsDeleted = true;
            entity.DeletedAtUtc = now;
            entity.DeletedBy = stamp;
            entity.IsActive = false;
            entity.ModifiedAtUtc = now;
            entity.ModifiedBy = stamp;
        }

        await db.SaveChangesAsync(cancellationToken);
        return BranchOperationResult.Ok();
    }

    private async Task<(int? CompanyId, string? Error)> ValidateContextAsync(CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated)
        {
            return (null, "User is not authenticated.");
        }

        var companyCode = (_currentUser.CompanyCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return (null, "Company context is missing.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var companyId = await db.Companies
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode && c.IsActive)
            .Select(c => (int?)c.CompanyId)
            .FirstOrDefaultAsync(cancellationToken);
        if (companyId is null)
        {
            return (null, "Company was not found.");
        }

        return (companyId, null);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
