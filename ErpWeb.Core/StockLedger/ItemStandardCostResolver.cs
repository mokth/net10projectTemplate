using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record EffectiveStandardCost(
    string ItemCode,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    decimal TotalStandardCost,
    int Revision,
    long Id);

public interface IItemStandardCostResolver
{
    Task<EffectiveStandardCost> ResolveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string itemCode,
        DateTime effectiveAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves exactly one approved standard-cost revision for a posting date. Overlaps and missing
/// revisions fail closed so a Standard posting cannot silently fall back to PO or item-master data.
/// </summary>
public sealed class ItemStandardCostResolver : IItemStandardCostResolver
{
    public async Task<EffectiveStandardCost> ResolveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string itemCode,
        DateTime effectiveAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = Required(companyCode, nameof(companyCode));
        var branch = Required(branchCode, nameof(branchCode));
        var item = Required(itemCode, nameof(itemCode));
        var date = effectiveAt.Date;

        var rows = await db.Set<ItemStandardCostRevision>().AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.ItemCode == item
                        && x.Status == ItemStandardCostRevisionStatuses.Approved
                        && x.EffectiveFrom <= date
                        && (x.EffectiveTo == null || x.EffectiveTo > date))
            .OrderByDescending(x => x.EffectiveFrom)
            .ThenByDescending(x => x.Revision)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
            throw Error($"No approved Standard Cost revision covers {item} on {date:yyyy-MM-dd}.");
        if (rows.Count > 1)
            throw Error($"Overlapping approved Standard Cost revisions cover {item} on {date:yyyy-MM-dd}.");

        var row = rows[0];
        var componentTotal = row.MaterialCost + row.LabourCost + row.MachineCost
                              + row.OverheadCost + row.SubcontractCost;
        if (row.TotalStandardCost < 0m || Math.Abs(componentTotal - row.TotalStandardCost) > 0.0000005m)
            throw Error($"Standard Cost revision {row.Id} has an invalid component total for {item}.");

        return new EffectiveStandardCost(
            row.ItemCode,
            row.EffectiveFrom,
            row.EffectiveTo,
            decimal.Round(row.TotalStandardCost, 6, MidpointRounding.AwayFromZero),
            row.Revision,
            row.Id);
    }

    private static string Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", name);
        return value.Trim();
    }

    private static StockLedgerException Error(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.CostPolicyInvalid, message));
}
