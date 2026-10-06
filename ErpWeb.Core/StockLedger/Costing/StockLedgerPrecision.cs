namespace ErpWeb.Core.StockLedger.Costing;

/// <summary>Shared monetary, quantity and FX precision for valuation calculations.</summary>
public static class StockLedgerPrecision
{
    public const int MoneyScale = 6;
    public const int QuantityScale = 6;
    public const int RateScale = 8;

    public static decimal Money(decimal value) =>
        decimal.Round(value, MoneyScale, MidpointRounding.AwayFromZero);

    public static decimal Quantity(decimal value) =>
        decimal.Round(value, QuantityScale, MidpointRounding.AwayFromZero);

    public static decimal Rate(decimal value) =>
        decimal.Round(value, RateScale, MidpointRounding.AwayFromZero);
}
