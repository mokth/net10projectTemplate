using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Admin;

/// <summary>
/// Company-scoped generic configurable lookup service. See <see cref="IAdLookupService"/> for the
/// maintenance / assignment-read split and the permission model.
/// </summary>
public sealed class AdLookupService : IAdLookupService
{
    private const string SystemActor = "SYSTEM";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITenantScopeContext _tenant;
    private readonly IAccessRightService _access;

    public AdLookupService(
        IDbContextFactory<AppDbContext> dbFactory,
        ITenantScopeContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    // ── Maintenance ───────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdLookupRow>> ListForMaintenanceAsync(
        string lookupType,
        bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null
            || !await _access.CanAsync(MenuCodes.AdminLookups, PermissionCodes.Access, cancellationToken))
        {
            return [];
        }

        var definition = AdLookupTypeRegistry.Find(lookupType);
        if (definition is null || !definition.UserMaintainable)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AdLookupValues
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.LookupType == definition.Code);

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Code)
            .Select(x => new AdLookupRow
            {
                Uid = x.Uid,
                LookupType = x.LookupType,
                Code = x.Code,
                Description = x.Description,
                SortOrder = x.SortOrder,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion,
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy,
                ModifiedDate = x.ModifiedDate,
                ModifiedBy = x.ModifiedBy
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IvMasterOperationResult<AdLookupEditVm>> GetAsync(
        long uid,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return FailVm(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        if (!await _access.CanAsync(MenuCodes.AdminLookups, PermissionCodes.Access, cancellationToken))
        {
            return FailVm(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AdLookupValues
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Uid == uid && x.CompanyCode == scope.CompanyCode, cancellationToken);

        // A row from another company is indistinguishable from a missing row — the tenant filter is the
        // security boundary and it is never relaxed.
        return entity is null
            ? FailVm(IvMasterErrorCode.NotFound, "Lookup value not found.")
            : IvMasterOperationResult<AdLookupEditVm>.Ok(MapEditVm(entity));
    }

    public async Task<IvMasterOperationResult<AdLookupEditVm>> SaveAsync(
        AdLookupEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return FailVm(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await _access.CanAsync(MenuCodes.AdminLookups, permission, cancellationToken))
        {
            return FailVm(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var definition = AdLookupTypeRegistry.Find(model.LookupType);
        if (definition is null)
        {
            return FailVm(
                IvMasterErrorCode.Validation,
                $"Unknown lookup type '{model.LookupType}'.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LookupType"] = "Unknown lookup type."
                });
        }

        if (!definition.UserMaintainable)
        {
            return FailVm(
                IvMasterErrorCode.Validation,
                $"Lookup type '{definition.DisplayName}' is not user-maintainable.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LookupType"] = "This lookup type is not user-maintainable."
                });
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var code = (model.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            errors["Code"] = "Code is required.";
        }
        else if (code.Length > AdLookupDefaults.CodeCeiling)
        {
            errors["Code"] = $"Code must be at most {AdLookupDefaults.CodeCeiling} characters.";
        }
        else if (code.Length > definition.MaxCodeLength)
        {
            // The real consumer-column width of this family. Never truncate to make it fit.
            errors["Code"] = $"Code must be at most {definition.MaxCodeLength} characters for {definition.DisplayName}.";
        }
        else if (code.Any(char.IsControl))
        {
            errors["Code"] = "Code must not contain control characters.";
        }
        else if (isNew && !IsAllowedNewCode(code))
        {
            errors["Code"] = "Code may contain only letters, digits, underscore, hyphen and full stop.";
        }

        var description = (model.Description ?? string.Empty).Trim();
        if (description.Length == 0)
        {
            errors["Description"] = "Description is required.";
        }
        else if (description.Length > AdLookupDefaults.DescriptionMaxLength)
        {
            errors["Description"] = $"Description must be at most {AdLookupDefaults.DescriptionMaxLength} characters.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<AdLookupEditVm>.Fail(
                IvMasterErrorCode.Validation,
                "Validation failed.",
                errors);
        }

        var actor = Truncate(scope.UserId, 20);
        var now = DateTime.UtcNow;
        var lookupType = definition.Code;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                // New manual codes are normalised to uppercase. Backfilled legacy values keep their exact
                // casing (the DBA script inserts them), so nothing is silently rewritten here.
                code = code.ToUpperInvariant();

                var duplicate = await db.AdLookupValues.AnyAsync(
                    x => x.CompanyCode == scope.CompanyCode
                         && x.LookupType == lookupType
                         && x.Code == code,
                    cancellationToken);
                if (duplicate)
                {
                    return DuplicateVm(code);
                }

                var entity = new AdLookupValue
                {
                    CompanyCode = scope.CompanyCode,
                    LookupType = lookupType,
                    Code = code,
                    Description = description,
                    SortOrder = model.SortOrder,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = actor,
                    ModifiedDate = now,
                    ModifiedBy = actor
                };
                db.AdLookupValues.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<AdLookupEditVm>.Ok(MapEditVm(entity));
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm(
                    IvMasterErrorCode.Concurrency,
                    "Concurrency token is missing. Reload and try again.");
            }

            var tracked = await db.AdLookupValues.FirstOrDefaultAsync(
                x => x.Uid == model.Uid && x.CompanyCode == scope.CompanyCode,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm(IvMasterErrorCode.NotFound, "Lookup value not found.");
            }

            if (!string.Equals(tracked.LookupType, lookupType, StringComparison.OrdinalIgnoreCase))
            {
                return FailVm(
                    IvMasterErrorCode.Validation,
                    "Lookup type cannot be changed.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["LookupType"] = "Lookup type cannot be changed."
                    });
            }

            if (!string.Equals(tracked.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                // Stable after creation: consumer rows reference this exact code.
                return FailVm(
                    IvMasterErrorCode.Validation,
                    "Code cannot be changed.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Code"] = "Code cannot be changed."
                    });
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm(
                    IvMasterErrorCode.Concurrency,
                    "This lookup value was modified by another user.");
            }

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.Description = description;
            tracked.SortOrder = model.SortOrder;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = now;
            tracked.ModifiedBy = actor;
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<AdLookupEditVm>.Ok(MapEditVm(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm(
                IvMasterErrorCode.Concurrency,
                "This lookup value was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return DuplicateVm(code);
        }
    }

    public async Task<IvMasterOperationResult<object>> SetActiveAsync(
        IReadOnlyList<AdLookupKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return FailObj(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        if (!await _access.CanAsync(MenuCodes.AdminLookups, PermissionCodes.Edit, cancellationToken))
        {
            return FailObj(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (items is null || items.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No record selected.");
        }

        var uids = items.Select(x => x.Uid).Distinct().ToList();
        var actor = Truncate(scope.UserId, 20);
        var now = DateTime.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entities = await db.AdLookupValues
            .Where(x => x.CompanyCode == scope.CompanyCode && uids.Contains(x.Uid))
            .ToListAsync(cancellationToken);

        if (entities.Count != uids.Count)
        {
            return FailObj(IvMasterErrorCode.NotFound, "One or more lookup values were not found.");
        }

        foreach (var token in items)
        {
            var entity = entities.Single(x => x.Uid == token.Uid);
            if (!RowVersionsEqual(entity.RowVersion, token.RowVersion))
            {
                return FailObj(
                    IvMasterErrorCode.Concurrency,
                    "One or more lookup values were modified by another user.");
            }

            db.Entry(entity).Property(x => x.RowVersion).OriginalValue = token.RowVersion;
            entity.IsActive = isActive;
            entity.ModifiedDate = now;
            entity.ModifiedBy = actor;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok(new object());
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailObj(
                IvMasterErrorCode.Concurrency,
                "One or more lookup values were modified by another user.");
        }
    }

    // ── Assignment reads (ungated by ADMIN_LOOKUPS) ───────────────────────────────────────────────

    public async Task<IReadOnlyList<IvCodeLookupRow>> ListForAssignmentAsync(
        string lookupType,
        string? existingCode = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await AdLookupAssignments.ListAsync(db, scope.CompanyCode, lookupType, existingCode, cancellationToken);
    }

    public Task<IReadOnlyList<IvCodeLookupRow>> ListAsync(
        string lookupType,
        CancellationToken cancellationToken = default) =>
        ListForAssignmentAsync(lookupType, existingCode: null, cancellationToken);

    public async Task<bool> ValidateAssignmentAsync(
        string lookupType,
        string? incomingCode,
        string? existingCode,
        bool allowBlank,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(incomingCode))
        {
            return allowBlank;
        }

        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await AdLookupAssignments.ValidateAsync(
            db, scope.CompanyCode, lookupType, incomingCode, existingCode, allowBlank, cancellationToken);
    }

    // ── Seeding ───────────────────────────────────────────────────────────────────────────────────

    public async Task EnsureLookupDefaultsAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        var actor = _tenant.TryCompanyScope()?.UserId ?? SystemActor;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await AdLookupDefaultsSeeder.EnsureDefaultsAsync(db, companyCode, actor, cancellationToken);
    }

    // ── Mapping / helpers ────────────────────────────────────────────────────────────────────────

    private static AdLookupEditVm MapEditVm(AdLookupValue x)
    {
        var definition = AdLookupTypeRegistry.Find(x.LookupType);
        return new AdLookupEditVm
        {
            Uid = x.Uid,
            LookupType = x.LookupType,
            Code = x.Code,
            Description = x.Description,
            SortOrder = x.SortOrder,
            IsActive = x.IsActive,
            RowVersion = x.RowVersion,
            CreatedDate = x.CreatedDate,
            CreatedBy = x.CreatedBy,
            ModifiedDate = x.ModifiedDate,
            ModifiedBy = x.ModifiedBy,
            MaxCodeLength = definition?.MaxCodeLength ?? AdLookupDefaults.CodeCeiling
        };
    }

    private static bool IsAllowedNewCode(string code) =>
        code.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.');

    private static IvMasterOperationResult<AdLookupEditVm> DuplicateVm(string code) =>
        IvMasterOperationResult<AdLookupEditVm>.Fail(
            IvMasterErrorCode.DuplicateKey,
            $"Lookup value '{code}' already exists for this type.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = "This code already exists for the selected lookup type."
            });

    private static IvMasterOperationResult<AdLookupEditVm> FailVm(
        IvMasterErrorCode code,
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        IvMasterOperationResult<AdLookupEditVm>.Fail(code, message, errors);

    private static IvMasterOperationResult<object> FailObj(IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<object>.Fail(code, message);

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        left is { Length: > 0 } && right is { Length: > 0 } && left.SequenceEqual(right);

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
               || message.Contains("2601", StringComparison.Ordinal)
               || message.Contains("2627", StringComparison.Ordinal);
    }
}
