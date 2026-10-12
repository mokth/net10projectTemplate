using ErpWeb.Core.StockLedger.Costing;

namespace ErpWeb.Core.Production;

/// <summary>Six-decimal PADJ value rules. Totals are authoritative; unit costs are derived afterward.</summary>
public static class ProductionStockAdjustmentMath
{
    public static decimal ProportionalTotal(decimal poolBaseQty, decimal poolTotalValue, decimal adjustmentBaseQty)
    {
        if (poolBaseQty <= 0m || poolTotalValue < 0m || adjustmentBaseQty <= 0m || adjustmentBaseQty > poolBaseQty)
            throw new ArgumentOutOfRangeException(nameof(adjustmentBaseQty), "Adjustment quantity must be positive and within a non-empty pool.");
        return adjustmentBaseQty == poolBaseQty
            ? StockLedgerPrecision.Money(poolTotalValue)
            : StockLedgerPrecision.Money(poolTotalValue * adjustmentBaseQty / poolBaseQty);
    }

    public static decimal InboundPoolTotal(decimal poolBaseQty, decimal poolTotalValue, decimal addBaseQty)
    {
        if (poolBaseQty <= 0m || poolTotalValue < 0m || addBaseQty <= 0m)
            throw new ArgumentOutOfRangeException(nameof(addBaseQty), "A non-empty verified pool and positive inbound quantity are required.");
        return StockLedgerPrecision.Money(poolTotalValue * addBaseQty / poolBaseQty);
    }

    public static decimal TotalFromUnitCost(decimal baseQty, decimal unitCost)
    {
        if (baseQty <= 0m || unitCost < 0m) throw new ArgumentOutOfRangeException(nameof(baseQty));
        return StockLedgerPrecision.Money(baseQty * unitCost);
    }

    public static decimal UnitCostFromTotal(decimal baseQty, decimal totalValue)
    {
        if (baseQty <= 0m || totalValue < 0m) throw new ArgumentOutOfRangeException(nameof(baseQty));
        return StockLedgerPrecision.Money(totalValue / baseQty);
    }
}
