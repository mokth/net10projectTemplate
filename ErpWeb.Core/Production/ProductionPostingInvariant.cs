using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class ProductionPostingInvariantException : InvalidOperationException
{
    public ProductionPostingInvariantException(string detail)
        : base($"Production valuation evidence is incomplete; posting was rolled back. {detail}")
    {
    }
}

/// <summary>Proves V2 movement, immutable evidence, and reconciled pool state before a Production post is sealed.</summary>
public static class ProductionPostingInvariant
{
    public static async Task AssertIssueVerifiedAsync(
        StockPostingContext context,
        long postingLinkId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var movements = await context.Db.ProductionBalLotMovements
            .Where(x => x.PostingLinkId == postingLinkId && x.MovementType == ProductionBalLotMovementTypes.Issue)
            .OrderBy(x => x.Uid)
            .ToListAsync(ct);
        if (movements.Count == 0)
            throw new ProductionPostingInvariantException("No ISSUE movement was recorded for the posting link.");

        await AssertMaterialMovementsAsync(context, postingLinkId, ct);
        await AssertMovementsAsync(context, movements, requireIssueHistory: true, ct);
        await AssertTouchedPoolsAsync(context, movements, ct);
    }

    public static async Task AssertOutputVerifiedAsync(
        StockPostingContext context,
        long outputId,
        long postingLinkId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var movements = await context.Db.ProductionBalLotMovements
            .Where(x => x.ProductionOutputId == outputId
                && x.PostingLinkId == postingLinkId
                && (x.MovementType == ProductionBalLotMovementTypes.Consume
                    || x.MovementType == ProductionBalLotMovementTypes.Produce))
            .OrderBy(x => x.Uid)
            .ToListAsync(ct);
        await AssertForwardConversionFactsAsync(context, outputId, postingLinkId, movements, ct);
        if (movements.Count == 0)
            return;

        await AssertMovementsAsync(context, movements, requireIssueHistory: false, ct);
        await AssertTouchedPoolsAsync(context, movements, ct);
    }

    public static void AssertSealed(StockPostingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Posting.SealedAtUtc is null)
            throw new ProductionPostingInvariantException("The V2 StockPosting was not sealed.");
    }

    private static async Task AssertForwardConversionFactsAsync(
        StockPostingContext context,
        long outputId,
        long postingLinkId,
        IReadOnlyList<ProductionBalLotMovement> movements,
        CancellationToken ct)
    {
        var allFacts = await context.Db.ProductionConversionCostFacts
            .Where(x => x.ProductionOutputId == outputId && x.ReversesFactId == null)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        if (allFacts.Count == 0)
            return;

        var produceMovements = movements
            .Where(x => x.MovementType == ProductionBalLotMovementTypes.Produce && x.OriginalMovementId == null)
            .ToList();
        if (produceMovements.Count != 1)
            throw new ProductionPostingInvariantException(
                "Absorbed conversion facts require exactly one current forward PRODUCE movement.");

        var produceMovement = produceMovements[0];
        var movementIds = movements.Select(x => x.Uid).ToHashSet();
        if (allFacts.Any(x => x.StockPostingId != context.Posting.Id
            || x.ProductionMovementId != produceMovement.Uid
            || !movementIds.Contains(x.ProductionMovementId)
            || x.CompanyCode != context.CompanyCode
            || x.BranchCode != context.BranchCode))
        {
            throw new ProductionPostingInvariantException(
                "Absorbed conversion facts are orphaned or belong to a different posting lineage.");
        }

        var output = await context.Db.ProductionOutputs
            .SingleOrDefaultAsync(x => x.Uid == outputId, ct)
            ?? throw new ProductionPostingInvariantException("Absorbed conversion facts point to a missing Production Output.");
        var operation = await context.Db.ProductionWorkOrderOperations
            .Include(x => x.Machines).ThenInclude(x => x.Labours)
            .Include(x => x.Labours)
            .SingleOrDefaultAsync(x => x.Uid == output.WorkOrderOperationId, ct)
            ?? throw new ProductionPostingInvariantException("Absorbed conversion facts point to a missing Work Order operation.");
        var order = await context.Db.ProductionWorkOrders
            .SingleOrDefaultAsync(x => x.Uid == output.WorkOrderId, ct)
            ?? throw new ProductionPostingInvariantException("Absorbed conversion facts point to a missing Work Order.");
        if (!ProductionSnapshotHashVersions.UsesAbsorbedConversionCost(order.SnapshotHashVersion))
            throw new ProductionPostingInvariantException("Absorbed conversion facts are not allowed for this snapshot hash version.");

        var expectedUom = Normalize(operation.PlannedOutputUom);
        if (expectedUom is null || !string.Equals(expectedUom, Normalize(output.OutputUom), StringComparison.Ordinal))
            throw new ProductionPostingInvariantException("Absorbed conversion facts use an invalid output UOM.");

        if (allFacts.Select(x => x.SourceLineKey).Distinct(StringComparer.Ordinal).Count() != allFacts.Count)
            throw new ProductionPostingInvariantException("Absorbed conversion facts contain duplicate source lines.");

        foreach (var fact in allFacts)
        {
            if (!ProductionConversionCostTypes.IsKnown(fact.CostType)
                || fact.BasisQty <= 0m
                || fact.RatePerOutputUnit <= 0m
                || fact.CostAmount <= 0m
                || fact.BasisQty != output.GoodQty
                || !string.Equals(Normalize(fact.BasisUom), expectedUom, StringComparison.Ordinal)
                || fact.WorkOrderId != order.Uid
                || fact.RouteStepId != output.RouteStepId
                || fact.WorkOrderOperationId != operation.Uid
                || fact.ProductionOutputId != output.Uid
                || fact.ProductionMovementId != produceMovement.Uid)
            {
                throw new ProductionPostingInvariantException(
                    $"Absorbed conversion fact {fact.Id} has incomplete or mismatched lineage.");
            }

            var expectedRate = fact.CostType switch
            {
                ProductionConversionCostTypes.Labour => ResolveLabourRate(operation, fact),
                ProductionConversionCostTypes.Machine => ResolveMachineRate(operation, fact),
                ProductionConversionCostTypes.UtilitiesOverhead =>
                    fact.WorkOrderLabourId is null && fact.WorkOrderMachineId is null
                        && fact.SourceLineKey == $"UTILITIES_OVERHEAD:{operation.Uid}"
                        ? operation.UtilitiesOverheadCostPerOutputUnit
                        : null,
                ProductionConversionCostTypes.Other =>
                    fact.WorkOrderLabourId is null && fact.WorkOrderMachineId is null
                        && fact.SourceLineKey == $"OTHER:{operation.Uid}"
                        ? operation.OtherCostPerOutputUnit
                        : null,
                _ => null
            };
            if (expectedRate is null || expectedRate <= 0m
                || StockLedgerPrecision.Money(expectedRate.Value) != fact.RatePerOutputUnit
                || StockLedgerPrecision.Money(fact.BasisQty * fact.RatePerOutputUnit) != fact.CostAmount)
            {
                throw new ProductionPostingInvariantException(
                    $"Absorbed conversion fact {fact.Id} does not match the frozen Work Order rate.");
            }
        }

        var consumedValue = StockLedgerPrecision.Money(
            movements.Where(x => x.MovementType == ProductionBalLotMovementTypes.Consume)
                .Sum(x => x.TotalCost));
        var expectedProduceValue = StockLedgerPrecision.Money(consumedValue + allFacts.Sum(x => x.CostAmount));
        if (produceMovement.TotalCost != expectedProduceValue)
            throw new ProductionPostingInvariantException(
                "Forward PRODUCE value does not equal consumed input value plus absorbed conversion facts.");

        static decimal? ResolveLabourRate(ProductionWorkOrderOperation operation, ProductionConversionCostFact fact)
        {
            if (fact.WorkOrderLabourId is not long labourId
                || fact.WorkOrderMachineId is not null
                || fact.SourceLineKey != $"LABOUR:{labourId}")
                return null;

            var direct = operation.Labours.SingleOrDefault(x => x.Uid == labourId);
            if (direct is not null)
                return direct.ContributesToPlan
                    && direct.RateBasis == ProductionLabourRateBases.PerOutputUnit
                    ? direct.Rate
                    : null;

            var machineLabour = operation.Machines
                .Where(x => x.IsSelected)
                .SelectMany(x => x.Labours)
                .SingleOrDefault(x => x.Uid == labourId);
            return machineLabour is not null
                && machineLabour.ContributesToPlan
                && machineLabour.RateBasis == ProductionLabourRateBases.PerOutputUnit
                ? machineLabour.Rate
                : null;
        }

        static decimal? ResolveMachineRate(ProductionWorkOrderOperation operation, ProductionConversionCostFact fact)
        {
            if (fact.WorkOrderMachineId is not long machineId
                || fact.WorkOrderLabourId is not null
                || fact.SourceLineKey != $"MACHINE:{machineId}")
                return null;
            var machine = operation.Machines.SingleOrDefault(x => x.Uid == machineId);
            return machine is { IsSelected: true } ? machine.CostPerOutputUnit : null;
        }
    }

    public static async Task AssertOutputRollbackVerifiedAsync(
        StockPostingContext context,
        long outputId,
        long originalPostingLinkId,
        long rollbackPostingLinkId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var original = await context.Db.ProductionBalLotMovements
            .Where(x => x.ProductionOutputId == outputId && x.PostingLinkId == originalPostingLinkId
                && (x.MovementType == ProductionBalLotMovementTypes.Consume
                    || x.MovementType == ProductionBalLotMovementTypes.Produce))
            .OrderBy(x => x.Uid).ToListAsync(ct);
        var reversals = await context.Db.ProductionBalLotMovements
            .Where(x => x.ProductionOutputId == outputId && x.PostingLinkId == rollbackPostingLinkId
                && (x.MovementType == ProductionBalLotMovementTypes.ConsumeReversal
                    || x.MovementType == ProductionBalLotMovementTypes.ProduceReversal))
            .OrderBy(x => x.Uid).ToListAsync(ct);
        if (original.Count != reversals.Count)
            throw new ProductionPostingInvariantException("Rollback movement count does not match the original output.");
        if (reversals.Any(x => x.OriginalMovementId is null
                || !original.Any(source => source.Uid == x.OriginalMovementId.Value)))
            throw new ProductionPostingInvariantException("Rollback contains an orphaned movement reversal.");

        foreach (var source in original)
        {
            var matches = reversals.Where(x => x.OriginalMovementId == source.Uid).ToList();
            if (matches.Count != 1)
                throw new ProductionPostingInvariantException($"Movement {source.Uid} does not have exactly one rollback reversal.");
            var reversal = matches[0];
            var expectedType = source.MovementType == ProductionBalLotMovementTypes.Consume
                ? ProductionBalLotMovementTypes.ConsumeReversal
                : ProductionBalLotMovementTypes.ProduceReversal;
            if (reversal.MovementType != expectedType
                || reversal.PostingLinkId != rollbackPostingLinkId
                || reversal.StockPostingId != context.Posting.Id
                || reversal.CompanyCode != context.CompanyCode
                || reversal.BranchCode != context.BranchCode
                || reversal.ProductionBalLotId != source.ProductionBalLotId
                || reversal.Qty != source.Qty
                || reversal.Uom != source.Uom
                || reversal.BaseQty != source.BaseQty
                || reversal.BaseUom != source.BaseUom
                || reversal.TotalCost != source.TotalCost
                || reversal.UnitCost != source.UnitCost
                || reversal.WorkOrderId != source.WorkOrderId
                || reversal.WorkOrderOperationId != source.WorkOrderOperationId
                || reversal.RouteStepId != source.RouteStepId
                || reversal.ProductionOutputId != outputId)
            {
                throw new ProductionPostingInvariantException($"Rollback reversal for movement {source.Uid} is not exact.");
            }
        }

        var originalFacts = await context.Db.ProductionConversionCostFacts
            .Where(x => x.ProductionOutputId == outputId && x.ReversesFactId == null)
            .ToListAsync(ct);
        var reversalFacts = await context.Db.ProductionConversionCostFacts
            .Where(x => x.ProductionOutputId == outputId && x.ReversesFactId != null)
            .ToListAsync(ct);
        if (reversalFacts.Count != originalFacts.Count)
            throw new ProductionPostingInvariantException("Rollback conversion-fact count does not match the original output.");
        if (reversalFacts.Any(x => x.ReversesFactId is null)
            || reversalFacts.Select(x => x.ReversesFactId!.Value).Distinct().Count() != reversalFacts.Count)
            throw new ProductionPostingInvariantException("Rollback conversion facts do not have unique source-fact lineage.");
        foreach (var source in originalFacts)
        {
            var matches = reversalFacts.Where(x => x.ReversesFactId == source.Id).ToList();
            if (matches.Count != 1)
                throw new ProductionPostingInvariantException($"Conversion fact {source.Id} does not have exactly one rollback reversal.");
            var reversal = matches[0];
            var originalProduce = original.SingleOrDefault(x =>
                x.Uid == source.ProductionMovementId
                && x.MovementType == ProductionBalLotMovementTypes.Produce);
            if (originalProduce is null)
                throw new ProductionPostingInvariantException($"Conversion fact {source.Id} is not linked to an original PRODUCE movement.");
            var produceReversal = reversals.SingleOrDefault(x =>
                x.OriginalMovementId == originalProduce.Uid
                && x.MovementType == ProductionBalLotMovementTypes.ProduceReversal);
            if (produceReversal is null)
                throw new ProductionPostingInvariantException($"Conversion fact {source.Id} is not linked to exactly one PRODUCE rollback reversal.");
            if (reversal.StockPostingId != context.Posting.Id
                || reversal.CompanyCode != context.CompanyCode
                || reversal.BranchCode != context.BranchCode
                || reversal.ProductionOutputId != outputId
                || reversal.ProductionMovementId != produceReversal.Uid
                || reversal.WorkOrderId != source.WorkOrderId
                || reversal.RouteStepId != source.RouteStepId
                || reversal.WorkOrderOperationId != source.WorkOrderOperationId
                || reversal.CostType != source.CostType
                || reversal.SourceLineKey != source.SourceLineKey
                || reversal.WorkOrderLabourId != source.WorkOrderLabourId
                || reversal.WorkOrderMachineId != source.WorkOrderMachineId
                || reversal.BasisQty != source.BasisQty
                || reversal.BasisUom != source.BasisUom
                || reversal.RatePerOutputUnit != source.RatePerOutputUnit
                || reversal.CostAmount != source.CostAmount
                || reversal.CreatedAtUtc != context.Posting.PostedAtUtc
                || reversal.CreatedBy != (context.UserId.Length > 10 ? context.UserId[..10] : context.UserId))
            {
                throw new ProductionPostingInvariantException($"Rollback conversion fact for {source.Id} is not an exact linked reversal.");
            }
        }

        await AssertMovementsAsync(context, reversals, requireIssueHistory: false, ct);
        await AssertTouchedPoolsAsync(context, reversals, ct);
    }

    private static string? Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static async Task AssertMovementsAsync(
        StockPostingContext context,
        IReadOnlyList<ProductionBalLotMovement> movements,
        bool requireIssueHistory,
        CancellationToken ct)
    {
        foreach (var movement in movements)
        {
            if (movement.LedgerVersion != 2)
                throw new ProductionPostingInvariantException($"Movement {movement.Uid} is not ledger version 2.");
            if (movement.LedgerEpochId != context.Epoch.Id)
                throw new ProductionPostingInvariantException($"Movement {movement.Uid} is not on the current ledger epoch.");
            if (movement.StockPostingId != context.Posting.Id)
                throw new ProductionPostingInvariantException($"Movement {movement.Uid} is not on the current StockPosting.");
            if (!string.Equals(movement.ValuationStatus, ProductionPoolValuationService.Verified, StringComparison.Ordinal))
                throw new ProductionPostingInvariantException($"Movement {movement.Uid} is not VERIFIED.");

            var evidence = await context.Db.ProductionValuationEvidenceRows
                .SingleOrDefaultAsync(x => x.MovementId == movement.Uid, ct)
                ?? throw new ProductionPostingInvariantException($"Movement {movement.Uid} has no valuation evidence.");
            if (evidence.ProductionBalLotId != movement.ProductionBalLotId)
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is on a different pool.");
            if (evidence.Generation <= 0)
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} has an invalid generation.");
            if (!string.Equals(evidence.Status, ProductionPoolValuationService.Verified, StringComparison.Ordinal))
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is not VERIFIED.");
            if (string.IsNullOrWhiteSpace(evidence.Basis))
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is missing a basis.");
            if (!string.Equals(evidence.Currency, "COMPANY_BASE", StringComparison.Ordinal))
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is not in company base currency.");
            if (evidence.Price is null)
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is missing a price.");
            if (evidence.ConversionFactor is null or <= 0m)
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is missing a conversion factor.");

            if (requireIssueHistory || movement.MovementType == ProductionBalLotMovementTypes.Issue)
            {
                if (evidence.InventoryHistoryId is null)
                    throw new ProductionPostingInvariantException($"ISSUE evidence for movement {movement.Uid} is missing inventory history.");
                var history = await context.Db.IvTrxHistories
                    .SingleOrDefaultAsync(x => x.Id == evidence.InventoryHistoryId.Value, ct)
                    ?? throw new ProductionPostingInvariantException($"ISSUE evidence for movement {movement.Uid} points to missing inventory history.");
                if (history.StockPostingId is not null && history.StockPostingId != context.Posting.Id)
                    throw new ProductionPostingInvariantException($"ISSUE history for movement {movement.Uid} is not on the current StockPosting.");
            }
        }
    }

    private static async Task AssertTouchedPoolsAsync(
        StockPostingContext context,
        IReadOnlyList<ProductionBalLotMovement> movements,
        CancellationToken ct)
    {
        foreach (var lotId in movements.Select(x => x.ProductionBalLotId).Distinct().OrderBy(x => x))
        {
            var lot = await context.Db.ProductionBalLots.SingleAsync(x => x.Uid == lotId, ct);
            var pool = await context.Db.ProductionPoolValuationRows
                .SingleOrDefaultAsync(x => x.ProductionBalLotId == lotId, ct)
                ?? throw new ProductionPostingInvariantException($"Production pool {lotId} has no valuation projection.");
            if (pool.TrackedBaseQty < 0m || pool.TrackedValue < 0m)
                throw new ProductionPostingInvariantException($"Production pool {lotId} has a negative tracked quantity or value.");
            if (pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost)
                throw new ProductionPostingInvariantException($"Production pool {lotId} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.");
            if (pool.TrackedBaseQty == 0m && pool.TrackedValue != 0m)
                throw new ProductionPostingInvariantException($"Production pool {lotId} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.");
            if (lot.BaseQty == 0m && lot.TotalCost != 0m)
                throw new ProductionPostingInvariantException($"Production pool {lotId} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.");

            var nonempty = lot.BaseQty != 0m || lot.TotalCost != 0m;
            if (nonempty && !string.Equals(pool.Status, ProductionPoolValuationService.Verified, StringComparison.Ordinal))
                throw new ProductionPostingInvariantException($"Production pool {lotId} is not VERIFIED.");
        }
    }

    private static async Task AssertMaterialMovementsAsync(
        StockPostingContext context,
        long postingLinkId,
        CancellationToken ct)
    {
        var materials = await context.Db.ProductionMaterialMovements
            .Where(x => x.PostingLinkId == postingLinkId
                && x.MovementType == ProductionMaterialMovementTypes.Issue)
            .OrderBy(x => x.Uid)
            .ToListAsync(ct);
        if (materials.Count == 0)
            throw new ProductionPostingInvariantException("No production material movement was recorded for the posting link.");

        foreach (var movement in materials)
        {
            if (movement.StockPostingId != context.Posting.Id
                || movement.InventoryHistoryId is null
                || movement.BaseQty <= 0m
                || movement.TotalCost < 0m
                || string.IsNullOrWhiteSpace(movement.SourceLineId)
                || movement.SplitOrdinal != 0)
            {
                throw new ProductionPostingInvariantException(
                    $"Production material movement {movement.Uid} is missing current-posting valuation identity.");
            }

            var facts = await context.Db.StockValuationFacts
                .Where(x => x.StockPostingId == context.Posting.Id
                    && x.InventoryHistoryId == movement.InventoryHistoryId
                    && x.Direction < 0
                    && x.MovementCode == "PRODUCTION_MATERIAL_OUT"
                    && x.ValuationStatus == StockValuationStatuses.Valued)
                .ToListAsync(ct);
            if (facts.Count == 0)
                throw new ProductionPostingInvariantException(
                    $"Production material movement {movement.Uid} has no VERIFIED V2 valuation facts.");

            var factQty = IvQty.Round(facts.Sum(x => x.BaseQty));
            var factValue = StockLedgerPrecision.Money(facts.Sum(x => x.CostAmount));
            if (factQty != movement.BaseQty || factValue != StockLedgerPrecision.Money(movement.TotalCost))
                throw new ProductionPostingInvariantException(
                    $"Production material movement {movement.Uid} does not reconcile to V2 valuation facts.");

            var expectedUnitCost = StockLedgerPrecision.Money(movement.TotalCost / movement.BaseQty);
            if (StockLedgerPrecision.Money(movement.UnitCost) != expectedUnitCost)
                throw new ProductionPostingInvariantException(
                    $"Production material movement {movement.Uid} has an incorrect authoritative unit cost.");

            if (facts.Any(x => x.ProductionPostingLinkId != postingLinkId
                || x.ProductionMovementId != movement.Uid
                || x.WorkOrderId != movement.WorkOrderId
                || x.WorkOrderOperationId != movement.WorkOrderOperationId))
            {
                throw new ProductionPostingInvariantException(
                    $"V2 valuation facts for production material movement {movement.Uid} have incomplete production lineage.");
            }
        }
    }
}
