using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // The caller has already checked source, periods, QA and PO under the branch
    // lock, activated the permit, and written the receipt in the same root.
    // This method records both historical cost legs and the complete FIFO split;
    // it never changes the original issue, reversal or physical stock quantity.
    internal static async Task AppendFifoReversedIssueCorrectionAsync(
        StockPostingContext context, FifoReversedReceiptPermit permit,
        long? insertedReceiptLayerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        permit.ClaimCorrection(context);
        var plan = permit.Plan;
        var db = context.Db;
        var projection = plan.Projection;
        if (projection.OriginalSplits.Count == 0 || projection.ProjectedSplits.Count == 0
            || projection.OriginalSplits.Sum(s => s.Quantity) != projection.ProjectedSplits.Sum(s => s.Quantity)
            || projection.OriginalSplits.Sum(s => s.Value) != projection.OriginalValue
            || projection.ProjectedSplits.Sum(s => s.Value) != projection.ProjectedValue
            || projection.ProjectedSplits.Any(s => s.LayerId <= 0 || s.Quantity <= 0m || s.Value < 0m)
            || plan.Removal == insertedReceiptLayerId.HasValue)
            throw FifoCorrectionError("The FIFO correction has an incomplete planned allocation.");

        await db.SaveChangesAsync(ct);
        var rootFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.StockPostingId == context.Posting.Id).ToArrayAsync(ct);
        var rootHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.StockPostingId == context.Posting.Id).ToArrayAsync(ct);
        var state = await db.StockCostStates.SingleOrDefaultAsync(s => s.CompanyCode == plan.CompanyCode
            && s.BranchCode == plan.BranchCode && s.ItemCode == plan.ItemCode && s.CostMethod == StockCostMethods.Fifo, ct);
        var balance = await db.IvBalLocs.AsNoTracking().SingleOrDefaultAsync(b => b.Id == plan.BalanceId
            && b.CompanyCode == plan.CompanyCode && b.BranchCode == plan.BranchCode && b.ICode == plan.ItemCode, ct);
        if (rootFacts.Length != 1 || rootHistories.Length != 1 || state is null || balance is null
            || rootFacts[0].InventoryHistoryId != rootHistories[0].Id
            || rootFacts[0].SignedQty != (plan.Removal ? -plan.ReceiptQuantity : plan.ReceiptQuantity)
            || rootFacts[0].SignedValue != (plan.Removal ? -plan.ReceiptValue : plan.ReceiptValue)
            || rootFacts[0].ItemCode != plan.ItemCode || rootFacts[0].CostMethod != StockCostMethods.Fifo
            || rootFacts[0].LedgerEpochId != plan.LedgerEpochId
            || rootHistories[0].EntryRole != (plan.Removal ? "REVERSAL" : "ORIGINAL")
            || state.OnHandBaseQty != plan.FinalQuantity || state.InventoryValue != plan.FinalValue
            || balance.StdQty != plan.FinalQuantity
            || await db.StockReplayRuns.AsNoTracking().AnyAsync(r => r.RootStockPostingId == context.Posting.Id, ct))
            throw FifoCorrectionError("The receipt changed after FIFO correction planning.");
        if (insertedReceiptLayerId is long receiptLayer)
        {
            var layer = await db.StockFifoLayers.AsNoTracking().SingleOrDefaultAsync(l => l.Id == receiptLayer, ct);
            if (layer is null || layer.CompanyCode != plan.CompanyCode || layer.BranchCode != plan.BranchCode
                || layer.ItemCode != plan.ItemCode || layer.OriginStockPostingId != context.Posting.Id
                || layer.OriginValuationFactId != rootFacts[0].Id || layer.OriginalQty != plan.ReceiptQuantity
                || layer.OriginalValue != plan.ReceiptValue)
                throw FifoCorrectionError("The FIFO correction cannot identify the newly posted receipt layer.");
        }
        var issue = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == plan.IssueHistoryId, ct);
        var reversal = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == plan.ReversalHistoryId, ct);
        var movement = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == plan.IssueMovementId, ct);
        var reverseMovement = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == plan.ReversalMovementId, ct);
        var issueFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId == issue.Id)
            .OrderBy(f => f.SplitOrdinal).ThenBy(f => f.Id).ToArrayAsync(ct);
        if (issueFacts.Length == 0 || issueFacts.Sum(f => f.BaseQty) != movement.BaseQty
            || issueFacts.Sum(f => f.CostAmount) != movement.TotalCost
            || movement.StockPostingId != issue.StockPostingId || reverseMovement.StockPostingId != reversal.StockPostingId
            || movement.Uid != reverseMovement.OriginalMovementId
            || issue.CompanyCode != plan.CompanyCode || issue.BranchCode != plan.BranchCode
            || reversal.CompanyCode != plan.CompanyCode || reversal.BranchCode != plan.BranchCode
            || movement.CompanyCode != plan.CompanyCode || movement.BranchCode != plan.BranchCode
            || reverseMovement.CompanyCode != plan.CompanyCode || reverseMovement.BranchCode != plan.BranchCode
            || movement.BaseQty <= 0m || reverseMovement.BaseQty != movement.BaseQty
            || projection.OriginalSplits.Sum(s => s.Quantity) != movement.BaseQty)
            throw FifoCorrectionError("The original FIFO issue changed after correction planning.");
        var issueAnchor = issueFacts[0];
        var reversalAnchor = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f =>
            f.ReversesValuationFactId == issueAnchor.Id && f.InventoryHistoryId == reversal.Id, ct);
        if (reversalAnchor is null) throw FifoCorrectionError("The FIFO issue reversal has no exact valuation anchor.");
        var current = await ProductionReplayEffectiveCostReader.ReadAsync(db, plan.CompanyCode, plan.BranchCode,
            [movement.Uid, reverseMovement.Uid], ct);
        if (current[movement.Uid].TotalCost != projection.OriginalValue
            || current[reverseMovement.Uid].TotalCost != projection.OriginalValue
            || current[movement.Uid].RestatementFactId != plan.ActiveIssueRestatementId
            || current[reverseMovement.Uid].RestatementFactId != plan.ActiveReversalRestatementId)
            throw FifoCorrectionError("The FIFO issue cost changed after correction planning.");

        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId;
        var run = new StockReplayRun
        {
            CompanyCode = plan.CompanyCode, BranchCode = plan.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt, LatestEffectiveAt = reversal.TrxDtTime,
            PlanWatermark = plan.BranchWatermark, PlanHash = plan.PlanHash,
            AffectedHistoryCount = 2, AffectedItemCount = 1, AffectedProductionMovementCount = 2,
            CreatedAtUtc = now, CreatedBy = user
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var generation = new StockFifoReversedIssueGeneration
        {
            CompanyCode = plan.CompanyCode, BranchCode = plan.BranchCode, ReplayRunId = run.Id,
            ReplayStockPostingId = context.Posting.Id, IssueHistoryId = issue.Id, ReversalHistoryId = reversal.Id,
            IssueMovementId = movement.Uid, ReversalMovementId = reverseMovement.Uid,
            PreviousGenerationId = plan.PreviousGenerationId, BaseQty = movement.BaseQty,
            TotalCost = projection.ProjectedValue, CreatedAtUtc = now, CreatedBy = user
        };
        var splits = projection.ProjectedSplits.Select((a, i) => new StockFifoReversedIssueSplit
        {
            CompanyCode = plan.CompanyCode, BranchCode = plan.BranchCode, SplitOrdinal = i,
            FifoLayerId = a.LayerId == long.MaxValue ? insertedReceiptLayerId
                ?? throw FifoCorrectionError("The projected FIFO receipt layer is missing.") : a.LayerId,
            ConsumedQty = a.Quantity, ConsumedValue = a.Value
        }).ToArray();
        if (splits.Select(s => s.FifoLayerId).Distinct().Count() != splits.Length)
            throw FifoCorrectionError("The FIFO correction has duplicate layer splits.");
        generation.SplitHash = StockReplayFifoReversedIssueGenerationReader.SplitHash(generation, splits);
        generation.Splits = splits;
        db.StockFifoReversedIssueGenerations.Add(generation);
        var delta = StockLedgerPrecision.Money(projection.ProjectedValue - projection.OriginalValue);
        var values = new List<(StockValuationFact Fact, StockValuationFact Owner)>();
        var costs = new List<(ProductionCostRestatementFact Fact, ProductionBalLotMovement Owner)>();
        var line = 2;
        // Zero-value legs still mark a changed FIFO allocation in both business
        // periods, so other replay routes cannot silently omit the split history.
        values.Add((NewValue(issueAnchor, -delta), issueAnchor));
        values.Add((NewValue(reversalAnchor, delta), reversalAnchor));
        AddCosts(movement, plan.ActiveIssueRestatementId);
        AddCosts(reverseMovement, plan.ActiveReversalRestatementId);
        db.StockValuationFacts.AddRange(values.Select(v => v.Fact));
        db.ProductionCostRestatementFacts.AddRange(costs.Select(c => c.Fact));
        await db.SaveChangesAsync(ct);
        db.StockReplayEffects.Add(new StockReplayEffect
        {
            CompanyCode = plan.CompanyCode, BranchCode = plan.BranchCode, ReplayRunId = run.Id,
            RootStockPostingId = context.Posting.Id, BusinessOwnerPostingId = issue.StockPostingId!.Value,
            EffectKind = StockReplayFifoReversedIssueGenerationReader.GenerationEffect,
            AffectedEntityType = StockReplayFifoReversedIssueGenerationReader.GenerationEntity,
            AffectedEntityId = generation.Id, OriginalEntityId = issue.Id, CreatedAtUtc = now
        });
        db.StockReplayEffects.AddRange(values.Select(v => new StockReplayEffect
        {
            CompanyCode = plan.CompanyCode, BranchCode = plan.BranchCode, ReplayRunId = run.Id,
            RootStockPostingId = context.Posting.Id, BusinessOwnerPostingId = v.Owner.StockPostingId,
            EffectKind = StockReplayFifoReversedIssueGenerationReader.ValueEffect,
            AffectedEntityType = StockReplayEffectTypes.StockValuationFact,
            AffectedEntityId = v.Fact.Id, OriginalEntityId = v.Owner.Id, CreatedAtUtc = now
        }));
        db.StockReplayEffects.AddRange(costs.Select(c => new StockReplayEffect
        {
            CompanyCode = plan.CompanyCode, BranchCode = plan.BranchCode, ReplayRunId = run.Id,
            RootStockPostingId = context.Posting.Id, BusinessOwnerPostingId = c.Owner.StockPostingId!.Value,
            EffectKind = "PRODUCTION_COST_RESTATEMENT", AffectedEntityType = "PRODUCTION_COST_RESTATEMENT_FACT",
            AffectedEntityId = c.Fact.Id, OriginalEntityId = c.Owner.Uid, CreatedAtUtc = now
        }));
        if (values.Count > 0) state.LastValuationFactId = values[^1].Fact.Id;
        await db.SaveChangesAsync(ct);
        var after = await ProductionEffectiveCostResolver.ResolveAsync(db, plan.CompanyCode, plan.BranchCode,
            [movement.Uid, reverseMovement.Uid], ct);
        if (after[movement.Uid].TotalCost != projection.ProjectedValue
            || after[reverseMovement.Uid].TotalCost != projection.ProjectedValue
            || values.Sum(v => v.Fact.SignedValue) != 0m)
            throw FifoCorrectionError("The FIFO issue and reversal corrections do not reconcile.");
        return;

        StockValuationFact NewValue(StockValuationFact baseline, decimal signed) => new()
        {
            CompanyCode = plan.CompanyCode, BranchCode = plan.BranchCode, LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id, PostingLineNo = line++, SplitOrdinal = 0,
            SourceLineId = baseline.SourceLineId, SourceDocumentType = baseline.SourceDocumentType,
            SourceDocumentId = baseline.SourceDocumentId, SourceDocumentNo = baseline.SourceDocumentNo,
            SourceDocumentLine = baseline.SourceDocumentLine, EffectiveAt = baseline.EffectiveAt,
            BusinessDate = baseline.BusinessDate, PeriodKey = baseline.PeriodKey, ItemCode = baseline.ItemCode,
            WarehouseCode = baseline.WarehouseCode, LocationCode = baseline.LocationCode,
            LotId = baseline.LotId, LotNo = baseline.LotNo, ItemStatus = baseline.ItemStatus, BaseUom = baseline.BaseUom,
            MovementCode = StockReplayFifoReversedIssueGenerationReader.ValueMovement,
            Direction = signed == 0m ? baseline.Direction : Math.Sign(signed), BaseQty = 0m, CostMethod = StockCostMethods.Fifo,
            UnitCost = StockLedgerPrecision.Money(Math.Abs(signed) / movement.BaseQty),
            CostAmount = Math.Abs(signed), BaseCurrency = baseline.BaseCurrency, BaseCostAmount = Math.Abs(signed),
            ValuationSource = StockValuationSources.Fifo, ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = checked(baseline.ValuationVersion + 1), FromBalLocId = baseline.FromBalLocId,
            ToBalLocId = baseline.ToBalLocId, OriginalValuationFactId = baseline.Id, CreatedAtUtc = now, CreatedBy = user
        };

        void AddCosts(ProductionBalLotMovement owner, long? priorId)
        {
            if (priorId is long previous)
                costs.Add((NewCost(owner, projection.OriginalValue, owner.TotalCost, previous), owner));
            if (projection.ProjectedValue != owner.TotalCost)
                costs.Add((NewCost(owner, owner.TotalCost, projection.ProjectedValue, null), owner));
        }

        ProductionCostRestatementFact NewCost(ProductionBalLotMovement owner, decimal prior, decimal corrected, long? reverseId) => new()
        {
            CompanyCode = plan.CompanyCode, BranchCode = plan.BranchCode, ReplayRequestId = context.Posting.RequestId,
            ReplayStockPostingId = context.Posting.Id, ProductionBalLotMovementId = owner.Uid,
            EffectiveAt = owner.MovementDate, PriorEffectiveTotalCost = prior, CorrectedEffectiveTotalCost = corrected,
            CorrectedUnitCost = StockLedgerPrecision.Money(corrected / owner.BaseQty),
            DeltaCost = StockLedgerPrecision.Money(corrected - prior), ReversesRestatementFactId = reverseId,
            CreatedAtUtc = now, CreatedBy = user
        };
    }

    private static StockLedgerException FifoCorrectionError(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static async Task VerifyFifoReversedIssueCorrectionWithinRootAsync(
        StockPostingContext context, FifoReversedReceiptPermit permit, long? insertedReceiptLayerId, CancellationToken ct)
    {
        if (context.Db.Database.CurrentTransaction is null || context.Posting.SealedAtUtc is null)
            throw FifoCorrectionError("FIFO correction reconciliation needs its sealed transaction root.");
        var db = context.Db;
        var plan = permit.Plan;
        var issue = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == plan.IssueHistoryId, ct);
        var reversal = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == plan.ReversalHistoryId, ct);
        var movement = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == plan.IssueMovementId, ct);
        var reverseMovement = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == plan.ReversalMovementId, ct);
        var ids = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId == issue.Id)
            .Select(f => f.Id).ToArrayAsync(ct);
        var rows = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => ids.Contains(c.IssueValuationFactId)).ToArrayAsync(ct);
        var baseline = rows.Select(c => new StockReplayFifoConsumptionPosition(c.Id, c.IssueValuationFactId,
            c.FifoLayerId, c.ConsumedQty, c.ConsumedValue, c.SplitOrdinal, true, plan.Projection.IssueKey)).ToArray();
        var current = await StockReplayFifoReversedIssueGenerationReader.ReadAsync(db, plan.CompanyCode, plan.BranchCode,
            issue, reversal, movement, reverseMovement, baseline, ct);
        var expected = plan.Projection.ProjectedSplits.Select((s, i) =>
            (Ordinal: i, LayerId: s.LayerId == long.MaxValue ? insertedReceiptLayerId ?? 0 : s.LayerId,
                s.Quantity, s.Value)).ToArray();
        var actual = current?.Splits.Select(s =>
            (Ordinal: s.SplitOrdinal, LayerId: s.FifoLayerId, Quantity: s.ConsumedQty, Value: s.ConsumedValue)).ToArray();
        if (current is null || current.Generation.ReplayStockPostingId != context.Posting.Id
            || current.Generation.PreviousGenerationId != plan.PreviousGenerationId
            || current.Generation.TotalCost != plan.Projection.ProjectedValue
            || actual is null || !actual.SequenceEqual(expected))
            throw FifoCorrectionError("The sealed FIFO issue correction differs from its prepared split.");
    }
}
