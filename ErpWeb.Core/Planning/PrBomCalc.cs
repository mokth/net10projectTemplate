using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Planning;

/// <summary>
/// Product Definition / BOM quantity contract.
/// <para>
/// Line <c>StdQty</c> is the component quantity required to produce header <c>BaseQty</c>
/// of the finished product. Tolerance does not participate.
/// </para>
/// <para>
/// Production Orders must <b>copy</b> BOM lines at creation — do not dynamically re-read current PrDefBOM.
/// </para>
/// </summary>
public static class PrBomCalc
{
    /// <summary>
    /// Required component quantity for a production run (BaseQty = 1, no scrap).
    /// Preserved for single-level callers.
    /// </summary>
    public static decimal RequiredQty(decimal productionQty, decimal stdQty) =>
        RequiredQty(productionQty, baseQty: 1m, stdQty, scrapPercent: 0m);

    /// <summary>
    /// Required component quantity:
    /// <c>IvQty.Round(ProductionQty / BaseQty × StdQty × (1 + ScrapPercent/100))</c>.
    /// Does not round StdQty/BaseQty before multiply. Does not use Tolerance.
    /// </summary>
    public static decimal RequiredQty(
        decimal productionQty,
        decimal baseQty,
        decimal stdQty,
        decimal scrapPercent)
    {
        if (baseQty <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(baseQty), "Base quantity must be greater than zero.");
        }

        var scrapFactor = 1m + (scrapPercent / 100m);
        return IvQty.Round(productionQty / baseQty * stdQty * scrapFactor);
    }
}
