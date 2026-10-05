using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public static class FinishedGoodReceiptMath
{
    public static (decimal BaseQty, decimal DestinationQty) Convert(decimal qty, decimal sourceFactor, decimal destinationFactor)
    {
        if (qty <= 0 || qty != IvQty.Round(qty) || sourceFactor <= 0 || destinationFactor <= 0)
            throw new InvalidOperationException("Quantity must be positive with at most four decimals; UOM factors must be positive.");
        var baseQty = IvQty.Round(qty * sourceFactor);
        var destinationQty = IvQty.Round(baseQty / destinationFactor);
        if (baseQty <= 0 || destinationQty <= 0 || IvQty.Round(destinationQty * destinationFactor) != baseQty
            || IvQty.Round(baseQty / sourceFactor) != qty)
            throw new InvalidOperationException("The UOM conversion cannot conserve quantity at four-decimal precision.");
        return (baseQty, destinationQty);
    }

    public static IReadOnlyDictionary<long, decimal> AllocateValue(decimal availableBaseQty, decimal totalValue,
        IReadOnlyCollection<(long Id, decimal BaseQty)> lines)
    {
        var qty = lines.Sum(x => x.BaseQty);
        if (availableBaseQty <= 0 || totalValue < 0 || lines.Count == 0 || lines.Any(x => x.BaseQty <= 0)
            || lines.Select(x => x.Id).Distinct().Count() != lines.Count || qty > availableBaseQty)
            throw new InvalidOperationException("Insufficient source quantity or invalid valuation allocation.");
        var value = qty == availableBaseQty ? totalValue : IvQty.Round(totalValue * qty / availableBaseQty);
        var remaining = value;
        var result = new Dictionary<long, decimal>();
        var ordered = lines.OrderBy(x => x.Id).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var amount = i == ordered.Length - 1 ? remaining : Math.Min(remaining, IvQty.Round(value * ordered[i].BaseQty / qty));
            result.Add(ordered[i].Id, amount); remaining -= amount;
        }
        return result;
    }
}
