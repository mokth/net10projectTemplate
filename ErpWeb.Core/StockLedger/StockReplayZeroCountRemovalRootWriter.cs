using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayZeroCountRemovalRootPlan(
    StockPostingCommand Command,
    StockReplayCountSheetRemovalPlan Removal,
    string FinancialHash);

/// <summary>
/// Removes an all-zero count whose later physical events retain the same
/// quantities after removal. This path writes only removal generations and
/// the count status; a count with an adjustment needs financial replay.
/// </summary>
public static class StockReplayZeroCountRemovalRootWriter
{
    public static async Task<StockReplayZeroCountRemovalRootPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode, int countId,
        CancellationToken cancellationToken = default)
    {
        var removal = await StockReplayCountSheetRemovalPlanner.BuildAsync(
            db, companyCode, branchCode, countId, cancellationToken);
        RequireZeroUnchanged(removal);
        await RequireNoAnchorFinancialEffectsAsync(db, removal, cancellationToken);
        var financialHash = await ReadLaterFinancialHashAsync(db, removal,
            cancellationToken);
        var command = await StockCountRollbackCommandFactory.CreateAsync(
            db, removal, cancellationToken);
        return new(command, removal, financialHash);
    }

    public static async Task<IReadOnlyList<StockCountReplayGeneration>> AppendWithinRootAsync(
        StockPostingContext context, StockReplayZeroCountRemovalRootPlan plan,
        string userId, string reason, DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 10
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 250)
            throw Invalid("Count rollback requires a user and reason within the audit limits.");
        var removal = plan.Removal;
        RequireZeroUnchanged(removal);
        if (context.Posting.RequestId != plan.Command.RequestId
            || context.Posting.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint)
            throw Stale("The count rollback command differs from the unsealed root.");
        await StockReplayCountSheetRemovalPlanner.ValidateWithinRootAsync(
            context, removal, cancellationToken);
        var currentCommand = await StockCountRollbackCommandFactory.CreateAsync(
            context.Db, removal, cancellationToken);
        if (currentCommand.RequestId != plan.Command.RequestId
            || currentCommand.Evidence.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint)
            throw Stale("The count rollback snapshot changed after planning.");
        await RequireNoAnchorFinancialEffectsAsync(
            context.Db, removal, cancellationToken);
        string currentFinancialHash;
        try
        {
            currentFinancialHash = await ReadLaterFinancialHashAsync(
                context.Db, removal, cancellationToken);
        }
        catch (StockLedgerException)
        {
            throw Stale("The zero-count financial tail changed after planning.");
        }
        if (!StringComparer.Ordinal.Equals(
                plan.FinancialHash, currentFinancialHash))
            throw Stale("The zero-count financial tail changed after planning.");

        var lineIds = removal.Lines.Select(l => l.CountLineId).ToArray();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            context.Db, removal.CompanyCode, removal.BranchCode,
            lineIds, cancellationToken);
        if (active.Count != lineIds.Length || removal.Lines.Any(l =>
                active[l.CountLineId].Id != -l.Physical.RemovedActiveEvent.EventId
                || active[l.CountLineId].IsRemoval
                || active[l.CountLineId].SignedAdjustmentQty != 0m
                || active[l.CountLineId].AdjustmentHistoryId is not null
                || active[l.CountLineId].AdjustmentBatchNo is not null))
            throw Stale("The active zero-count generations changed after planning.");

        var header = await context.Db.IvStockCountHdrs.SingleAsync(h =>
            h.Id == removal.CountHeaderId
            && h.CompanyCode == removal.CompanyCode
            && h.BranchCode == removal.BranchCode, cancellationToken);
        if (header.Status != IvStockCountStatuses.Posted
            || !await StockCountFirstBatchPointer.MatchesAsync(
                context.Db, removal.CompanyCode, removal.BranchCode,
                lineIds, header.PostedBatchNo, cancellationToken)
            || !header.RowVersion.SequenceEqual(removal.HeaderRowVersion))
            throw Stale("The zero-count header changed after planning.");

        context.Db.ChangeTracker.DetectChanges();
        var pileIds = removal.Lines.Select(l => l.BalLocId).ToHashSet();
        if (Dirty<IvStockCountHdr>(h => h.Id == removal.CountHeaderId)
            || Dirty<IvStockCountLine>(l => lineIds.Contains(l.Id))
            || Dirty<IvBalLoc>(b => pileIds.Contains(b.Id))
            || Dirty<IvTrxHistory>(_ => true)
            || Dirty<StockValuationFact>(_ => true)
            || Dirty<StockCostState>(_ => true)
            || Dirty<StockFifoLayer>(_ => true)
            || Dirty<StockFifoLayerConsumption>(_ => true)
            || Dirty<StockPosting>(p => p.Id != context.Posting.Id))
            throw Invalid("The count or its later stock evidence has pending edits.");

        var createdAtUtc = DateTime.UtcNow;
        var generations = removal.Lines.Select(line =>
        {
            var prior = active[line.CountLineId];
            return new StockCountReplayGeneration
            {
                CompanyCode = removal.CompanyCode,
                BranchCode = removal.BranchCode,
                StockCountLineId = line.CountLineId,
                AnchorPostingId = prior.AnchorPostingId,
                AnchorPostingSequence = prior.AnchorPostingSequence,
                WritePostingId = context.Posting.Id,
                PreviousGenerationId = prior.Id,
                EffectiveAt = prior.EffectiveAt,
                SignedAdjustmentQty = 0m,
                IsRemoval = true,
                CreatedAtUtc = createdAtUtc
            };
        }).ToArray();
        context.Db.StockCountReplayGenerations.AddRange(generations);
        header.Status = IvStockCountStatuses.RolledBack;
        header.RolledBackBy = userId;
        header.RolledBackOn = now;
        header.RollbackReason = reason.Trim();
        header.ModifiedBy = userId;
        header.ModifiedDate = now;
        await context.Db.SaveChangesAsync(cancellationToken);
        return generations;

        bool Dirty<T>(Func<T, bool> matches) where T : class =>
            context.Db.ChangeTracker.Entries<T>().Any(e =>
                matches(e.Entity) && e.State != EntityState.Unchanged);
    }

    private static void RequireZeroUnchanged(
        StockReplayCountSheetRemovalPlan plan)
    {
        StockReplayCountSheetRemovalPlanner.ValidatePreparedPlan(plan);
        if (plan.Lines.Any(l =>
                l.Physical.RemovedActiveEvent.SignedDelta != 0m
                || l.Physical.Projection.FinalQuantity
                    != l.Physical.CurrentQuantity
                || l.Physical.LaterEvents.Count
                    != l.Physical.Projection.LaterSteps.Count
                || l.Physical.LaterEvents.Zip(
                    l.Physical.Projection.LaterSteps).Any(pair =>
                    pair.First.EventId != pair.Second.EventId
                    || pair.First.SignedDelta
                        != pair.Second.SignedDelta)))
            throw Invalid("Count rollback needs adjustment or descendant replay.");
    }

    private static async Task RequireNoAnchorFinancialEffectsAsync(
        AppDbContext db, StockReplayCountSheetRemovalPlan plan,
        CancellationToken cancellationToken)
    {
        if (await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.StockPostingId == plan.AnchorPostingId, cancellationToken)
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.StockPostingId == plan.AnchorPostingId, cancellationToken))
            throw Invalid("The original count anchor has financial effects.");
    }

    private static async Task<string> ReadLaterFinancialHashAsync(
        AppDbContext db, StockReplayCountSheetRemovalPlan plan,
        CancellationToken cancellationToken)
    {
        var hasLaterPhysical = plan.Lines.Any(l =>
            l.Physical.LaterEvents.Count != 0);
        var method = await new StockCostMethodResolver().ResolveAsync(db,
            plan.CompanyCode, plan.BranchCode, plan.EffectiveAt,
            cancellationToken);
        if (method == StockCostMethods.Fifo)
        {
            var fifoPileIds = plan.Lines.Select(l => l.BalLocId).ToArray();
            var fifoPiles = await db.IvBalLocs.AsNoTracking()
                .Where(b => b.CompanyCode == plan.CompanyCode
                    && b.BranchCode == plan.BranchCode
                    && fifoPileIds.Contains(b.Id))
                .Select(b => new { b.Id, b.ICode, b.StdUom })
                .ToDictionaryAsync(b => b.Id, cancellationToken);
            if (fifoPiles.Count != fifoPileIds.Length
                || fifoPiles.Values.Any(p => string.IsNullOrWhiteSpace(p.ICode)
                    || string.IsNullOrWhiteSpace(p.StdUom)))
                throw Invalid("The zero-count FIFO piles changed items.");
            var snapshots = new List<object>();
            foreach (var itemCode in fifoPiles.Values.Select(p => p.ICode)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var itemLines = plan.Lines.Where(l =>
                    fifoPiles[l.BalLocId].ICode == itemCode).ToArray();
                var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                    db, plan.CompanyCode, plan.BranchCode, itemCode,
                    cancellationToken);
                string? tailHash = null;
                if (itemLines.Any(l => l.Physical.LaterEvents.Count != 0))
                {
                    if (itemLines.Select(l =>
                            fifoPiles[l.BalLocId].StdUom)
                        .Distinct(StringComparer.Ordinal).Count() != 1)
                        throw Invalid("A zero-count FIFO item has mixed base units.");
                    var line = itemLines.OrderBy(l =>
                        l.Physical.RemovedActiveEvent.Key).First();
                    var tail = StockReplayFifoTailProjector
                        .InsertCountAdjustment(snapshot,
                            line.Physical.RemovedActiveEvent.Key, 0m,
                            null, fifoPiles[line.BalLocId].StdUom);
                    if (tail.FinalQuantity != snapshot.CurrentQuantity
                        || tail.FinalValue != snapshot.CurrentValue)
                        throw Invalid("A zero-count FIFO tail changes its active pool.");
                    var proof = new StockReplayFifoCountSheetRemovalProjection(
                        tail.BeforeCandidate, [], tail.Issues,
                        tail.FinalLayers, tail.FinalQuantity, tail.FinalValue);
                    tailHash = await StockReplayFifoCountRemovalTailEvidenceReader
                        .ReadAsync(db, plan, itemCode, itemLines, snapshot,
                            proof, cancellationToken);
                }
                snapshots.Add(new
                {
                    ItemCode = itemCode, snapshot.SnapshotHash,
                    snapshot.CurrentQuantity, snapshot.CurrentValue,
                    TailHash = tailHash
                });
            }
            return StockPostingFingerprint.Create(new
            {
                plan.PlanHash, method, snapshots
            }, new { }).RequestFingerprint;
        }
        if (method is not (StockCostMethods.MovingAverage
            or StockCostMethods.Standard))
        {
            if (hasLaterPhysical)
                throw Invalid("A later zero-count tail needs WAC or Standard valuation proof.");
            return string.Empty;
        }
        var pileIds = plan.Lines.Select(l => l.BalLocId).ToArray();
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == plan.CompanyCode
                && b.BranchCode == plan.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != plan.Lines.Count
            || piles.Values.Any(p => string.IsNullOrWhiteSpace(p.ICode)))
            throw Invalid("The zero-count piles changed items.");
        var hashes = new List<string>();
        foreach (var group in plan.Lines.GroupBy(l => piles[l.BalLocId].ICode)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var lines = group.ToArray();
            var before = new StockReplayEventKey(plan.EffectiveAt,
                StockReplayEventKey.OrdinaryClass, long.MaxValue,
                "\uffff", int.MaxValue);
            var targets = lines.Select(l =>
                l.Physical.RemovedActiveEvent.EventId).ToHashSet();
            if (method == StockCostMethods.MovingAverage)
            {
                var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
                    db, plan.CompanyCode, plan.BranchCode, group.Key,
                    before, cancellationToken);
                if (tail.LaterEvents.Count(e => targets.Contains(e.EventId))
                        != lines.Length
                    || lines.Any(l => !tail.LaterEvents.Any(e =>
                            e.EventId == l.Physical.RemovedActiveEvent.EventId
                            && e.Key == l.Physical.RemovedActiveEvent.Key
                            && e.AbsoluteCountQty == l.PhysicalQty
                            && e.OldSignedQuantity == 0m
                            && e.OldSignedValue == 0m)
                        || !tail.CurrentQuantityByPile.TryGetValue(
                            l.BalLocId, out var current)
                        || current != l.Physical.CurrentQuantity))
                    throw Invalid("The zero count has changed WAC evidence.");
                hashes.Add(StockPostingFingerprint.Create(new
                {
                    group.Key, method, tail.CurrentQuantity,
                    tail.CurrentValue, tail.LaterEvents,
                    physical = tail.PhysicalByPile.OrderBy(p => p.Key)
                        .Select(p => new { p.Key, p.Value }).ToArray(),
                    piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                        .ToArray()
                }, new { }).RequestFingerprint);
            }
            else
            {
                var tail = await StockReplayStandardFinancialTailReader
                    .ReadLaterAsync(db, plan.CompanyCode, plan.BranchCode,
                        group.Key, before, cancellationToken);
                if (tail.LaterEvents.Count(e => targets.Contains(e.EventId))
                        != lines.Length
                    || lines.Any(l => !tail.LaterEvents.Any(e =>
                            e.EventId == l.Physical.RemovedActiveEvent.EventId
                            && e.Key == l.Physical.RemovedActiveEvent.Key
                            && e.Kind == StockReplayStandardEventKinds.Count
                            && e.AbsoluteCountQty == l.PhysicalQty
                            && e.OldSignedQuantity == 0m
                            && e.OldSignedValue == 0m)
                        || !tail.CurrentQuantityByPile.TryGetValue(
                            l.BalLocId, out var current)
                        || current != l.Physical.CurrentQuantity))
                    throw Invalid("The zero count has changed Standard evidence.");
                hashes.Add(StockPostingFingerprint.Create(new
                {
                    group.Key, method, tail.CurrentQuantity,
                    tail.CurrentValue, tail.LaterEvents,
                    physical = tail.PhysicalByPile.OrderBy(p => p.Key)
                        .Select(p => new { p.Key, p.Value }).ToArray(),
                    piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                        .ToArray()
                }, new { }).RequestFingerprint);
            }
        }
        return StockPostingFingerprint.Create(new
        {
            plan.PlanHash, method, hashes
        }, new { }).RequestFingerprint;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
