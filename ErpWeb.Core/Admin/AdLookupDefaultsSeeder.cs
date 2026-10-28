using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Admin;

/// <summary>
/// Idempotent seeding of default lookup values.
///
/// <para>
/// Deliberately static and <see cref="AppDbContext"/>-based: company provisioning must seed inside its
/// existing transaction and context. Opening a second DbContext there was explicitly ruled out by the
/// plan because a failure could leave a half-provisioned company.
/// </para>
/// </summary>
internal static class AdLookupDefaultsSeeder
{
    internal static async Task EnsureDefaultsAsync(
        AppDbContext db,
        string companyCode,
        string actor,
        CancellationToken cancellationToken)
    {
        var company = (companyCode ?? string.Empty).Trim();
        if (company.Length == 0)
        {
            return;
        }

        var stamp = Truncate((actor ?? string.Empty).Trim(), 20);
        if (stamp.Length == 0)
        {
            stamp = "SYSTEM";
        }

        var now = DateTime.UtcNow;

        foreach (var definition in AdLookupTypeRegistry.Maintainable)
        {
            if (definition.DefaultValues is not { Count: > 0 } seeds)
            {
                continue;
            }

            var existing = await db.AdLookupValues
                .Where(x => x.CompanyCode == company && x.LookupType == definition.Code)
                .Select(x => new { x.Code, x.SortOrder })
                .ToListAsync(cancellationToken);

            var known = new HashSet<string>(existing.Select(x => x.Code), StringComparer.OrdinalIgnoreCase);
            var sort = existing.Count == 0 ? 0 : existing.Max(x => x.SortOrder);

            foreach (var seed in seeds)
            {
                if (!known.Add(seed.Code))
                {
                    continue;
                }

                sort += AdLookupDefaults.SeedSortStep;
                db.AdLookupValues.Add(new AdLookupValue
                {
                    CompanyCode = company,
                    LookupType = definition.Code,
                    Code = seed.Code,
                    Description = seed.Description,
                    SortOrder = sort,
                    IsActive = true,
                    CreatedDate = now,
                    CreatedBy = stamp,
                    ModifiedDate = now,
                    ModifiedBy = stamp
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
