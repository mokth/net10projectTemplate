using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record CorrectedReversedFinishedGoodPlan(
        long SourceRootId, long OriginalMovementId, long ReversalMovementId,
        long OriginalOwnerId, long ReversalOwnerId, long ActiveRestatementId,
        decimal OriginalValue, decimal OriginalUnitCost, decimal CurrentValue,
        decimal CurrentUnitCost, decimal Quantity, DateTime OriginalAt, DateTime ReversalAt,
        string EvidenceHash);

    private static async Task<CorrectedReversedFinishedGoodPlan?> PrepareCorrectedReversedFinishedGoodAsync(
        AppDbContext db, string company, string branch, ProductionBalLotMovement produce,
        ProductionBalLotMovement outbound, ProductionBalLotMovement reversal,
        long? removingReceiptOwnerId, CancellationToken ct)
    {
        var rows = await db.ProductionCostRestatementFacts.AsNoTracking().Where(r =>
            r.ProductionBalLotMovementId == outbound.Uid || r.ProductionBalLotMovementId == reversal.Uid)
            .ToArrayAsync(ct);
        if (rows.Length == 0) return null;
        if (removingReceiptOwnerId is not long sourceRootId
            || rows.Any(r => r.ProductionBalLotMovementId != outbound.Uid)
            || outbound.StockPostingId is not long ownerId || reversal.StockPostingId is not long reversalOwnerId
            || produce.BaseQty <= 0m || outbound.BaseQty <= 0m || outbound.BaseQty > produce.BaseQty)
            throw ProductionReplayError("Corrected finished-good reversal requires its original receipt removal.");
        var effective = (await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch,
            [outbound.Uid, produce.Uid], ct));
        var fgCost = effective[outbound.Uid];
        var produceCost = effective[produce.Uid];
        var activeRow = rows.SingleOrDefault(r => r.Id == fgCost.RestatementFactId
            && r.ReversesRestatementFactId is null && r.ReplayStockPostingId == sourceRootId);
        var produceRestatements = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(r => r.ProductionBalLotMovementId == produce.Uid).ToArrayAsync(ct);
        var originalValue = outbound.BaseQty == produce.BaseQty ? produce.TotalCost
            : StockLedgerPrecision.Money(produce.TotalCost * outbound.BaseQty / produce.BaseQty);
        var currentValue = outbound.BaseQty == produce.BaseQty ? produceCost.TotalCost
            : StockLedgerPrecision.Money(produceCost.TotalCost * outbound.BaseQty / produce.BaseQty);
        if (activeRow is null || produceCost.RestatementFactId is not long produceActiveId
            || !produceRestatements.Any(r => r.Id == produceActiveId && r.ReplayStockPostingId == sourceRootId)
            || !await ProductionFinishedGoodCorrectionHistory.MatchesOutputAsync(db, outbound.Uid, produce.Uid, ct)
            || fgCost.TotalCost != currentValue
            || outbound.TotalCost != originalValue || reversal.TotalCost != currentValue
            || reversal.UnitCost != fgCost.UnitCost || currentValue == originalValue)
            throw ProductionReplayError("Corrected finished-good reversal does not match its output value.");
        var owners = await db.StockPostings.AsNoTracking().Where(p =>
            p.Id == ownerId || p.Id == reversalOwnerId || p.Id == sourceRootId).ToDictionaryAsync(p => p.Id, ct);
        if (owners.Count != 3 || !owners.TryGetValue(ownerId, out var owner)
            || !owners.TryGetValue(reversalOwnerId, out var reverseOwner)
            || !owners.TryGetValue(sourceRootId, out var sourceRoot)
            || sourceRoot.CompanyCode != company || sourceRoot.BranchCode != branch
            || sourceRoot.SealedAtUtc is null || sourceRoot.LedgerEpochId != outbound.LedgerEpochId
            || sourceRoot.SourceModule != "INVENTORY"
            || sourceRoot.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
            || sourceRoot.PostingRole != "PRIMARY" || sourceRoot.CommandType != $"IV_POST_{sourceRoot.SourceDocumentType}"
            || reverseOwner.ReversesPostingId != owner.Id || reverseOwner.SealedAtUtc is null
            || reverseOwner.PostingSequence <= sourceRoot.PostingSequence
            || await db.StockPostings.AsNoTracking().AnyAsync(p =>
                p.ReversesPostingId == sourceRoot.Id && p.SealedAtUtc != null, ct))
            throw ProductionReplayError("Corrected finished-good reversal has no active source receipt.");
        var facts = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .Where(f => f.ProductionMovementId == outbound.Uid || f.ProductionMovementId == reversal.Uid)
            .ToArrayAsync(ct);
        if (facts.Length != 2) throw ProductionReplayError("Corrected finished-good facts are incomplete.");
        var fact = facts.Single(f => f.ProductionMovementId == outbound.Uid);
        var reverseFact = facts.Single(f => f.ProductionMovementId == reversal.Uid);
        var history = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == fact.InventoryHistoryId, ct);
        var valuations = await db.StockValuationFacts.AsNoTracking().Where(v =>
            v.InventoryHistoryId == fact.InventoryHistoryId || v.InventoryHistoryId == reverseFact.InventoryHistoryId)
            .ToArrayAsync(ct);
        if (valuations.Length != 2) throw ProductionReplayError("Corrected finished-good valuation is incomplete.");
        var receipt = valuations.Single(v => v.InventoryHistoryId == fact.InventoryHistoryId);
        var physicalReverse = valuations.Single(v => v.InventoryHistoryId == reverseFact.InventoryHistoryId);
        var deltas = await db.StockValuationFacts.AsNoTracking().Where(v =>
            v.OriginalValuationFactId == receipt.Id && v.InventoryHistoryId == null).ToArrayAsync(ct);
        var activeDeltas = deltas.Where(d => d.ReversesValuationFactId is null
            && d.StockPostingId == sourceRootId).ToArray();
        if (activeDeltas.Length != 1)
            throw ProductionReplayError("Corrected finished-good value reversal is incomplete.");
        var delta = activeDeltas[0];
        var inverseDeltas = deltas.Where(d => d.ReversesValuationFactId == delta.Id).ToArray();
        if (inverseDeltas.Length != 1)
            throw ProductionReplayError("Corrected finished-good value reversal is incomplete.");
        var reverseDelta = inverseDeltas[0];
        var earlier = await ProductionFinishedGoodCorrectionHistory.ReadAsync(db, company, branch,
            outbound.Uid, activeRow.Id, delta.Id, ct, reverseDelta.Id);
        if (earlier is null)
            throw ProductionReplayError("Earlier finished-good corrections have invalid ownership.");
        var runs = await db.StockReplayRuns.AsNoTracking().Where(r =>
            r.RootStockPostingId == sourceRootId || r.RootStockPostingId == reversalOwnerId)
            .ToDictionaryAsync(r => r.RootStockPostingId, ct);
        var ids = new[] { delta.Id, reverseDelta.Id };
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact && ids.Contains(e.AffectedEntityId))
            .ToArrayAsync(ct);
        if (!runs.TryGetValue(sourceRootId, out var sourceRun)
            || !runs.TryGetValue(reversalOwnerId, out var reverseRun) || effects.Length != 2
            || receipt.BaseQty != outbound.BaseQty
            || delta.StockPostingId != sourceRootId || reverseDelta.StockPostingId != reversalOwnerId
            || delta.SignedValue != currentValue - originalValue
            || delta.ValuationSource != StockValuationSources.ProductionActual
            || delta.BaseCostAmount != delta.CostAmount || delta.ValuationVersion != receipt.ValuationVersion + 1
            || delta.UnitCost != StockLedgerPrecision.Money(delta.CostAmount / receipt.BaseQty)
            || reverseDelta.ReversesValuationFactId != delta.Id || reverseDelta.Direction != -delta.Direction
            || reverseDelta.CostAmount != delta.CostAmount || reverseDelta.BaseCostAmount != delta.BaseCostAmount
            || reverseDelta.BaseQty != 0m || reverseDelta.CostMethod != StockCostMethods.MovingAverage
            || reverseDelta.ValuationStatus != StockValuationStatuses.Reversed
            || reverseDelta.ValuationSource != StockValuationSources.OriginalReversal
            || reverseDelta.ValuationVersion != delta.ValuationVersion
            || reverseDelta.CompanyCode != company || reverseDelta.BranchCode != branch
            || reverseDelta.LedgerEpochId != outbound.LedgerEpochId || reverseDelta.ItemCode != delta.ItemCode
            || reverseDelta.EffectiveAt != delta.EffectiveAt || reverseDelta.BusinessDate != delta.BusinessDate
            || reverseDelta.PeriodKey != delta.PeriodKey || reverseDelta.SourceLineId != delta.SourceLineId
            || reverseDelta.SourceDocumentType != delta.SourceDocumentType || reverseDelta.SourceDocumentId != delta.SourceDocumentId
            || reverseDelta.SourceDocumentNo != delta.SourceDocumentNo || reverseDelta.SourceDocumentLine != delta.SourceDocumentLine
            || reverseDelta.MovementCode != delta.MovementCode || reverseDelta.BaseCurrency != delta.BaseCurrency
            || reverseDelta.BaseUom != delta.BaseUom || reverseDelta.WarehouseCode != delta.WarehouseCode
            || reverseDelta.LocationCode != delta.LocationCode || reverseDelta.LotId != delta.LotId
            || reverseDelta.LotNo != delta.LotNo || reverseDelta.ItemStatus != delta.ItemStatus
            || reverseDelta.FromBalLocId != delta.FromBalLocId || reverseDelta.ToBalLocId != delta.ToBalLocId
            || receipt.CostMethod != StockCostMethods.MovingAverage
            || sourceRun.RootRequestId != sourceRoot.RequestId || reverseRun.RootRequestId != reverseOwner.RequestId
            || sourceRun.RootDocumentType != sourceRoot.SourceDocumentType || sourceRun.RootDocumentId != sourceRoot.SourceDocumentId
            || reverseRun.RootDocumentType != reverseOwner.SourceDocumentType || reverseRun.RootDocumentId != reverseOwner.SourceDocumentId
            || effects.Any(e => e.CompanyCode != company || e.BranchCode != branch
                || e.EffectKind != StockReplayEffectTypes.FinishedGoodReceiptValue
                || e.BusinessOwnerPostingId != ownerId || e.OriginalEntityId != receipt.Id)
            || effects.Count(e => e.AffectedEntityId == delta.Id && e.RootStockPostingId == sourceRootId && e.ReplayRunId == sourceRun.Id) != 1
            || effects.Count(e => e.AffectedEntityId == reverseDelta.Id && e.RootStockPostingId == reversalOwnerId && e.ReplayRunId == reverseRun.Id) != 1)
            throw ProductionReplayError("Corrected finished-good value reversal has invalid ownership.");
        StockReplayWacValueEventFactory.CreateReceiptRestatement(delta, receipt, history, owner, sourceRoot);
        var state = await db.StockCostStates.AsNoTracking().SingleOrDefaultAsync(s =>
            s.CompanyCode == company && s.BranchCode == branch && s.ItemCode == receipt.ItemCode, ct);
        var piles = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && b.ICode == receipt.ItemCode).ToArrayAsync(ct);
        if (state is null || state.CostMethod != StockCostMethods.MovingAverage
            || state.OnHandBaseQty != 0m || state.InventoryValue != 0m
            || state.LastValuationFactId != physicalReverse.Id || state.LastPostingSequence != reverseOwner.PostingSequence
            || piles.Length != 1 || piles[0].Id != fact.DestinationBalanceId || piles[0].StdQty != 0m)
            throw ProductionReplayError("Reversed finished goods have later stock activity requiring full replay.");
        var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            owner.PostingSequence, $"H{history.Id:D10}", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db, company, branch, receipt.ItemCode, key, ct);
        if (tail.LaterEvents.Count != 0 || tail.CurrentQuantity != 0m || tail.CurrentValue != 0m)
            throw ProductionReplayError("Reversed finished goods have a later WAC event requiring replay.");
        var hash = StockPostingFingerprint.Create(new { sourceRootId, outbound.Uid, ReversalId = reversal.Uid }, new
        {
            activeRow.Id, DeltaId = delta.Id, ReverseDeltaId = reverseDelta.Id, earlier.Count, earlier.LastFactId,
            SourceRunId = sourceRun.Id, ReverseRunId = reverseRun.Id,
            OwnerSequence = owner.PostingSequence, ReverseSequence = reverseOwner.PostingSequence,
            state.LastValuationFactId, state.LastPostingSequence, StateVersion = Convert.ToHexString(state.RowVersion ?? []),
            PileId = piles[0].Id, piles[0].StdQty, fgCost.TotalCost, fgCost.UnitCost
        }).SourceSnapshotHash;
        return new(sourceRootId, outbound.Uid, reversal.Uid, ownerId, reversalOwnerId,
            activeRow.Id, originalValue, outbound.UnitCost, currentValue, fgCost.UnitCost,
            outbound.BaseQty, owner.EffectiveAt, reverseOwner.EffectiveAt, hash);
    }

    private static async Task VerifyCorrectedReversedFinishedGoodProjectionAsync(
        AppDbContext db, string company, string branch, ProductionLeafReplayPlan plan,
        StockReplayWacStep issueStep, CancellationToken ct)
    {
        var output = plan.Output!;
        var corrected = output.CorrectedReversedFinishedGood!;
        var issue = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == plan.MovementId, ct);
        var consume = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == output.ConsumeMovementId, ct);
        var produce = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == output.ProduceMovementId, ct);
        var costs = await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, [consume.Uid, produce.Uid], ct);
        var desiredIssue = -issueStep.SignedValue;
        var desiredConsume = consume.BaseQty == issue.BaseQty ? desiredIssue
            : StockLedgerPrecision.Money(desiredIssue * consume.BaseQty / issue.BaseQty);
        var desiredProduce = StockLedgerPrecision.Money(costs[produce.Uid].TotalCost
            + desiredConsume - costs[consume.Uid].TotalCost);
        if (desiredIssue < 0m || string.Equals(issue.ItemCode, produce.ItemCode, StringComparison.OrdinalIgnoreCase)
            || desiredProduce != produce.TotalCost
            || corrected.OriginalValue != (corrected.Quantity == produce.BaseQty ? desiredProduce
                : StockLedgerPrecision.Money(desiredProduce * corrected.Quantity / produce.BaseQty)))
            throw ProductionReplayError("The reversed finished-good value needs a wider Production replay.");
    }

    private static async Task ApplyCorrectedReversedFinishedGoodAsync(StockPostingContext context,
        CorrectedReversedFinishedGoodPlan plan, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var db = context.Db;
        var run = await db.StockReplayRuns.AsNoTracking().SingleAsync(r => r.RootStockPostingId == context.Posting.Id, ct);
        if (db.Database.CurrentTransaction is null || context.CostMethod != StockCostMethods.MovingAverage
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
            || context.Posting.PostingRole != "REVERSAL"
            || context.Posting.ReversesPostingId != plan.SourceRootId
            || run.CompanyCode != context.CompanyCode || run.BranchCode != context.BranchCode
            || run.RootRequestId != context.Posting.RequestId
            || run.RootDocumentType != context.Posting.SourceDocumentType || run.RootDocumentId != context.Posting.SourceDocumentId
            || string.IsNullOrWhiteSpace(run.PlanHash) || run.EarliestEffectiveAt > plan.OriginalAt
            || run.LatestEffectiveAt < plan.ReversalAt)
            throw ProductionReplayError("The reversed finished-good correction does not match its replay root.");
        var closed = await IvPeriodCloseGuard.EnsureOpenAsync(db, context.CompanyCode, context.BranchCode, plan.OriginalAt, ct)
            ?? await IvPeriodCloseGuard.EnsureOpenAsync(db, context.CompanyCode, context.BranchCode, plan.ReversalAt, ct);
        if (closed is not null) throw new StockLedgerException(new(StockLedgerErrorCodes.ClosedPeriod, closed));
        var costs = await ProductionEffectiveCostResolver.ResolveAsync(db, context.CompanyCode, context.BranchCode,
            [plan.OriginalMovementId, plan.ReversalMovementId], ct);
        if (costs[plan.OriginalMovementId].RestatementFactId != plan.ActiveRestatementId
            || costs[plan.OriginalMovementId].TotalCost != plan.CurrentValue
            || costs[plan.ReversalMovementId].RestatementFactId is not null
            || costs[plan.ReversalMovementId].TotalCost != plan.CurrentValue)
            throw ProductionReplayError("The reversed finished-good cost changed after planning.");
        var now = DateTime.UtcNow;
        ProductionCostRestatementFact NewFact(long movementId, DateTime effectiveAt,
            decimal prior, decimal desired, decimal unit, long? reverses) => new()
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            ReplayRequestId = context.Posting.RequestId, ReplayStockPostingId = context.Posting.Id,
            ProductionBalLotMovementId = movementId, EffectiveAt = effectiveAt,
            PriorEffectiveTotalCost = prior, CorrectedEffectiveTotalCost = desired,
            CorrectedUnitCost = unit, DeltaCost = StockLedgerPrecision.Money(desired - prior),
            ReversesRestatementFactId = reverses, CreatedAtUtc = now,
            CreatedBy = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId
        };
        var restoreOriginal = NewFact(plan.OriginalMovementId, plan.OriginalAt,
            plan.CurrentValue, plan.OriginalValue, plan.OriginalUnitCost, plan.ActiveRestatementId);
        var restoreReversal = NewFact(plan.ReversalMovementId, plan.ReversalAt,
            plan.CurrentValue, plan.OriginalValue, plan.OriginalUnitCost, null);
        db.ProductionCostRestatementFacts.AddRange(restoreOriginal, restoreReversal);
        await db.SaveChangesAsync(ct);
        foreach (var (fact, ownerId) in new[] { (restoreOriginal, plan.OriginalOwnerId), (restoreReversal, plan.ReversalOwnerId) })
            db.StockReplayEffects.Add(new StockReplayEffect
            {
                CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
                ReplayRunId = run.Id, RootStockPostingId = context.Posting.Id,
                BusinessOwnerPostingId = ownerId, EffectKind = "PRODUCTION_COST_RESTATEMENT",
                AffectedEntityType = "PRODUCTION_COST_RESTATEMENT_FACT", AffectedEntityId = fact.Id,
                OriginalEntityId = fact.ProductionBalLotMovementId, CreatedAtUtc = now
            });
        await db.SaveChangesAsync(ct);
        var final = await ProductionEffectiveCostResolver.ResolveAsync(db, context.CompanyCode, context.BranchCode,
            [plan.OriginalMovementId, plan.ReversalMovementId], ct);
        if (final[plan.OriginalMovementId].TotalCost != plan.OriginalValue
            || final[plan.ReversalMovementId].TotalCost != plan.OriginalValue)
            throw ProductionReplayError("The reversed finished-good costs did not reconcile.");
    }
}
