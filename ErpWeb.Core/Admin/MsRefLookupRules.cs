using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Admin;

public enum MsRefLookupKind
{
    Department,
    Project
}

/// <summary>
/// Shared legacy-aware validation for the optional Department / Project codes on transaction
/// saves. This exists so all eight sales and purchase services apply an identical rule; do not
/// re-implement it per service.
///
/// Locked rule:
///   * Blank is allowed (the fields are optional).
///   * A new or changed non-blank code must exist AND be active.
///   * An existing historical orphan (a code that is not in the master) may remain unchanged
///     when editing an old document, so historical edits are not blocked.
///   * Changing the field means the new value must pass normal master validation.
///
/// Callers pass the value that is currently persisted alongside the incoming value; the rule
/// only rejects when the value is dirty.
/// </summary>
public static class MsRefLookupRules
{
    public const int MaxCodeLength = 20;

    /// <summary>
    /// Returns null when the incoming value is acceptable, otherwise a user-facing message.
    /// </summary>
    public static async Task<string?> ValidateAsync(
        AppDbContext db,
        string companyCode,
        string? branchCode,
        MsRefLookupKind kind,
        string? priorValue,
        string? incomingValue,
        CancellationToken cancellationToken = default)
    {
        var incoming = Normalize(incomingValue);

        // Blank is always allowed for these optional dimensions.
        if (incoming is null)
        {
            return null;
        }

        // Unchanged value: preserve historical orphans so old documents stay editable.
        if (IsUnchanged(priorValue, incoming))
        {
            return null;
        }

        // New or changed value: must exist and be active.
        var existsActive = await ExistsActiveAsync(
            db, companyCode, branchCode, kind, incoming, cancellationToken);

        return existsActive ? null : MessageFor(kind, incoming);
    }

    public static async Task<bool> ExistsActiveAsync(
        AppDbContext db,
        string companyCode,
        string? branchCode,
        MsRefLookupKind kind,
        string code,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);
        if (normalized is null)
        {
            return false;
        }

        var branch = string.IsNullOrWhiteSpace(branchCode) ? null : branchCode.Trim();

        if (kind == MsRefLookupKind.Department)
        {
            var query = db.MsDepts.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode && x.DeptCode == normalized && x.IsActive);
            if (branch is not null)
            {
                query = query.Where(x => x.BranchCode == branch);
            }

            return await query.AnyAsync(cancellationToken);
        }

        var projectQuery = db.MsProjects.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode
                && x.ProjCode == normalized
                && x.Status == MsProjectStatus.Active);
        if (branch is not null)
        {
            projectQuery = projectQuery.Where(x => x.BranchCode == branch);
        }

        return await projectQuery.AnyAsync(cancellationToken);
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > MaxCodeLength ? trimmed[..MaxCodeLength] : trimmed;
    }

    /// <summary>Case-insensitive comparison against the currently persisted value.</summary>
    public static bool IsUnchanged(string? priorValue, string? incomingValue)
    {
        var prior = Normalize(priorValue);
        var incoming = Normalize(incomingValue);
        return prior is not null
            && incoming is not null
            && string.Equals(prior, incoming, StringComparison.OrdinalIgnoreCase);
    }

    private static string MessageFor(MsRefLookupKind kind, string code) =>
        kind == MsRefLookupKind.Department
            ? $"Department '{code}' does not exist or is not active."
            : $"Project '{code}' does not exist or is not active.";
}
