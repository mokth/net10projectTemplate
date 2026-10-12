using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Quantity in RequiredUom and base quantity are independent, frozen facts.</summary>
public sealed record ProductionMaterialExecutionFact(
    long MaterialMovementId, string MovementType, decimal Qty, decimal BaseQty,
    long? OriginalMovementId = null, long? ReversesMaterialMovementId = null,
    long? ProductionBalLotMovementId = null, string? ProductionBalLotMovementType = null);

public sealed record ProductionMaterialExecutionTotals(
    decimal EffectiveIssuedQty = 0m, decimal EffectiveReturnedQty = 0m,
    decimal EffectiveConsumedQty = 0m, decimal NetActiveAdjustmentQty = 0m,
    decimal EffectiveIssuedBaseQty = 0m, decimal EffectiveReturnedBaseQty = 0m,
    decimal EffectiveConsumedBaseQty = 0m, decimal NetActiveAdjustmentBaseQty = 0m)
{
    public static readonly ProductionMaterialExecutionTotals Empty = new();
    public decimal EffectiveSupplyQty => IvQty.Round(EffectiveIssuedQty - EffectiveReturnedQty + NetActiveAdjustmentQty);
    public decimal EffectiveSupplyBaseQty => IvQty.Round(EffectiveIssuedBaseQty - EffectiveReturnedBaseQty + NetActiveAdjustmentBaseQty);
    public decimal RemainingProductionMaterialQty => IvQty.Round(EffectiveSupplyQty - EffectiveConsumedQty);
    public decimal RemainingProductionMaterialBaseQty => IvQty.Round(EffectiveSupplyBaseQty - EffectiveConsumedBaseQty);
    public decimal OutstandingQty(decimal requiredQty) => IvQty.Round(Math.Max(requiredQty - EffectiveSupplyQty, 0m));
}

public static class ProductionMaterialExecutionFacts
{
    public static ProductionMaterialExecutionTotals Calculate(IEnumerable<ProductionMaterialExecutionFact> source)
    {
        var rows = source.ToArray();
        var qty = ProductionMaterialMovementTotals.FromRows(rows.Select(x => (x.MovementType, x.Qty)));
        var bases = ProductionMaterialMovementTotals.FromRows(rows.Select(x => (x.MovementType, x.BaseQty)));
        var adjustments = rows.Where(x => x.MovementType == ProductionMaterialMovementTypes.Adjustment).ToArray();
        var reversed = new HashSet<long>();
        foreach (var reversal in adjustments.Where(x => x.ReversesMaterialMovementId.HasValue))
        {
            var original = adjustments.SingleOrDefault(x => x.MaterialMovementId == reversal.ReversesMaterialMovementId);
            if (original is null || original.ReversesMaterialMovementId.HasValue
                || !reversed.Add(original.MaterialMovementId) || reversal.Qty != original.Qty || reversal.BaseQty != original.BaseQty
                || !IsInverse(original.ProductionBalLotMovementType, reversal.ProductionBalLotMovementType))
                throw new InvalidOperationException("Material adjustment reversal lineage is incomplete or inconsistent.");
        }
        var adjustmentQty = 0m;
        var adjustmentBaseQty = 0m;
        foreach (var adjustment in adjustments.Where(x => x.ReversesMaterialMovementId is null && !reversed.Contains(x.MaterialMovementId)))
        {
            if (adjustment.ProductionBalLotMovementId is null || adjustment.Qty <= 0m || adjustment.BaseQty <= 0m)
                throw new InvalidOperationException("Material adjustment requires its exact Production movement and positive quantity magnitudes.");
            var direction = adjustment.ProductionBalLotMovementType switch
            {
                ProductionBalLotMovementTypes.AdjustIn => 1,
                ProductionBalLotMovementTypes.AdjustOut => -1,
                _ => throw new InvalidOperationException("Material adjustment direction must come from its linked ADJUST_IN/ADJUST_OUT movement.")
            };
            adjustmentQty += direction * adjustment.Qty;
            adjustmentBaseQty += direction * adjustment.BaseQty;
        }
        return new(qty.Issued, qty.Returned, qty.Consumed, IvQty.Round(adjustmentQty),
            bases.Issued, bases.Returned, bases.Consumed, IvQty.Round(adjustmentBaseQty));
    }

    private static bool IsInverse(string? original, string? reversal) =>
        (original, reversal) is (ProductionBalLotMovementTypes.AdjustIn, ProductionBalLotMovementTypes.AdjustOut)
            or (ProductionBalLotMovementTypes.AdjustOut, ProductionBalLotMovementTypes.AdjustIn);

    public static async Task<IReadOnlyDictionary<long, ProductionMaterialExecutionTotals>> ReadAsync(
        AppDbContext db, string company, string branch, IReadOnlyCollection<long> materialIds, CancellationToken ct)
    {
        var rows = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && materialIds.Contains(x.WorkOrderMaterialId))
            .Select(x => new { x.WorkOrderMaterialId, x.Uid, x.MovementType, x.Qty, x.BaseQty, x.OriginalMovementId,
                x.ReversesMaterialMovementId, x.ProductionBalLotMovementId,
                BalanceMovementType = x.ProductionBalLotMovement == null ? null : x.ProductionBalLotMovement.MovementType })
            .ToListAsync(ct);
        return rows.GroupBy(x => x.WorkOrderMaterialId).ToDictionary(g => g.Key,
            g => Calculate(g.Select(x => new ProductionMaterialExecutionFact(x.Uid, x.MovementType, x.Qty, x.BaseQty,
                x.OriginalMovementId, x.ReversesMaterialMovementId, x.ProductionBalLotMovementId, x.BalanceMovementType))));
    }
}
