using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Admin;

/// <summary>
/// DB-level assignment helpers shared by <see cref="AdLookupService"/> and domain services that need the
/// same semantics without taking a dependency on the maintenance service (for example the supplier lookup
/// service, which is also constructed directly by tests).
/// </summary>
/// <remarks>
/// These are assignment reads only. They are deliberately ungated by <c>ADMIN_LOOKUPS</c>: business users
/// must be able to populate and validate dropdowns without lookup-master maintenance rights. The tenant
/// company scope is always supplied by the caller and is never broadened here.
/// </remarks>
public static class AdLookupAssignments
{
    /// <summary>Active values for one family, plus the currently bound legacy/inactive value when supplied.</summary>
    public static async Task<IReadOnlyList<IvCodeLookupRow>> ListAsync(
        AppDbContext db,
        string companyCode,
        string lookupType,
        string? existingCode,
        CancellationToken cancellationToken = default)
    {
        var definition = AdLookupTypeRegistry.Find(lookupType);
        if (definition is null || string.IsNullOrWhiteSpace(companyCode))
        {
            return [];
        }

        var values = await db.AdLookupValues
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.LookupType == definition.Code)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Code)
            .Select(x => new AssignmentRow(x.Code, x.Description, x.IsActive))
            .ToListAsync(cancellationToken);

        var rows = values.Where(x => x.IsActive).ToList();

        var legacy = (existingCode ?? string.Empty).Trim();
        if (legacy.Length > 0 && !rows.Any(x => string.Equals(x.Code, legacy, StringComparison.OrdinalIgnoreCase)))
        {
            // Present the currently bound inactive/legacy value so an edit screen can display it. It is
            // appended (not sorted into place) to make it obvious it is a retained legacy binding.
            var stored = values.FirstOrDefault(x => string.Equals(x.Code, legacy, StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                rows.Add(stored);
            }
        }

        return rows
            .Select(x => new IvCodeLookupRow { Code = x.Code, Desc = x.Description })
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="incomingCode"/> may be stored. Blank follows <paramref name="allowBlank"/>;
    /// an unchanged existing value (legacy/inactive) survives an unrelated edit; anything new must resolve
    /// to an ACTIVE value and must fit the consumer column. Values are never truncated to fit.
    /// </summary>
    public static async Task<bool> ValidateAsync(
        AppDbContext db,
        string companyCode,
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

        var incoming = incomingCode.Trim();
        var existing = (existingCode ?? string.Empty).Trim();

        if (existing.Length > 0
            && string.Equals(incoming, existing, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var definition = AdLookupTypeRegistry.Find(lookupType);
        if (definition is null || string.IsNullOrWhiteSpace(companyCode))
        {
            // Fail closed: an unknown family cannot validate anything.
            return false;
        }

        // Never truncate to fit — an over-width pickup is simply invalid for this consumer.
        if (incoming.Length > definition.MaxCodeLength)
        {
            return false;
        }

        var codes = await db.AdLookupValues
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode
                        && x.LookupType == definition.Code
                        && x.IsActive)
            .Select(x => x.Code)
            .ToListAsync(cancellationToken);

        return codes.Any(x => string.Equals(x, incoming, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Local projection so the inactive flag is available without changing the shared row type.</summary>
    private sealed record AssignmentRow(string Code, string Description, bool IsActive);
}
