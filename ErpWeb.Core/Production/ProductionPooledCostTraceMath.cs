using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;

namespace ErpWeb.Core.Production;

/// <summary>
/// Pure arithmetic used by the finished-good cost trace. This helper deliberately has no
/// dependency on EF, posting services, or current costing state.
/// </summary>
public static class ProductionPooledCostTraceMath
{
    public sealed record ComponentAtom(
        string Key,
        string ComponentType,
        decimal Amount);

    public sealed record ComponentAllocation(
        string Key,
        string ComponentType,
        decimal Amount);

    public sealed record AllocationResult(
        decimal OutboundValue,
        IReadOnlyList<ComponentAllocation> Allocations);

    /// <summary>
    /// Allocates a scalar pooled value to its component atoms for one outbound quantity.
    /// The largest atom receives the exact 6-decimal residual. Ties are resolved by key so
    /// replay is deterministic across database providers and query orderings.
    /// </summary>
    public static AllocationResult Allocate(
        decimal poolBaseQty,
        decimal poolValue,
        decimal outboundBaseQty,
        IReadOnlyCollection<ComponentAtom> atoms)
    {
        var poolQty = IvQty.Round(poolBaseQty);
        var outboundQty = IvQty.Round(outboundBaseQty);
        if (poolQty <= 0m || poolValue < 0m || outboundQty <= 0m || outboundQty > poolQty)
            throw new InvalidOperationException("Invalid production pool allocation quantity or value.");

        var outboundValue = outboundQty == poolQty
            ? StockLedgerPrecision.Money(poolValue)
            : StockLedgerPrecision.Money(poolValue * outboundQty / poolQty);

        return Allocate(poolBaseQty, poolValue, outboundBaseQty, outboundValue, atoms);
    }

    /// <summary>
    /// Allocates an explicitly frozen outbound value to component atoms. The explicit value is
    /// used by historical replay so stored movement values remain authoritative.
    /// </summary>
    public static AllocationResult Allocate(
        decimal poolBaseQty,
        decimal poolValue,
        decimal outboundBaseQty,
        decimal outboundValue,
        IReadOnlyCollection<ComponentAtom> atoms)
    {
        var poolQty = IvQty.Round(poolBaseQty);
        var outboundQty = IvQty.Round(outboundBaseQty);
        var totalValue = StockLedgerPrecision.Money(poolValue);
        var exactOutbound = StockLedgerPrecision.Money(outboundValue);

        if (poolQty <= 0m || totalValue < 0m || outboundQty <= 0m || outboundQty > poolQty
            || exactOutbound < 0m || exactOutbound > totalValue)
            throw new InvalidOperationException("Invalid production pool allocation quantity or value.");

        var source = atoms.ToArray();
        if (source.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != source.Length)
            throw new InvalidOperationException("Production trace component atom keys must be unique.");
        if (source.Any(x => string.IsNullOrWhiteSpace(x.Key)
            || string.IsNullOrWhiteSpace(x.ComponentType)
            || x.Amount < 0m))
            throw new InvalidOperationException("Production trace component atoms are invalid.");

        var atomTotal = StockLedgerPrecision.Money(source.Sum(x => x.Amount));
        if (atomTotal != totalValue)
            throw new InvalidOperationException("Production trace component atoms do not reconcile to pool value.");

        if (exactOutbound == 0m)
        {
            return new AllocationResult(
                exactOutbound,
                source.Select(x => new ComponentAllocation(x.Key, x.ComponentType, 0m)).ToArray());
        }

        var positive = source.Where(x => x.Amount > 0m)
            .OrderByDescending(x => x.Amount)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
        if (positive.Length == 0)
            throw new InvalidOperationException("A positive production trace value has no component atom.");

        var residualKey = positive[0].Key;
        var nonResidual = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var allocated = 0m;
        foreach (var atom in source.Where(x => x.Key != residualKey)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var raw = exactOutbound * atom.Amount / totalValue;
            var amount = StockLedgerPrecision.Money(raw);
            amount = Math.Min(amount, StockLedgerPrecision.Money(atom.Amount));
            amount = Math.Min(amount, StockLedgerPrecision.Money(exactOutbound - allocated));
            nonResidual.Add(atom.Key, amount);
            allocated = StockLedgerPrecision.Money(allocated + amount);
        }

        var residual = StockLedgerPrecision.Money(exactOutbound - allocated);
        var residualAtom = source.Single(x => x.Key == residualKey);
        if (residual < 0m || residual > StockLedgerPrecision.Money(residualAtom.Amount))
            throw new InvalidOperationException("Production trace component residual exceeded its atom.");

        var allocations = source.Select(atom => new ComponentAllocation(
            atom.Key,
            atom.ComponentType,
            atom.Key == residualKey ? residual : nonResidual[atom.Key])).ToList();

        // The residual atom is encountered according to the input order, so calculate the
        // final total independently of that order and fail closed if the caller supplied an
        // atom that cannot receive the residual.
        var sum = StockLedgerPrecision.Money(allocations.Sum(x => x.Amount));
        if (sum != exactOutbound)
            throw new InvalidOperationException("Production trace component allocation did not reconcile.");

        return new AllocationResult(exactOutbound, allocations);
    }

    public static IReadOnlyList<ComponentAtom> Subtract(
        IReadOnlyCollection<ComponentAtom> atoms,
        IReadOnlyCollection<ComponentAllocation> allocations)
    {
        var byKey = allocations.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var result = new List<ComponentAtom>(atoms.Count);
        foreach (var atom in atoms)
        {
            byKey.TryGetValue(atom.Key, out var allocation);
            var remaining = StockLedgerPrecision.Money(atom.Amount - (allocation?.Amount ?? 0m));
            if (remaining < 0m)
                throw new InvalidOperationException("Production trace component balance became negative.");
            result.Add(atom with { Amount = remaining });
        }

        var sourceValue = StockLedgerPrecision.Money(atoms.Sum(x => x.Amount));
        var resultValue = StockLedgerPrecision.Money(result.Sum(x => x.Amount));
        var allocatedValue = StockLedgerPrecision.Money(allocations.Sum(x => x.Amount));
        if (StockLedgerPrecision.Money(sourceValue - allocatedValue) != resultValue)
            throw new InvalidOperationException("Production trace component balance did not reconcile.");
        return result;
    }
}
