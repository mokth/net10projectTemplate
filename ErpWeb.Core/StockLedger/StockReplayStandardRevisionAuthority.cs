using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Binds every proposed Standard replay event to its approved event-date revision.
/// Call before simulating or writing an affected tail; caller rechecks under the
/// branch lock if any policy or revision evidence changes after planning.
/// </summary>
public static class StockReplayStandardRevisionAuthority
{
    public static async Task ValidateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string itemCode,
        StockReplayStandardEvent candidate,
        IReadOnlyCollection<StockReplayStandardEvent> laterActiveEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        if (string.IsNullOrWhiteSpace(companyCode)
            || string.IsNullOrWhiteSpace(branchCode)
            || string.IsNullOrWhiteSpace(itemCode))
            throw new ArgumentException("A Standard replay needs an exact tenant and item scope.");

        var company = companyCode.Trim();
        var branch = branchCode.Trim();
        var item = itemCode.Trim();
        var events = new[] { candidate }.Concat(laterActiveEvents).ToArray();
        var first = events.Min(e => e.Key.EffectiveAt.Date);
        var last = events.Max(e => e.Key.EffectiveAt.Date);
        var policies = await db.StockCostPolicyRevisions.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.Status == StockCostPolicyStatuses.Active
                && x.EffectiveFrom <= last
                && (x.EffectiveTo == null || x.EffectiveTo > first))
            .OrderBy(x => x.EffectiveFrom).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var cursor = first;
        for (var i = 0; i < policies.Count; i++)
        {
            var policy = policies[i];
            if (i == 0 ? policy.EffectiveFrom > cursor
                : policy.EffectiveFrom != cursor)
                throw PolicyError($"Standard replay has a gap or overlap in the costing policy between {first:yyyy-MM-dd} and {last:yyyy-MM-dd}.");
            if (!string.Equals(policy.CostMethod.Trim(), StockCostMethods.Standard,
                    StringComparison.OrdinalIgnoreCase))
                throw PolicyError($"Standard replay crosses a costing method change on {policy.EffectiveFrom:yyyy-MM-dd}.");
            cursor = policy.EffectiveTo ?? DateTime.MaxValue;
            if (cursor <= policy.EffectiveFrom)
                throw PolicyError($"Standard costing policy {policy.Id} has an invalid date range.");
        }
        if (cursor <= last)
            throw PolicyError($"Standard replay has no policy covering {last:yyyy-MM-dd}.");

        var resolver = new ItemStandardCostResolver();
        var revisions = new Dictionary<DateTime, EffectiveStandardCost>();
        foreach (var e in events)
        {
            var day = e.Key.EffectiveAt.Date;
            if (!revisions.TryGetValue(day, out var revision))
            {
                revision = await resolver.ResolveAsync(db, company, branch, item,
                    day, cancellationToken);
                revisions.Add(day, revision);
            }
            if (e.StandardCostRevisionId != revision.Id
                || e.UnitCost != revision.TotalStandardCost)
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.LedgerMismatch,
                    $"Standard replay event {e.EventId} for {item} on {day:yyyy-MM-dd} does not match approved cost revision {revision.Id}."));
        }
    }

    private static StockLedgerException PolicyError(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.CostPolicyInvalid, message));
}
