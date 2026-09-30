using ErpWeb.Core.Inventory;
using ErpWeb.Core.Planning;

namespace ErpWeb.Core.Production;

public static class ProductionWorkOrderCalc
{
    public static decimal RequiredQty(
        decimal plannedOutputQty,
        decimal bomOutputQty,
        decimal componentQty,
        decimal scrapPercent) =>
        PrBomCalc.RequiredQty(plannedOutputQty, bomOutputQty, componentQty, scrapPercent);

    public static decimal NetIssuedQty(decimal issuedQty, decimal returnedQty) =>
        IvQty.Round(issuedQty - returnedQty);

    public static decimal OpenRequirementQty(decimal requiredQty, decimal issuedQty, decimal returnedQty) =>
        IvQty.Round(Math.Max(requiredQty - issuedQty + returnedQty, 0m));

    public static decimal OpenProductionQty(
        decimal plannedQty,
        decimal goodQty,
        decimal approvedVarianceQty) =>
        IvQty.Round(Math.Max(plannedQty - goodQty - approvedVarianceQty, 0m));

    public static decimal OperationProcessedQty(
        decimal goodQty,
        decimal scrapQty,
        decimal rejectQty,
        decimal holdQty,
        decimal reworkQty) =>
        IvQty.Round(goodQty + scrapQty + rejectQty + holdQty + reworkQty);

    public static decimal OperationRemainingQty(decimal inputQty, decimal processedQty) =>
        IvQty.Round(Math.Max(inputQty - processedQty, 0m));

    public static decimal AvailableForNextOperation(
        decimal goodQty,
        decimal releasedHoldQty,
        decimal acceptedReworkOutputQty,
        decimal transferredQty) =>
        IvQty.Round(Math.Max(goodQty + releasedHoldQty + acceptedReworkOutputQty - transferredQty, 0m));

    public static decimal ProgressPercent(decimal numerator, decimal denominator)
    {
        if (denominator <= 0m)
        {
            return 0m;
        }

        return decimal.Round(Math.Clamp(numerator / denominator * 100m, 0m, 100m), 2,
            MidpointRounding.AwayFromZero);
    }
}

