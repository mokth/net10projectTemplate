using System.Security.Claims;
using ErpWeb.Core.Authentication;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Security;

/// <summary>
/// Validates a signed-in <see cref="ClaimsPrincipal"/> against the live database. Shared by the
/// cookie revalidation events and the Blazor circuit revalidation provider so both enforce the same
/// security state. Deliberately independent of <see cref="ICurrentUserService"/> so it cannot be
/// influenced by a stale in-circuit principal.
/// </summary>
public interface IAuthSecurityStateValidator
{
    Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed class AuthSecurityStateValidator : IAuthSecurityStateValidator
{
    private const string DeletedMarker = "deleted";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AuthSecurityStateValidator> _logger;

    public AuthSecurityStateValidator(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<AuthSecurityStateValidator> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var subject = Find(principal, AppClaimTypes.Subject);
        var companyCode = Find(principal, AppClaimTypes.CompanyCode);
        if (!int.TryParse(subject, out var uid) || uid <= 0 || string.IsNullOrWhiteSpace(companyCode))
        {
            return false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Company state: a missing Company row is a legacy install and is allowed; an existing but
        // inactive Company blocks access (matches login semantics).
        var company = await db.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyCode == companyCode, cancellationToken);
        if (company is not null && !company.IsActive)
        {
            _logger.LogWarning("Revalidation rejected: inactive company {CompanyCode}", companyCode);
            return false;
        }

        var user = await db.UserLogins
            .AsNoTracking()
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return false;
        }

        if (user.active != true)
        {
            return false;
        }

        if (!Matches(Find(principal, AppClaimTypes.BranchCode), user.BranchCode) ||
            !Matches(Find(principal, AppClaimTypes.LocationCode), user.LocationCode) ||
            !Matches(Find(principal, AppClaimTypes.Level), user.userlevel) ||
            !MatchesBool(Find(principal, AppClaimTypes.ChangePassword), user.changepass) ||
            !MatchesBool(Find(principal, AppClaimTypes.CanViewPrice), user.CanViewPrice))
        {
            return false;
        }

        // The authoritative role (userlevel) must still exist, belong to the same company and be
        // active — the ADMIN/SYSTEM_ADMIN bypass is claim-based, so this is what keeps a stale
        // claim from surviving role deactivation.
        var level = (user.userlevel ?? string.Empty).Trim();
        if (level.Length == 0)
        {
            return false;
        }

        var roleActive = await db.Roles
            .AsNoTracking()
            .AnyAsync(r =>
                r.CompanyCode == companyCode &&
                r.RoleCode == level &&
                r.IsActive, cancellationToken);
        if (!roleActive)
        {
            // SQL collation may be case-insensitive on SQL Server but not elsewhere; the RoleCode
            // comparison must never be case-sensitive across providers.
            var roles = await db.Roles
                .AsNoTracking()
                .Where(r => r.CompanyCode == companyCode && r.IsActive)
                .Select(r => r.RoleCode)
                .ToListAsync(cancellationToken);
            if (!roles.Any(r => string.Equals(r, level, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Find(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value;

    private static bool Matches(string? claim, string? value) =>
        string.Equals(claim, value, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesBool(string? claim, bool value) =>
        string.Equals(claim, value ? "true" : "false", StringComparison.OrdinalIgnoreCase);
}
