using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Services;

public sealed class CompanyContext : ICompanyContext
{
    private readonly ICurrentUserService _currentUser;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly object _gate = new();
    private ResolvedSnapshot? _snapshot;

    public CompanyContext(
        ICurrentUserService currentUser,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _currentUser = currentUser;
        _dbFactory = dbFactory;
    }

    public bool IsResolved => _snapshot is not null;

    public int CompanyId => Require().CompanyId;
    public string CompanyCode => Require().CompanyCode;
    public long BranchId => Require().BranchId;
    public string BranchCode => Require().BranchCode;
    public string? LegacyLocationCode => Require().LegacyLocationCode;
    public string TimeZoneId => Require().TimeZoneId;
    public string BaseCurrencyCode => Require().BaseCurrencyCode;

    public async Task ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (_snapshot is not null)
        {
            return;
        }

        if (!_currentUser.IsAuthenticated)
        {
            throw new InvalidOperationException("User is not authenticated.");
        }

        var companyCode = (_currentUser.CompanyCode ?? string.Empty).Trim();
        var branchCode = (_currentUser.BranchCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            throw new InvalidOperationException("CompanyCode claim is missing.");
        }

        if (string.IsNullOrWhiteSpace(branchCode))
        {
            branchCode = "HQ";
        }

        companyCode = companyCode.ToUpperInvariant();
        branchCode = branchCode.ToUpperInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = await db.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyCode == companyCode && c.IsActive, cancellationToken);
        if (company is null)
        {
            throw new InvalidOperationException($"Company '{companyCode}' was not found or is inactive.");
        }

        var branch = await db.Branches
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.CompanyId == company.CompanyId && b.BranchCode == branchCode && b.IsActive,
                cancellationToken);

        if (branch is null && !string.Equals(branchCode, "HQ", StringComparison.Ordinal))
        {
            branch = await db.Branches
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    b => b.CompanyId == company.CompanyId && b.BranchCode == "HQ" && b.IsActive,
                    cancellationToken);
            if (branch is not null)
            {
                branchCode = "HQ";
            }
        }

        if (branch is null)
        {
            throw new InvalidOperationException(
                $"Branch '{branchCode}' was not found for company '{companyCode}'. Run scripts/init-branch.sql or create the branch.");
        }

        var snapshot = new ResolvedSnapshot(
            company.CompanyId,
            company.CompanyCode,
            branch.Id,
            branch.BranchCode,
            _currentUser.LocationCode,
            string.IsNullOrWhiteSpace(company.TimeZoneId) ? "Asia/Kuala_Lumpur" : company.TimeZoneId!,
            string.IsNullOrWhiteSpace(company.CurrencyCode) ? "MYR" : company.CurrencyCode!);

        lock (_gate)
        {
            _snapshot ??= snapshot;
        }
    }

    private ResolvedSnapshot Require()
    {
        if (_snapshot is null)
        {
            throw new InvalidOperationException("ICompanyContext has not been resolved. Call ResolveAsync first.");
        }

        return _snapshot;
    }

    private sealed record ResolvedSnapshot(
        int CompanyId,
        string CompanyCode,
        long BranchId,
        string BranchCode,
        string? LegacyLocationCode,
        string TimeZoneId,
        string BaseCurrencyCode);
}
