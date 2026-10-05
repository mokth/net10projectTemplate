using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Path B: reconstruct historical DP material facts without inventing business reasons.
/// ConsumeQty uses original CONSUME only (not CONSUME - CONSUME_REVERSAL).
/// Handoff uses PrProductionBalLotMovement CONSUME with no WorkOrderMaterialID.
/// </summary>
public static class ProductionOutputMaterialLegacyBackfill
{
    public static async Task<int> BackfillMissingFactsAsync(
        AppDbContext db, DateTime now, string actor, CancellationToken cancellationToken = default)
    {
        var outputs = await db.ProductionOutputs
            .Include(x => x.Materials)
            .Where(x => !x.Materials.Any())
            .ToListAsync(cancellationToken);
        var count = 0;
        foreach (var output in outputs)
        {
            var operation = await db.ProductionWorkOrderOperations
                .Include(x => x.Materials)
                .Include(x => x.RouteStep)
                .SingleOrDefaultAsync(x => x.Uid == output.WorkOrderOperationId, cancellationToken);
            if (operation is null)
                continue;

            var processed = ProductionMaterialExecutionCalc.ProcessedThisPost(
                output.GoodQty, output.ScrapQty, output.RejectQty, output.HoldQty);

            foreach (var material in operation.Materials.OrderBy(x => x.Uid))
            {
                var standard = ProductionMaterialExecutionCalc.DailyProductionStandardQty(
                    material.RequiredQty, operation.PlannedOutputQty, processed);
                var consume = await db.ProductionMaterialMovements.AsNoTracking()
                    .Where(x => x.ProductionOutputId == output.Uid
                        && x.WorkOrderMaterialId == material.Uid
                        && x.MovementType == ProductionMaterialMovementTypes.Consume)
                    .SumAsync(x => (decimal?)x.Qty, cancellationToken) ?? 0m;
                if (output.Status == ProductionOutputStatuses.New)
                    consume = standard;

                var variance = ProductionMaterialExecutionCalc.DailyProductionVarianceQty(consume, standard);
                db.ProductionOutputMaterials.Add(new ProductionOutputMaterial
                {
                    CompanyCode = output.CompanyCode,
                    BranchCode = output.BranchCode,
                    ProductionOutputId = output.Uid,
                    WorkOrderMaterialId = material.Uid,
                    IsHandoff = false,
                    ComponentCode = material.ComponentCode,
                    RequiredUom = material.RequiredUom ?? string.Empty,
                    SupplySource = material.SupplySource,
                    IssueMethod = material.IssueMethod,
                    TolerancePercent = material.Tolerance,
                    WoBomRequiredQty = material.RequiredQty,
                    ConversionFactorToBase = material.ConversionFactorToBase <= 0m ? 1m : material.ConversionFactorToBase,
                    BaseUom = material.BaseUom,
                    StandardQty = standard,
                    ConsumeQty = consume,
                    VarianceQty = variance,
                    VarianceReasonCode = variance == 0m
                        ? null
                        : ProductionMaterialVarianceReasonCodes.LegacyUnclassified,
                    CreatedDate = now,
                    CreatedBy = actor,
                });
                count++;
            }

            var handoffConsume = await db.ProductionBalLotMovements.AsNoTracking()
                .Where(x => x.ProductionOutputId == output.Uid
                    && x.MovementType == ProductionBalLotMovementTypes.Consume
                    && x.WorkOrderMaterialId == null)
                .SumAsync(x => (decimal?)x.Qty, cancellationToken) ?? 0m;
            if (handoffConsume > 0m || processed > 0m)
            {
                long? priorId = null;
                if (operation.RouteStepId is long routeStepId)
                {
                    var siblings = await db.ProductionWorkOrderOperations.AsNoTracking()
                        .Where(x => x.RouteStepId == routeStepId)
                        .OrderBy(x => x.ProcessSequence)
                        .ThenBy(x => x.Uid)
                        .ToListAsync(cancellationToken);
                    if (ProductionProcessHandoff.TryGetImmediatePrior(siblings, operation, out var prior) is null)
                        priorId = prior?.Uid;
                }

                if (priorId is not null)
                {
                    db.ProductionOutputMaterials.Add(new ProductionOutputMaterial
                    {
                        CompanyCode = output.CompanyCode,
                        BranchCode = output.BranchCode,
                        ProductionOutputId = output.Uid,
                        IsHandoff = true,
                        HandoffFromOperationId = priorId,
                        ComponentCode = operation.RouteStep!.OutputItemCode,
                        RequiredUom = operation.PlannedOutputUom ?? operation.RouteStep.OutputUom ?? output.OutputUom,
                        SupplySource = ProductionProcessHandoff.SupplySource,
                        IssueMethod = ProductionProcessHandoff.IssueMethod,
                        ConversionFactorToBase = 1m,
                        StandardQty = processed,
                        ConsumeQty = handoffConsume > 0m ? handoffConsume : processed,
                        VarianceQty = 0m,
                        CreatedDate = now,
                        CreatedBy = actor,
                    });
                    count++;
                }
            }
        }

        if (count > 0)
            await db.SaveChangesAsync(cancellationToken);
        return count;
    }
}
