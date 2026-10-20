using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoCountSheetRemovalItem(
    string ItemCode,
    IReadOnlyList<int> CountLineIds,
    StockReplayFifoActiveLayerSnapshot Snapshot,
    StockReplayFifoCountSheetRemovalProjection? Projection,
    string TailEvidenceHash,
    string ItemHash);

public sealed record StockReplayFifoCountSheetRemovalPlan(
    StockPostingCommand Command,
    StockReplayCountSheetRemovalPlan Sheet,
    IReadOnlyList<StockReplayFifoCountSheetRemovalItem> Items,
    string PlanHash);

/// <summary>
/// Binds a posted count's physical removal to the active FIFO pool of each
/// counted item. Supported later MR and MI/Scrap ownership is rechecked under
/// the root. Writing is currently limited to one isolated adjustment.
/// </summary>
public static class StockReplayFifoCountSheetRemovalPlanner
{
    /// <summary>
    /// The bounded FIFO removal writer accepts one unchanged adjustment and
    /// any zero-adjustment lines without later physical movements.
    /// Later proven MR receipts and unchanged MI/Scrap issues may remain when
    /// they do not depend on a removed count-created layer.
    /// </summary>
    public static StockReplayFifoCountSheetRemovalItem RequireIsolatedAdjustment(
        StockReplayFifoCountSheetRemovalPlan plan)
    {
        ValidatePreparedPlan(plan);
        var adjustedLines = plan.Sheet.Lines.Where(l =>
            l.Physical.RemovedActiveEvent.SignedDelta != 0m).ToArray();
        if (adjustedLines.Length != 1
            || plan.Sheet.Lines.Any(l =>
                l.Physical.RemovedActiveEvent.SignedDelta == 0m
                && (l.Physical.LaterEvents.Count != 0
                    || l.Physical.Projection.FinalQuantity
                        != l.Physical.CurrentQuantity)))
            throw Invalid("The isolated FIFO reversal needs one adjustment and unchanged zero lines.");
        var line = adjustedLines[0];
        var item = plan.Items.Single(i => i.CountLineIds.Contains(line.CountLineId));
        var removed = item.Projection?.RemovedCounts;
        if (plan.Items.Any(i => i != item && i.Projection is not null)
            || removed is null || removed.Count != 1
            || removed[0].SignedAdjustmentQuantity == 0m
            || item.Projection!.LaterIssues.Any(issue =>
                !issue.DepletesPool
                || !issue.PreviousConsumptions.Select(c =>
                        (c.LayerId, c.Quantity, c.Value))
                    .OrderBy(c => c.LayerId)
                    .SequenceEqual(issue.Allocations.Select(a =>
                            (a.LayerId, a.Quantity, a.Value))
                        .OrderBy(a => a.LayerId)))
            || item.Projection.FinalQuantity
                != item.Snapshot.CurrentQuantity
                    - removed[0].SignedAdjustmentQuantity)
            throw Invalid("The FIFO count has a tail or an adjustment that needs a broader reversal writer.");
        var count = removed[0];
        if (count.SignedAdjustmentQuantity > 0m)
        {
            var receipt = count.Receipt;
            if (receipt is null || count.Consumptions.Count != 0
                || item.Snapshot.ActiveConsumptions.Any(c =>
                    c.LayerId == receipt.LayerId)
                || item.Snapshot.Layers.Count(l =>
                    l.LayerId == receipt.LayerId) != 1
                || receipt.ReceiptKey != count.Key
                || receipt.OriginalQuantity != receipt.RemainingQuantity
                || receipt.OriginalValue != receipt.RemainingValue
                || item.Snapshot.Layers.Any(l =>
                    l.LayerId != receipt.LayerId
                    && l.ReceiptKey is { } key
                    && key.CompareTo(count.Key) >= 0
                    && key.ClassPriority != StockReplayEventKey.OrdinaryClass)
                || item.Projection.FinalValue
                    != item.Snapshot.CurrentValue - receipt.OriginalValue)
                throw Invalid("The positive FIFO count has a dependent layer or later event.");
        }
        else
        {
            var consumptionIds = count.Consumptions
                .Select(c => c.ConsumptionId).Order().ToArray();
            if (count.Receipt is not null
                || consumptionIds.Length == 0
                || line.Physical.LaterEvents.Any(e =>
                    !item.Projection.LaterIssues.Any(i => i.Key == e.Key)
                    && !item.Projection.BeforeFirstCount.LaterReceipts
                        .Any(r => r.ReceiptKey == e.Key))
                || count.Consumptions.Any(c => !c.DepletesPool
                    || c.IssueKey != count.Key)
                || !consumptionIds.SequenceEqual(item.Snapshot
                    .ActiveConsumptions.Where(c =>
                        c.IssueKey == count.Key)
                    .Select(c => c.ConsumptionId).Order())
                || count.Consumptions.Sum(c => c.Quantity)
                    != -count.SignedAdjustmentQuantity
                || item.Snapshot.Layers.Any(l =>
                    l.ReceiptKey is not { } key
                    || key.CompareTo(count.Key) >= 0
                        && key.ClassPriority
                            != StockReplayEventKey.OrdinaryClass)
                || item.Projection.FinalValue
                    != item.Snapshot.CurrentValue
                        + count.Consumptions.Sum(c => c.Value))
                throw Invalid("The negative FIFO count has dependent allocations or a later event.");
        }
        return item;
    }

    public static async Task<StockReplayFifoCountSheetRemovalPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode, int countId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sheet = await StockReplayCountSheetRemovalPlanner.BuildAsync(
            db, companyCode, branchCode, countId, cancellationToken);
        if (await new StockCostMethodResolver().ResolveAsync(db,
                companyCode, branchCode, sheet.EffectiveAt,
                cancellationToken) != StockCostMethods.Fifo)
            throw Invalid("The count sheet has no FIFO cost authority.");
        var items = await ReadItemsAsync(db, sheet, cancellationToken);
        if (items.All(i => i.Projection is null))
            throw Invalid("An all-zero FIFO count uses the zero removal path.");
        var command = await StockCountRollbackCommandFactory.CreateAsync(
            db, sheet, cancellationToken);
        var plan = new StockReplayFifoCountSheetRemovalPlan(command, sheet,
            items, string.Empty);
        plan = plan with { PlanHash = Hash(plan) };
        ValidatePreparedPlan(plan);
        return plan;
    }

    public static void ValidatePreparedPlan(
        StockReplayFifoCountSheetRemovalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        StockReplayCountSheetRemovalPlanner.ValidatePreparedPlan(plan.Sheet);
        var lines = plan.Sheet.Lines.ToDictionary(l => l.CountLineId);
        if (plan.Items.Count == 0
            || plan.Items.All(i => i.Projection is null)
            || plan.Items.SelectMany(i => i.CountLineIds).Order()
                .SequenceEqual(lines.Keys.Order()) is false
            || plan.Items.Select(i => i.ItemCode)
                .Distinct(StringComparer.Ordinal).Count() != plan.Items.Count
            || plan.Items.Any(item => !ValidItem(plan.Sheet, item, lines))
            || !StringComparer.Ordinal.Equals(plan.PlanHash, Hash(plan)))
            throw Stale("The prepared FIFO count removal sheet changed.");
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayFifoCountSheetRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        if (context.Posting.RequestId != plan.Command.RequestId
            || context.Posting.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint)
            throw Stale("The FIFO count removal plan differs from its root.");
        await StockReplayCountSheetRemovalPlanner.ValidateWithinRootAsync(
            context, plan.Sheet, cancellationToken);
        var currentCommand = await StockCountRollbackCommandFactory.CreateAsync(
            context.Db, plan.Sheet, cancellationToken);
        if (currentCommand.RequestId != plan.Command.RequestId
            || currentCommand.Evidence.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint)
            throw Stale("The FIFO count source snapshot changed.");
        if (await new StockCostMethodResolver().ResolveAsync(
                context.Db, context.CompanyCode, context.BranchCode,
                plan.Sheet.EffectiveAt,
                cancellationToken) != StockCostMethods.Fifo)
            throw Stale("The count's event-date FIFO policy changed.");
        var last = plan.Sheet.Lines.SelectMany(l =>
                l.Physical.LaterEvents.Select(e => e.Key.EffectiveAt.Date))
            .Append(plan.Sheet.EffectiveAt.Date).Max();
        try
        {
            await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
                context, plan.Sheet.EffectiveAt.Date, last,
                cancellationToken);
        }
        catch (StockLedgerException)
        {
            throw Stale("The count's FIFO policy changed across its tail.");
        }
        var db = context.Db;
        if (db.ChangeTracker.Entries<StockCostState>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified
                    or EntityState.Deleted)
            || db.ChangeTracker.Entries<StockFifoLayer>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified
                    or EntityState.Deleted)
            || db.ChangeTracker.Entries<StockFifoLayerConsumption>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified
                    or EntityState.Deleted))
            throw Stale("The FIFO count cost pool has pending changes.");
        IReadOnlyList<StockReplayFifoCountSheetRemovalItem> current;
        try
        {
            current = await ReadItemsAsync(db, plan.Sheet,
                cancellationToken);
        }
        catch (StockLedgerException)
        {
            throw Stale("The FIFO count removal dependencies changed.");
        }
        if (!StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan with { Items = current })))
            throw Stale("The FIFO count removal dependencies changed.");
    }

    private static async Task<IReadOnlyList<StockReplayFifoCountSheetRemovalItem>>
        ReadItemsAsync(AppDbContext db,
            StockReplayCountSheetRemovalPlan sheet,
            CancellationToken cancellationToken)
    {
        StockReplayCountSheetRemovalPlanner.ValidatePreparedPlan(sheet);
        var lineIds = sheet.Lines.Select(l => l.CountLineId).ToArray();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, sheet.CompanyCode, sheet.BranchCode, lineIds,
            cancellationToken);
        if (active.Count != lineIds.Length || sheet.Lines.Any(l =>
                !active.TryGetValue(l.CountLineId, out var generation)
                || generation.IsRemoval
                || generation.PreviousGenerationId is not null
                || generation.AnchorPostingId != sheet.AnchorPostingId
                || generation.WritePostingId != sheet.AnchorPostingId
                || generation.Id != -l.Physical.RemovedActiveEvent.EventId
                || generation.SignedAdjustmentQty
                    != l.Physical.RemovedActiveEvent.SignedDelta))
            throw Invalid("The count's original FIFO generations are not active.");
        var pileIds = sheet.Lines.Select(l => l.BalLocId).ToArray();
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != pileIds.Length
            || piles.Values.Any(p => string.IsNullOrWhiteSpace(p.ICode)))
            throw Invalid("The counted FIFO piles changed items.");
        var items = new List<StockReplayFifoCountSheetRemovalItem>();
        foreach (var group in sheet.Lines.GroupBy(l => piles[l.BalLocId].ICode)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var lines = group.OrderBy(l => l.Physical.RemovedActiveEvent.Key)
                .ToArray();
            var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                db, sheet.CompanyCode, sheet.BranchCode, group.Key,
                cancellationToken);
            var keys = lines.Where(l =>
                    l.Physical.RemovedActiveEvent.SignedDelta != 0m)
                .Select(l => l.Physical.RemovedActiveEvent.Key).ToArray();
            var projection = keys.Length == 0 ? null
                : StockReplayFifoCountSheetProjector.Remove(snapshot, keys);
            var removed = projection?.RemovedCounts.ToDictionary(c => c.Key);
            if (projection is not null && (removed!.Count != keys.Length
                || lines.Any(l =>
                    l.Physical.RemovedActiveEvent.SignedDelta != 0m
                    && (!removed.TryGetValue(
                            l.Physical.RemovedActiveEvent.Key, out var count)
                        || count.SignedAdjustmentQuantity
                            != l.Physical.RemovedActiveEvent.SignedDelta))))
                throw Invalid("The count's FIFO and physical removals disagree.");
            var expectedFinal = StockLedgerPrecision.Quantity(
                snapshot.CurrentQuantity + lines.Sum(l =>
                    l.Physical.Projection.FinalQuantity
                    - l.Physical.CurrentQuantity));
            if (expectedFinal != (projection?.FinalQuantity
                    ?? snapshot.CurrentQuantity))
                throw Invalid("The count removal does not reconcile its FIFO piles.");
            var tailEvidenceHash = await StockReplayFifoCountRemovalTailEvidenceReader
                .ReadAsync(db, sheet, group.Key, lines, snapshot, projection,
                    cancellationToken);
            var ids = lines.Select(l => l.CountLineId).ToArray();
            items.Add(new(group.Key, ids, snapshot, projection,
                tailEvidenceHash,
                ItemHash(sheet, group.Key, lines, snapshot, projection,
                    tailEvidenceHash)));
        }
        return items;
    }

    private static bool ValidItem(StockReplayCountSheetRemovalPlan sheet,
        StockReplayFifoCountSheetRemovalItem item,
        IReadOnlyDictionary<int, StockReplayCountSheetRemovalLine> lines)
    {
        if (string.IsNullOrWhiteSpace(item.ItemCode)
            || item.Snapshot.CompanyCode != sheet.CompanyCode
            || item.Snapshot.BranchCode != sheet.BranchCode
            || item.Snapshot.ItemCode != item.ItemCode
            || item.CountLineIds.Count == 0
            || string.IsNullOrWhiteSpace(item.TailEvidenceHash)
            || item.CountLineIds.Distinct().Count()
                != item.CountLineIds.Count
            || item.CountLineIds.Any(id => !lines.ContainsKey(id)))
            return false;
        var selected = item.CountLineIds.Select(id => lines[id]).ToArray();
        var keys = selected.Where(l =>
                l.Physical.RemovedActiveEvent.SignedDelta != 0m)
            .Select(l => l.Physical.RemovedActiveEvent.Key).ToArray();
        StockReplayFifoCountSheetRemovalProjection? projected;
        try
        {
            projected = keys.Length == 0 ? null
                : StockReplayFifoCountSheetProjector.Remove(
                    item.Snapshot, keys);
        }
        catch (StockLedgerException)
        {
            return false;
        }
        return StringComparer.Ordinal.Equals(item.ItemHash,
                ItemHash(sheet, item.ItemCode, selected,
                    item.Snapshot, item.Projection,
                    item.TailEvidenceHash))
            && StringComparer.Ordinal.Equals(
                Fingerprint(projected), Fingerprint(item.Projection));
    }

    private static string ItemHash(StockReplayCountSheetRemovalPlan sheet,
        string itemCode,
        IReadOnlyCollection<StockReplayCountSheetRemovalLine> lines,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoCountSheetRemovalProjection? projection,
        string tailEvidenceHash) =>
        StockPostingFingerprint.Create(new
        {
            sheet.PlanHash, itemCode,
            Lines = lines.OrderBy(l => l.Physical.RemovedActiveEvent.Key)
                .Select(l => new
                {
                    l.CountLineId, l.BalLocId, l.Physical.PlanHash
                }).ToArray(),
            snapshot, projection, tailEvidenceHash
        }, new { }).RequestFingerprint;

    private static string Hash(StockReplayFifoCountSheetRemovalPlan plan) =>
        StockPostingFingerprint.Create(new
        {
            plan.Command.RequestId,
            plan.Command.Evidence.RequestFingerprint,
            plan.Sheet.PlanHash,
            Items = plan.Items.OrderBy(i => i.ItemCode, StringComparer.Ordinal)
                .Select(i => i.ItemHash).ToArray()
        }, new { }).RequestFingerprint;

    private static string Fingerprint(object? value) =>
        StockPostingFingerprint.Create(value, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch,
            message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
