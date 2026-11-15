using ErpWeb.Core.Services;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public interface IPurchaseSourcingFeatureGuard
{
    /// <summary>
    /// Cached/UI read of <c>PROCUREMENT.SOURCING_ENABLED</c> for the current signed-in company.
    /// Fails closed (<c>false</c>) when unauthenticated, company is missing, or settings lookup fails.
    /// </summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Authoritative database read of <c>dbo.AdSmParam</c> bypassing process-local setting caches.
    /// When <paramref name="lockRowForUpdate"/> is <c>true</c> on SQL Server inside an active transaction,
    /// acquires <c>UPDLOCK, HOLDLOCK</c> on the company setting key so concurrent admin disable/clear
    /// and sourcing writes/conversions are strictly linearized.
    /// </summary>
    Task<bool> IsEnabledAuthoritativeAsync(
        AppDbContext db,
        string companyCode,
        bool lockRowForUpdate = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <c>PROCUREMENT.SOURCING_ENABLED</c> is not
    /// authoritatively enabled for <paramref name="companyCode"/>.
    /// </summary>
    Task EnsureEnabledAuthoritativeAsync(
        AppDbContext db,
        string companyCode,
        bool lockRowForUpdate = false,
        CancellationToken cancellationToken = default);
}

public sealed class PurchaseSourcingFeatureGuard : IPurchaseSourcingFeatureGuard
{
    private const string CompanyScopeCode = "COMPANY";

    private readonly IAppSettingService _settings;
    private readonly ICurrentUserService _currentUser;

    public PurchaseSourcingFeatureGuard(
        IAppSettingService settings,
        ICurrentUserService currentUser)
    {
        _settings = settings;
        _currentUser = currentUser;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var companyCode = _currentUser.CompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return false;
        }

        try
        {
            return await _settings.GetFlagAsync(
                AppSettingModules.Procurement,
                AppSettingCatalogue.ProcurementKeys.SourcingEnabled,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fail closed on any setting storage failure without breaking navigation or existing PR/PO.
            return false;
        }
    }

    public async Task<bool> IsEnabledAuthoritativeAsync(
        AppDbContext db,
        string companyCode,
        bool lockRowForUpdate = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var normalizedCompany = companyCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedCompany) || normalizedCompany.Length > 5)
        {
            return false;
        }

        try
        {
            AdSmParam? row;
            var provider = db.Database.ProviderName ?? string.Empty;
            if (lockRowForUpdate && provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                row = await db.AdSmParams
                    .FromSqlInterpolated($@"
SELECT *
FROM dbo.AdSmParam WITH (UPDLOCK, HOLDLOCK)
WHERE ModuleCode = {AppSettingModules.Procurement}
  AND ParamKey = {AppSettingCatalogue.ProcurementKeys.SourcingEnabled}
  AND ScopeCode = {CompanyScopeCode}
  AND CompanyCode = {normalizedCompany}
  AND BranchCode = {string.Empty}")
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                row = await db.AdSmParams
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        x => x.ModuleCode == AppSettingModules.Procurement
                             && x.ParamKey == AppSettingCatalogue.ProcurementKeys.SourcingEnabled
                             && x.ScopeCode == CompanyScopeCode
                             && x.CompanyCode == normalizedCompany
                             && x.BranchCode == string.Empty,
                        cancellationToken);
            }

            if (row is null || !row.IsActive)
            {
                return false;
            }

            if (row.ValueFlag.HasValue)
            {
                return row.ValueFlag.Value;
            }

            return string.Equals(row.ValueText?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.ValueText?.Trim(), "1", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task EnsureEnabledAuthoritativeAsync(
        AppDbContext db,
        string companyCode,
        bool lockRowForUpdate = false,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAuthoritativeAsync(db, companyCode, lockRowForUpdate, cancellationToken))
        {
            throw new InvalidOperationException(
                "Supplier Quotation Comparison (PROCUREMENT.SOURCING_ENABLED) is turned off for this company.");
        }
    }
}
