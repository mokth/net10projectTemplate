using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Security;

public sealed class CompanyService : ICompanyService
{

    public const string AdminRole = "ADMIN";

    public const string SystemAdminRole = "SYSTEM_ADMIN";

    public const string UserRole = "USER";

    public const string DefaultBranchCode = "HQ";

    public const string DefaultLocationCode = "MAIN";

    public const string DefaultTemplateCompanyCode = "DEMO";

    private static readonly (string MenuCode, string PermissionCode)[] DefaultUserGrants =
    [
        ("DASHBOARD", PermissionCodes.Access),
        ("INVENTORY_DEMO", PermissionCodes.Access),
        ("ADMIN_COMPANY", PermissionCodes.Access),
        ("ADMIN_COMPANY", PermissionCodes.Edit)
    ];

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private readonly ICurrentUserService _currentUser;

    private readonly IAccessRightService _accessRights;

    private readonly IPasswordPolicy _passwordPolicy;

    private readonly ILogger<CompanyService> _logger;

    public CompanyService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        IPasswordPolicy passwordPolicy,
        ILogger<CompanyService> logger)
    {
        _dbFactory = dbFactory;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _passwordPolicy = passwordPolicy;
        _logger = logger;
    }

    public async Task<CompanyOperationResult> GetCompaniesAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return CompanyOperationResult.Fail(context.Error);
        }
        if (!await _accessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Access, cancellationToken))
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Companies.AsNoTracking();
        if (!context.IsSystemAdmin)
        {
            query = query.Where(c => c.CompanyCode == context.CompanyCode);
        }
        var companies = await query
            .OrderBy(c => c.CompanyCode)
            .ToListAsync(cancellationToken);
        return CompanyOperationResult.Ok(companies);
    }

    public async Task<CompanyOperationResult> GetCompanyAsync(
        int companyId,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return CompanyOperationResult.Fail(context.Error);
        }
        if (!await _accessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Access, cancellationToken))
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = await db.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId, cancellationToken);
        if (company is null)
        {
            return CompanyOperationResult.Fail("Company not found.");
        }
        if (!context.IsSystemAdmin &&
            !string.Equals(company.CompanyCode, context.CompanyCode, StringComparison.OrdinalIgnoreCase))
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        return CompanyOperationResult.Ok(company);
    }

    public async Task<CompanyOperationResult> GetOwnCompanyAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return CompanyOperationResult.Fail(context.Error);
        }
        if (!await _accessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Access, cancellationToken))
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = await db.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyCode == context.CompanyCode, cancellationToken);
        if (company is null)
        {
            return CompanyOperationResult.Fail("Company not found.");
        }
        return CompanyOperationResult.Ok(company);
    }

    public async Task<CompanyOperationResult> AddCompanyAsync(
        Company company,
        CompanyBootstrapRequest bootstrap,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return CompanyOperationResult.Fail(context.Error);
        }
        if (!context.IsSystemAdmin)
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        if (!await _accessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Add, cancellationToken))
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        var validationError = ValidateCompany(company, requireCode: true);
        if (validationError is not null)
        {
            return CompanyOperationResult.Fail(validationError);
        }
        var bootstrapError = ValidateBootstrap(bootstrap);
        if (bootstrapError is not null)
        {
            return CompanyOperationResult.Fail(bootstrapError);
        }
        var companyCode = company.CompanyCode.Trim().ToUpperInvariant();
        var adminLoginId = bootstrap.AdminLoginId.Trim();
        var adminName = string.IsNullOrWhiteSpace(bootstrap.AdminDisplayName)
            ? $"{company.CompanyName.Trim()} Admin"
            : bootstrap.AdminDisplayName.Trim();
        var branchCode = NormalizeOrDefault(bootstrap.BranchCode, DefaultBranchCode, 5);
        var locationCode = NormalizeOrDefault(bootstrap.LocationCode, DefaultLocationCode, 10);
        var policyResult = _passwordPolicy.Validate(bootstrap.AdminPassword, adminLoginId);
        if (!policyResult.IsValid)
        {
            return CompanyOperationResult.Fail(string.Join(" ", policyResult.Errors));
        }
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Companies.AnyAsync(c => c.CompanyCode == companyCode, cancellationToken);
        if (exists)
        {
            return CompanyOperationResult.Fail("Company code already exists.");
        }
        var templateCompanyCode = await ResolveTemplateCompanyCodeAsync(
            db,
            bootstrap.TemplateCompanyCode,
            context.CompanyCode!,
            cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entity = MapNewEntity(company, companyCode, context.UserId!);
            db.Companies.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            db.Branches.Add(new Branch
            {
                CompanyId = entity.CompanyId,
                BranchCode = branchCode,
                BranchName = string.Equals(branchCode, DefaultBranchCode, StringComparison.OrdinalIgnoreCase)
                    ? "Head Office"
                    : branchCode,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = Truncate(context.UserId!, 50)
            });
            await db.SaveChangesAsync(cancellationToken);
            var adminRole = new Role
            {
                CompanyCode = companyCode,
                RoleCode = AdminRole,
                RoleName = "Administrator",
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = Truncate(context.UserId!, 10)
            };
            var userRole = new Role
            {
                CompanyCode = companyCode,
                RoleCode = UserRole,
                RoleName = "User",
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = Truncate(context.UserId!, 10)
            };
            db.Roles.AddRange(adminRole, userRole);
            await db.SaveChangesAsync(cancellationToken);
            await CopyOrSeedRolePermissionsAsync(
                db,
                templateCompanyCode,
                adminRole,
                userRole,
                context.UserId!,
                cancellationToken);
            var adminUser = new UserLogin
            {
                id = adminLoginId,
                name = Truncate(adminName, 50),
                password = PasswordHasher.Hash(bootstrap.AdminPassword),
                email = NullIfWhiteSpace(bootstrap.AdminEmail),
                active = true,
                userlevel = AdminRole,
                Created = DateTime.Now,
                UserID = Truncate(context.UserId!, 10),
                CompanyCode = companyCode,
                BranchCode = branchCode,
                LocationCode = locationCode,
                changepass = true
            };
            db.UserLogins.Add(adminUser);
            await db.SaveChangesAsync(cancellationToken);
            db.UserRoleMappings.Add(new UserRoleMapping
            {
                UserUid = adminUser.uid,
                RoleId = adminRole.RoleId
            });
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            _logger.LogInformation(
                "Company provisioned. UserId={UserId} CompanyCode={CompanyCode} AdminLoginId={AdminLoginId} Template={Template}",
                context.UserId,
                entity.CompanyCode,
                adminLoginId,
                templateCompanyCode);
            return CompanyOperationResult.Ok(
                entity,
                new CompanyBootstrapResult
                {
                    CompanyCode = companyCode,
                    AdminLoginId = adminLoginId,
                    BranchCode = branchCode,
                    LocationCode = locationCode,
                    TemplateCompanyCode = templateCompanyCode
                });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Company provisioning failed for {CompanyCode}", companyCode);
            return CompanyOperationResult.Fail("Unable to create company. Changes were rolled back.");
        }
    }

    public async Task<CompanyOperationResult> UpdateCompanyAsync(
        Company company,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return CompanyOperationResult.Fail(context.Error);
        }
        if (!await _accessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Edit, cancellationToken))
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        var validationError = ValidateCompany(company, requireCode: false);
        if (validationError is not null)
        {
            return CompanyOperationResult.Fail(validationError);
        }
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Companies.FirstOrDefaultAsync(c => c.CompanyId == company.CompanyId, cancellationToken);
        if (entity is null)
        {
            return CompanyOperationResult.Fail("Company not found.");
        }
        if (!context.IsSystemAdmin &&
            !string.Equals(entity.CompanyCode, context.CompanyCode, StringComparison.OrdinalIgnoreCase))
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        var wasActive = entity.IsActive;
        ApplyEditableFields(entity, company);
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = Truncate(context.UserId!, 10);
        if (wasActive && !entity.IsActive)
        {
            await DeactivateCompanyUsersAsync(db, entity.CompanyCode, context.UserId!, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Company updated. UserId={UserId} CompanyId={CompanyId} CompanyCode={CompanyCode}",
            context.UserId,
            entity.CompanyId,
            entity.CompanyCode);
        return CompanyOperationResult.Ok(entity);
    }

    public async Task<CompanyOperationResult> DeleteCompaniesAsync(
        IReadOnlyCollection<int> companyIds,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return CompanyOperationResult.Fail(context.Error);
        }
        if (!context.IsSystemAdmin)
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        if (!await _accessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Delete, cancellationToken))
        {
            return CompanyOperationResult.Fail("Not authorized.");
        }
        if (companyIds.Count == 0)
        {
            return CompanyOperationResult.Fail("No company selected.");
        }
        var ids = companyIds.Distinct().ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var companies = await db.Companies.Where(c => ids.Contains(c.CompanyId)).ToListAsync(cancellationToken);
        if (companies.Count == 0)
        {
            return CompanyOperationResult.Fail("Company not found.");
        }
        foreach (var company in companies)
        {
            company.IsActive = false;
            company.ModifiedDate = DateTime.UtcNow;
            company.ModifiedBy = Truncate(context.UserId!, 10);
            await DeactivateCompanyUsersAsync(db, company.CompanyCode, context.UserId!, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Companies deactivated. UserId={UserId} Count={Count}",
            context.UserId,
            companies.Count);
        return CompanyOperationResult.Ok();
    }

    private static async Task DeactivateCompanyUsersAsync(
        AppDbContext db,
        string companyCode,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        var users = await db.UserLogins
            .Where(u => u.CompanyCode == companyCode && u.active == true)
            .ToListAsync(cancellationToken);
        var stamp = Truncate(updatedBy, 10);
        var now = DateTime.Now;
        foreach (var user in users)
        {
            user.active = false;
            user.Updated = now;
            user.UpdatedUID = stamp;
        }
    }

    private static async Task<string> ResolveTemplateCompanyCodeAsync(
        AppDbContext db,
        string? requested,
        string creatorCompanyCode,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var code = requested.Trim().ToUpperInvariant();
            var exists = await db.Roles.AnyAsync(r => r.CompanyCode == code, cancellationToken);
            if (exists)
            {
                return code;
            }
        }
        if (await db.Roles.AnyAsync(r => r.CompanyCode == DefaultTemplateCompanyCode, cancellationToken))
        {
            return DefaultTemplateCompanyCode;
        }
        if (await db.Roles.AnyAsync(r => r.CompanyCode == creatorCompanyCode, cancellationToken))
        {
            return creatorCompanyCode;
        }
        return DefaultTemplateCompanyCode;
    }

    private static async Task CopyOrSeedRolePermissionsAsync(
        AppDbContext db,
        string templateCompanyCode,
        Role adminRole,
        Role userRole,
        string createdBy,
        CancellationToken cancellationToken)
    {
        var stamp = Truncate(createdBy, 10);
        var now = DateTime.UtcNow;
        var templateRoles = await db.Roles
            .AsNoTracking()
            .Where(r => r.CompanyCode == templateCompanyCode &&
                        (r.RoleCode == AdminRole || r.RoleCode == UserRole))
            .ToListAsync(cancellationToken);
        var templateRoleIds = templateRoles.Select(r => r.RoleId).ToList();
        var templateGrants = templateRoleIds.Count == 0
            ? []
            : await db.RoleMenuPermissions
                .AsNoTracking()
                .Where(x => templateRoleIds.Contains(x.RoleId) && x.IsAllowed)
                .ToListAsync(cancellationToken);
        var byRoleCode = templateRoles.ToDictionary(
            r => r.RoleCode,
            r => r,
            StringComparer.OrdinalIgnoreCase);
        CopyRoleGrants(db, templateGrants, byRoleCode, AdminRole, adminRole.RoleId, stamp, now);
        var userCopied = CopyRoleGrants(db, templateGrants, byRoleCode, UserRole, userRole.RoleId, stamp, now);
        if (!userCopied)
        {
            await SeedDefaultUserGrantsAsync(db, userRole.RoleId, stamp, now, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool CopyRoleGrants(
        AppDbContext db,
        IReadOnlyList<RoleMenuPermission> templateGrants,
        IReadOnlyDictionary<string, Role> templateRolesByCode,
        string roleCode,
        int targetRoleId,
        string createdBy,
        DateTime now)
    {
        if (!templateRolesByCode.TryGetValue(roleCode, out var templateRole))
        {
            return false;
        }
        var grants = templateGrants.Where(g => g.RoleId == templateRole.RoleId).ToList();
        if (grants.Count == 0)
        {
            return false;
        }
        foreach (var grant in grants)
        {
            db.RoleMenuPermissions.Add(new RoleMenuPermission
            {
                RoleId = targetRoleId,
                MenuId = grant.MenuId,
                PermissionId = grant.PermissionId,
                IsAllowed = true,
                CreatedDate = now,
                CreatedBy = createdBy
            });
        }
        return true;
    }

    private static async Task SeedDefaultUserGrantsAsync(
        AppDbContext db,
        int userRoleId,
        string createdBy,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var menuCodes = DefaultUserGrants.Select(g => g.MenuCode).Distinct().ToList();
        var permissionCodes = DefaultUserGrants.Select(g => g.PermissionCode).Distinct().ToList();
        var menus = await db.Menus
            .AsNoTracking()
            .Where(m => menuCodes.Contains(m.MenuCode) && m.IsActive)
            .ToDictionaryAsync(m => m.MenuCode, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var permissions = await db.Permissions
            .AsNoTracking()
            .Where(p => permissionCodes.Contains(p.PermissionCode) && p.IsActive)
            .ToDictionaryAsync(p => p.PermissionCode, StringComparer.OrdinalIgnoreCase, cancellationToken);
        foreach (var (menuCode, permissionCode) in DefaultUserGrants)
        {
            if (!menus.TryGetValue(menuCode, out var menu) ||
                !permissions.TryGetValue(permissionCode, out var permission))
            {
                continue;
            }
            db.RoleMenuPermissions.Add(new RoleMenuPermission
            {
                RoleId = userRoleId,
                MenuId = menu.MenuId,
                PermissionId = permission.PermissionId,
                IsAllowed = true,
                CreatedDate = now,
                CreatedBy = createdBy
            });
        }
    }

    private UserContext ValidateUserContext()
    {
        if (!_currentUser.IsAuthenticated)
        {
            return UserContext.Fail("Not authorized.");
        }
        if (string.IsNullOrWhiteSpace(_currentUser.SubjectUid) ||
            string.IsNullOrWhiteSpace(_currentUser.UserId))
        {
            return UserContext.Fail("Invalid user identity.");
        }
        var companyCode = _currentUser.CompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return UserContext.Fail("Invalid company context.");
        }
        var isSystemAdmin = _currentUser.IsInRole(SystemAdminRole);
        var isAdmin = isSystemAdmin || _currentUser.IsInRole(AdminRole);
        return UserContext.Ok(companyCode, _currentUser.UserId, isAdmin, isSystemAdmin);
    }

    private static string? ValidateBootstrap(CompanyBootstrapRequest bootstrap)
    {
        var loginId = (bootstrap.AdminLoginId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(loginId))
        {
            return "First admin user ID is required.";
        }
        if (loginId.Length > 10)
        {
            return "First admin user ID must be at most 10 characters.";
        }
        var displayName = (bootstrap.AdminDisplayName ?? string.Empty).Trim();
        if (displayName.Length > 50)
        {
            return "First admin name must be at most 50 characters.";
        }
        var email = (bootstrap.AdminEmail ?? string.Empty).Trim();
        if (email.Length > 50)
        {
            return "First admin email must be at most 50 characters.";
        }
        if (string.IsNullOrWhiteSpace(bootstrap.AdminPassword))
        {
            return "First admin password is required.";
        }
        if (!string.Equals(bootstrap.AdminPassword, bootstrap.ConfirmPassword, StringComparison.Ordinal))
        {
            return "Password confirmation does not match.";
        }
        var branch = (bootstrap.BranchCode ?? string.Empty).Trim();
        if (branch.Length > 5)
        {
            return "Branch code must be at most 5 characters.";
        }
        var location = (bootstrap.LocationCode ?? string.Empty).Trim();
        if (location.Length > 10)
        {
            return "Location code must be at most 10 characters.";
        }
        return null;
    }

    private static string? ValidateCompany(Company company, bool requireCode)
    {
        var companyCode = (company.CompanyCode ?? string.Empty).Trim();
        var companyName = (company.CompanyName ?? string.Empty).Trim();
        if (requireCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
            {
                return "Company code is required.";
            }
            if (companyCode.Length > 5)
            {
                return "Company code must be at most 5 characters.";
            }
        }
        if (string.IsNullOrWhiteSpace(companyName))
        {
            return "Company name is required.";
        }
        if (companyName.Length > 100)
        {
            return "Company name must be at most 100 characters.";
        }
        if (company.FiscalYearStartMonth is < 1 or > 12)
        {
            return "Fiscal year start month must be between 1 and 12.";
        }
        if (!string.IsNullOrWhiteSpace(company.CurrencyCode) && company.CurrencyCode.Trim().Length != 3)
        {
            return "Currency code must be 3 characters.";
        }
        return null;
    }

    private static Company MapNewEntity(Company source, string companyCode, string userId) =>
        new()
        {
            CompanyCode = companyCode,
            CompanyName = source.CompanyName.Trim(),
            LegalName = NullIfWhiteSpace(source.LegalName),
            RegistrationNo = NullIfWhiteSpace(source.RegistrationNo),
            TaxNo = NullIfWhiteSpace(source.TaxNo),
            Phone = NullIfWhiteSpace(source.Phone),
            Fax = NullIfWhiteSpace(source.Fax),
            Email = NullIfWhiteSpace(source.Email),
            Website = NullIfWhiteSpace(source.Website),
            Address1 = NullIfWhiteSpace(source.Address1),
            Address2 = NullIfWhiteSpace(source.Address2),
            Address3 = NullIfWhiteSpace(source.Address3),
            City = NullIfWhiteSpace(source.City),
            State = NullIfWhiteSpace(source.State),
            PostCode = NullIfWhiteSpace(source.PostCode),
            Country = NullIfWhiteSpace(source.Country),
            LogoUrl = NullIfWhiteSpace(source.LogoUrl),
            CurrencyCode = NormalizeCurrency(source.CurrencyCode),
            TimeZoneId = NullIfWhiteSpace(source.TimeZoneId),
            FiscalYearStartMonth = source.FiscalYearStartMonth,
            IsActive = source.IsActive,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = Truncate(userId, 10)
        };

    private static void ApplyEditableFields(Company entity, Company source)
    {
        entity.CompanyName = source.CompanyName.Trim();
        entity.LegalName = NullIfWhiteSpace(source.LegalName);
        entity.RegistrationNo = NullIfWhiteSpace(source.RegistrationNo);
        entity.TaxNo = NullIfWhiteSpace(source.TaxNo);
        entity.Phone = NullIfWhiteSpace(source.Phone);
        entity.Fax = NullIfWhiteSpace(source.Fax);
        entity.Email = NullIfWhiteSpace(source.Email);
        entity.Website = NullIfWhiteSpace(source.Website);
        entity.Address1 = NullIfWhiteSpace(source.Address1);
        entity.Address2 = NullIfWhiteSpace(source.Address2);
        entity.Address3 = NullIfWhiteSpace(source.Address3);
        entity.City = NullIfWhiteSpace(source.City);
        entity.State = NullIfWhiteSpace(source.State);
        entity.PostCode = NullIfWhiteSpace(source.PostCode);
        entity.Country = NullIfWhiteSpace(source.Country);
        entity.LogoUrl = NullIfWhiteSpace(source.LogoUrl);
        entity.CurrencyCode = NormalizeCurrency(source.CurrencyCode);
        entity.TimeZoneId = NullIfWhiteSpace(source.TimeZoneId);
        entity.FiscalYearStartMonth = source.FiscalYearStartMonth;
        entity.IsActive = source.IsActive;
    }

    private static string NormalizeOrDefault(string? value, string fallback, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }
        return Truncate(trimmed, maxLength);
    }

    private static string? NormalizeCurrency(string? value)
    {
        var trimmed = NullIfWhiteSpace(value);
        return trimmed?.ToUpperInvariant();
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private readonly record struct UserContext(
        string? CompanyCode,
        string? UserId,
        bool IsAdmin,
        bool IsSystemAdmin,
        string? Error)
    {
        public static UserContext Ok(string companyCode, string userId, bool isAdmin, bool isSystemAdmin) =>
            new(companyCode, userId, isAdmin, isSystemAdmin, null);

        public static UserContext Fail(string error) =>
            new(null, null, false, false, error);
    }
}
