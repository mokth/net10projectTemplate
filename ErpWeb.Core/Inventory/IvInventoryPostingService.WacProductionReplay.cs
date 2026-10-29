using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // A receipt can affect one verified material issue and, when present,
    // one bounded Production output with no downstream WIP consumer.
    private sealed record ProductionLeafReplayPlan(
        int HistoryId, long MovementId, long MaterialMovementId,
        long PoolId, int PoolGeneration, string PoolRowVersion,
        decimal Quantity, ProductionEffectiveCost EffectiveCost,
        string ActiveValuationIds, DateTime EffectiveAt,
        StandardProductionReplayPlan? Output = null);

    private static Task<ProductionLeafReplayPlan?> PlanWacProductionLeafAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyList<StockReplayWacEvent> events, DateTime candidateDate,
        CancellationToken cancellationToken)
    {
        var ids = events.Where(e => e.EventId is > 0 and <= int.MaxValue)
            .Select(e => (int)e.EventId).ToArray();
        return PlanProductionLeafAsync(db, companyCode, branchCode, ids, cancellationToken,
            candidateDate, allowOutput: true);
    }

    private static Task<ProductionLeafReplayPlan?> PlanFifoProductionLeafAsync(
        AppDbContext db, string companyCode, string branchCode,
        StockReplayFifoTailProjection projection, StockReplayEventKey candidateKey,
        CancellationToken cancellationToken)
    {
        var ids = projection.Issues.Where(i => !i.IsCandidate
                && i.Key.CompareTo(candidateKey) > 0)
            .Select(i => StockReplayFifoMiRootWriter.HistoryId(i.Key)).ToArray();
        return PlanProductionLeafAsync(db, companyCode, branchCode, ids, cancellationToken,
            candidateKey.EffectiveAt, allowOutput: true);
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
        DateTime? candidateDate = null, bool allowOutput = false)
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
            var output = await PlanStandardProductionAsync(db, companyCode, branchCode,
                [historyId], date, cancellationToken, allowRestatements: true);
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
        IReadOnlyList<StockReplayWacEvent> events, DateTime candidateDate,
        ProductionLeafReplayPlan? expected,
        CancellationToken cancellationToken)
    {
        var current = await PlanWacProductionLeafAsync(db, companyCode, branchCode,
            events, candidateDate, cancellationToken);
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
        db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType,
            RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt,
            LatestEffectiveAt = plan.Output is not null && plan.Output.LatestEffectiveAt > latestEffectiveAt
                ? plan.Output.LatestEffectiveAt : latestEffectiveAt,
            PlanWatermark = branchWatermark, PlanHash = planHash,
            AffectedHistoryCount = affectedHistoryCount, AffectedItemCount = 1,
            AffectedProductionMovementCount = plan.Output?.MovementCount ?? 1,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
        });
        await db.SaveChangesAsync(cancellationToken);
        if (plan.Output is null)
            await ProductionLeafIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, plan.EffectiveCost, cancellationToken);
        else
            await ProductionOutputIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, plan.Output.IssueMovementId!.Value,
                plan.Output.ConsumeMovementId!.Value, plan.Output.ProduceMovementId!.Value,
                plan.Output.OutputReversed, plan.EffectiveCost, cancellationToken);
    }

    private static StockLedgerException ProductionReplayError(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.ReversalDependency, message));
}
