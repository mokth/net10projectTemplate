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
    private sealed record TerminalAdjustmentReplayPlan(
        IReadOnlyList<ProductionEffectiveCost> ExpectedAdjustments,
        DateTime LatestEffectiveAt,
        string StableHash)
    {
        public bool Equals(TerminalAdjustmentReplayPlan? other) =>
            other is not null
            && StableHash == other.StableHash
            && LatestEffectiveAt == other.LatestEffectiveAt
            && ExpectedAdjustments.SequenceEqual(other.ExpectedAdjustments);

        public override int GetHashCode() => HashCode.Combine(StableHash, LatestEffectiveAt, ExpectedAdjustments.Count);
    }

    private sealed record TerminalProductionReturnReplayPlan(
        int ReturnBatchId, long ReturnMovementId, long ReturnFactId, int ReturnHistoryId,
        decimal ReturnBaseQty, ProductionEffectiveCost ExpectedReturn,
        DateTime EffectiveAt, string StableHash);

    // A receipt can affect one verified material issue and one bounded output,
    // terminal pool-average adjustment set, terminal return, or full-balance work-order transfer.
    private sealed record ProductionLeafReplayPlan(
        int HistoryId, long MovementId, long MaterialMovementId,
        long PoolId, int PoolGeneration, string PoolRowVersion,
        decimal Quantity, ProductionEffectiveCost EffectiveCost,
        string ActiveValuationIds, DateTime EffectiveAt,
        StandardProductionReplayPlan? Output = null,
        WacFinishedGoodCostPlan? FinishedGoodCost = null,
        ReversedFinishedGoodPairPlan? ReversedFinishedGoodPair = null,
        TerminalAdjustmentReplayPlan? TerminalAdjustments = null,
        TerminalProductionReturnReplayPlan? TerminalReturn = null);

    private static async Task<ProductionLeafReplayPlan?> PlanWacProductionLeafAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyList<StockReplayWacEvent> events, IReadOnlyList<StockReplayWacStep> projectedSteps,
        DateTime candidateDate,
        CancellationToken cancellationToken, long? removingReceiptOwnerId = null,
        int? insertingReceiptBatchId = null)
    {
        var ids = events.Where(e => e.EventId is > 0 and <= int.MaxValue)
            .Select(e => (int)e.EventId).ToArray();
        var plan = await PlanProductionLeafAsync(db, companyCode, branchCode, ids, cancellationToken,
            candidateDate, allowOutput: true, removingReceiptOwnerId: removingReceiptOwnerId,
            allowProductionReturn: true);
        if (plan?.TerminalReturn is { } prtn
            && !events.Any(e => e.EventId == prtn.ReturnHistoryId
                && e.ReturnOfProductionIssueEventId == plan.HistoryId))
            throw ProductionReplayError("Production stock return is missing from the WAC replay tail.");
        if (plan?.Output is not null && plan.Output.ActiveFinishedGood is null
            && plan.Output.CorrectedReversedFinishedGood is null
            && plan.Output.ActiveWorkOrderTransfer is null
            && plan.Output.OutputTerminalAdjustments is null)
        {
            var issueSteps = projectedSteps.Where(s => s.EventId == plan.HistoryId).ToArray();
            if (issueSteps.Length != 1 || issueSteps[0].SignedQuantity != -plan.Quantity
                || issueSteps[0].SignedValue > 0m)
                throw ProductionReplayError("The Production issue projection is incomplete.");
            var reversedFgPair = await PlanReversedFinishedGoodPairAsync(db, companyCode, branchCode,
                plan, -issueSteps[0].SignedValue, cancellationToken);
            if (reversedFgPair is not null)
                plan = plan with { ReversedFinishedGoodPair = reversedFgPair };
        }
        if (plan?.Output?.ActiveFinishedGood is not null)
        {
            var steps = projectedSteps.Where(s => s.EventId == plan.HistoryId).ToArray();
            if (steps.Length != 1 || steps[0].SignedQuantity != -plan.Quantity)
                throw ProductionReplayError("The backdated receipt changes finished-good source quantity.");
            if (steps[0].SignedValue != -plan.EffectiveCost.TotalCost)
                plan = plan with { FinishedGoodCost = await PlanWacFinishedGoodCostAsync(
                    db, companyCode, branchCode, plan, steps[0], removingReceiptOwnerId,
                    insertingReceiptBatchId, cancellationToken) };
        }
        if (plan?.Output?.CorrectedReversedFinishedGood is not null)
        {
            var steps = projectedSteps.Where(s => s.EventId == plan.HistoryId).ToArray();
            if (steps.Length != 1 || steps[0].SignedQuantity != -plan.Quantity)
                throw ProductionReplayError("The reversed finished-good source projection is incomplete.");
            await VerifyCorrectedReversedFinishedGoodProjectionAsync(db, companyCode, branchCode,
                plan, steps[0], cancellationToken);
        }
        return plan;
    }

    private static async Task<ProductionLeafReplayPlan?> PlanFifoProductionLeafAsync(
        AppDbContext db, string companyCode, string branchCode,
        StockReplayFifoTailProjection projection, StockReplayEventKey candidateKey,
        CancellationToken cancellationToken)
    {
        var ids = projection.Issues.Where(i => !i.IsCandidate
                && i.Key.CompareTo(candidateKey) > 0)
            .Select(i => StockReplayFifoMiRootWriter.HistoryId(i.Key)).ToArray();
        var plan = await PlanProductionLeafAsync(db, companyCode, branchCode, ids, cancellationToken,
            candidateKey.EffectiveAt, allowOutput: true);
        if (plan?.Output is not null && plan.Output.ActiveFinishedGood is null
            && plan.Output.CorrectedReversedFinishedGood is null
            && plan.Output.ActiveWorkOrderTransfer is null
            && plan.Output.OutputTerminalAdjustments is null)
        {
            var issues = projection.Issues.Where(i => !i.IsCandidate && i.Key.CompareTo(candidateKey) > 0
                && StockReplayFifoMiRootWriter.HistoryId(i.Key) == plan.HistoryId).ToArray();
            if (issues.Length != 1 || issues[0].Quantity != plan.Quantity)
                throw ProductionReplayError("The Production issue projection is incomplete.");
            var reversedFgPair = await PlanReversedFinishedGoodPairAsync(db, companyCode, branchCode,
                plan, issues[0].Value, cancellationToken);
            if (reversedFgPair is not null)
                plan = plan with { ReversedFinishedGoodPair = reversedFgPair };
        }
        if (plan?.Output?.ActiveFinishedGood is not null)
        {
            var issues = projection.Issues.Where(i => !i.IsCandidate && i.Key.CompareTo(candidateKey) > 0
                && StockReplayFifoMiRootWriter.HistoryId(i.Key) == plan.HistoryId).ToArray();
            if (issues.Length != 1 || issues[0].Quantity != plan.Quantity
                || issues[0].Value != plan.EffectiveCost.TotalCost)
                throw ProductionReplayError("The backdated receipt would change finished-good cost; full downstream replay is required.");
        }
        return plan;
    }

    private static Task<StandardProductionReplayPlan?> PlanStandardProductionTailAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyList<StockReplayStandardEvent> events, DateTime candidateDate,
        CancellationToken cancellationToken)
    {
        var ids = events.Where(e => e.Kind == StockReplayStandardEventKinds.Movement
                && e.EventId is > 0 and <= int.MaxValue)
            .Select(e => (int)e.EventId).ToArray();
        return PlanStandardProductionAsync(db, companyCode, branchCode,
            ids, candidateDate, cancellationToken);
    }

    private static async Task<ProductionLeafReplayPlan?> PlanProductionLeafAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyList<int> ids, CancellationToken cancellationToken,
        DateTime? candidateDate = null, bool allowOutput = false,
        long? removingReceiptOwnerId = null,
        bool allowProductionReturn = false)
    {
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id) && h.TrxType == IvTrxTypes.IssueToProduction)
            .Select(h => h.Id).ToArrayAsync(cancellationToken);
        if (histories.Length == 0)
            return null;
        if (histories.Length != 1)
            throw ProductionReplayError("Backdated receipt replay needs one unconsumed Production issue.");

        var historyId = histories[0];
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.CompanyCode == companyCode && m.BranchCode == branchCode
                && m.InventoryHistoryId == historyId
                && m.MovementType == ProductionBalLotMovementTypes.Issue
                && m.OriginalMovementId == null)
            .ToArrayAsync(cancellationToken);
        if (movements.Length != 1)
            throw ProductionReplayError("Production issue has no unique material-pool movement.");
        var movement = movements[0];
        var authority = (await ProductionIssueEffectiveCostReader.ReadAsync(
            db, companyCode, branchCode, [movement.Uid], cancellationToken))[movement.Uid];
        var effective = (await ProductionEffectiveCostResolver.ResolveAsync(
            db, companyCode, branchCode, [movement.Uid], cancellationToken))[movement.Uid];
        var otherMovements = await db.ProductionBalLotMovements.AsNoTracking().CountAsync(
            m => m.ProductionBalLotId == movement.ProductionBalLotId, cancellationToken);
        if (allowOutput && otherMovements > 1 && candidateDate is DateTime date)
        {
            var poolMovements = await db.ProductionBalLotMovements.AsNoTracking()
                .Where(m => m.ProductionBalLotId == movement.ProductionBalLotId)
                .OrderBy(m => m.Uid).ToArrayAsync(cancellationToken);
            var nonIssue = poolMovements.Where(m => m.Uid != movement.Uid).ToArray();
            if (nonIssue.Length > 0 && nonIssue.All(m =>
                    m.MovementType is ProductionBalLotMovementTypes.AdjustOut or ProductionBalLotMovementTypes.AdjustIn
                    && m.DocumentType == ProductionDocumentTypes.ProductionStockAdjustment
                    && m.OriginalMovementId == null))
            {
                return await PlanTerminalAdjustmentsAsync(db, companyCode, branchCode,
                    historyId, movement, authority, effective, nonIssue, date, cancellationToken);
            }
            if (allowProductionReturn && nonIssue.Length == 1
                && nonIssue[0].MovementType == ProductionBalLotMovementTypes.Return
                && nonIssue[0].DocumentType == ProductionDocumentTypes.ProductionStockReturn
                && nonIssue[0].OriginalMovementId == null)
            {
                return await PlanTerminalProductionReturnAsync(db, companyCode, branchCode,
                    historyId, movement, authority, effective, nonIssue[0], date, cancellationToken);
            }
            var output = await PlanStandardProductionAsync(db, companyCode, branchCode,
                [historyId], date, cancellationToken, allowRestatements: true,
                removingReceiptOwnerId: removingReceiptOwnerId);
            var outputLot = await db.ProductionBalLots.AsNoTracking().SingleAsync(
                l => l.Uid == movement.ProductionBalLotId, cancellationToken);
            var outputPool = await db.ProductionPoolValuationRows.AsNoTracking().SingleAsync(
                p => p.ProductionBalLotId == movement.ProductionBalLotId, cancellationToken);
            if (output is null || output.Leaf is not null
                || authority.TotalCost != effective.TotalCost || authority.UnitCost != effective.UnitCost)
                throw ProductionReplayError("Production output does not match its active issue valuation.");
            return new(historyId, movement.Uid, authority.MaterialMovementId,
                outputLot.Uid, outputPool.Generation, Convert.ToHexString(outputLot.RowVersion ?? []),
                authority.BaseQty, effective, string.Join(",", authority.ActiveValuationFactIds.Order()),
                movement.MovementDate, output);
        }
        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db, companyCode,
            branchCode, [movement.Uid], cancellationToken);
        if (graph.OrderedMovementIds.Count != 1 || graph.OrderedMovementIds[0] != movement.Uid
            || graph.Edges.Count != 0
            || await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking().AnyAsync(a =>
                a.ReceiptMovementId == movement.Uid || a.OutboundMovementId == movement.Uid,
                cancellationToken))
            throw ProductionReplayError("Backdated receipt replay cannot reprice Production material that has downstream consumers.");

        var lot = await db.ProductionBalLots.AsNoTracking().SingleOrDefaultAsync(
            l => l.Uid == movement.ProductionBalLotId, cancellationToken);
        var pool = await db.ProductionPoolValuationRows.AsNoTracking().SingleOrDefaultAsync(
            p => p.ProductionBalLotId == movement.ProductionBalLotId, cancellationToken);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking().SingleOrDefaultAsync(
            e => e.MovementId == movement.Uid, cancellationToken);
        if (lot is null || pool is null || evidence is null
            || lot.CompanyCode != companyCode || lot.BranchCode != branchCode
            || lot.Kind != ProductionBalLotKinds.MaterialIn
            || lot.ItemCode != movement.ItemCode
            || lot.OriginalIssueMovementId != authority.MaterialMovementId
            || lot.BaseQty != authority.BaseQty || lot.Qty != movement.Qty
            || pool.Generation <= 0 || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != effective.TotalCost
            || lot.TotalCost != effective.TotalCost || lot.AverageUnitCost != effective.UnitCost
            || evidence.ProductionBalLotId != lot.Uid || evidence.Generation != pool.Generation
            || evidence.Status != ProductionPoolValuationService.Verified
            || otherMovements != 1 || authority.TotalCost != effective.TotalCost
            || authority.UnitCost != effective.UnitCost)
            throw ProductionReplayError("Production issue does not have one unchanged verified material pool.");

        return new(historyId, movement.Uid, authority.MaterialMovementId,
            lot.Uid, pool.Generation, Convert.ToHexString(lot.RowVersion ?? []),
            authority.BaseQty, effective,
            string.Join(",", authority.ActiveValuationFactIds.Order()), movement.MovementDate);
    }

    private static async Task RecheckWacProductionLeafAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyList<StockReplayWacEvent> events, IReadOnlyList<StockReplayWacStep> projectedSteps,
        DateTime candidateDate,
        ProductionLeafReplayPlan? expected,
        CancellationToken cancellationToken, long? removingReceiptOwnerId = null,
        int? insertingReceiptBatchId = null)
    {
        var current = await PlanWacProductionLeafAsync(db, companyCode, branchCode,
            events, projectedSteps, candidateDate, cancellationToken, removingReceiptOwnerId,
            insertingReceiptBatchId);
        if (current != expected)
            throw new StockLedgerException(new StockLedgerError(
                StockLedgerErrorCodes.StaleDocument,
                "The Production material pool changed after receipt replay planning.", Retryable: true));
    }

    private static async Task RecheckFifoProductionLeafAsync(
        AppDbContext db, string companyCode, string branchCode,
        StockReplayFifoTailProjection projection, StockReplayEventKey candidateKey,
        ProductionLeafReplayPlan? expected, CancellationToken cancellationToken)
    {
        var current = await PlanFifoProductionLeafAsync(db, companyCode, branchCode,
            projection, candidateKey, cancellationToken);
        if (current != expected)
            throw new StockLedgerException(new StockLedgerError(
                StockLedgerErrorCodes.StaleDocument,
                "The Production material pool changed after receipt replay planning.", Retryable: true));
    }

    private static Task ApplyWacProductionLeafAsync(
        StockPostingContext context, ProductionLeafReplayPlan? plan,
        string inventoryPlanHash, long branchWatermark,
        IReadOnlyList<StockReplayWacEvent> events, CancellationToken cancellationToken) =>
        plan is null ? Task.CompletedTask : ApplyProductionLeafAsync(context, plan,
            inventoryPlanHash, branchWatermark, events.Max(e => e.Key.EffectiveAt),
            events.Count, cancellationToken);

    private static Task ApplyFifoProductionLeafAsync(
        StockPostingContext context, ProductionLeafReplayPlan? plan,
        string inventoryPlanHash, long branchWatermark,
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayFifoTailProjection projection,
        StockReplayEventKey candidateKey, CancellationToken cancellationToken)
    {
        if (plan is null)
            return Task.CompletedTask;
        var issueDates = projection.Issues.Where(i => !i.IsCandidate
                && i.Key.CompareTo(candidateKey) > 0)
            .Select(i => i.Key.EffectiveAt).ToArray();
        var receiptDates = snapshot.Layers.Where(l => l.ReceiptKey is { } key
                && key.CompareTo(candidateKey) > 0)
            .Select(l => l.ReceiptKey!.Value.EffectiveAt).ToArray();
        return ApplyProductionLeafAsync(context, plan, inventoryPlanHash, branchWatermark,
            issueDates.Concat(receiptDates).Append(plan.EffectiveAt).Max(),
            issueDates.Length + receiptDates.Length, cancellationToken);
    }

    private static async Task ApplyProductionLeafAsync(
        StockPostingContext context, ProductionLeafReplayPlan plan,
        string inventoryPlanHash, long branchWatermark, DateTime latestEffectiveAt,
        int affectedHistoryCount, CancellationToken cancellationToken)
    {
        var db = context.Db;
        if (await db.StockReplayRuns.AsNoTracking().AnyAsync(r =>
                r.RootStockPostingId == context.Posting.Id, cancellationToken))
            throw ProductionReplayError("The receipt replay root already has another cost replay run.");
        var planHash = StockPostingFingerprint.Create(new { inventoryPlanHash, production = plan },
            new { }).RequestFingerprint;
        var effectiveLatest = latestEffectiveAt;
        if (plan.Output is not null && plan.Output.LatestEffectiveAt > effectiveLatest)
            effectiveLatest = plan.Output.LatestEffectiveAt;
        if (plan.ReversedFinishedGoodPair is not null && plan.ReversedFinishedGoodPair.ReversalAt > effectiveLatest)
            effectiveLatest = plan.ReversedFinishedGoodPair.ReversalAt;
        if (plan.TerminalAdjustments is not null && plan.TerminalAdjustments.LatestEffectiveAt > effectiveLatest)
            effectiveLatest = plan.TerminalAdjustments.LatestEffectiveAt;
        if (plan.TerminalReturn is not null && plan.TerminalReturn.EffectiveAt > effectiveLatest)
            effectiveLatest = plan.TerminalReturn.EffectiveAt;
        db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType,
            RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt,
            LatestEffectiveAt = effectiveLatest,
            PlanWatermark = branchWatermark, PlanHash = planHash,
            AffectedHistoryCount = affectedHistoryCount + (plan.FinishedGoodCost is not null ? 1
                : plan.Output?.CorrectedReversedFinishedGood is not null ? 2 : 0),
            AffectedItemCount = plan.FinishedGoodCost is not null
                || plan.Output?.CorrectedReversedFinishedGood is not null
                || plan.ReversedFinishedGoodPair is not null ? 2 : 1,
            AffectedProductionMovementCount = (plan.TerminalAdjustments is not null
                ? 1 + plan.TerminalAdjustments.ExpectedAdjustments.Count
                : plan.TerminalReturn is not null ? 2
                : plan.Output?.MovementCount ?? 1)
                + (plan.ReversedFinishedGoodPair is not null ? 2 : 0),
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
        });
        await db.SaveChangesAsync(cancellationToken);
        if (plan.TerminalReturn is { } terminalReturn)
            await ApplyTerminalProductionReturnAsync(context, plan, terminalReturn, cancellationToken);
        else if (plan.TerminalAdjustments is not null)
        {
            await ProductionTerminalAdjustmentIssueSetCorrectionWriter.AppendWithinRootAsync(
                context, plan.EffectiveCost, plan.TerminalAdjustments.ExpectedAdjustments, cancellationToken);
        }
        else if (plan.FinishedGoodCost is not null)
            await ApplyWacFinishedGoodCostAsync(context, plan, plan.FinishedGoodCost, cancellationToken);
        else if (plan.Output?.ActiveFinishedGood is not null)
        {
            var issue = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
                context, [plan.MovementId], cancellationToken))[plan.MovementId];
            if (issue.BaseQty != plan.Quantity || issue.TotalCost != plan.EffectiveCost.TotalCost
                || issue.UnitCost != plan.EffectiveCost.UnitCost)
                throw ProductionReplayError("The finished-good source cost changed after replay planning.");
        }
        else if (plan.Output is null)
            await ProductionLeafIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, plan.EffectiveCost, cancellationToken);
        else if (plan.Output.ActiveWorkOrderTransfer is { } activeTransfer)
        {
            await ProductionOutputIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, plan.Output.IssueMovementId!.Value,
                plan.Output.ConsumeMovementId!.Value, plan.Output.ProduceMovementId!.Value,
                outputReversed: false, plan.EffectiveCost, cancellationToken,
                activeTransferOutId: activeTransfer.OutMovementId,
                activeTransferOutValue: activeTransfer.ExpectedOut.TotalCost);
            await ProductionWorkOrderTransferPairCostCorrectionWriter.AppendFromSourceReceiptsWithinRootAsync(
                context, activeTransfer.TransferId, activeTransfer.ExpectedOut,
                activeTransfer.ExpectedIn, cancellationToken);
        }
        else if (plan.Output.OutputTerminalAdjustments is { } outputAdjustments)
        {
            await ProductionOutputIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, plan.Output.IssueMovementId!.Value,
                plan.Output.ConsumeMovementId!.Value, plan.Output.ProduceMovementId!.Value,
                outputReversed: false, plan.EffectiveCost, cancellationToken,
                outputTerminalAdjustmentIds: outputAdjustments.ExpectedAdjustments.Select(x => x.MovementId).ToArray(),
                outputTerminalAdjustmentPoolBaseQty: outputAdjustments.ExpectedPoolBaseQty,
                outputTerminalAdjustmentPoolValue: outputAdjustments.ExpectedPoolValue);
            await ProductionOutputTerminalAdjustmentCorrectionWriter.AppendWithinRootAsync(
                context, plan.Output.ProduceMovementId!.Value,
                outputAdjustments.ExpectedProduce, outputAdjustments.ExpectedAdjustments, cancellationToken);
        }
        else
        {
            await ProductionOutputIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, plan.Output.IssueMovementId!.Value,
                plan.Output.ConsumeMovementId!.Value, plan.Output.ProduceMovementId!.Value,
                plan.Output.OutputReversed, plan.EffectiveCost, cancellationToken,
                allowReversedFinishedGood: plan.ReversedFinishedGoodPair is not null);
            if (plan.Output.CorrectedReversedFinishedGood is not null)
                await ApplyCorrectedReversedFinishedGoodAsync(context,
                    plan.Output.CorrectedReversedFinishedGood, cancellationToken);
            if (plan.ReversedFinishedGoodPair is not null)
                await ApplyReversedFinishedGoodPairAsync(context,
                    plan.ReversedFinishedGoodPair, cancellationToken);
        }
    }

    private static async Task<ProductionLeafReplayPlan> PlanTerminalAdjustmentsAsync(
        AppDbContext db, string companyCode, string branchCode,
        int historyId, ProductionBalLotMovement issue, ProductionIssueCostAuthority authority,
        ProductionEffectiveCost effectiveIssue, ProductionBalLotMovement[] adjustments,
        DateTime candidateDate, CancellationToken cancellationToken)
    {
        var adjustmentIds = adjustments.Select(m => m.Uid).ToArray();
        var allIds = adjustmentIds.Append(issue.Uid).ToArray();
        var lot = await db.ProductionBalLots.AsNoTracking().SingleOrDefaultAsync(
            l => l.Uid == issue.ProductionBalLotId, cancellationToken);
        var pool = await db.ProductionPoolValuationRows.AsNoTracking().SingleOrDefaultAsync(
            p => p.ProductionBalLotId == issue.ProductionBalLotId, cancellationToken);
        if (lot is null || pool is null
            || lot.CompanyCode != companyCode || lot.BranchCode != branchCode
            || lot.Kind != ProductionBalLotKinds.MaterialIn
            || lot.OriginalIssueMovementId != authority.MaterialMovementId
            || pool.Generation <= 0 || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost
            || authority.TotalCost != effectiveIssue.TotalCost
            || authority.UnitCost != effectiveIssue.UnitCost)
            throw ProductionReplayError("Production material pool does not have verified terminal adjustments.");

        var allIdSet = allIds.ToHashSet();
        var activeDependencies = await ProductionActiveEdgeResolver.Dependencies(db).AsNoTracking()
            .Where(e => allIds.Contains(e.ContributorMovementId) || allIds.Contains(e.ConsumerMovementId))
            .ToArrayAsync(cancellationToken);
        if (activeDependencies.Any(e => !allIdSet.Contains(e.ContributorMovementId)
                || !allIdSet.Contains(e.ConsumerMovementId)))
            throw ProductionReplayError("Production material pool has unsupported downstream dependencies.");

        var postingIds = adjustments.Append(issue).Select(m => m.StockPostingId ?? 0).Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (postingIds.Contains(0) || postings.Count != postingIds.Length
            || postings.Values.Any(p => p.SealedAtUtc is null || p.CompanyCode != companyCode
                || p.BranchCode != branchCode))
            throw ProductionReplayError("Production adjustment posting authority is incomplete.");

        var issueSequence = postings[issue.StockPostingId!.Value].PostingSequence;
        var facts = await db.ProductionStockAdjustmentFacts.AsNoTracking()
            .Where(f => adjustmentIds.Contains(f.ProductionMovementId))
            .ToArrayAsync(cancellationToken);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(e => allIds.Contains(e.MovementId))
            .ToArrayAsync(cancellationToken);
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(a => allIds.Contains(a.ReceiptMovementId) || allIds.Contains(a.OutboundMovementId))
            .ToArrayAsync(cancellationToken);
        if (facts.Length != adjustments.Length || evidence.Length != allIds.Length
            || allocations.Any(a => !allIdSet.Contains(a.ReceiptMovementId) || !allIdSet.Contains(a.OutboundMovementId)))
            throw ProductionReplayError("Production adjustment evidence or allocations are incomplete.");

        var factByMovementId = facts.ToDictionary(f => f.ProductionMovementId);
        foreach (var adj in adjustments)
        {
            var posting = postings[adj.StockPostingId!.Value];
            var fact = factByMovementId.GetValueOrDefault(adj.Uid);
            var ev = evidence.SingleOrDefault(e => e.MovementId == adj.Uid);
            var outAllocs = allocations.Where(a => a.OutboundMovementId == adj.Uid).ToArray();
            var isInbound = adj.MovementType == ProductionBalLotMovementTypes.AdjustIn;
            var isOutbound = adj.MovementType == ProductionBalLotMovementTypes.AdjustOut;
            if ((!isInbound && !isOutbound)
                || adj.CompanyCode != companyCode || adj.BranchCode != branchCode
                || adj.LedgerVersion != 2 || adj.LedgerEpochId != issue.LedgerEpochId
                || adj.MovementDate.Year != candidateDate.Year || adj.MovementDate.Month != candidateDate.Month
                || adj.MovementDate < issue.MovementDate
                || posting.PostingSequence <= issueSequence
                || posting.SourceModule != "PRODUCTION"
                || posting.SourceDocumentType != ProductionDocumentTypes.ProductionStockAdjustment
                || fact is null || fact.ReversesFactId is not null || fact.StockPostingId != posting.Id
                || ev is null || ev.ProductionBalLotId != lot.Uid
                || ev.Generation <= 0 || ev.Generation > pool.Generation
                || ev.Status != ProductionPoolValuationService.Verified)
                throw ProductionReplayError("Production adjustment is outside the bounded open-month terminal replay.");

            if (isOutbound)
            {
                if (fact.CostBasisCode is not ("POOLED_AVERAGE"
                        or ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost)
                    || ev.Basis != "POOLED_AVERAGE"
                    || outAllocs.Length == 0
                    || outAllocs.Any(a => a.StockPostingId != posting.Id)
                    || StockLedgerPrecision.Quantity(outAllocs.Sum(a => a.BaseQty)) != adj.BaseQty)
                    throw ProductionReplayError("Production ADJUST_OUT is outside the bounded open-month pool-average replay.");
            }
            else if (!ProductionTerminalAdjustmentIssueSetCorrectionWriter.IsValidInboundAdjustmentAuthority(
                    fact, ev.Basis, authority.MaterialMovementId, allowOriginalIssueCost: true)
                || outAllocs.Length != 0)
            {
                throw ProductionReplayError("Production ADJUST_IN lacks verified inbound cost authority.");
            }
        }

        var orderedMovements = adjustments
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => postings[m.StockPostingId!.Value].PostingSequence)
            .ThenBy(m => m.PostingLineNo)
            .ThenBy(m => m.Uid)
            .ToArray();
        var effectiveCosts = await ProductionReplayEffectiveCostReader.ReadAsync(db, companyCode, branchCode,
            orderedMovements.Select(m => m.Uid).ToArray(), cancellationToken);
        var orderedExpected = orderedMovements.Select(m => effectiveCosts[m.Uid]).ToArray();
        var steps = orderedMovements.Select(m =>
            ProductionTerminalAdjustmentIssueSetCorrectionWriter.BuildCostStep(m, factByMovementId[m.Uid])).ToArray();
        var projection = ProductionTerminalAdjustmentCostProjection.Project(
            issue.BaseQty, effectiveIssue.TotalCost, steps);
        var sourceGen = evidence.Single(e => e.MovementId == issue.Uid).Generation;
        var expectedQty = StockLedgerPrecision.Quantity(issue.Qty
            + orderedMovements.Where(m => m.MovementType == ProductionBalLotMovementTypes.AdjustIn).Sum(m => m.Qty)
            - orderedMovements.Where(m => m.MovementType == ProductionBalLotMovementTypes.AdjustOut).Sum(m => m.Qty));
        if (sourceGen + projection.FinalGenerationOffset != pool.Generation
            || orderedMovements.Any(m => evidence.Single(e => e.MovementId == m.Uid).Generation
                != sourceGen + projection.GenerationOffsets[m.Uid])
            || projection.RemainingBaseQty != lot.BaseQty
            || lot.Qty != expectedQty
            || projection.RemainingValue != lot.TotalCost
            || orderedMovements.Any(m => projection.OutboundValues[m.Uid] != effectiveCosts[m.Uid].TotalCost))
            throw ProductionReplayError("Production terminal adjustment state does not reconcile.");

        var latestAt = orderedMovements.Max(m => m.MovementDate);
        var stableHash = StockPostingFingerprint.Create(new
        {
            issue.Uid, effectiveIssue, lot.BaseQty, lot.TotalCost, pool.Generation,
            LotRowVersion = Convert.ToHexString(lot.RowVersion ?? []),
            Adjustments = orderedMovements.Select(m => new
            {
                m.Uid, m.StockPostingId, postings[m.StockPostingId!.Value].PostingSequence,
                m.BaseQty, m.MovementDate, Effective = effectiveCosts[m.Uid],
                FactId = facts.Single(f => f.ProductionMovementId == m.Uid).Id
            }).ToArray()
        }, new { }).RequestFingerprint;

        return new(historyId, issue.Uid, authority.MaterialMovementId,
            lot.Uid, pool.Generation, Convert.ToHexString(lot.RowVersion ?? []),
            authority.BaseQty, effectiveIssue, string.Join(",", authority.ActiveValuationFactIds.Order()),
            issue.MovementDate,
            TerminalAdjustments: new(orderedExpected, latestAt, stableHash));
    }

    private static async Task<ProductionLeafReplayPlan> PlanTerminalProductionReturnAsync(
        AppDbContext db, string companyCode, string branchCode,
        int historyId, ProductionBalLotMovement issue, ProductionIssueCostAuthority authority,
        ProductionEffectiveCost effectiveIssue, ProductionBalLotMovement returnMove,
        DateTime candidateDate, CancellationToken cancellationToken)
    {
        var lot = await db.ProductionBalLots.AsNoTracking().SingleOrDefaultAsync(
            l => l.Uid == issue.ProductionBalLotId, cancellationToken);
        var pool = await db.ProductionPoolValuationRows.AsNoTracking().SingleOrDefaultAsync(
            p => p.ProductionBalLotId == issue.ProductionBalLotId, cancellationToken);
        var returnFact = await db.ProductionStockReturnFacts.AsNoTracking().SingleOrDefaultAsync(
            f => f.ProductionMovementId == returnMove.Uid && f.ReversesFactId == null, cancellationToken);
        var returnDoc = returnFact is null ? null : await db.ProductionStockReturns.AsNoTracking()
            .Include(r => r.Batch).Include(r => r.Lines)
            .SingleOrDefaultAsync(r => r.BatchId == returnFact.BatchId
                && r.CompanyCode == companyCode && r.BranchCode == branchCode, cancellationToken);
        if (lot is null || pool is null || returnFact is null || returnDoc is null
            || lot.CompanyCode != companyCode || lot.BranchCode != branchCode
            || lot.Kind != ProductionBalLotKinds.MaterialIn
            || lot.OriginalIssueMovementId != authority.MaterialMovementId
            || pool.Generation <= 0 || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost
            || returnDoc.Batch.BatchStatus != IvBatchStatuses.Posted
            || returnDoc.ReversalPostingId is not null || returnDoc.PostingId is not long returnPostingId
            || returnDoc.Lines.Count(l => l.DocumentRevision == returnDoc.DocumentRevision) != 1
            || returnFact.StockPostingId != returnPostingId
            || returnMove.StockPostingId != returnPostingId
            || returnMove.MovementDate.Year != candidateDate.Year
            || returnMove.MovementDate.Month != candidateDate.Month
            || returnMove.MovementDate < issue.MovementDate
            || returnMove.BaseQty <= 0m || returnMove.BaseQty > issue.BaseQty
            || returnFact.BaseQty != returnMove.BaseQty
            || lot.BaseQty != issue.BaseQty - returnMove.BaseQty
            || authority.TotalCost != effectiveIssue.TotalCost
            || authority.UnitCost != effectiveIssue.UnitCost)
            throw ProductionReplayError("Production stock return is outside the bounded open-month replay.");

        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => p.Id == issue.StockPostingId || p.Id == returnPostingId)
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (postings.Count != 2
            || postings.Values.Any(p => p.SealedAtUtc is null || p.CompanyCode != companyCode || p.BranchCode != branchCode)
            || postings[returnPostingId].PostingSequence <= postings[issue.StockPostingId!.Value].PostingSequence)
            throw ProductionReplayError("Production stock return posting authority is incomplete.");

        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db, companyCode, branchCode,
            [issue.Uid], cancellationToken);
        if (graph.OrderedMovementIds.Count != 2 || graph.Edges.Count != 1
            || graph.Edges[0].ContributorMovementId != issue.Uid
            || graph.Edges[0].ConsumerMovementId != returnMove.Uid)
            throw ProductionReplayError("Production material pool has unsupported downstream dependencies.");

        var effectiveReturn = (await ProductionReplayEffectiveCostReader.ReadAsync(db, companyCode, branchCode,
            [returnMove.Uid], cancellationToken))[returnMove.Uid];
        var expectedReturnVal = StockLedgerPrecision.Money(
            effectiveIssue.TotalCost * returnMove.BaseQty / issue.BaseQty);
        var expectedRemainingVal = StockLedgerPrecision.Money(effectiveIssue.TotalCost - expectedReturnVal);
        if (effectiveReturn.TotalCost != expectedReturnVal
            || lot.TotalCost != expectedRemainingVal)
            throw ProductionReplayError("Production stock return effective cost does not reconcile.");

        var stableHash = StockPostingFingerprint.Create(new
        {
            IssueMovementId = issue.Uid, effectiveIssue, returnDoc.BatchId, returnDoc.DocumentRevision,
            ReturnRowVersion = Convert.ToHexString(returnDoc.RowVersion ?? []),
            ReturnFactId = returnFact.Id, returnFact.InventoryHistoryId,
            ReturnMovementId = returnMove.Uid, ReturnBaseQty = returnMove.BaseQty,
            effectiveReturn, LotBaseQty = lot.BaseQty, lot.TotalCost, pool.Generation,
            LotRowVersion = Convert.ToHexString(lot.RowVersion ?? [])
        }, new { }).RequestFingerprint;

        return new(historyId, issue.Uid, authority.MaterialMovementId,
            lot.Uid, pool.Generation, Convert.ToHexString(lot.RowVersion ?? []),
            authority.BaseQty, effectiveIssue, string.Join(",", authority.ActiveValuationFactIds.Order()),
            issue.MovementDate,
            TerminalReturn: new(returnDoc.BatchId, returnMove.Uid, returnFact.Id,
                returnFact.InventoryHistoryId, returnMove.BaseQty, effectiveReturn,
                returnMove.MovementDate, stableHash));
    }

    private static async Task ApplyTerminalProductionReturnAsync(
        StockPostingContext context, ProductionLeafReplayPlan plan,
        TerminalProductionReturnReplayPlan prtn, CancellationToken cancellationToken)
    {
        var db = context.Db;
        if (context.CostMethod != StockCostMethods.MovingAverage)
            throw ProductionReplayError("Bounded Production stock return replay requires WAC valuation.");
        var authority = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [plan.MovementId], cancellationToken))[plan.MovementId];
        var correctedReturn = StockLedgerPrecision.Money(
            authority.TotalCost * prtn.ReturnBaseQty / authority.BaseQty);
        var correctedReturnUnit = StockLedgerPrecision.Money(correctedReturn / prtn.ReturnBaseQty);
        var remainingValue = StockLedgerPrecision.Money(authority.TotalCost - correctedReturn);
        if (remainingValue < 0m)
            throw ProductionReplayError("Production stock return correction would leave negative pool value.");

        var returnHistory = await db.IvTrxHistories.AsNoTracking()
            .SingleAsync(h => h.Id == prtn.ReturnHistoryId, cancellationToken);
        var activeReturnFacts = (await StockReplayValuationGenerationResolver.ResolveWithinReplayRootAsync(
            context, [returnHistory], cancellationToken))[returnHistory.Id];
        if (activeReturnFacts.Count != 1 || activeReturnFacts[0].CostAmount != correctedReturn)
            throw ProductionReplayError("Production stock return Inventory valuation does not match Production restatement.");

        var lot = await db.ProductionBalLots.SingleAsync(l => l.Uid == plan.PoolId, cancellationToken);
        var pool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == plan.PoolId, cancellationToken);
        await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, plan.EffectiveCost, authority.TotalCost, authority.UnitCost, cancellationToken);
        await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, prtn.ExpectedReturn, correctedReturn, correctedReturnUnit, cancellationToken);
        lot.TotalCost = remainingValue;
        lot.AverageUnitCost = lot.BaseQty > 0m
            ? StockLedgerPrecision.Money(remainingValue / lot.BaseQty) : 0m;
        pool.TrackedValue = remainingValue;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static StockLedgerException ProductionReplayError(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.ReversalDependency, message));
}
