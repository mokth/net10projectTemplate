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
        if (scope is null)
        {
            return [];
        }

        // Resolve the family BEFORE authorizing: the grant is per family, so the family must be known
        // and maintainable first. An unknown or reserved family is not a lookup the caller may read.
        var definition = AdLookupTypeRegistry.Find(lookupType);
        if (definition is not { UserMaintainable: true })
        {
            return [];
        }

        if (!await CanMaintainAsync(definition, PermissionCodes.Access, cancellationToken))
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AdLookupValues
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Uid == uid && x.CompanyCode == scope.CompanyCode, cancellationToken);

        if (entity is null)
        {
            return NotFoundVm();
        }

        // Authorize the PERSISTED family, never a value supplied by the caller. A row belonging to a
        // family the caller may not maintain is indistinguishable from a missing row: the tenant scope
        // and the family grant are the boundary and neither is ever relaxed.
        var definition = AdLookupTypeRegistry.Find(entity.LookupType);
        if (!await CanMaintainAsync(definition, PermissionCodes.Access, cancellationToken))
        {
            return NotFoundVm();
        }

        return IvMasterOperationResult<AdLookupEditVm>.Ok(MapEditVm(entity));
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

        // NEW and EDIT read the family from different trust sources: creation has no persisted row, so
        // it must trust the submitted family; an edit must trust ONLY the persisted family. Keeping
        // them separate is what lets the edit path authorize before it discloses anything.
        return isNew
            ? await CreateAsync(model, scope, cancellationToken)
            : await UpdateAsync(model, scope, cancellationToken);
    }

    private async Task<IvMasterOperationResult<AdLookupEditVm>> CreateAsync(
        AdLookupEditVm model,
        TenantScope scope,
        CancellationToken cancellationToken)
    {
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

        if (!await CanMaintainAsync(definition, PermissionCodes.Add, cancellationToken))
        {
            return FailVm(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var (code, description, errors) = ValidateFields(model, definition, isNew: true);
        if (errors.Count > 0)
        {
            return ValidationVm(errors);
        }

        var actor = Truncate(scope.UserId, 20);
        var now = DateTime.UtcNow;
        var lookupType = definition.Code;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
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
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return DuplicateVm(code);
        }
    }

    private async Task<IvMasterOperationResult<AdLookupEditVm>> UpdateAsync(
        AdLookupEditVm model,
        TenantScope scope,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var tracked = await db.AdLookupValues.FirstOrDefaultAsync(
            x => x.Uid == model.Uid && x.CompanyCode == scope.CompanyCode,
            cancellationToken);

        // A row from another company, a row whose persisted family is unknown/reserved and a row the
        // caller may not maintain are all reported the same way, and all before anything about the row
        // (including its family) is disclosed.
        if (tracked is null)
        {
            return NotFoundVm();
        }

        var definition = AdLookupTypeRegistry.Find(tracked.LookupType);
        if (!await CanMaintainAsync(definition, PermissionCodes.Edit, cancellationToken))
        {
            return NotFoundVm();
        }

        try
        {
            if (!string.Equals(tracked.LookupType, (model.LookupType ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return FailVm(
                    IvMasterErrorCode.Validation,
                    "Lookup type cannot be changed.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["LookupType"] = "Lookup type cannot be changed."
                    });
            }

            if (model.RowVersion is null || model.RowVersion.Length == 0)
            {
                return FailVm(
                    IvMasterErrorCode.Concurrency,
                    "Concurrency token is missing. Reload and try again.");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm(
                    IvMasterErrorCode.Concurrency,
                    "This lookup value was modified by another user.");
            }

            // Validate against the PERSISTED family's real consumer-column width, never the submitted one.
            var (code, description, errors) = ValidateFields(model, definition!, isNew: false);
            if (errors.Count > 0)
            {
                return ValidationVm(errors);
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

            db.Entry(tracked).Property(x => x.RowVersion).OriginalValue = model.RowVersion;
            tracked.Description = description;
            tracked.SortOrder = model.SortOrder;
            tracked.IsActive = model.IsActive;
            tracked.ModifiedDate = DateTime.UtcNow;
            tracked.ModifiedBy = Truncate(scope.UserId, 20);
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<AdLookupEditVm>.Ok(MapEditVm(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm(
                IvMasterErrorCode.Concurrency,
                "This lookup value was modified by another user.");
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

        if (items is null || items.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No record selected.");
        }

        var uids = items.Select(x => x.Uid).Distinct().ToList();
        if (uids.Count != items.Count)
        {
            return FailObj(IvMasterErrorCode.Validation, "One or more records were selected more than once.");
        }

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

        // Authorize EVERY distinct PERSISTED family first. A mixed selection is all-or-nothing: one
        // family the caller may not maintain rejects the whole batch so nothing is half-applied.
        foreach (var lookupType in entities.Select(x => x.LookupType).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var definition = AdLookupTypeRegistry.Find(lookupType);
            if (!await CanMaintainAsync(definition, PermissionCodes.Edit, cancellationToken))
            {
                return FailObj(IvMasterErrorCode.NotFound, "One or more lookup values were not found.");
            }
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

    /// <summary>
    /// The single maintenance permission policy: the family's OWN menu grant, or the all-family
    /// <see cref="MenuCodes.AdminLookups"/> grant.
    ///
    /// <para>
    /// Fails CLOSED. A family that is not maintainable, or whose <c>MaintenanceMenuCode</c> is missing,
    /// is denied outright — it never degrades to the admin grant, because that would silently widen a
    /// misconfigured family to every administrator. The menu code always comes from the registry, never
    /// from a UI/request value.
    /// </para>
    /// </summary>
    private async Task<bool> CanMaintainAsync(
        AdLookupTypeDefinition? definition,
        string permissionCode,
        CancellationToken cancellationToken)
    {
        if (!HasMaintainablePlacement(definition))
        {
            return false;
        }

        return await _access.CanAsync(definition!.MaintenanceMenuCode!, permissionCode, cancellationToken)
            || await _access.CanAsync(MenuCodes.AdminLookups, permissionCode, cancellationToken);
    }

    /// <summary>
    /// Shape gate for the policy above, exposed for tests so the fail-closed rule can be asserted
    /// without inventing a fake registry entry in the shipped metadata. A maintainable family without
    /// a menu code is denied — it must never inherit the admin grant by accident.
    /// </summary>
    internal static bool HasMaintainablePlacement(AdLookupTypeDefinition? definition) =>
        definition is { UserMaintainable: true }
        && !string.IsNullOrWhiteSpace(definition.MaintenanceMenuCode);

    /// <summary>
    /// Field validation shared by create and edit. The code limit is the PERSISTED family's real
    /// consumer-column width; an over-width value is refused, never truncated.
    /// </summary>
    private static (string Code, string Description, Dictionary<string, string> Errors) ValidateFields(
        AdLookupEditVm model,
        AdLookupTypeDefinition definition,
        bool isNew)
    {
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

        return (code, description, errors);
    }

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

    /// <summary>
    /// Deliberately one message for "missing", "other company" and "family you may not maintain", so a
    /// caller cannot probe another company's or another family's rows through error differences.
    /// </summary>
    private static IvMasterOperationResult<AdLookupEditVm> NotFoundVm() =>
        FailVm(IvMasterErrorCode.NotFound, "Lookup value not found.");

    private static IvMasterOperationResult<AdLookupEditVm> ValidationVm(
        IReadOnlyDictionary<string, string> errors) =>
        IvMasterOperationResult<AdLookupEditVm>.Fail(
            IvMasterErrorCode.Validation,
            "Validation failed.",
            errors);

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
