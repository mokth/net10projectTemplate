using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed record ProductionEffectiveCost(
    long MovementId,
    decimal TotalCost,
    decimal UnitCost,
    long? RestatementFactId);

/// <summary>Reads the active effective cost of immutable Production movements in one batch.</summary>
public static class ProductionEffectiveCostResolver
{
    public static async Task<IReadOnlyDictionary<long, ProductionEffectiveCost>> ResolveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<long> movementIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(movementIds);
        if (movementIds.Count == 0)
            return new Dictionary<long, ProductionEffectiveCost>();

        var ids = movementIds.Distinct().ToArray();
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.CompanyCode == companyCode && m.BranchCode == branchCode
                        && ids.Contains(m.Uid))
            .Select(m => new ProductionEffectiveCost(m.Uid, m.TotalCost, m.UnitCost, null))
            .ToArrayAsync(cancellationToken);
        if (movements.Length != ids.Length)
            throw Corrupt("Production movement identity does not match the requested tenant.");

        var facts = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(f => f.CompanyCode == companyCode && f.BranchCode == branchCode
                        && ids.Contains(f.ProductionBalLotMovementId))
            .ToArrayAsync(cancellationToken);
        return Resolve(movements, facts);
    }

    public static IReadOnlyDictionary<long, ProductionEffectiveCost> Resolve(
        IReadOnlyCollection<ProductionEffectiveCost> originalCosts,
        IReadOnlyCollection<ProductionCostRestatementFact> facts)
    {
        ArgumentNullException.ThrowIfNull(originalCosts);
        ArgumentNullException.ThrowIfNull(facts);
        var originals = new Dictionary<long, ProductionEffectiveCost>();
        foreach (var original in originalCosts)
        {
            if (original.MovementId <= 0 || !originals.TryAdd(original.MovementId, original))
                throw Corrupt("Production movement cost input has duplicate or invalid identity.");
        }
        foreach (var group in facts.GroupBy(f => f.ProductionBalLotMovementId))
        {
            if (!originals.TryGetValue(group.Key, out var original))
                throw Corrupt($"Restatement references missing Production movement {group.Key}.");

            var rows = group.ToArray();
            var ids = rows.Select(f => f.Id).ToHashSet();
            if (ids.Count != rows.Length || ids.Contains(0))
                throw Corrupt($"Production movement {group.Key} has duplicate restatement IDs.");
            var byId = rows.ToDictionary(f => f.Id);
            var reversedIds = new HashSet<long>();
            foreach (var fact in rows)
            {
                if (fact.CompanyCode != rows[0].CompanyCode
                    || fact.BranchCode != rows[0].BranchCode
                    || fact.CorrectedEffectiveTotalCost < 0m
                    || fact.CorrectedUnitCost < 0m
                    || fact.DeltaCost != fact.CorrectedEffectiveTotalCost - fact.PriorEffectiveTotalCost)
                    throw Corrupt($"Production movement {group.Key} has invalid restatement {fact.Id}.");
                if (fact.ReversesRestatementFactId is long reversed)
                {
                    if (!ids.Contains(reversed) || !reversedIds.Add(reversed)
                        || byId[reversed].ReversesRestatementFactId is not null
                        || fact.PriorEffectiveTotalCost
                            != byId[reversed].CorrectedEffectiveTotalCost
                        || fact.CorrectedEffectiveTotalCost
                            != byId[reversed].PriorEffectiveTotalCost
                        || fact.CorrectedUnitCost != original.UnitCost)
                        throw Corrupt($"Production movement {group.Key} has invalid reversal {fact.Id}.");
                }
                else if (fact.PriorEffectiveTotalCost != original.TotalCost)
                    throw Corrupt($"Production movement {group.Key} has invalid prior cost in restatement {fact.Id}.");
            }

            var active = rows.Where(f => f.ReversesRestatementFactId is null
                                         && !reversedIds.Contains(f.Id)).ToArray();
            if (active.Length > 1)
                throw Corrupt($"Production movement {group.Key} has ambiguous active effective cost.");
            if (active.Length == 0)
                continue;

            var value = active[0];
            originals[group.Key] = new ProductionEffectiveCost(
                group.Key, value.CorrectedEffectiveTotalCost, value.CorrectedUnitCost, value.Id);
        }
        return originals;
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
