using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Restates both sides of an isolated, already reversed Production output.</summary>
internal static class ProductionReversedOutputCostCorrectionWriter
{
    public static async Task AppendWithinRootAsync(
        StockPostingContext context, ProductionBalLotMovement consume,
        ProductionBalLotMovement produce, ProductionEffectiveCost expectedConsume,
        ProductionEffectiveCost expectedProduce, decimal desiredConsume,
        decimal desiredProduce, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var db = context.Db;
        if (db.Database.CurrentTransaction is null
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
            || context.Posting.PostingRole is not ("PRIMARY" or "REVERSAL")
            || context.CostMethod is not (StockCostMethods.MovingAverage or StockCostMethods.Fifo)
            || consume.Uid == produce.Uid || consume.ProductionOutputId is not long outputId
            || consume.ProductionOutputId != produce.ProductionOutputId
            || consume.StockPostingId is not long ownerId || produce.StockPostingId != ownerId
            || consume.MovementType != ProductionBalLotMovementTypes.Consume
            || produce.MovementType != ProductionBalLotMovementTypes.Produce
            || consume.OriginalMovementId is not null || produce.OriginalMovementId is not null
            || consume.BaseQty <= 0m || produce.BaseQty <= 0m
            || desiredConsume < 0m || desiredProduce < 0m
            || StockLedgerPrecision.Money(desiredConsume) != desiredConsume
            || StockLedgerPrecision.Money(desiredProduce) != desiredProduce)
            throw Invalid("Reversed Production output correction has invalid authority.");

        var originals = new[] { consume, produce };
        var ids = originals.Select(m => m.Uid).ToArray();
        var reversals = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.OriginalMovementId.HasValue && ids.Contains(m.OriginalMovementId.Value))
            .ToArrayAsync(ct);
        var output = await db.ProductionOutputs.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Uid == outputId && o.CompanyCode == context.CompanyCode
                && o.BranchCode == context.BranchCode, ct);
        var owner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == ownerId, ct);
        var reverseOwner = output?.ReversalPostingId is long reverseId
            ? await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == reverseId, ct)
            : null;
        var run = await db.StockReplayRuns.AsNoTracking().SingleOrDefaultAsync(r =>
            r.CompanyCode == context.CompanyCode && r.BranchCode == context.BranchCode
            && r.RootStockPostingId == context.Posting.Id, ct);
        if (output is null || output.Status != ProductionOutputStatuses.New || output.DeletedAtUtc is not null
            || output.PostingId != ownerId || owner is null || reverseOwner is null || run is null
            || owner.CompanyCode != context.CompanyCode || owner.BranchCode != context.BranchCode
            || reverseOwner.CompanyCode != context.CompanyCode || reverseOwner.BranchCode != context.BranchCode
            || owner.LedgerEpochId != context.Epoch.Id || reverseOwner.LedgerEpochId != context.Epoch.Id
            || owner.SealedAtUtc is null || reverseOwner.SealedAtUtc is null
            || owner.PostingRole != "PRIMARY" || reverseOwner.PostingRole != "REVERSAL"
            || owner.SourceModule != "PRODUCTION" || reverseOwner.SourceModule != "PRODUCTION"
            || owner.SourceDocumentType != ProductionDocumentTypes.ProductionOutput
            || reverseOwner.SourceDocumentType != ProductionDocumentTypes.ProductionOutput
            || owner.CommandType != "PRODUCTION_OUTPUT_POST" || reverseOwner.CommandType != "PRODUCTION_OUTPUT_ROLLBACK"
            || owner.SourceDocumentId != outputId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != output.DocumentNo
            || output.DocumentRevision != owner.DocumentRevision + 1
            || reverseOwner.SourceDocumentId != owner.SourceDocumentId
            || reverseOwner.SourceDocumentNo != owner.SourceDocumentNo
            || reverseOwner.DocumentRevision != owner.DocumentRevision
            || reverseOwner.ReversesPostingId != owner.Id
            || owner.PostingSequence >= reverseOwner.PostingSequence
            || reverseOwner.PostingSequence >= context.Posting.PostingSequence
            || owner.EffectiveAt != consume.MovementDate || owner.EffectiveAt != produce.MovementDate
            || run.RootRequestId != context.Posting.RequestId
            || run.RootDocumentType != context.Posting.SourceDocumentType
            || run.RootDocumentId != context.Posting.SourceDocumentId
            || run.EarliestEffectiveAt > owner.EffectiveAt || run.LatestEffectiveAt < owner.EffectiveAt
            || reversals.Length != 2
            || await db.ProductionBalLotMovements.AsNoTracking().AnyAsync(m =>
                m.ProductionBalLotId == produce.ProductionBalLotId
                && m.MovementType == ProductionBalLotMovementTypes.FgReceiptOut
                && m.OriginalMovementId == null, ct))
            throw Invalid("Reversed Production output has no exact isolated business owner.");
        if ((await IvPeriodCloseGuard.EnsureOpenAsync(db, context.CompanyCode,
                context.BranchCode, owner.EffectiveAt, ct)
            ?? await IvPeriodCloseGuard.EnsureOpenAsync(db, context.CompanyCode,
                context.BranchCode, reverseOwner.EffectiveAt, ct)) is string closed)
            throw new StockLedgerException(new(StockLedgerErrorCodes.ClosedPeriod, closed));

        var reversedConsume = reversals.SingleOrDefault(m => m.OriginalMovementId == consume.Uid);
        var reversedProduce = reversals.SingleOrDefault(m => m.OriginalMovementId == produce.Uid);
        if (reversedConsume is null || reversedProduce is null
            || originals.Any(m => m.CompanyCode != context.CompanyCode || m.BranchCode != context.BranchCode
                || m.LedgerVersion != 2 || m.LedgerEpochId != context.Epoch.Id
                || m.ValuationStatus != ProductionPoolValuationService.Verified)
            || !Matches(consume, reversedConsume, ProductionBalLotMovementTypes.ConsumeReversal)
            || !Matches(produce, reversedProduce, ProductionBalLotMovementTypes.ProduceReversal))
            throw Invalid("Reversed Production output has incomplete paired movements.");

        // Other inputs may contribute unchanged WIP to this output. Prove their
        // original and reversal costs still cancel before changing the output value.
        var otherInputs = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionOutputId == outputId && m.StockPostingId == ownerId
                && m.OriginalMovementId == null && m.Uid != consume.Uid && m.Uid != produce.Uid)
            .ToArrayAsync(ct);
        var otherIds = otherInputs.Select(m => m.Uid).ToArray();
        var otherReversals = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.OriginalMovementId.HasValue && otherIds.Contains(m.OriginalMovementId.Value))
            .ToArrayAsync(ct);
        if (otherReversals.Length != otherInputs.Length || otherInputs.Any(m =>
                m.MovementType != ProductionBalLotMovementTypes.Consume
                || m.CompanyCode != context.CompanyCode || m.BranchCode != context.BranchCode
                || m.LedgerVersion != 2 || m.LedgerEpochId != context.Epoch.Id
                || m.ValuationStatus != ProductionPoolValuationService.Verified
                || otherReversals.Count(r => r.OriginalMovementId == m.Uid) != 1
                || !Matches(m, otherReversals.Single(r => r.OriginalMovementId == m.Uid),
                    ProductionBalLotMovementTypes.ConsumeReversal)))
            throw Invalid("Reversed Production output has an inconsistent additional input.");
        await ProductionOutputReversalCostAuthority.ValidateBaselineAsync(db,
            context.CompanyCode, context.BranchCode, originals.Concat(otherInputs).ToArray(),
            reversals.Concat(otherReversals).ToArray(), ct);
        if (otherInputs.Length != 0)
        {
            var otherCosts = await ProductionReplayEffectiveCostReader.ReadAsync(db,
                context.CompanyCode, context.BranchCode,
                otherIds.Concat(otherReversals.Select(m => m.Uid)).ToArray(), ct);
            if (otherInputs.Any(m =>
                {
                    var reverse = otherReversals.Single(r => r.OriginalMovementId == m.Uid);
                    return otherCosts[m.Uid].TotalCost != otherCosts[reverse.Uid].TotalCost
                        || otherCosts[m.Uid].UnitCost != otherCosts[reverse.Uid].UnitCost;
                }))
                throw Invalid("Reversed Production output has an inconsistent additional input cost.");
        }

        var all = new[] { consume, reversedConsume, produce, reversedProduce };
        var effective = await ProductionReplayEffectiveCostReader.ReadAsync(db,
            context.CompanyCode, context.BranchCode, all.Select(m => m.Uid).ToArray(), ct);
        if (effective[consume.Uid] != expectedConsume || effective[produce.Uid] != expectedProduce
            || effective[consume.Uid].TotalCost != effective[reversedConsume.Uid].TotalCost
            || effective[consume.Uid].UnitCost != effective[reversedConsume.Uid].UnitCost
            || effective[produce.Uid].TotalCost != effective[reversedProduce.Uid].TotalCost
            || effective[produce.Uid].UnitCost != effective[reversedProduce.Uid].UnitCost)
            throw Invalid("Reversed Production output costs changed after replay planning.");

        var created = new List<(ProductionCostRestatementFact Fact, long OwnerId)>();
        foreach (var (movement, desired) in new[]
        {
            (consume, desiredConsume), (reversedConsume, desiredConsume),
            (produce, desiredProduce), (reversedProduce, desiredProduce)
        })
        {
            var prior = effective[movement.Uid];
            var desiredUnit = StockLedgerPrecision.Money(desired / movement.BaseQty);
            if (desired == prior.TotalCost)
            {
                if (desiredUnit != prior.UnitCost)
                    throw Invalid("Reversed Production output unit cost is inconsistent.");
                continue;
            }
            if (prior.RestatementFactId is long activeId)
                created.Add((NewFact(movement, prior.TotalCost, movement.TotalCost,
                    movement.UnitCost, activeId), movement.StockPostingId!.Value));
            if (desired != movement.TotalCost)
                created.Add((NewFact(movement, movement.TotalCost, desired,
                    desiredUnit, null), movement.StockPostingId!.Value));
        }
        db.ProductionCostRestatementFacts.AddRange(created.Select(x => x.Fact));
        await db.SaveChangesAsync(ct);
        db.StockReplayEffects.AddRange(created.Select(x => new StockReplayEffect
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            ReplayRunId = run.Id, RootStockPostingId = context.Posting.Id,
            BusinessOwnerPostingId = x.OwnerId, EffectKind = "PRODUCTION_COST_RESTATEMENT",
            AffectedEntityType = "PRODUCTION_COST_RESTATEMENT_FACT", AffectedEntityId = x.Fact.Id,
            OriginalEntityId = x.Fact.ProductionBalLotMovementId, CreatedAtUtc = DateTime.UtcNow
        }));
        await db.SaveChangesAsync(ct);
        var final = await ProductionEffectiveCostResolver.ResolveAsync(db, context.CompanyCode,
            context.BranchCode, all.Select(m => m.Uid).ToArray(), ct);
        if (final[consume.Uid].TotalCost != desiredConsume || final[reversedConsume.Uid].TotalCost != desiredConsume
            || final[produce.Uid].TotalCost != desiredProduce || final[reversedProduce.Uid].TotalCost != desiredProduce)
            throw Invalid("Reversed Production output costs did not reconcile.");
        return;

        bool Matches(ProductionBalLotMovement original, ProductionBalLotMovement reversed, string reversalType) =>
            reversed.CompanyCode == context.CompanyCode && reversed.BranchCode == context.BranchCode
            && reversed.LedgerVersion == 2 && reversed.LedgerEpochId == context.Epoch.Id
            && reversed.ValuationStatus == ProductionPoolValuationService.Verified
            && reversed.StockPostingId == reverseOwner.Id && reversed.PostingLinkId == reverseOwner.ProductionPostingLinkId
            && reversed.MovementType == reversalType && reversed.OriginalMovementId == original.Uid
            && reversed.ProductionOutputId == outputId && reversed.ProductionBalLotId == original.ProductionBalLotId
            && reversed.WorkOrderId == original.WorkOrderId && reversed.WorkOrderOperationId == original.WorkOrderOperationId
            && reversed.WorkOrderMaterialId == original.WorkOrderMaterialId && reversed.RouteStepId == original.RouteStepId
            && reversed.MovementDate == original.MovementDate && reversed.BaseQty == original.BaseQty
            && reversed.Qty == original.Qty && reversed.Uom == original.Uom && reversed.BaseUom == original.BaseUom;

        ProductionCostRestatementFact NewFact(ProductionBalLotMovement movement, decimal prior,
            decimal desired, decimal unit, long? reverses) => new()
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            ReplayRequestId = context.Posting.RequestId, ReplayStockPostingId = context.Posting.Id,
            ProductionBalLotMovementId = movement.Uid, EffectiveAt = movement.MovementDate,
            PriorEffectiveTotalCost = prior, CorrectedEffectiveTotalCost = desired,
            CorrectedUnitCost = unit, DeltaCost = StockLedgerPrecision.Money(desired - prior),
            ReversesRestatementFactId = reverses, CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId
        };
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
