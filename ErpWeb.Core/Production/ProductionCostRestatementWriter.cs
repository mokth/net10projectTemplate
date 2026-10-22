using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed record ProductionCostRestatementWriteResult(
    ProductionCostRestatementFact? Reversal,
    ProductionCostRestatementFact? Replacement);

/// <summary>
/// Appends one movement's cost correction inside an unsealed replay root.
/// The caller must propagate the resulting value through dependent pools and
/// reconcile them before sealing the root.
/// </summary>
internal static class ProductionCostRestatementWriter
{
    public static async Task<ProductionCostRestatementWriteResult> AppendWithinRootAsync(
        StockPostingContext context,
        ProductionEffectiveCost expected,
        decimal correctedTotalCost,
        decimal correctedUnitCost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expected);
        context.EnsureUnsealed();
        if (expected.MovementId <= 0 || correctedTotalCost < 0m
            || correctedUnitCost < 0m
            || StockLedgerPrecision.Money(correctedTotalCost) != correctedTotalCost
            || StockLedgerPrecision.Money(correctedUnitCost) != correctedUnitCost
            || context.Posting.Id <= 0 || context.Posting.RequestId == Guid.Empty
            || context.Posting.LedgerEpochId != context.Epoch.Id
            || context.Posting.PostingSequence <= 0)
            throw Invalid("Production restatement has invalid cost or replay root.");

        var db = context.Db;
        if (db.ChangeTracker.Entries<ProductionCostRestatementFact>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified
                    or EntityState.Deleted))
            throw Stale("Production restatement rows have pending changes.");
        var movement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Uid == expected.MovementId
                && m.CompanyCode == context.CompanyCode
                && m.BranchCode == context.BranchCode,
                cancellationToken);
        if (movement is null || movement.LedgerVersion != 2
            || movement.LedgerEpochId != context.Epoch.Id
            || movement.StockPostingId is not long ownerId
            || movement.OriginalMovementId is not null
            || movement.ValuationStatus != ProductionPoolValuationService.Verified
            || movement.BaseQty <= 0m || movement.TotalCost < 0m
            || movement.UnitCost < 0m
            || StockLedgerPrecision.Money(correctedTotalCost
                / movement.BaseQty) != correctedUnitCost)
            throw Invalid("Production movement has no verified V2 cost authority.");
        var owner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == ownerId,
                cancellationToken);
        if (owner is null || owner.Id == context.Posting.Id
            || owner.CompanyCode != context.CompanyCode
            || owner.BranchCode != context.BranchCode
            || owner.LedgerEpochId != context.Epoch.Id
            || owner.PostingRole != "PRIMARY"
            || owner.SealedAtUtc is null
            || owner.PostingSequence <= 0
            || context.Posting.PostingSequence <= owner.PostingSequence
            || movement.MovementDate != owner.EffectiveAt
            || await db.StockPostings.AsNoTracking().AnyAsync(p =>
                p.ReversesPostingId == owner.Id && p.SealedAtUtc != null,
                cancellationToken)
            || await db.ProductionBalLotMovements.AsNoTracking().AnyAsync(m =>
                m.OriginalMovementId == movement.Uid,
                cancellationToken))
            throw Invalid("Production movement has no active sealed business owner.");
        if (movement.MovementType == ProductionBalLotMovementTypes.AdjustIn
            && await db.ProductionStockAdjustmentFacts.AsNoTracking().AnyAsync(f =>
                f.ProductionMovementId == movement.Uid && f.ReversesFactId == null
                && (f.CostBasisCode == InventoryCostEvidenceTypes.ManualApproved
                    || f.CostBasisCode == InventoryCostEvidenceTypes.ZeroCostApproved),
                cancellationToken))
            throw Invalid("Approved manual or zero-cost PADJ authority is frozen.");
        var run = await db.StockReplayRuns.AsNoTracking()
            .SingleOrDefaultAsync(r => r.CompanyCode == context.CompanyCode
                && r.BranchCode == context.BranchCode
                && r.RootStockPostingId == context.Posting.Id,
                cancellationToken);
        if (run is null || run.RootRequestId != context.Posting.RequestId
            || run.RootDocumentType != context.Posting.SourceDocumentType
            || run.RootDocumentId != context.Posting.SourceDocumentId
            || run.EarliestEffectiveAt > owner.EffectiveAt
            || run.LatestEffectiveAt < owner.EffectiveAt)
            throw Invalid("Production restatement lacks a matching replay run.");

        var priorRows = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(f => f.ProductionBalLotMovementId == movement.Uid)
            .ToArrayAsync(cancellationToken);
        var priorRootIds = priorRows.Select(f => f.ReplayStockPostingId)
            .Distinct().ToArray();
        var priorRoots = await db.StockPostings.AsNoTracking()
            .Where(p => priorRootIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var priorRuns = await db.StockReplayRuns.AsNoTracking()
            .Where(r => priorRootIds.Contains(r.RootStockPostingId))
            .ToDictionaryAsync(r => r.RootStockPostingId, cancellationToken);
        if (priorRoots.Count != priorRootIds.Length
            || priorRuns.Count != priorRootIds.Length
            || priorRows.Any(f =>
                f.CompanyCode != context.CompanyCode
                || f.BranchCode != context.BranchCode
                || f.EffectiveAt != owner.EffectiveAt
                || !priorRoots.TryGetValue(f.ReplayStockPostingId, out var root)
                || !priorRuns.TryGetValue(f.ReplayStockPostingId, out var priorRun)
                || root.CompanyCode != context.CompanyCode
                || root.BranchCode != context.BranchCode
                || root.LedgerEpochId != context.Epoch.Id
                || root.RequestId != f.ReplayRequestId
                || priorRun.CompanyCode != context.CompanyCode
                || priorRun.BranchCode != context.BranchCode
                || priorRun.RootRequestId != root.RequestId
                || root.Id != context.Posting.Id && root.SealedAtUtc is null))
            throw Invalid("Production restatement has invalid prior root lineage.");
        var current = ProductionEffectiveCostResolver.Resolve(
            [new ProductionEffectiveCost(movement.Uid,
                movement.TotalCost, movement.UnitCost, null)], priorRows)[movement.Uid];
        if (current != expected)
            throw Stale("Production movement effective cost changed after planning.");
        if (correctedTotalCost == current.TotalCost)
        {
            if (correctedUnitCost != current.UnitCost)
                throw Invalid("Production restatement cannot change unit cost without value.");
            return new(null, null);
        }

        var now = DateTime.UtcNow;
        ProductionCostRestatementFact? reversal = null;
        if (current.RestatementFactId is long activeId)
        {
            reversal = NewFact(current.TotalCost, movement.TotalCost,
                movement.UnitCost, activeId);
            db.ProductionCostRestatementFacts.Add(reversal);
        }
        ProductionCostRestatementFact? replacement = null;
        if (correctedTotalCost != movement.TotalCost)
        {
            replacement = NewFact(movement.TotalCost, correctedTotalCost,
                correctedUnitCost, null);
            db.ProductionCostRestatementFacts.Add(replacement);
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var fact in new[] { reversal, replacement }.OfType<ProductionCostRestatementFact>())
            db.StockReplayEffects.Add(new StockReplayEffect
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                ReplayRunId = run.Id,
                RootStockPostingId = context.Posting.Id,
                BusinessOwnerPostingId = owner.Id,
                EffectKind = "PRODUCTION_COST_RESTATEMENT",
                AffectedEntityType = "PRODUCTION_COST_RESTATEMENT_FACT",
                AffectedEntityId = fact.Id,
                OriginalEntityId = movement.Uid,
                CreatedAtUtc = now
            });
        await db.SaveChangesAsync(cancellationToken);
        return new(reversal, replacement);

        ProductionCostRestatementFact NewFact(decimal prior, decimal corrected,
            decimal unitCost, long? reverses) => new()
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            ReplayRequestId = context.Posting.RequestId,
            ReplayStockPostingId = context.Posting.Id,
            ProductionBalLotMovementId = movement.Uid,
            EffectiveAt = owner.EffectiveAt,
            PriorEffectiveTotalCost = prior,
            CorrectedEffectiveTotalCost = corrected,
            CorrectedUnitCost = unitCost,
            DeltaCost = corrected - prior,
            ReversesRestatementFactId = reverses,
            CreatedAtUtc = now,
            CreatedBy = context.UserId.Length > 100
                ? context.UserId[..100] : context.UserId
        };
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch,
            message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
