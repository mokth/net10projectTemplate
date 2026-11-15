using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger.Costing;

/// <summary>Splits one purchase-price variance using the stock available at its effective time.</summary>
internal static class StockPurchaseVarianceAllocator
{
    internal static (decimal Inventory, decimal Consumed) Allocate(
        decimal variance, decimal settledQuantity, decimal capitalizableQuantity,
        decimal inventoryValue, string costMethod)
    {
        var inventory = variance == 0m || capitalizableQuantity <= 0m
            ? 0m
            : StockLedgerPrecision.Money(variance * capitalizableQuantity / settledQuantity);
        if (costMethod == StockCostMethods.Standard)
            inventory = 0m;
        else if (inventory < 0m)
            inventory = Math.Max(-inventoryValue, inventory);

        inventory = StockLedgerPrecision.Money(inventory);
        return (inventory, StockLedgerPrecision.Money(variance - inventory));
    }
}
