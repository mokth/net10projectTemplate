namespace ErpWeb.Core.Services;

/// <summary>
/// A resolved tenancy scope: which company, branch and location a request belongs to, and who asked.
///
/// <para>
/// Deliberately module-neutral. The pre-existing <c>InventoryTenantScope</c> carries exactly this data
/// but lives in the Inventory namespace and is named for it, which made every consumer outside
/// Inventory depend on Inventory infrastructure to find out what company it was in.
/// </para>
/// </summary>
public sealed class TenantScope
{
    public required string CompanyCode { get; init; }

    public string? BranchCode { get; init; }

    public string? LocationCode { get; init; }

    public required string UserId { get; init; }
}

/// <summary>
/// The shared tenant context. Everything that needs to know "which company / branch / user is this"
/// should depend on this, not on a module-specific context type.
/// </summary>
public interface ITenantScopeContext
{
    /// <summary>Company scope only. Null when unauthenticated or the company claim is unusable.</summary>
    TenantScope? TryCompanyScope();

    /// <summary>Company + branch. Null when either is missing.</summary>
    TenantScope? TryBranchScope();

    /// <summary>Company + branch + location, for writes that stamp all three.</summary>
    TenantScope? TryWriteScope();
}

/// <summary>
/// Resolves the tenant scope from the signed-in user's claims. This is the SINGLE implementation of
/// claim normalisation — <c>InventoryTenantContext</c> delegates here rather than repeating it, so the
/// two can never disagree about what a valid company code is.
///
/// <para>
/// A claim is rejected (yielding null) when it is blank or longer than its maximum. A company code
/// longer than 5 characters is therefore treated as "no company", which is a deliberate fail-closed
/// choice: it is better that a scope is refused than that a truncated code silently matches another
/// tenant's data.
/// </para>
/// </summary>
public sealed class TenantScopeContext : ITenantScopeContext
{
    public const int MaxCompanyLength = 5;
    public const int MaxBranchLength = 5;
    public const int MaxLocationLength = 10;
    public const int MaxUserIdLength = 10;

    private readonly ICurrentUserService _currentUser;

    public TenantScopeContext(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    public TenantScope? TryCompanyScope() =>
        BuildScope(requireBranch: false, requireLocation: false);

    public TenantScope? TryBranchScope() =>
        BuildScope(requireBranch: true, requireLocation: false);

    public TenantScope? TryWriteScope() =>
        BuildScope(requireBranch: true, requireLocation: true);

    private TenantScope? BuildScope(bool requireBranch, bool requireLocation)
    {
        if (!_currentUser.IsAuthenticated ||
            string.IsNullOrWhiteSpace(_currentUser.SubjectUid))
        {
            return null;
        }

        var company = NormalizeClaim(_currentUser.CompanyCode, MaxCompanyLength);
        var userId = NormalizeClaim(_currentUser.UserId, MaxUserIdLength);
        if (company is null || userId is null)
        {
            return null;
        }

        var branch = NormalizeClaim(_currentUser.BranchCode, MaxBranchLength);
        var location = NormalizeClaim(_currentUser.LocationCode, MaxLocationLength);

        if (requireBranch && branch is null)
        {
            return null;
        }

        if (requireLocation && location is null)
        {
            return null;
        }

        return new TenantScope
        {
            CompanyCode = company,
            BranchCode = branch,
            LocationCode = location,
            UserId = userId
        };
    }

    private static string? NormalizeClaim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? null : trimmed;
    }
}
