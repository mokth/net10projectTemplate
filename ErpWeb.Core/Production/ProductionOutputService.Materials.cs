using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionOutputService
{
    private async Task<string?> PersistMaterialFactsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionOutput output,
        ProductionWorkOrderOperation operation,
        IReadOnlyList<ProductionOutputMaterialInput> inputs,
        bool interactive,
        CancellationToken cancellationToken)
    {
        var processed = ProductionMaterialExecutionCalc.ProcessedThisPost(
            output.GoodQty, output.ScrapQty, output.RejectQty, output.HoldQty);
        var woMaterials = operation.Materials.OrderBy(x => x.Uid).ToList();
        var expectedIds = woMaterials.Select(x => x.Uid).ToHashSet();
        var submitted = (inputs ?? []).Where(x => x.WorkOrderMaterialId != 0).ToList();
        if (submitted.Any(x => x.WorkOrderMaterialId <= 0))
            return "Handoff consumption cannot be submitted from the client.";
        if (submitted.Select(x => x.WorkOrderMaterialId).Distinct().Count() != submitted.Count)
            return "Duplicate material lines are not allowed.";

        if (submitted.Count == 0)
        {
            submitted = woMaterials.Select(m => new ProductionOutputMaterialInput
            {
                WorkOrderMaterialId = m.Uid,
                ConsumeQty = ProductionMaterialExecutionCalc.DailyProductionStandardQty(
                    m.RequiredQty, operation.PlannedOutputQty, processed),
            }).ToList();
        }

        var submittedIds = submitted.Select(x => x.WorkOrderMaterialId).ToHashSet();
        if (!submittedIds.SetEquals(expectedIds))
            return "The material list must match the Work Order operation materials exactly.";

        var byId = submitted.ToDictionary(x => x.WorkOrderMaterialId);
        var now = _clock.Now;
        var user = TruncateUser(scope.UserId);

        var existing = await db.ProductionOutputMaterials
            .Where(x => x.ProductionOutputId == output.Uid)
            .ToListAsync(cancellationToken);
        var existingByMaterial = existing
            .Where(x => x.WorkOrderMaterialId is not null)
            .ToDictionary(x => x.WorkOrderMaterialId!.Value);

        foreach (var material in woMaterials)
        {
            var input = byId[material.Uid];
            var standard = ProductionMaterialExecutionCalc.DailyProductionStandardQty(
                material.RequiredQty, operation.PlannedOutputQty, processed);
            var max = ProductionMaterialExecutionCalc.DailyProductionMaxQty(standard, material.Tolerance);
            var consume = IvQty.Round(input.ConsumeQty);
            var blocking = ProductionOutputMaterialSupport.BlockingReason(material);
            if (standard > 0m && blocking is not null)
                return blocking;

            var error = ProductionOutputMaterialFacts.ValidateConsume(
                consume, standard, max, input.VarianceReasonCode, input.VarianceReasonText, interactive);
            if (error is not null)
                return $"{material.ComponentCode}: {error}";

            var (code, text) = ProductionOutputMaterialFacts.NormalizeReason(
                consume, standard, input.VarianceReasonCode, input.VarianceReasonText);
            var variance = ProductionMaterialExecutionCalc.DailyProductionVarianceQty(consume, standard);

            if (!existingByMaterial.TryGetValue(material.Uid, out var row))
            {
                row = new ProductionOutputMaterial
                {
                    CompanyCode = output.CompanyCode,
                    BranchCode = output.BranchCode,
                    ProductionOutputId = output.Uid,
                    CreatedDate = now,
                    CreatedBy = user,
                };
                db.ProductionOutputMaterials.Add(row);
            }
            else
            {
                row.ModifiedDate = now;
                row.ModifiedBy = user;
            }

            row.WorkOrderMaterialId = material.Uid;
            row.IsHandoff = false;
            row.HandoffFromOperationId = null;
            row.ComponentCode = material.ComponentCode;
            row.RequiredUom = material.RequiredUom ?? string.Empty;
            row.SupplySource = material.SupplySource;
            row.IssueMethod = material.IssueMethod;
            row.TolerancePercent = material.Tolerance;
            row.WoBomRequiredQty = material.RequiredQty;
            row.ConversionFactorToBase = material.ConversionFactorToBase <= 0m ? 1m : material.ConversionFactorToBase;
            row.BaseUom = material.BaseUom;
            row.StandardQty = standard;
            row.ConsumeQty = consume;
            row.VarianceQty = variance;
            row.VarianceReasonCode = code;
            row.VarianceReasonText = text;
        }

        foreach (var orphan in existing.Where(x => !x.IsHandoff && x.WorkOrderMaterialId is long id && !expectedIds.Contains(id)))
            db.ProductionOutputMaterials.Remove(orphan);

        var handoffError = await UpsertHandoffFactAsync(
            db, output, operation, processed, existing, now, user, cancellationToken);
        return handoffError;
    }

    private static async Task<string?> UpsertHandoffFactAsync(
        AppDbContext db,
        ProductionOutput output,
        ProductionWorkOrderOperation operation,
        decimal processed,
        List<ProductionOutputMaterial> existing,
        DateTime now,
        string user,
        CancellationToken cancellationToken)
    {
        if (operation.RouteStepId is not long routeStepId)
            return null;

        var siblings = await db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.RouteStepId == routeStepId)
            .OrderBy(x => x.ProcessSequence)
            .ThenBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        if (ProductionProcessHandoff.TryGetImmediatePrior(siblings, operation, out var prior) is { } priorError)
            return priorError;

        var handoffRow = existing.FirstOrDefault(x => x.IsHandoff);
        if (prior is null)
        {
            if (handoffRow is not null)
                db.ProductionOutputMaterials.Remove(handoffRow);
            return null;
        }

        var uom = operation.PlannedOutputUom ?? operation.RouteStep?.OutputUom ?? output.OutputUom;
        if (handoffRow is null)
        {
            handoffRow = new ProductionOutputMaterial
            {
                CompanyCode = output.CompanyCode,
                BranchCode = output.BranchCode,
                ProductionOutputId = output.Uid,
                CreatedDate = now,
                CreatedBy = user,
            };
            db.ProductionOutputMaterials.Add(handoffRow);
        }
        else
        {
            handoffRow.ModifiedDate = now;
            handoffRow.ModifiedBy = user;
        }

        handoffRow.WorkOrderMaterialId = null;
        handoffRow.IsHandoff = true;
        handoffRow.HandoffFromOperationId = prior.Uid;
        handoffRow.ComponentCode = operation.RouteStep!.OutputItemCode;
        handoffRow.RequiredUom = uom;
        handoffRow.SupplySource = ProductionProcessHandoff.SupplySource;
        handoffRow.IssueMethod = ProductionProcessHandoff.IssueMethod;
        handoffRow.TolerancePercent = 0m;
        handoffRow.WoBomRequiredQty = 0m;
        handoffRow.ConversionFactorToBase = 1m;
        handoffRow.BaseUom = uom;
        handoffRow.StandardQty = processed;
        handoffRow.ConsumeQty = processed;
        handoffRow.VarianceQty = 0m;
        handoffRow.VarianceReasonCode = null;
        handoffRow.VarianceReasonText = null;
        return null;
    }

    private static bool ReplayPayloadMatches(ProductionOutput existing, ProductionOutputCreateRequest request)
    {
        if (existing.WorkOrderOperationId != request.WorkOrderOperationId)
            return false;
        if (existing.ProductionDate != request.ProductionDate)
            return false;
        if (!string.Equals(Normalize(existing.ShiftCode), Normalize(request.ShiftCode), StringComparison.Ordinal))
            return false;
        if (!string.Equals(Normalize(existing.ActualMachineCode), Normalize(request.ActualMachineCode), StringComparison.Ordinal))
            return false;
        if (!string.Equals(Normalize(existing.OperatorCode), Normalize(request.OperatorCode), StringComparison.Ordinal))
            return false;
        if (IvQty.Round(existing.GoodQty) != IvQty.Round(request.GoodQty)
            || IvQty.Round(existing.ScrapQty) != IvQty.Round(request.ScrapQty)
            || IvQty.Round(existing.RejectQty) != IvQty.Round(request.RejectQty)
            || IvQty.Round(existing.HoldQty) != IvQty.Round(request.HoldQty))
            return false;
        if (!string.Equals((existing.OutputLotNo ?? string.Empty).Trim(), (request.OutputLotNo ?? string.Empty).Trim(), StringComparison.Ordinal))
            return false;

        var incoming = (request.Materials ?? [])
            .Where(x => x.WorkOrderMaterialId > 0)
            .OrderBy(x => x.WorkOrderMaterialId)
            .Select(x => (
                x.WorkOrderMaterialId,
                IvQty.Round(x.ConsumeQty),
                ProductionMaterialVarianceReasonCodes.Normalize(x.VarianceReasonCode) ?? "",
                (x.VarianceReasonText ?? string.Empty).Trim()))
            .ToList();
        if (incoming.Count == 0)
            return true;

        var saved = existing.Materials
            .Where(x => !x.IsHandoff && x.WorkOrderMaterialId is not null)
            .OrderBy(x => x.WorkOrderMaterialId)
            .Select(x => (
                x.WorkOrderMaterialId!.Value,
                IvQty.Round(x.ConsumeQty),
                ProductionMaterialVarianceReasonCodes.Normalize(x.VarianceReasonCode) ?? "",
                (x.VarianceReasonText ?? string.Empty).Trim()))
            .ToList();
        return saved.SequenceEqual(incoming);
    }

    private async Task RebuildWorkOrderMaterialExecutionProjectionAsync(
        AppDbContext db,
        IReadOnlyCollection<long> materialIds,
        long currentOutputId,
        ProductionOutputProjectionTransition transition,
        CancellationToken cancellationToken)
    {
        foreach (var materialId in materialIds.Distinct().OrderBy(x => x))
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == materialId, cancellationToken);
            var facts = await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => x.WorkOrderMaterialId == materialId)
                .Select(x => new { x.MovementType, x.Qty })
                .ToListAsync(cancellationToken);
            material.ConsumedQty = ProductionMaterialMovementTotals.EffectiveConsumed(
                facts.Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume).Sum(x => x.Qty),
                facts.Where(x => x.MovementType == ProductionMaterialMovementTypes.ConsumeReversal).Sum(x => x.Qty));

            var postedStandard = await db.ProductionOutputMaterials.AsNoTracking()
                .Where(x => x.WorkOrderMaterialId == materialId
                    && x.ProductionOutputId != currentOutputId
                    && x.ProductionOutput != null
                    && x.ProductionOutput.Status == ProductionOutputStatuses.Posted)
                .SumAsync(x => (decimal?)x.StandardQty, cancellationToken) ?? 0m;

            var currentStandard = await db.ProductionOutputMaterials.AsNoTracking()
                .Where(x => x.WorkOrderMaterialId == materialId && x.ProductionOutputId == currentOutputId)
                .Select(x => (decimal?)x.StandardQty)
                .SingleOrDefaultAsync(cancellationToken) ?? 0m;

            var standardToDate = transition == ProductionOutputProjectionTransition.IncludeCurrentPost
                ? IvQty.Round(postedStandard + currentStandard)
                : IvQty.Round(postedStandard);
            material.VarianceQty = IvQty.Round(material.ConsumedQty - standardToDate);
        }
    }

    private static async Task<decimal> AvailableQtyAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderMaterial material,
        DateTime productionDate,
        CancellationToken cancellationToken)
    {
        IQueryable<ProductionBalLot> lots;
        if (string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.OrdinalIgnoreCase))
        {
            lots = db.ProductionBalLots.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.Kind == ProductionBalLotKinds.Wip
                    && x.WorkOrderId == material.WorkOrderId
                    && x.ProducingRouteStepId == material.ProducingRouteStepId
                    && x.ItemCode == material.ComponentCode
                    && x.BaseQty > 0m);
        }
        else
        {
            lots = db.ProductionBalLots.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.Kind == ProductionBalLotKinds.MaterialIn
                    && x.WorkOrderMaterialId == material.Uid
                    && x.BaseQty > 0m);
        }

        lots = lots.Where(x => !x.LastMovementDate.HasValue || x.LastMovementDate <= productionDate);
        var availableBase = await lots.SumAsync(x => (decimal?)x.BaseQty, cancellationToken) ?? 0m;
        var factor = material.ConversionFactorToBase <= 0m ? 1m : material.ConversionFactorToBase;
        return ProductionMaterialExecutionCalc.IssueQtyForBaseQty(availableBase, factor);
    }

    private static async Task<decimal> AvailableHandoffQtyAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderOperation operation,
        long priorOperationId,
        DateTime productionDate,
        CancellationToken cancellationToken)
    {
        var lotNo = ProductionProcessHandoff.HandoffLotNo(priorOperationId);
        var availableBase = await db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.Kind == ProductionBalLotKinds.Wip
                && x.WorkOrderId == operation.WorkOrderId
                && x.ProducingRouteStepId == null
                && x.WorkOrderOperationId == priorOperationId
                && x.ItemCode == operation.RouteStep!.OutputItemCode
                && x.LotNo == lotNo
                && x.BaseQty > 0m
                && (!x.LastMovementDate.HasValue || x.LastMovementDate <= productionDate))
            .SumAsync(x => (decimal?)x.BaseQty, cancellationToken) ?? 0m;
        var factor = 1m;
        if (ProductionProcessHandoff.ResolveProducerContract(operation, operation.RouteStep, out var contract) is null
            && contract is not null
            && contract.ConversionFactorToBase > 0m)
        {
            factor = contract.ConversionFactorToBase;
        }

        return ProductionMaterialExecutionCalc.IssueQtyForBaseQty(availableBase, factor);
    }

    private static ProductionOutputMaterialLine ToLine(
        ProductionWorkOrderMaterial material,
        decimal standard,
        decimal consume,
        string? reasonCode,
        string? reasonText,
        decimal available,
        string? blocking)
    {
        var max = ProductionMaterialExecutionCalc.DailyProductionMaxQty(standard, material.Tolerance);
        var remaining = IvQty.Round(available - consume);
        return new ProductionOutputMaterialLine
        {
            WorkOrderMaterialId = material.Uid,
            ComponentCode = material.ComponentCode,
            Description = material.ComponentDescription,
            IssueMethod = material.IssueMethod,
            SupplySource = material.SupplySource,
            TolerancePercent = material.Tolerance,
            RequiredQty = material.RequiredQty,
            WoBomRequiredQty = material.RequiredQty,
            RequiredUom = material.RequiredUom ?? string.Empty,
            StandardQty = standard,
            ConsumeQty = consume,
            MaxConsumeQty = max,
            VarianceQty = ProductionMaterialExecutionCalc.DailyProductionVarianceQty(consume, standard),
            VarianceReasonCode = reasonCode,
            VarianceReasonText = reasonText,
            AvailableQty = available,
            RemainingAfterConsume = remaining,
            BlockingReason = blocking,
            IsHandoff = false,
            IsConsumeEditable = true,
        };
    }
}
