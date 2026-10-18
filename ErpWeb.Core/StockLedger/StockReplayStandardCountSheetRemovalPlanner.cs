using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayStandardCountSheetRemovalItem(
    string ItemCode,
    IReadOnlyList<int> CountLineIds,
    IReadOnlyList<StockReplayStandardEvent> RemovedEvents,
    StockReplayStandardFinancialTail Tail,
    decimal FinalQuantity,
    decimal FinalValue,
    string PlanHash);

public sealed record StockReplayStandardCountSheetRemovalPlan(
    StockPostingCommand Command,
    StockReplayCountSheetRemovalPlan Sheet,
    IReadOnlyList<StockReplayStandardCountSheetRemovalItem> Items,
    string PlanHash);

/// <summary>
/// Projects a complete Standard count sheet with no later count or value-only
/// events. Later single-pile Standard movements retain their approved value.
/// The original adjustment generations must still be active.
/// </summary>
public static class StockReplayStandardCountSheetRemovalPlanner
{
    public static async Task<StockReplayStandardCountSheetRemovalPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode, int countId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sheet = await StockReplayCountSheetRemovalPlanner.BuildAsync(
            db, companyCode, branchCode, countId, cancellationToken);
        var method = await new StockCostMethodResolver().ResolveAsync(db,
            companyCode, branchCode, sheet.EffectiveAt, cancellationToken);
        if (method != StockCostMethods.Standard)
            throw Invalid("The count sheet has no Standard cost authority.");
        var items = await ReadItemsAsync(db, sheet, cancellationToken);
        var command = await StockCountRollbackCommandFactory.CreateAsync(
            db, sheet, cancellationToken);
        var plan = new StockReplayStandardCountSheetRemovalPlan(command,
            sheet, items, string.Empty);
        return plan with { PlanHash = Hash(plan) };
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayStandardCountSheetRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (context.Posting.RequestId != plan.Command.RequestId
            || context.Posting.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint
            || !StringComparer.Ordinal.Equals(plan.PlanHash, Hash(plan)))
            throw Stale("The Standard count removal plan differs from its root.");
        await StockReplayCountSheetRemovalPlanner.ValidateWithinRootAsync(
            context, plan.Sheet, cancellationToken);
        var currentCommand = await StockCountRollbackCommandFactory.CreateAsync(
            context.Db, plan.Sheet, cancellationToken);
        if (currentCommand.RequestId != plan.Command.RequestId
            || currentCommand.Evidence.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint)
            throw Stale("The Standard count source snapshot changed.");
        var method = await new StockCostMethodResolver().ResolveAsync(
            context.Db, context.CompanyCode, context.BranchCode,
            plan.Sheet.EffectiveAt, cancellationToken);
        if (method != StockCostMethods.Standard)
            throw Stale("The count's event-date costing policy changed.");
        context.CostMethod = StockCostMethods.Standard;
        IReadOnlyList<StockReplayStandardCountSheetRemovalItem> current;
        try
        {
            current = await ReadItemsAsync(context.Db, plan.Sheet,
                cancellationToken);
        }
        catch (StockLedgerException)
        {
            throw Stale("The Standard count removal dependencies changed.");
        }
        if (!StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan with { Items = current })))
            throw Stale("The Standard count removal dependencies changed.");
    }

    private static async Task<IReadOnlyList<StockReplayStandardCountSheetRemovalItem>>
        ReadItemsAsync(AppDbContext db, StockReplayCountSheetRemovalPlan sheet,
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
                || generation.AnchorPostingId != sheet.AnchorPostingId
                || generation.WritePostingId != sheet.AnchorPostingId
                || generation.Id != -l.Physical.RemovedActiveEvent.EventId))
            throw Invalid("The count's original Standard generations are not active.");
        var pileIds = sheet.Lines.Select(l => l.BalLocId).ToArray();
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != sheet.Lines.Count
            || piles.Values.Any(p => string.IsNullOrWhiteSpace(p.ICode)))
            throw Invalid("The counted Standard piles changed items.");
        var items = new List<StockReplayStandardCountSheetRemovalItem>();
        foreach (var group in sheet.Lines.GroupBy(l => piles[l.BalLocId].ICode)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var lines = group.OrderBy(l => l.Physical.RemovedActiveEvent.Key)
                .ToArray();
            var firstKey = lines[0].Physical.RemovedActiveEvent.Key;
            if (firstKey.EffectiveAt == DateTime.MinValue
                || lines.Any(l => l.Physical.RemovedActiveEvent.Key.EffectiveAt
                        != firstKey.EffectiveAt
                    || l.Physical.RemovedActiveEvent.Key.OriginalPostingSequence
                        != firstKey.OriginalPostingSequence))
                throw Invalid("The count item needs descendant physical replay.");
            var before = new StockReplayEventKey(firstKey.EffectiveAt,
                StockReplayEventKey.OrdinaryClass, long.MaxValue,
                "\uffff", int.MaxValue);
            var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
                db, sheet.CompanyCode, sheet.BranchCode, group.Key,
                before, cancellationToken);
            var targetIds = lines.Select(l =>
                l.Physical.RemovedActiveEvent.EventId).ToHashSet();
            var removed = tail.LaterEvents.Where(e =>
                    targetIds.Contains(e.EventId))
                .OrderBy(e => e.Key).ToArray();
            var later = tail.LaterEvents.Where(e =>
                    !targetIds.Contains(e.EventId))
                .OrderBy(e => e.Key).ToArray();
            var physicalEvents = tail.PhysicalByPile.Values
                .SelectMany(e => e).ToArray();
            if (targetIds.Count != lines.Length
                || removed.Length != lines.Length
                || removed.Any(e => e.Kind != StockReplayStandardEventKinds.Count
                    || !targetIds.Contains(e.EventId))
                || later.Any(e => e.Kind != StockReplayStandardEventKinds.Movement
                    || e.Key.CompareTo(
                        lines[^1].Physical.RemovedActiveEvent.Key) <= 0
                    || e.ReplaySignedQuantity != e.OldSignedQuantity)
                || physicalEvents.Length != tail.LaterEvents.Count
                || physicalEvents.Select(e => e.EventId).Distinct().Count()
                    != physicalEvents.Length
                || !physicalEvents.Select(e => e.EventId).Order()
                    .SequenceEqual(tail.LaterEvents.Select(e => e.EventId).Order()))
                throw Invalid("The count item needs later Standard financial replay.");
            var eventById = removed.ToDictionary(e => e.EventId);
            if (lines.Any(l => !eventById.TryGetValue(
                        l.Physical.RemovedActiveEvent.EventId, out var e)
                    || e.Key != l.Physical.RemovedActiveEvent.Key
                    || e.OldSignedQuantity
                        != l.Physical.RemovedActiveEvent.SignedDelta
                    || e.AbsoluteCountQty != l.PhysicalQty
                    || !tail.CurrentQuantityByPile.TryGetValue(
                        l.BalLocId, out var current)
                    || current != l.Physical.CurrentQuantity
                    || l.Physical.Projection.FinalQuantity
                        != IvQty.Round(current - e.OldSignedQuantity)))
                throw Invalid("The count's Standard and physical removals disagree.");
            var finalQuantity = IvQty.Round(tail.CurrentQuantity
                - removed.Sum(e => e.OldSignedQuantity));
            var finalValue = StockLedgerPrecision.Money(tail.CurrentValue
                - removed.Sum(e => e.OldSignedValue));
            if (finalQuantity < 0m || finalValue < 0m
                || finalQuantity == 0m && finalValue != 0m
                || finalQuantity != IvQty.Round(
                    tail.CurrentQuantityByPile.Sum(p => p.Value)
                    - lines.Sum(l =>
                        l.Physical.RemovedActiveEvent.SignedDelta)))
                throw Invalid("The count removal does not reconcile its Standard pool.");
            var selectedIds = lines.Select(l => l.CountLineId).ToArray();
            var hash = StockPostingFingerprint.Create(new
            {
                group.Key, sheet.PlanHash, selectedIds,
                physical = lines.Select(l => l.Physical.PlanHash).ToArray(),
                removed, later, tail.CurrentQuantity, tail.CurrentValue,
                piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                    .Select(p => new
                    {
                        p.Key, p.Value,
                        Events = tail.PhysicalByPile[p.Key]
                    }).ToArray(),
                finalQuantity, finalValue
            }, new { }).RequestFingerprint;
            items.Add(new(group.Key, selectedIds, removed, tail,
                finalQuantity, finalValue, hash));
        }
        return items;
    }

    private static string Hash(StockReplayStandardCountSheetRemovalPlan plan) =>
        StockPostingFingerprint.Create(new
        {
            plan.Sheet.PlanHash,
            plan.Command.RequestId,
            plan.Command.Evidence.RequestFingerprint,
            items = plan.Items.Select(i => new
            {
                i.ItemCode, i.CountLineIds, i.PlanHash,
                i.FinalQuantity, i.FinalValue
            }).ToArray()
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
