using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Pure, database-free rules for deciding how Sales Order demand is presented to
/// production planning.  This class deliberately contains no persistence or UI
/// concerns so the same rules can be exercised by SO, DR, and fulfilment paths.
/// </summary>
public static class SaProductionDemandRules
{
    public const decimal QuantityTolerance = 0.0001m;

    public const string StockOrPurchased = "STOCK_OR_PURCHASED";
    public const string AutoProduction = "AUTO_PRODUCTION";
    public const string SetupRequired = "SETUP_REQUIRED";
    public const string InternalPhantom = "INTERNAL_PHANTOM";

    public const string LegacyConversionUnresolvedMessage =
        "The Sales Order line has no safely resolvable production UOM conversion. Review its frozen quantity snapshot before planning production demand.";

    /// <summary>
    /// Maps the persisted item supply method and current Product Definition count
    /// to the read-only production-demand state shown to Sales users.
    /// </summary>
    public static string DetermineState(string? mfgType, int activeDefinitionCount)
    {
        var normalized = PrMfgTypes.Normalize(mfgType);
        return normalized switch
        {
            PrMfgTypes.Make when activeDefinitionCount > 0 => AutoProduction,
            PrMfgTypes.Make => SetupRequired,
            PrMfgTypes.Phantom => InternalPhantom,
            _ => StockOrPurchased
        };
    }

    public static bool IsEligibleSalesOrderStatus(string? status) =>
        string.Equals(status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, SaSoStatuses.Shipped, StringComparison.OrdinalIgnoreCase);

    public static bool IsDirectProductionCandidate(
        string? status,
        string? mfgType,
        int activeDefinitionCount) =>
        IsEligibleSalesOrderStatus(status)
        && string.Equals(PrMfgTypes.Normalize(mfgType), PrMfgTypes.Make, StringComparison.Ordinal)
        && activeDefinitionCount > 0;

    /// <summary>
    /// Resolves the frozen Sales Order production conversion without treating a
    /// legacy zero <c>StdPsize</c> as a factor of one when a real snapshot ratio
    /// is available.
    /// </summary>
    public static decimal? ResolveFrozenStdFactor(
        decimal stdPsize,
        decimal orderQty,
        decimal stdQty)
    {
        if (stdPsize > QuantityTolerance)
        {
            return stdPsize;
        }

        if (orderQty > QuantityTolerance && stdQty > QuantityTolerance)
        {
            return stdQty / orderQty;
        }

        return null;
    }

    public static decimal ToProductionQty(decimal salesQty, decimal factor)
    {
        EnsureResolvedFactor(factor);
        return SaSoQty.RoundQty(salesQty * factor);
    }

    public static decimal ToSalesQty(decimal productionQty, decimal factor)
    {
        EnsureResolvedFactor(factor);
        return SaSoQty.RoundQty(productionQty / factor);
    }

    private static void EnsureResolvedFactor(decimal factor)
    {
        if (factor <= QuantityTolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), LegacyConversionUnresolvedMessage);
        }
    }
}
