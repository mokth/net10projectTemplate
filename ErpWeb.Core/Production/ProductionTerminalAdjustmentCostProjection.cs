using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

internal sealed record ProductionTerminalAdjustmentCostStep(
    long MovementId,
    decimal BaseQty,
    bool IsInbound = false,
    string CostBasisCode = ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost,
    decimal? FixedTotalValue = null);

internal sealed record ProductionTerminalAdjustmentProjectedCost(
    decimal RemainingBaseQty,
    decimal RemainingValue,
    IReadOnlyDictionary<long, decimal> OutboundValues,
    IReadOnlyDictionary<long, int> GenerationOffsets,
    int FinalGenerationOffset);

/// <summary>
/// Replays an isolated Production pool with one source receipt (ISSUE or PRODUCE)
/// and terminal ADJUST_OUT / ADJUST_IN adjustments in canonical posting order across
/// all approved PADJ cost authorities.
/// </summary>
internal static class ProductionTerminalAdjustmentCostProjection
{
    public static ProductionTerminalAdjustmentProjectedCost Project(
        decimal issueBaseQty, decimal issueValue,
        IReadOnlyList<ProductionTerminalAdjustmentCostStep> orderedAdjustments)
    {
        ArgumentNullException.ThrowIfNull(orderedAdjustments);
        if (issueBaseQty <= 0m || issueValue < 0m
            || StockLedgerPrecision.Money(issueValue) != issueValue
            || orderedAdjustments.Count == 0
            || orderedAdjustments.Any(a => a.MovementId <= 0 || a.BaseQty <= 0m)
            || orderedAdjustments.Select(a => a.MovementId).Distinct().Count()
                != orderedAdjustments.Count)
            throw Invalid("Production terminal adjustment projection has invalid inputs.");

        var remainingQty = issueBaseQty;
        var remainingValue = issueValue;
        var netInboundValue = 0m;
        var netOutboundValue = 0m;
        var generationOffset = 0;
        var values = new Dictionary<long, decimal>(orderedAdjustments.Count);
        var generationOffsets = new Dictionary<long, int>(orderedAdjustments.Count);
        foreach (var adjustment in orderedAdjustments)
        {
            if (!adjustment.IsInbound)
            {
                if (adjustment.CostBasisCode is not ("POOLED_AVERAGE"
                        or ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost)
                    || remainingQty <= 0m || adjustment.BaseQty > remainingQty)
                    throw Invalid($"Production ADJUST_OUT {adjustment.MovementId} exceeds its preceding pool quantity or has an unsupported basis.");
                var cost = ProductionStockAdjustmentMath.ProportionalTotal(
                    remainingQty, remainingValue, adjustment.BaseQty);
                remainingQty = StockLedgerPrecision.Quantity(
                    remainingQty - adjustment.BaseQty);
                remainingValue = StockLedgerPrecision.Money(remainingValue - cost);
                netOutboundValue = StockLedgerPrecision.Money(netOutboundValue + cost);
                if (remainingValue < 0m || remainingQty == 0m && remainingValue != 0m)
                    throw Invalid($"Production ADJUST_OUT {adjustment.MovementId} strands pool value.");
                values.Add(adjustment.MovementId, cost);
                generationOffsets.Add(adjustment.MovementId, generationOffset);
                continue;
            }

            if (remainingQty == 0m && remainingValue == 0m)
                generationOffset++;
            decimal inboundCost;
            switch (adjustment.CostBasisCode)
            {
                case ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost:
                    if (remainingQty <= 0m)
                        throw Invalid($"Production ADJUST_IN {adjustment.MovementId} requires positive preceding pool quantity for CURRENT_PRODUCTION_POOL_COST.");
                    inboundCost = ProductionStockAdjustmentMath.InboundPoolTotal(
                        remainingQty, remainingValue, adjustment.BaseQty);
                    break;
                case ProductionStockAdjustmentCostBasisCodes.OriginalIssueCost:
                    if (remainingQty != 0m)
                        throw Invalid($"Production ADJUST_IN {adjustment.MovementId} requires an empty pool for ORIGINAL_ISSUE_COST.");
                    inboundCost = StockLedgerPrecision.Money(
                        issueValue * adjustment.BaseQty / issueBaseQty);
                    break;
                case ProductionStockAdjustmentCostBasisCodes.StandardCost:
                case InventoryCostEvidenceTypes.ManualApproved:
                    if (adjustment.FixedTotalValue is not decimal fixedValue
                        || fixedValue < 0m
                        || StockLedgerPrecision.Money(fixedValue) != fixedValue)
                        throw Invalid($"Production ADJUST_IN {adjustment.MovementId} lacks a valid fixed cost authority.");
                    inboundCost = fixedValue;
                    break;
                case InventoryCostEvidenceTypes.ZeroCostApproved:
                    if (adjustment.FixedTotalValue != 0m)
                        throw Invalid($"Production ADJUST_IN {adjustment.MovementId} with ZERO_COST_APPROVED must have zero cost.");
                    inboundCost = 0m;
                    break;
                default:
                    throw Invalid($"Production ADJUST_IN {adjustment.MovementId} has unsupported cost basis '{adjustment.CostBasisCode}'.");
            }
            remainingQty = StockLedgerPrecision.Quantity(
                remainingQty + adjustment.BaseQty);
            remainingValue = StockLedgerPrecision.Money(remainingValue + inboundCost);
            netInboundValue = StockLedgerPrecision.Money(netInboundValue + inboundCost);
            if (remainingValue < 0m)
                throw Invalid($"Production ADJUST_IN {adjustment.MovementId} produced negative pool value.");
            values.Add(adjustment.MovementId, inboundCost);
            generationOffsets.Add(adjustment.MovementId, generationOffset);
        }
        if (StockLedgerPrecision.Money(issueValue + netInboundValue - netOutboundValue)
            != remainingValue)
            throw Invalid("Production terminal adjustments do not conserve pool value.");
        return new(remainingQty, remainingValue, values, generationOffsets, generationOffset);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
