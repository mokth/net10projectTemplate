using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Settings;

/// <summary>
/// Reads and writes dynamic settings.
///
/// <para>
/// The catalogue is the ONLY way a key becomes a setting: every entry point resolves through
/// <see cref="AppSettingCatalogue.Find"/> first, so a row inserted by hand under a key nobody declared is
/// ignored and cannot influence behaviour.
/// </para>
///
/// <para>
/// Reads are cached per (tenant, module) with epoch-versioned keys — see
/// <see cref="AppSettingCacheVersions"/>. That is not a micro-optimisation: the pricing path reads a
/// company setting once per line, and without a cache this service would make that cost worse.
/// </para>
/// </summary>
public sealed class AppSettingService : IAppSettingService
{
    private const string ScopeGlobal = "GLOBAL";
    private const string ScopeCompany = "COMPANY";
    private const string ScopeBranch = "BRANCH";

    /// <summary>Absolute lifetime. Also the bound on staleness in a multi-instance deployment.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITenantScopeContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IMemoryCache _cache;
    private readonly AppSettingCacheVersions _versions;
    private readonly AppSettingProviderRegistry _providers;
    private readonly ILogger<AppSettingService> _logger;

    public AppSettingService(
        IDbContextFactory<AppDbContext> dbFactory,
        ITenantScopeContext tenant,
        IAccessRightService accessRights,
        IMemoryCache cache,
        AppSettingCacheVersions versions,
        AppSettingProviderRegistry providers,
        ILogger<AppSettingService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _cache = cache;
        _versions = versions;
        _providers = providers;
        _logger = logger;
    }

    // ============================== reads ==============================

    public async Task<IvMasterOperationResult<string?>> GetValueAsync(
        string module,
        string key,
        AppSettingScope scope,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken = default)
    {
        var definition = AppSettingCatalogue.Find(module, key);
        if (definition is null)
        {
            return Fail<string?>(IvMasterErrorCode.NotFound, $"'{module}.{key}' is not a known setting.");
        }

        var resolution = await ResolveOneAsync(
            definition,
            snapshot: null,
            scope: NormaliseRequestedScope(scope),
            companyCode: companyCode,
            branchCode: branchCode,
            cancellationToken: cancellationToken);

        return IvMasterOperationResult<string?>.Ok(resolution.Value);
    }

    public async Task<IvMasterOperationResult<string?>> GetForCurrentUserAsync(
        string module,
        string key,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return Fail<string?>(
                IvMasterErrorCode.InvalidScope,
                "A company and branch scope is required to read a setting for the current user.");
        }

        return await GetValueAsync(
            module,
            key,
            AppSettingScope.Branch,
            scope.CompanyCode,
            scope.BranchCode,
            cancellationToken);
    }

    public async Task<bool> GetFlagAsync(string module, string key, CancellationToken cancellationToken = default)
    {
        var result = await GetForCurrentUserAsync(module, key, cancellationToken);

        return result.Succeeded
            && string.Equals(result.Data, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<decimal?> GetNumberAsync(string module, string key, CancellationToken cancellationToken = default)
    {
        var result = await GetForCurrentUserAsync(module, key, cancellationToken);

        if (!result.Succeeded)
        {
            return null;
        }

        return decimal.TryParse(result.Data, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<AppSettingListRow>>> ListForModuleAsync(
        string module,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSettingsAccessAsync(PermissionCodes.Access, cancellationToken) is { } denied)
        {
            return Fail<IReadOnlyList<AppSettingListRow>>(denied, "Access denied.");
        }

        if (!AppSettingModules.IsKnown(module))
        {
            return Fail<IReadOnlyList<AppSettingListRow>>(
                IvMasterErrorCode.Validation,
                $"'{module}' is not a declared settings module.");
        }

        // The grid shows every level the operator may edit, so it always resolves at Branch depth.
        var readScope = AppSettingScope.Company | AppSettingScope.Branch;
        var definitions = AppSettingCatalogue.ForModule(module);

        var snapshot = await LoadSnapshotAsync(module, readScope, companyCode, branchCode, cancellationToken);

        var rows = new List<AppSettingListRow>(definitions.Count);
        foreach (var definition in definitions)
        {
            var resolution = await ResolveOneAsync(
                definition,
                snapshot,
                readScope,
                companyCode,
                branchCode,
                cancellationToken);

            rows.Add(BuildListRow(definition, resolution, companyCode, branchCode, snapshot.Rows));
        }

        if (snapshot.OrphanCount > 0)
        {
            // Visible, never applied. A hand-inserted row is a data-quality signal, not a setting.
            _logger.LogWarning(
                "AdSmParam holds {OrphanCount} row(s) in module {Module} whose key is not in the catalogue; "
                + "they are ignored.",
                snapshot.OrphanCount,
                module);
        }

        return IvMasterOperationResult<IReadOnlyList<AppSettingListRow>>.Ok(rows);
    }

    // ============================== writes ==============================

    public async Task<IvMasterOperationResult<AppSettingEditVm>> SaveAsync(
        AppSettingEditVm vm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);

        if (await RequireSettingsAccessAsync(PermissionCodes.Edit, cancellationToken) is { } denied)
        {
            return Fail<AppSettingEditVm>(denied, "Access denied.");
        }

        var definition = AppSettingCatalogue.Find(vm.Module, vm.Key);
        if (definition is null)
        {
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.NotFound,
                $"'{vm.Module}.{vm.Key}' is not a known setting, so no row can be created for it.");
        }

        if (definition.Backing == AppSettingBacking.ExistingColumn)
        {
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.Validation,
                $"'{definition.Module}.{definition.Key}' is maintained on its own screen and is read-only here.");
        }

        if (ValidateScopeTarget(definition, vm.Scope, vm.CompanyCode, vm.BranchCode) is { } scopeError)
        {
            return Fail<AppSettingEditVm>(IvMasterErrorCode.Validation, scopeError);
        }

        if (!AppSettingResolver.TryNormaliseForStorage(definition, vm.Value, out var canonical, out var valueError))
        {
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.Validation,
                valueError ?? "The value is not valid for this setting.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Value"] = valueError ?? "Invalid value."
                });
        }

        var actor = _tenant.TryCompanyScope()?.UserId;
        if (actor is null)
        {
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.InvalidScope,
                "A signed-in user is required to change a setting.");
        }

        try
        {
            return await UpsertAsync(definition, vm, canonical!, actor, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            Invalidate(vm.Scope, vm.CompanyCode, vm.BranchCode);
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.Concurrency,
                "This setting was changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The insert race. The filtered-free unique key is the last line of defence; the pre-check
            // above handles the ordinary case and keeps SQLite (NULLs DISTINCT) honest.
            Invalidate(vm.Scope, vm.CompanyCode, vm.BranchCode);
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.Concurrency,
                "Another user created this setting at the same scope. Reload and try again.");
        }
    }

    public async Task<IvMasterOperationResult<AppSettingEditVm>> ClearAsync(
        AppSettingEditVm vm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);

        if (await RequireSettingsAccessAsync(PermissionCodes.Edit, cancellationToken) is { } denied)
        {
            return Fail<AppSettingEditVm>(denied, "Access denied.");
        }

        var definition = AppSettingCatalogue.Find(vm.Module, vm.Key);
        if (definition is null)
        {
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.NotFound,
                $"'{vm.Module}.{vm.Key}' is not a known setting.");
        }

        if (definition.Backing == AppSettingBacking.ExistingColumn)
        {
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.Validation,
                $"'{definition.Module}.{definition.Key}' is maintained on its own screen and cannot be cleared here.");
        }

        if (ValidateScopeTarget(definition, vm.Scope, vm.CompanyCode, vm.BranchCode) is { } scopeError)
        {
            return Fail<AppSettingEditVm>(IvMasterErrorCode.Validation, scopeError);
        }

        var scopeCode = ScopeCodeOf(vm.Scope);
        var company = vm.Scope == AppSettingScope.Global ? null : vm.CompanyCode?.Trim();
        var branch = vm.Scope == AppSettingScope.Branch ? vm.BranchCode?.Trim() : null;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await FindRowAsync(db, definition.Module, definition.Key, scopeCode, company, branch, cancellationToken);
        if (existing is null)
        {
            // Nothing stored at this level already means "the default applies".
            return IvMasterOperationResult<AppSettingEditVm>.Ok(vm);
        }

        if (!TryReadRowVersion(vm.RowVersion, out var original))
        {
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.Validation,
                "The row version is required to clear an existing setting. Reload and try again.");
        }

        db.AdSmParams.Remove(existing);
        db.Entry(existing).Property(x => x.RowVersion).OriginalValue = original;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            Invalidate(vm.Scope, vm.CompanyCode, vm.BranchCode);
            return Fail<AppSettingEditVm>(
                IvMasterErrorCode.Concurrency,
                "This setting was changed by someone else. Reload and try again.");
        }

        Invalidate(vm.Scope, vm.CompanyCode, vm.BranchCode);

        return IvMasterOperationResult<AppSettingEditVm>.Ok(vm);
    }

    private async Task<IvMasterOperationResult<AppSettingEditVm>> UpsertAsync(
        AppSettingDefinition definition,
        AppSettingEditVm vm,
        string canonical,
        string actor,
        CancellationToken cancellationToken)
    {
        var scopeCode = ScopeCodeOf(vm.Scope);
        var company = vm.Scope == AppSettingScope.Global ? null : vm.CompanyCode?.Trim();
        var branch = vm.Scope == AppSettingScope.Branch ? vm.BranchCode?.Trim() : null;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await FindRowAsync(db, definition.Module, definition.Key, scopeCode, company, branch, cancellationToken);

        AdSmParam entity;
        if (existing is null)
        {
            entity = new AdSmParam
            {
                ModuleCode = definition.Module,
                ParamKey = definition.Key,
                ScopeCode = scopeCode,
                CompanyCode = company,
                BranchCode = branch,
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = actor
            };

            ApplyCanonicalValue(entity, definition, canonical);
            entity.Remark = string.IsNullOrWhiteSpace(vm.Remark) ? null : vm.Remark.Trim();
            db.AdSmParams.Add(entity);
        }
        else
        {
            if (!TryReadRowVersion(vm.RowVersion, out var original))
            {
                return Fail<AppSettingEditVm>(
                    IvMasterErrorCode.Validation,
                    "The row version is required to update an existing setting. Reload and try again.");
            }

            entity = existing;
            ApplyCanonicalValue(entity, definition, canonical);
            entity.Remark = string.IsNullOrWhiteSpace(vm.Remark) ? null : vm.Remark.Trim();
            entity.ModifiedDate = DateTime.UtcNow;
            entity.ModifiedBy = actor;
            db.Entry(entity).Property(x => x.RowVersion).OriginalValue = original;
        }

        await db.SaveChangesAsync(cancellationToken);

        // Only after a successful commit: a failed write must never leave the cache thinking it changed.
        Invalidate(vm.Scope, company, branch);

        vm.CompanyCode = company;
        vm.BranchCode = branch;
        vm.RowVersion = entity.RowVersion is { Length: > 0 } ? Convert.ToBase64String(entity.RowVersion) : null;

        return IvMasterOperationResult<AppSettingEditVm>.Ok(vm);
    }

    // ============================== resolution ==============================

    private async Task<SettingResolution> ResolveOneAsync(
        AppSettingDefinition definition,
        AppSettingModuleSnapshot? snapshot,
        AppSettingScope scope,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken)
    {
        if (definition.Backing == AppSettingBacking.ExistingColumn)
        {
            if (!_providers.TryGet(definition.Module, definition.Key, out var provider))
            {
                // I9: a missing provider is a WIRING defect, not a data condition. Fail loudly rather
                // than quietly returning the default, which would hide the mistake forever.
                throw new InvalidOperationException(
                    $"The setting {definition.Module}.{definition.Key} is backed by an existing column but no "
                    + "IAppSettingValueProvider is registered for it.");
            }

            var raw = await provider.ReadRawAsync(AppSettingScope.Company, companyCode, branchCode, cancellationToken);
            return AppSettingResolver.ResolveColumnBacked(definition, raw);
        }

        snapshot ??= await LoadSnapshotAsync(definition.Module, scope, companyCode, branchCode, cancellationToken);

        var levels = LevelsFor(snapshot, definition, scope);

        return AppSettingResolver.Resolve(definition, levels.Branch, levels.Company, levels.Global);
    }

    /// <summary>
    /// Picks the three raw values the resolver may consider. A level the caller cannot resolve at — or
    /// that the definition does not allow — is passed as null so the resolver never invents a scope (I3).
    /// </summary>
    private static AppSettingRawLevels LevelsFor(
        AppSettingModuleSnapshot snapshot,
        AppSettingDefinition definition,
        AppSettingScope scope)
    {
        if (!snapshot.Levels.TryGetValue(definition.Key.Trim().ToUpperInvariant(), out var stored))
        {
            return new AppSettingRawLevels(null, null, null);
        }

        var company = IncludesCompany(scope) ? stored.Company : null;
        var branch = IncludesBranch(scope) ? stored.Branch : null;

        return new AppSettingRawLevels(stored.Global, company, branch);
    }

    private async Task<AppSettingModuleSnapshot> LoadSnapshotAsync(
        string module,
        AppSettingScope scope,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken)
    {
        var cacheKey =
            $"AppSetting:v1:{_versions.Stamp(companyCode, branchCode)}:"
            + $"{companyCode?.Trim() ?? "*"}:{branchCode?.Trim() ?? "*"}:{module.Trim().ToUpperInvariant()}";

        if (_cache.TryGetValue(cacheKey, out AppSettingModuleSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        var snapshot = await QuerySnapshotAsync(module, scope, companyCode, branchCode, cancellationToken);
        _cache.Set(cacheKey, snapshot, CacheLifetime);

        return snapshot;
    }

    private async Task<AppSettingModuleSnapshot> QuerySnapshotAsync(
        string module,
        AppSettingScope scope,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken)
    {
        var company = string.IsNullOrWhiteSpace(companyCode) ? null : companyCode.Trim();
        var branch = string.IsNullOrWhiteSpace(branchCode) ? null : branchCode.Trim();

        var wantsCompany = IncludesCompany(scope) && company is not null;
        var wantsBranch = IncludesBranch(scope) && wantsCompany && branch is not null;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.AdSmParams
            .AsNoTracking()
            .Where(x => x.IsActive
                && x.ModuleCode == module
                && (x.ScopeCode == ScopeGlobal
                    || (wantsCompany && x.ScopeCode == ScopeCompany && x.CompanyCode == company)
                    || (wantsBranch && x.ScopeCode == ScopeBranch && x.CompanyCode == company && x.BranchCode == branch)))
            .ToListAsync(cancellationToken);

        var levels = new Dictionary<string, AppSettingRawLevels>(StringComparer.OrdinalIgnoreCase);
        var orphanCount = 0;

        foreach (var row in rows)
        {
            // Containment: a key the catalogue does not declare is never resolved, only counted.
            if (AppSettingCatalogue.Find(module, row.ParamKey) is null)
            {
                orphanCount++;
                continue;
            }

            var key = row.ParamKey.Trim().ToUpperInvariant();
            levels.TryGetValue(key, out var current);
            current ??= new AppSettingRawLevels(null, null, null);

            var value = ReadStoredValue(row);

            levels[key] = row.ScopeCode.Trim().ToUpperInvariant() switch
            {
                ScopeGlobal => current with { Global = value },
                ScopeCompany => current with { Company = value },
                ScopeBranch => current with { Branch = value },
                _ => current
            };
        }

        return new AppSettingModuleSnapshot
        {
            Module = module,
            Rows = rows,
            Levels = levels,
            OrphanCount = orphanCount
        };
    }

    // ============================== helpers ==============================

    private static AppSettingScope NormaliseRequestedScope(AppSettingScope scope) =>
        scope == AppSettingScope.None ? AppSettingScope.Company : scope;

    /// <summary>
    /// A requested scope is an INCLUSIVITY LEVEL, not an exclusive bitmask: asking for
    /// <see cref="AppSettingScope.Branch"/> means "resolve as deep as the branch", so the company and
    /// global levels are consulted too. Reading it as a bitmask would make a branch-level read silently
    /// ignore every company default, which is the opposite of a fallback ladder.
    /// </summary>
    private static int Depth(AppSettingScope scope) => scope switch
    {
        AppSettingScope.Global => 1,
        AppSettingScope.Company => 2,
        _ => 3
    };

    private static bool IncludesCompany(AppSettingScope scope) => Depth(scope) >= 2;

    private static bool IncludesBranch(AppSettingScope scope) => Depth(scope) >= 3;

    private static string ScopeCodeOf(AppSettingScope scope) => scope switch
    {
        AppSettingScope.Global => ScopeGlobal,
        AppSettingScope.Branch => ScopeBranch,
        _ => ScopeCompany
    };

    /// <summary>
    /// The definition's allowed scopes beat the request (I3), and a scope needs its tenant target.
    /// </summary>
    private static string? ValidateScopeTarget(
        AppSettingDefinition definition,
        AppSettingScope scope,
        string? companyCode,
        string? branchCode)
    {
        if (scope == AppSettingScope.None)
        {
            return "A scope is required.";
        }

        if (!definition.Allows(scope))
        {
            return $"'{definition.Module}.{definition.Key}' cannot be stored at {scope}; allowed: {definition.AllowedScopes}.";
        }

        if (scope is AppSettingScope.Company or AppSettingScope.Branch && string.IsNullOrWhiteSpace(companyCode))
        {
            return "A company code is required for a company or branch setting.";
        }

        if (scope == AppSettingScope.Branch && string.IsNullOrWhiteSpace(branchCode))
        {
            return "A branch code is required for a branch setting.";
        }

        return null;
    }

    private void Invalidate(AppSettingScope scope, string? companyCode, string? branchCode)
    {
        switch (scope)
        {
            case AppSettingScope.Global:
                // A global row is the last fallback for every tenant, so nothing is unaffected.
                _versions.BumpGlobal();
                break;

            case AppSettingScope.Branch:
                if (!string.IsNullOrWhiteSpace(companyCode) && !string.IsNullOrWhiteSpace(branchCode))
                {
                    _versions.BumpBranch(companyCode!, branchCode!);
                }
                else
                {
                    _versions.BumpGlobal();
                }

                break;

            default:
                if (!string.IsNullOrWhiteSpace(companyCode))
                {
                    // Company snapshots AND that company's branch snapshots, which may have fallen back
                    // to this value.
                    _versions.BumpCompany(companyCode!);
                }
                else
                {
                    _versions.BumpGlobal();
                }

                break;
        }
    }

    private async Task<IvMasterErrorCode?> RequireSettingsAccessAsync(
        string permissionCode,
        CancellationToken cancellationToken) =>
        await _accessRights.CanAsync(MenuCodes.AdminSettings, permissionCode, cancellationToken)
            ? null
            : IvMasterErrorCode.AccessDenied;

    private static async Task<AdSmParam?> FindRowAsync(
        AppDbContext db,
        string module,
        string key,
        string scopeCode,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken) =>
        await db.AdSmParams.FirstOrDefaultAsync(
            x => x.ModuleCode == module
                && x.ParamKey == key
                && x.ScopeCode == scopeCode
                && x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.IsActive,
            cancellationToken);

    private static string? ReadStoredValue(AdSmParam row)
    {
        if (row.ValueText is not null)
        {
            return row.ValueText;
        }

        if (row.ValueNumber is not null)
        {
            return row.ValueNumber.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (row.ValueDate is not null)
        {
            return row.ValueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (row.ValueFlag is not null)
        {
            return row.ValueFlag.Value ? "true" : "false";
        }

        return null;
    }

    /// <summary>
    /// Writes the canonical value into the one typed column that matches the definition's type. All four
    /// are assigned every time because the database requires exactly one to be populated.
    /// </summary>
    private static void ApplyCanonicalValue(AdSmParam entity, AppSettingDefinition definition, string canonical)
    {
        entity.ValueText = null;
        entity.ValueNumber = null;
        entity.ValueDate = null;
        entity.ValueFlag = null;

        switch (definition.Type)
        {
            case AppSettingType.Number:
                entity.ValueNumber = decimal.Parse(canonical, NumberStyles.Number, CultureInfo.InvariantCulture);
                break;

            case AppSettingType.Date:
                entity.ValueDate = DateTime.ParseExact(canonical, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;

            case AppSettingType.Flag:
                entity.ValueFlag = string.Equals(canonical, "true", StringComparison.OrdinalIgnoreCase);
                break;

            default:
                // Text and Token both live in the text column.
                entity.ValueText = canonical;
                break;
        }
    }

    private static bool TryReadRowVersion(string? value, out byte[] original)
    {
        original = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            original = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsUniqueViolation(Exception ex) => SqlErrorClassifier.IsUniqueViolation(ex);

    private static AppSettingListRow BuildListRow(
        AppSettingDefinition definition,
        SettingResolution resolution,
        string? companyCode,
        string? branchCode,
        IReadOnlyList<AdSmParam> rows)
    {
        var global = FindStored(rows, definition.Key, ScopeGlobal, null, null);
        var company = string.IsNullOrWhiteSpace(companyCode)
            ? null
            : FindStored(rows, definition.Key, ScopeCompany, companyCode.Trim(), null);
        var branch = string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode)
            ? null
            : FindStored(rows, definition.Key, ScopeBranch, companyCode.Trim(), branchCode.Trim());

        var governing = resolution.Provenance switch
        {
            AppSettingScope.Global => global,
            AppSettingScope.Company => company,
            AppSettingScope.Branch => branch,
            _ => null
        };

        return new AppSettingListRow
        {
            Module = definition.Module,
            Key = definition.Key,
            Type = definition.Type,
            AllowedScopes = definition.AllowedScopes,
            DefaultValue = definition.DefaultValue,
            Description = definition.Description,
            AllowedTokens = definition.AllowedTokens ?? [],
            IsReadOnly = definition.Backing == AppSettingBacking.ExistingColumn,
            Value = resolution.Value,
            Provenance = resolution.Provenance,
            IsDefault = resolution.IsDefault,
            RejectedReason = resolution.RejectedReason,
            StoredGlobal = ToStoredRow(global),
            StoredCompany = ToStoredRow(company),
            StoredBranch = ToStoredRow(branch),
            ModifiedBy = governing?.ModifiedBy,
            ModifiedDate = governing?.ModifiedDate,
            Remark = governing?.Remark
        };
    }

    private static AdSmParam? FindStored(
        IReadOnlyList<AdSmParam> rows,
        string key,
        string scopeCode,
        string? companyCode,
        string? branchCode) =>
        rows.FirstOrDefault(x =>
            string.Equals(x.ParamKey, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ScopeCode, scopeCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase));

    private static AppSettingStoredRow? ToStoredRow(AdSmParam? row) =>
        row is null
            ? null
            : new AppSettingStoredRow
            {
                ParamId = row.ParamId,
                Scope = row.ScopeCode,
                Value = ReadStoredValue(row),
                RowVersion = row.RowVersion is { Length: > 0 } ? Convert.ToBase64String(row.RowVersion) : null,
                Remark = row.Remark,
                ModifiedBy = row.ModifiedBy,
                ModifiedDate = row.ModifiedDate
            };

    private static IvMasterOperationResult<T> Fail<T>(
        IvMasterErrorCode code,
        string message,
        IReadOnlyDictionary<string, string>? validationErrors = null) =>
        IvMasterOperationResult<T>.Fail(code, message, validationErrors);
}
