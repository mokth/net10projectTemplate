using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;

namespace ErpWeb.Core.Production;

public sealed record ProductionContribution(
    long BalanceId,
    long ReceiptMovementId,
    DateTime EffectiveAt,
    long PostingSequence,
    int PostingLineNo,
    decimal AvailableBaseQty);

public sealed record ProductionContributionDemand(string SourceLineId, decimal BaseQty);
public sealed record ProductionContributionAllocation(
    string SourceLineId, long BalanceId, long ReceiptMovementId, decimal BaseQty);

public interface IProductionContributionAllocator
{
    IReadOnlyList<ProductionContributionAllocation> BuildPlan(
        IReadOnlyCollection<ProductionContribution> contributions,
        IReadOnlyCollection<ProductionContributionDemand> demands);
}

/// <summary>Pure FIFO allocator. One mutable budget per receipt prevents pooled stock being spent twice.</summary>
public sealed class ProductionContributionAllocator : IProductionContributionAllocator
{
    public IReadOnlyList<ProductionContributionAllocation> BuildPlan(
        IReadOnlyCollection<ProductionContribution> contributions,
        IReadOnlyCollection<ProductionContributionDemand> demands)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(demands);

        var ordered = contributions
            .Where(x => x.AvailableBaseQty > 0m)
            .OrderBy(x => x.EffectiveAt)
            .ThenBy(x => x.PostingSequence)
            .ThenBy(x => x.PostingLineNo)
            .ThenBy(x => x.ReceiptMovementId)
            .ToArray();
        var remaining = ordered.ToDictionary(x => x.ReceiptMovementId, x => IvQty.Round(x.AvailableBaseQty));
        var result = new List<ProductionContributionAllocation>();

        foreach (var demand in demands.OrderBy(x => x.SourceLineId, StringComparer.Ordinal))
        {
            var needed = IvQty.Round(demand.BaseQty);
            if (needed <= 0m)
                throw Invalid($"Demand '{demand.SourceLineId}' must have positive base quantity.");

            foreach (var receipt in ordered)
            {
                if (needed <= 0m) break;
                var available = remaining[receipt.ReceiptMovementId];
                if (available <= 0m) continue;
                var take = IvQty.Round(Math.Min(available, needed));
                if (take <= 0m) continue;
                result.Add(new(demand.SourceLineId, receipt.BalanceId, receipt.ReceiptMovementId, take));
                remaining[receipt.ReceiptMovementId] = IvQty.Round(available - take);
                needed = IvQty.Round(needed - take);
            }

            if (needed > 0m)
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.InsufficientBaseQty,
                    $"Insufficient production contribution quantity for '{demand.SourceLineId}' ({needed:n4} short)."));
        }

        return result;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new(StockLedgerErrorCodes.InvalidStockIdentity, message));
}
