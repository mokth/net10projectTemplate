using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private const string ReversedIssueValueEffect = "PRODUCTION_ISSUE_REVERSAL_VALUE";
    private const string ReversedIssueValueMovement = "PRODUCTION_ISSUE_REPLAY_VALUE";

    private sealed record ExistingWacPairCorrection(
        decimal Cost, long IssueDeltaId, long ReversalDeltaId,
        long IssueRestatementId, long ReversalRestatementId);

    // A correction is usable only when both Inventory value legs and both
    // Production restatements belong to the same sealed receipt replay.
    private static async Task<ExistingWacPairCorrection?> ReadExistingWacReversedIssueCorrectionAsync(
        AppDbContext db, string company, string branch, StockValuationFact issueFact,
        StockValuationFact reversalFact, ProductionBalLotMovement issueMovement,
        ProductionBalLotMovement reversalMovement, StockValuationFact[] values,
        ProductionCostRestatementFact[] production, long receiptOwnerId, CancellationToken ct)
    {
        if (values.Length != 2 || production.Length != 2
            || values.Any(v => v.StockPostingId != receiptOwnerId)
            || production.Any(p => p.ReplayStockPostingId != receiptOwnerId))
            return null;
        var issueDeltas = values.Where(v => v.OriginalValuationFactId == issueFact.Id).ToArray();
        var reverseDeltas = values.Where(v => v.OriginalValuationFactId == reversalFact.Id).ToArray();
        var issueCosts = production.Where(p => p.ProductionBalLotMovementId == issueMovement.Uid).ToArray();
        var reverseCosts = production.Where(p => p.ProductionBalLotMovementId == reversalMovement.Uid).ToArray();
        if (issueDeltas.Length != 1 || reverseDeltas.Length != 1 || issueCosts.Length != 1 || reverseCosts.Length != 1)
            return null;
        var issueDelta = issueDeltas[0];
        var reverseDelta = reverseDeltas[0];
        var issueCost = issueCosts[0];
        var reverseCost = reverseCosts[0];
        var root = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == receiptOwnerId, ct);
        var run = await db.StockReplayRuns.AsNoTracking().SingleOrDefaultAsync(r =>
            r.RootStockPostingId == receiptOwnerId && r.CompanyCode == company && r.BranchCode == branch, ct);
        if (root is null || run is null || root.CompanyCode != company || root.BranchCode != branch
            || root.SealedAtUtc is null || root.PostingRole != "PRIMARY"
            || root.LedgerEpochId != issueFact.LedgerEpochId
            || run.RootRequestId != root.RequestId || run.RootDocumentType != root.SourceDocumentType
            || run.RootDocumentId != root.SourceDocumentId
            || run.EarliestEffectiveAt > issueFact.EffectiveAt || run.LatestEffectiveAt < reversalFact.EffectiveAt
            || !MatchesValue(issueDelta, issueFact) || !MatchesValue(reverseDelta, reversalFact)
            || !MatchesProduction(issueCost, issueMovement) || !MatchesProduction(reverseCost, reversalMovement)
            || issueCost.CorrectedEffectiveTotalCost != reverseCost.CorrectedEffectiveTotalCost
            || issueCost.CorrectedUnitCost != reverseCost.CorrectedUnitCost)
            return null;
        var difference = StockLedgerPrecision.Money(issueCost.CorrectedEffectiveTotalCost - issueFact.CostAmount);
        if (difference == 0m || issueDelta.SignedValue != -difference || reverseDelta.SignedValue != difference)
            return null;

        var valueIds = values.Select(v => v.Id).ToArray();
        var productionIds = production.Select(p => p.Id).ToArray();
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact && valueIds.Contains(e.AffectedEntityId)
            || e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT" && productionIds.Contains(e.AffectedEntityId))
            .ToArrayAsync(ct);
        if (effects.Length != 4 || effects.Any(e => e.CompanyCode != company || e.BranchCode != branch
                || e.ReplayRunId != run.Id || e.RootStockPostingId != root.Id)
            || !HasEffect(issueDelta.Id, StockReplayEffectTypes.StockValuationFact, ReversedIssueValueEffect,
                issueFact.StockPostingId, issueFact.Id)
            || !HasEffect(reverseDelta.Id, StockReplayEffectTypes.StockValuationFact, ReversedIssueValueEffect,
                reversalFact.StockPostingId, reversalFact.Id)
            || !HasEffect(issueCost.Id, "PRODUCTION_COST_RESTATEMENT_FACT", "PRODUCTION_COST_RESTATEMENT",
                issueMovement.StockPostingId!.Value, issueMovement.Uid)
            || !HasEffect(reverseCost.Id, "PRODUCTION_COST_RESTATEMENT_FACT", "PRODUCTION_COST_RESTATEMENT",
                reversalMovement.StockPostingId!.Value, reversalMovement.Uid))
            return null;
        try
        {
            var effective = await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch,
                [issueMovement.Uid, reversalMovement.Uid], ct);
            if (effective[issueMovement.Uid].RestatementFactId != issueCost.Id
                || effective[reversalMovement.Uid].RestatementFactId != reverseCost.Id
                || effective[issueMovement.Uid].TotalCost != issueCost.CorrectedEffectiveTotalCost
                || effective[reversalMovement.Uid].TotalCost != issueCost.CorrectedEffectiveTotalCost)
                return null;
        }
        catch (StockLedgerException)
        {
            return null;
        }
        return new(issueCost.CorrectedEffectiveTotalCost, issueDelta.Id, reverseDelta.Id,
            issueCost.Id, reverseCost.Id);

        bool MatchesValue(StockValuationFact value, StockValuationFact original) =>
            value.Id > 0 && value.CompanyCode == company && value.BranchCode == branch
            && value.LedgerEpochId == original.LedgerEpochId && value.ItemCode == original.ItemCode
            && value.InventoryHistoryId is null && value.BaseQty == 0m
            && value.CostMethod == StockCostMethods.MovingAverage
            && value.MovementCode == ReversedIssueValueMovement
            && value.ValuationStatus == StockValuationStatuses.Valued
            && value.ValuationSource == StockValuationSources.MovingAverage
            && value.ReversesValuationFactId is null && value.Direction is -1 or 1
            && value.CostAmount > 0m && value.BaseCostAmount == value.CostAmount
            && value.EffectiveAt == original.EffectiveAt && value.BusinessDate == original.BusinessDate
            && value.PeriodKey == original.PeriodKey && value.BaseUom == original.BaseUom
            && value.FromBalLocId == original.FromBalLocId && value.ToBalLocId == original.ToBalLocId
            && value.WarehouseCode == original.WarehouseCode && value.LocationCode == original.LocationCode
            && value.LotId == original.LotId && value.LotNo == original.LotNo && value.ItemStatus == original.ItemStatus
            && value.BaseCurrency == original.BaseCurrency && value.SourceLineId == original.SourceLineId
            && value.SourceDocumentType == original.SourceDocumentType
            && value.SourceDocumentId == original.SourceDocumentId
            && value.SourceDocumentNo == original.SourceDocumentNo
            && value.SourceDocumentLine == original.SourceDocumentLine;

        bool MatchesProduction(ProductionCostRestatementFact cost, ProductionBalLotMovement movement) =>
            cost.Id > 0 && cost.CompanyCode == company && cost.BranchCode == branch
            && cost.ReplayRequestId == root.RequestId && cost.EffectiveAt == movement.MovementDate
            && cost.ReversesRestatementFactId is null && cost.PriorEffectiveTotalCost == movement.TotalCost
            && cost.CorrectedEffectiveTotalCost >= 0m
            && cost.DeltaCost == cost.CorrectedEffectiveTotalCost - cost.PriorEffectiveTotalCost
            && cost.CorrectedUnitCost == StockLedgerPrecision.Money(cost.CorrectedEffectiveTotalCost / movement.BaseQty);

        bool HasEffect(long affected, string type, string kind, long owner, long original) =>
            effects.Count(e => e.AffectedEntityId == affected && e.AffectedEntityType == type
                && e.EffectKind == kind && e.BusinessOwnerPostingId == owner && e.OriginalEntityId == original) == 1;
    }

    private static async Task AppendWacReversedIssueCorrectionAsync(
        StockPostingContext context, WacReversedIssueProof proof,
        string inventoryPlanHash, long branchWatermark, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var db = context.Db;
        var rollback = context.Posting.PostingRole == "REVERSAL";
        if (context.CostMethod != StockCostMethods.MovingAverage
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
            || context.Posting.PostingRole is not ("PRIMARY" or "REVERSAL")
            || proof.IsCostNeutral || proof.IssueQuantity <= 0m
            || proof.ProjectedIssueValue < 0m || string.IsNullOrWhiteSpace(inventoryPlanHash)
            || proof.IssueDeltaId.HasValue != proof.ReversalDeltaId.HasValue
            || proof.IssueDeltaId.HasValue != proof.IssueRestatementId.HasValue
            || proof.IssueDeltaId.HasValue != proof.ReversalRestatementId.HasValue)
            throw InvalidPair("The reversed issue correction has invalid replay authority.");

        var issueFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(f => f.Id == proof.IssueFactId, ct);
        var reverseFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(f => f.Id == proof.ReversalFactId, ct);
        var issue = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == proof.IssueMovementId, ct);
        var reverse = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == proof.ReversalMovementId, ct);
        var rootFacts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.StockPostingId == context.Posting.Id).ToArrayAsync(ct);
        var rootHistories = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.StockPostingId == context.Posting.Id).ToArrayAsync(ct);
        var state = await db.StockCostStates.SingleAsync(s => s.CompanyCode == context.CompanyCode
            && s.BranchCode == context.BranchCode && s.ItemCode == issueFact.ItemCode, ct);
        var balance = await db.IvBalLocs.AsNoTracking().SingleAsync(b => b.Id == proof.BalanceId
            && b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode, ct);
        if (rootFacts.Length != 1 || rootHistories.Length != 1
            || rootFacts[0].InventoryHistoryId != rootHistories[0].Id
            || rootFacts[0].SignedQty != (rollback ? -proof.ReceiptQuantity : proof.ReceiptQuantity)
            || rootFacts[0].SignedValue != (rollback ? -proof.ReceiptValue : proof.ReceiptValue)
            || rootFacts[0].ItemCode != issueFact.ItemCode
            || rootFacts[0].LedgerEpochId != context.Epoch.Id
            || rootHistories[0].EntryRole != (rollback ? "REVERSAL" : "ORIGINAL")
            || issueFact.InventoryHistoryId != proof.IssueHistoryId
            || reverseFact.InventoryHistoryId != proof.ReversalHistoryId
            || issueFact.CompanyCode != context.CompanyCode || issueFact.BranchCode != context.BranchCode
            || reverseFact.CompanyCode != context.CompanyCode || reverseFact.BranchCode != context.BranchCode
            || issueFact.LedgerEpochId != context.Epoch.Id || reverseFact.LedgerEpochId != context.Epoch.Id
            || issue.StockPostingId != issueFact.StockPostingId || reverse.StockPostingId != reverseFact.StockPostingId
            || issue.BaseQty != proof.IssueQuantity || reverse.BaseQty != proof.IssueQuantity
            || issue.MovementDate != proof.IssueEffectiveAt || reverse.MovementDate != proof.ReversalEffectiveAt
            || state.OnHandBaseQty != proof.ProjectedQuantity || state.InventoryValue != proof.ProjectedValue
            || balance.StdQty != proof.ProjectedQuantity
            || await db.StockReplayRuns.AsNoTracking().AnyAsync(r => r.RootStockPostingId == context.Posting.Id, ct))
            throw InvalidPair("The receipt or reversed issue changed after replay planning.");
        var currentCosts = await ProductionReplayEffectiveCostReader.ReadAsync(db,
            context.CompanyCode, context.BranchCode, [issue.Uid, reverse.Uid], ct);
        if (currentCosts[issue.Uid].TotalCost != proof.OriginalIssueValue
            || currentCosts[reverse.Uid].TotalCost != proof.OriginalIssueValue
            || currentCosts[issue.Uid].RestatementFactId != proof.IssueRestatementId
            || currentCosts[reverse.Uid].RestatementFactId != proof.ReversalRestatementId)
            throw InvalidPair("The reversed Production issue cost changed after replay planning.");

        var priorIds = new[] { proof.IssueDeltaId, proof.ReversalDeltaId }
            .Where(id => id.HasValue).Select(id => id!.Value).ToArray();
        var priorValues = await db.StockValuationFacts.AsNoTracking()
            .Where(f => priorIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        if (priorValues.Count != priorIds.Length)
            throw InvalidPair("The prior issue value corrections are incomplete.");
        var run = new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt,
            LatestEffectiveAt = new[] { proof.ReversalEffectiveAt, proof.IssueEffectiveAt,
                proof.Output?.LatestEffectiveAt ?? proof.IssueEffectiveAt }.Max(),
            PlanWatermark = branchWatermark,
            PlanHash = StockPostingFingerprint.Create(new { inventoryPlanHash, proof }, new { }).RequestFingerprint,
            AffectedHistoryCount = 2, AffectedItemCount = 1,
            AffectedProductionMovementCount = proof.Output is null ? 2 : 6,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var valueWrites = new List<(StockValuationFact Fact, StockValuationFact Owner)>();
        var costWrites = new List<(ProductionCostRestatementFact Fact, ProductionBalLotMovement Owner)>();
        var nextLine = 2;
        AddPair(issueFact, issue, proof.IssueDeltaId, proof.IssueRestatementId);
        AddPair(reverseFact, reverse, proof.ReversalDeltaId, proof.ReversalRestatementId);
        if (valueWrites.Sum(w => w.Fact.SignedValue) != 0m
            || valueWrites.Any(w => w.Fact.BaseQty != 0m))
            throw InvalidPair("The issue and reversal value corrections do not balance.");
        db.StockValuationFacts.AddRange(valueWrites.Select(w => w.Fact));
        db.ProductionCostRestatementFacts.AddRange(costWrites.Select(w => w.Fact));
        await db.SaveChangesAsync(ct);
        db.StockReplayEffects.AddRange(valueWrites.Select(w => new StockReplayEffect
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            ReplayRunId = run.Id, RootStockPostingId = context.Posting.Id,
            BusinessOwnerPostingId = w.Owner.StockPostingId,
            EffectKind = ReversedIssueValueEffect, AffectedEntityType = StockReplayEffectTypes.StockValuationFact,
            AffectedEntityId = w.Fact.Id, OriginalEntityId = w.Owner.Id, CreatedAtUtc = now
        }));
        db.StockReplayEffects.AddRange(costWrites.Select(w => new StockReplayEffect
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            ReplayRunId = run.Id, RootStockPostingId = context.Posting.Id,
            BusinessOwnerPostingId = w.Owner.StockPostingId!.Value,
            EffectKind = "PRODUCTION_COST_RESTATEMENT", AffectedEntityType = "PRODUCTION_COST_RESTATEMENT_FACT",
            AffectedEntityId = w.Fact.Id, OriginalEntityId = w.Owner.Uid, CreatedAtUtc = now
        }));
        if (valueWrites.Count != 0)
            state.LastValuationFactId = valueWrites.MaxBy(w => w.Fact.PostingLineNo).Fact.Id;
        await db.SaveChangesAsync(ct);
        var finalCosts = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, [issue.Uid, reverse.Uid], ct);
        if (finalCosts[issue.Uid].TotalCost != proof.ProjectedIssueValue
            || finalCosts[reverse.Uid].TotalCost != proof.ProjectedIssueValue)
            throw InvalidPair("The issue and reversal Production costs did not reconcile.");
        if (proof.Output is { } output)
        {
            var consume = await db.ProductionBalLotMovements.AsNoTracking()
                .SingleAsync(m => m.Uid == output.ConsumeMovementId, ct);
            var produce = await db.ProductionBalLotMovements.AsNoTracking()
                .SingleAsync(m => m.Uid == output.ProduceMovementId, ct);
            var projectedConsume = ReversedOutputConsumeValue(
                proof.ProjectedIssueValue, output.ConsumedQuantity, proof.IssueQuantity);
            await ProductionReversedOutputCostCorrectionWriter.AppendWithinRootAsync(context,
                consume, produce, output.ConsumeCost, output.ProduceCost,
                projectedConsume,
                StockLedgerPrecision.Money(projectedConsume + output.UnchangedInputAndConversionValue), ct);
        }
        return;

        void AddPair(StockValuationFact original, ProductionBalLotMovement movement,
            long? priorValueId, long? priorRestatementId)
        {
            if (priorValueId is long priorId)
            {
                var prior = priorValues[priorId];
                var inverse = NewValue(original);
                inverse.Direction = -prior.Direction;
                inverse.CostAmount = inverse.BaseCostAmount = prior.CostAmount;
                inverse.UnitCost = prior.UnitCost;
                inverse.ValuationStatus = StockValuationStatuses.Reversed;
                inverse.ValuationSource = StockValuationSources.OriginalReversal;
                inverse.ValuationVersion = prior.ValuationVersion;
                inverse.ReversesValuationFactId = prior.Id;
                valueWrites.Add((inverse, original));
            }
            var desiredDelta = StockLedgerPrecision.Money(proof.ProjectedIssueValue - original.CostAmount);
            if (desiredDelta != 0m)
            {
                var replacement = NewValue(original);
                replacement.Direction = original.Direction * Math.Sign(desiredDelta);
                replacement.CostAmount = replacement.BaseCostAmount = Math.Abs(desiredDelta);
                replacement.UnitCost = StockLedgerPrecision.Money(replacement.CostAmount / proof.IssueQuantity);
                replacement.ValuationStatus = StockValuationStatuses.Valued;
                replacement.ValuationSource = StockValuationSources.MovingAverage;
                replacement.ValuationVersion = checked(original.ValuationVersion + 1);
                valueWrites.Add((replacement, original));
            }
            if (priorRestatementId is long previous)
                costWrites.Add((NewCost(movement, proof.OriginalIssueValue,
                    movement.TotalCost, movement.UnitCost, previous), movement));
            if (proof.ProjectedIssueValue != movement.TotalCost)
                costWrites.Add((NewCost(movement, movement.TotalCost, proof.ProjectedIssueValue,
                    StockLedgerPrecision.Money(proof.ProjectedIssueValue / movement.BaseQty), null), movement));
        }

        StockValuationFact NewValue(StockValuationFact original) => new()
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id, StockPostingId = context.Posting.Id,
            PostingLineNo = nextLine++, SplitOrdinal = 0,
            SourceLineId = original.SourceLineId, SourceDocumentType = original.SourceDocumentType,
            SourceDocumentId = original.SourceDocumentId, SourceDocumentNo = original.SourceDocumentNo,
            SourceDocumentLine = original.SourceDocumentLine, EffectiveAt = original.EffectiveAt,
            BusinessDate = original.BusinessDate, PeriodKey = original.PeriodKey,
            ItemCode = original.ItemCode, WarehouseCode = original.WarehouseCode,
            LocationCode = original.LocationCode, LotId = original.LotId, LotNo = original.LotNo,
            ItemStatus = original.ItemStatus, BaseUom = original.BaseUom,
            MovementCode = ReversedIssueValueMovement, BaseQty = 0m,
            CostMethod = StockCostMethods.MovingAverage, BaseCurrency = original.BaseCurrency,
            FromBalLocId = original.FromBalLocId, ToBalLocId = original.ToBalLocId,
            OriginalValuationFactId = original.Id, CreatedAtUtc = now,
            CreatedBy = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId
        };

        ProductionCostRestatementFact NewCost(ProductionBalLotMovement movement, decimal prior,
            decimal corrected, decimal unit, long? reverses) => new()
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            ReplayRequestId = context.Posting.RequestId, ReplayStockPostingId = context.Posting.Id,
            ProductionBalLotMovementId = movement.Uid, EffectiveAt = movement.MovementDate,
            PriorEffectiveTotalCost = prior, CorrectedEffectiveTotalCost = corrected,
            CorrectedUnitCost = unit, DeltaCost = StockLedgerPrecision.Money(corrected - prior),
            ReversesRestatementFactId = reverses, CreatedAtUtc = now,
            CreatedBy = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId
        };
    }

    private static StockLedgerException InvalidPair(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static async Task<bool> ValidateClearedWacReversedIssueCyclesAsync(
        AppDbContext db, string company, string branch, IvTrxBatch batch,
        IvTrxHistory issueHistory, IvTrxHistory reverseHistory,
        StockValuationFact issueFact, StockValuationFact reverseFact,
        ProductionBalLotMovement issueMovement, ProductionBalLotMovement reverseMovement,
        IvTrxHistory[] histories, StockValuationFact[] receiptFacts,
        StockValuationFact[] valueFacts, ProductionCostRestatementFact[] productionFacts,
        CancellationToken ct)
    {
        var originals = histories.Where(h => h.EntryRole == "ORIGINAL").ToArray();
        var reversals = histories.Where(h => h.EntryRole == "REVERSAL").ToArray();
        if (originals.Length == 0 || originals.Length != reversals.Length
            || receiptFacts.Length != histories.Length
            || valueFacts.Length != originals.Length * 4
            || productionFacts.Length != originals.Length * 4)
            return false;
        var rootIds = histories.Select(h => h.StockPostingId!.Value).ToArray();
        var roots = await db.StockPostings.AsNoTracking().Where(p => rootIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var runs = await db.StockReplayRuns.AsNoTracking().Where(r => rootIds.Contains(r.RootStockPostingId))
            .ToDictionaryAsync(r => r.RootStockPostingId, ct);
        var valueIds = valueFacts.Select(f => f.Id).ToArray();
        var productionIds = productionFacts.Select(f => f.Id).ToArray();
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact && valueIds.Contains(e.AffectedEntityId)
            || e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT" && productionIds.Contains(e.AffectedEntityId))
            .ToArrayAsync(ct);
        if (roots.Count != rootIds.Length || runs.Count != rootIds.Length
            || effects.Length != valueFacts.Length + productionFacts.Length)
            return false;
        try
        {
            _ = await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch,
                [issueMovement.Uid, reverseMovement.Uid], ct);
        }
        catch (StockLedgerException)
        {
            return false;
        }

        foreach (var original in originals)
        {
            var matching = reversals.Where(h => h.ReversesHistoryId == original.Id).ToArray();
            if (matching.Length != 1) return false;
            var reversed = matching[0];
            var owner = roots[original.StockPostingId!.Value];
            var reverseOwner = roots[reversed.StockPostingId!.Value];
            if (original.LedgerVersion != 2 || original.LedgerEpochId != issueFact.LedgerEpochId
                || original.BatchStatus != IvBatchStatuses.Posted || original.ReversesHistoryId is not null
                || original.TrxDtTime != batch.TrxDtTime || original.ToBalLocId != issueHistory.FromBalLocId
                || original.FromBalLocId is not null || original.ToStdQty is not > 0m
                || original.ToStdUom != issueFact.BaseUom
                || reversed.LedgerVersion != 2 || reversed.LedgerEpochId != original.LedgerEpochId
                || reversed.BatchStatus != IvBatchStatuses.Posted || reversed.TrxDtTime != original.TrxDtTime
                || reversed.DocumentRevision != original.DocumentRevision || reversed.TrxLineNo != original.TrxLineNo
                || reversed.FromBalLocId != original.ToBalLocId || reversed.ToBalLocId is not null
                || reversed.FrStdQty != original.ToStdQty || reversed.FrStdUom != original.ToStdUom
                || !MatchesRoot(owner, "PRIMARY", original) || !MatchesRoot(reverseOwner, "REVERSAL", reversed)
                || reverseOwner.ReversesPostingId != owner.Id || reverseOwner.PostingSequence <= owner.PostingSequence)
                return false;
            var posted = receiptFacts.Where(f => f.InventoryHistoryId == original.Id).ToArray();
            var removed = receiptFacts.Where(f => f.InventoryHistoryId == reversed.Id).ToArray();
            if (posted.Length != 1 || removed.Length != 1) return false;
            var postedFact = posted[0];
            var removedFact = removed[0];
            if (postedFact.StockPostingId != owner.Id || postedFact.Direction != 1
                || postedFact.BaseQty != original.ToStdQty || postedFact.ToBalLocId != original.ToBalLocId
                || postedFact.LedgerEpochId != issueFact.LedgerEpochId || postedFact.EffectiveAt != original.TrxDtTime
                || postedFact.BaseUom != original.ToStdUom || postedFact.CostAmount != postedFact.BaseCostAmount
                || postedFact.CostMethod != StockCostMethods.MovingAverage
                || postedFact.ValuationStatus != StockValuationStatuses.Valued
                || postedFact.ReversesValuationFactId is not null || postedFact.OriginalValuationFactId is not null
                || removedFact.StockPostingId != reverseOwner.Id || removedFact.Direction != -1
                || removedFact.BaseQty != postedFact.BaseQty || removedFact.CostAmount != postedFact.CostAmount
                || removedFact.BaseCostAmount != postedFact.BaseCostAmount
                || removedFact.LedgerEpochId != postedFact.LedgerEpochId || removedFact.EffectiveAt != reversed.TrxDtTime
                || removedFact.BaseUom != postedFact.BaseUom || removedFact.CostMethod != postedFact.CostMethod
                || removedFact.FromBalLocId != original.ToBalLocId || removedFact.OriginalValuationFactId != postedFact.Id
                || removedFact.ReversesValuationFactId != postedFact.Id
                || removedFact.ValuationStatus != StockValuationStatuses.Reversed)
                return false;

            var postedValues = valueFacts.Where(f => f.StockPostingId == owner.Id).ToArray();
            var removedValues = valueFacts.Where(f => f.StockPostingId == reverseOwner.Id).ToArray();
            var postedCosts = productionFacts.Where(f => f.ReplayStockPostingId == owner.Id).ToArray();
            var removedCosts = productionFacts.Where(f => f.ReplayStockPostingId == reverseOwner.Id).ToArray();
            if (postedValues.Length != 2 || removedValues.Length != 2
                || postedCosts.Length != 2 || removedCosts.Length != 2
                || !MatchesLeg(issueFact, issueMovement) || !MatchesLeg(reverseFact, reverseMovement))
                return false;
            continue;

            bool MatchesLeg(StockValuationFact baseline, ProductionBalLotMovement movement)
            {
                var originalValues = postedValues.Where(f => f.OriginalValuationFactId == baseline.Id).ToArray();
                var inverseValues = removedValues.Where(f => f.OriginalValuationFactId == baseline.Id).ToArray();
                var originalCosts = postedCosts.Where(f => f.ProductionBalLotMovementId == movement.Uid).ToArray();
                var inverseCosts = removedCosts.Where(f => f.ProductionBalLotMovementId == movement.Uid).ToArray();
                if (originalValues.Length != 1 || inverseValues.Length != 1
                    || originalCosts.Length != 1 || inverseCosts.Length != 1) return false;
                var value = originalValues[0];
                var inverse = inverseValues[0];
                var cost = originalCosts[0];
                var inverseCost = inverseCosts[0];
                if (!MatchesValue(value, baseline) || !MatchesValue(inverse, baseline)
                    || value.ValuationStatus != StockValuationStatuses.Valued
                    || value.ValuationSource != StockValuationSources.MovingAverage || value.ReversesValuationFactId is not null
                    || inverse.ValuationStatus != StockValuationStatuses.Reversed || inverse.ReversesValuationFactId != value.Id
                    || inverse.ValuationSource != StockValuationSources.OriginalReversal
                    || inverse.Direction != -value.Direction || inverse.CostAmount != value.CostAmount
                    || cost.ReversesRestatementFactId is not null || cost.PriorEffectiveTotalCost != movement.TotalCost
                    || inverseCost.ReversesRestatementFactId != cost.Id
                    || inverseCost.PriorEffectiveTotalCost != cost.CorrectedEffectiveTotalCost
                    || inverseCost.CorrectedEffectiveTotalCost != movement.TotalCost
                    || value.SignedValue != baseline.Direction * cost.DeltaCost
                    || !HasEffect(value.Id, StockReplayEffectTypes.StockValuationFact, ReversedIssueValueEffect,
                        owner.Id, baseline.StockPostingId, baseline.Id)
                    || !HasEffect(inverse.Id, StockReplayEffectTypes.StockValuationFact, ReversedIssueValueEffect,
                        reverseOwner.Id, baseline.StockPostingId, baseline.Id)
                    || !HasEffect(cost.Id, "PRODUCTION_COST_RESTATEMENT_FACT", "PRODUCTION_COST_RESTATEMENT",
                        owner.Id, movement.StockPostingId!.Value, movement.Uid)
                    || !HasEffect(inverseCost.Id, "PRODUCTION_COST_RESTATEMENT_FACT", "PRODUCTION_COST_RESTATEMENT",
                        reverseOwner.Id, movement.StockPostingId!.Value, movement.Uid))
                    return false;
                return true;
            }
        }
        return reversals.All(r => originals.Count(o => o.Id == r.ReversesHistoryId) == 1);

        bool MatchesRoot(StockPosting root, string role, IvTrxHistory history) =>
            root.CompanyCode == company && root.BranchCode == branch && root.SealedAtUtc is not null
            && root.LedgerEpochId == issueFact.LedgerEpochId && root.PostingRole == role
            && root.SourceModule == "INVENTORY" && root.SourceDocumentType == batch.TrxType
            && root.SourceDocumentId == batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && root.SourceDocumentNo == batch.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && root.DocumentRevision == history.DocumentRevision && root.EffectiveAt == history.TrxDtTime
            && root.PostingSequence > 0 && runs.TryGetValue(root.Id, out var run)
            && run.CompanyCode == company && run.BranchCode == branch && run.RootRequestId == root.RequestId
            && run.RootDocumentType == root.SourceDocumentType && run.RootDocumentId == root.SourceDocumentId
            && run.EarliestEffectiveAt <= issueHistory.TrxDtTime && run.LatestEffectiveAt >= reverseHistory.TrxDtTime;

        bool MatchesValue(StockValuationFact value, StockValuationFact baseline) =>
            value.CompanyCode == company && value.BranchCode == branch && value.LedgerEpochId == baseline.LedgerEpochId
            && value.InventoryHistoryId is null && value.BaseQty == 0m && value.ItemCode == baseline.ItemCode
            && value.CostMethod == StockCostMethods.MovingAverage && value.MovementCode == ReversedIssueValueMovement
            && value.CostAmount > 0m && value.CostAmount == value.BaseCostAmount
            && value.EffectiveAt == baseline.EffectiveAt && value.BusinessDate == baseline.BusinessDate
            && value.PeriodKey == baseline.PeriodKey && value.BaseUom == baseline.BaseUom
            && value.FromBalLocId == baseline.FromBalLocId && value.ToBalLocId == baseline.ToBalLocId
            && value.WarehouseCode == baseline.WarehouseCode && value.LocationCode == baseline.LocationCode
            && value.LotId == baseline.LotId && value.LotNo == baseline.LotNo && value.ItemStatus == baseline.ItemStatus
            && value.BaseCurrency == baseline.BaseCurrency && value.SourceLineId == baseline.SourceLineId
            && value.SourceDocumentType == baseline.SourceDocumentType
            && value.SourceDocumentId == baseline.SourceDocumentId
            && value.SourceDocumentNo == baseline.SourceDocumentNo
            && value.SourceDocumentLine == baseline.SourceDocumentLine;

        bool HasEffect(long id, string type, string kind, long rootId, long ownerId, long originalId) =>
            runs.TryGetValue(rootId, out var run) && effects.Count(e =>
                e.CompanyCode == company && e.BranchCode == branch && e.ReplayRunId == run.Id
                && e.RootStockPostingId == rootId && e.BusinessOwnerPostingId == ownerId
                && e.OriginalEntityId == originalId && e.AffectedEntityId == id
                && e.AffectedEntityType == type && e.EffectKind == kind) == 1;
    }
}
