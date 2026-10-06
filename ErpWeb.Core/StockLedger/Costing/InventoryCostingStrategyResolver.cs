namespace ErpWeb.Core.StockLedger.Costing;

public sealed class InventoryCostingStrategyResolver
{
    private readonly IReadOnlyDictionary<string, IInventoryCostingStrategy> _strategies;

    public InventoryCostingStrategyResolver(IEnumerable<IInventoryCostingStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        _strategies = strategies
            .ToDictionary(x => x.CostMethod, StringComparer.OrdinalIgnoreCase);
    }

    public IInventoryCostingStrategy Resolve(string costMethod)
    {
        if (_strategies.TryGetValue((costMethod ?? string.Empty).Trim(), out var strategy))
            return strategy;

        throw new StockLedgerException(new(
            StockLedgerErrorCodes.CostPolicyInvalid,
            $"No inventory costing strategy is registered for '{costMethod}'."));
    }
}
