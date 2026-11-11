using System.Collections.Immutable;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

internal sealed record StockReplayWacMultiPileReceiptInsertionPlan(
    string Company, string Branch, string Item, IReadOnlyList<StockReplayPhysicalPlan> Piles,
    StockReplayWacEvent Candidate, StockReplayWacFinancialTail Tail,
    StockReplayWacResult Projection, string Hash);

internal sealed record StockReplayWacMultiPileReceiptRemovalPlan(
    StockReplayWacRemovalPlan Anchor, IReadOnlyList<StockReplayPhysicalRemovalPlan> PhysicalAnchors,
    IReadOnlyList<StockReplayPhysicalRemovalPlan> Piles,
    IReadOnlyDictionary<int, StockReplayWacEvent> SourceEvents, StockReplayWacEvent Removed,
    StockReplayWacFinancialTail Tail, StockReplayWacRemovalResult Projection, string Hash);

internal static class StockReplayWacMultiPileReceiptPlanner
{
    internal static void ValidatePreparedInsertion(StockReplayWacMultiPileReceiptInsertionPlan plan)
    {
        if (plan.Piles.Count is < 2 or > 10 || plan.Hash != InsertionHash(plan))
            throw Stale("The prepared receipt insertion plan changed.");
    }

    internal static void ValidatePreparedRemoval(StockReplayWacMultiPileReceiptRemovalPlan plan)
    {
        if (plan.Piles.Count is < 2 or > 10 || plan.PhysicalAnchors.Count != plan.Piles.Count
            || plan.Hash != RemovalHash(plan))
            throw Stale("The prepared receipt removal plan changed.");
    }

    internal static async Task<StockReplayWacMultiPileReceiptInsertionPlan> BuildInsertionAsync(
        AppDbContext db, string company, string branch,
        IReadOnlyList<StockReplayFifoMrReceiptAuthority> lines, CancellationToken ct)
    {
        if (lines.Count is < 2 or > 10 || lines.Select(l => l.ItemCode).Distinct(StringComparer.Ordinal).Count() != 1
            || lines.Select(l => l.BalLocId).Distinct().Count() < 2
            || lines.Any(l => l.EffectiveAt != lines[0].EffectiveAt || l.Quantity <= 0m || l.Value < 0m))
            throw Invalid("The receipt needs approved lines for one item in multiple existing piles.");
        var piles = new List<StockReplayPhysicalPlan>();
        foreach (var group in lines.GroupBy(l => l.BalLocId))
            piles.Add(await StockReplayInventoryCandidatePlanner.BuildSingleAsync(db, company, branch,
                group.Key, lines[0].EffectiveAt, IvQty.Round(group.Sum(l => l.Quantity)), ct));
        var first = piles[0].Candidate;
        var candidate = new StockReplayWacEvent(first.EventId, first.Key, 0m, 0m,
            IvQty.Round(lines.Sum(l => l.Quantity)), StockLedgerPrecision.Money(lines.Sum(l => l.Value)));
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db, company, branch,
            lines[0].ItemCode, candidate.Key, ct);
        var projection = StockReplayWacTailProjector.SimulateMultiPileReceiptInsertion(tail, piles, candidate);
        var plan = new StockReplayWacMultiPileReceiptInsertionPlan(company, branch,
            lines[0].ItemCode, piles, candidate, tail, projection, string.Empty);
        return plan with { Hash = InsertionHash(plan) };
    }

    internal static async Task<StockReplayWacWritePermit> PrepareInsertionAsync(
        StockPostingContext context, StockReplayWacMultiPileReceiptInsertionPlan plan, CancellationToken ct)
    {
        context.EnsureUnsealed();
        ValidatePreparedInsertion(plan);
        if (context.CompanyCode != plan.Company || context.BranchCode != plan.Branch
            || context.CostMethod != StockCostMethods.MovingAverage)
            throw Stale("The receipt insertion plan changed.");
        foreach (var pile in plan.Piles)
            await StockReplayPhysicalPlanner.ValidateWithinRootAsync(context, pile, ct);
        await ValidatePileItemsAsync(context, plan.Item, plan.Piles.Select(p => p.BalLocId).ToArray(), ct);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(context.Db, plan.Company,
            plan.Branch, plan.Item, plan.Candidate.Key, ct);
        var projection = StockReplayWacTailProjector.SimulateMultiPileReceiptInsertion(tail, plan.Piles, plan.Candidate);
        if (plan.Hash != InsertionHash(plan with { Tail = tail, Projection = projection }))
            throw Stale("The receipt costing dependencies changed.");
        return new StockReplayWacWritePermit(context, plan.Item, plan.Hash,
            tail.CurrentQuantity, tail.CurrentValue, projection.FinalQuantity, projection.FinalValue,
            plan.Piles.ToImmutableDictionary(p => p.BalLocId, p => p.Projection.FinalQuantity));
    }

    internal static async Task<StockReplayWacMultiPileReceiptRemovalPlan> BuildRemovalAsync(
        AppDbContext db, string company, string branch, IReadOnlyList<IvTrxHistory> histories,
        StockPosting owner, CancellationToken ct)
    {
        var originals = histories.OrderBy(h => h.Id).ToArray();
        if (originals.Length is < 2 or > 10 || originals.Any(h => h.StockPostingId != owner.Id
                || h.ICode != originals[0].ICode || h.ToBalLocId is not > 0 || h.ToStdQty is not > 0m)
            || originals.Select(h => h.ToBalLocId).Distinct().Count() < 2)
            throw Invalid("The posted receipt does not have exact item and pile histories.");
        var piles = new List<StockReplayPhysicalRemovalPlan>();
        foreach (var group in originals.GroupBy(h => h.ToBalLocId!.Value).OrderBy(g => g.Min(h => h.Id)))
        {
            var history = group.OrderBy(h => h.Id).First();
            var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"H{history.Id:D10}", 0);
            piles.Add(await StockReplayPhysicalPlanner.BuildRemovalAsync(db, company, branch,
                history.ToBalLocId!.Value, new StockReplayPhysicalEvent(history.Id, key, history.ToStdQty!.Value), ct));
        }
        var anchor = await StockReplayWacRemovalPlanner.BuildAsync(db, company, branch,
            originals[0].ICode!, piles[0], ct);
        return ComposeRemoval(anchor, piles, originals, owner);
    }

    internal static StockReplayWacMultiPileReceiptRemovalPlan ComposeRemoval(
        StockReplayWacRemovalPlan anchor, IReadOnlyList<StockReplayPhysicalRemovalPlan> physicalAnchors,
        IReadOnlyList<IvTrxHistory> histories, StockPosting owner)
    {
        var originals = histories.OrderBy(h => h.Id).ToArray();
        if (originals.Length is < 2 or > 10 || originals[0].Id != anchor.RemovedActiveEvent.EventId
            || originals.Any(h => h.ICode != anchor.ItemCode || h.StockPostingId != owner.Id
                || h.ToBalLocId is not > 0 || h.ToStdQty is not > 0m)
            || originals.Select(h => h.Id).Distinct().Count() != originals.Length)
            throw Invalid("The receipt removal does not match its original histories.");
        var groups = originals.GroupBy(h => h.ToBalLocId!.Value).OrderBy(g => g.Min(h => h.Id)).ToArray();
        if (groups.Length < 2 || physicalAnchors.Count != groups.Length
            || physicalAnchors[0].PlanHash != anchor.PhysicalPlan.PlanHash)
            throw Invalid("The receipt removal does not have one physical plan per pile.");
        var ids = originals.Select(h => (long)h.Id).ToHashSet();
        var source = new[] { anchor.RemovedActiveEvent }
            .Concat(anchor.Tail.LaterEvents.Where(e => ids.Contains(e.EventId)))
            .OrderBy(e => e.Key).ToArray();
        if (source.Length != originals.Length || source.Select(e => e.EventId).Distinct().Count() != originals.Length)
            throw Invalid("The receipt does not have one active WAC event per line.");
        var lastKey = source[^1].Key;
        if (anchor.Tail.LaterEvents.Any(e => !ids.Contains(e.EventId) && e.Key.CompareTo(lastKey) <= 0)
            || physicalAnchors.Any(p => p.LaterEvents.Any(e => !ids.Contains(e.EventId)
                && e.Key.CompareTo(lastKey) <= 0)))
            throw Invalid("Another movement overlaps the receipt lines.");
        var sourceById = source.ToDictionary(e => checked((int)e.EventId));
        foreach (var history in originals)
        {
            var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"H{history.Id:D10}", 0);
            if (!sourceById.TryGetValue(history.Id, out var e) || e.Key != key
                || e.OldSignedQuantity != history.ToStdQty || e.ReplaySignedQuantity != history.ToStdQty
                || e.OldSignedValue < 0m || e.FixedSignedValue != e.OldSignedValue || e.AbsoluteCountQty is not null)
                throw Invalid("The receipt does not have exact active WAC values.");
            if (history.Id != originals[0].Id)
            {
                var expected = new StockReplayPhysicalEvent(history.Id, key, history.ToStdQty!.Value);
                if (!anchor.Tail.PhysicalByPile.TryGetValue(history.ToBalLocId!.Value, out var events)
                    || events.Count(p => p == expected) != 1
                    || anchor.Tail.PhysicalByPile.Any(p => p.Key != history.ToBalLocId
                        && p.Value.Any(other => other.EventId == history.Id)))
                    throw Invalid("A receipt line does not have its exact active physical event.");
            }
        }
        // Keep the original physical anchors for revalidation under the root lock.
        // These combined plans only project removal of all receipt lines in each pile.
        var piles = new List<StockReplayPhysicalRemovalPlan>(groups.Length);
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i].OrderBy(h => h.Id).ToArray();
            var physical = physicalAnchors[i];
            if (physical.BalLocId != groups[i].Key || physical.RemovedActiveEvent.EventId != group[0].Id)
                throw Invalid("A receipt pile does not match its removal anchor.");
            var events = new[] { physical.RemovedActiveEvent }
                .Concat(physical.LaterEvents.Where(e => ids.Contains(e.EventId))).ToArray();
            if (events.Length != group.Length || group.Any(h => events.Count(e => e.EventId == h.Id
                && e.Key == sourceById[h.Id].Key && e.SignedDelta == h.ToStdQty && e.AbsoluteCountQty is null) != 1))
                throw Invalid("A receipt pile does not have exact active line quantities.");
            var removedPhysical = physical.RemovedActiveEvent with { SignedDelta = IvQty.Round(group.Sum(h => h.ToStdQty!.Value)) };
            var later = physical.LaterEvents.Where(e => !ids.Contains(e.EventId)).ToArray();
            var physicalProjection = StockReplayPhysicalSimulator.SimulateRemoval(physical.CurrentQuantity, removedPhysical, later);
            piles.Add(physical with { RemovedActiveEvent = removedPhysical, LaterEvents = later, Projection = physicalProjection });
        }
        var quantity = IvQty.Round(source.Sum(e => e.OldSignedQuantity));
        var value = StockLedgerPrecision.Money(source.Sum(e => e.OldSignedValue));
        var removed = source[0] with { OldSignedQuantity = quantity, OldSignedValue = value,
            ReplaySignedQuantity = quantity, FixedSignedValue = value };
        var tail = anchor.Tail with
        {
            LaterEvents = anchor.Tail.LaterEvents.Where(e => !ids.Contains(e.EventId)).ToArray(),
            PhysicalByPile = anchor.Tail.PhysicalByPile.ToDictionary(p => p.Key,
                p => (IReadOnlyList<StockReplayPhysicalEvent>)p.Value.Where(e => !ids.Contains(e.EventId)).ToArray())
        };
        var projection = StockReplayWacTailProjector.SimulateMultiPileReceiptRemoval(tail, piles, removed);
        var plan = new StockReplayWacMultiPileReceiptRemovalPlan(anchor, physicalAnchors, piles, sourceById,
            removed, tail, projection, string.Empty);
        return plan with { Hash = RemovalHash(plan) };
    }

    internal static async Task<(StockReplayWacMultiPileReceiptRemovalPlan Plan, StockReplayWacWritePermit Permit)>
        PrepareRemovalAsync(StockPostingContext context, StockReplayWacMultiPileReceiptRemovalPlan plan,
            IReadOnlyList<IvTrxHistory> histories, StockPosting owner, CancellationToken ct)
    {
        context.EnsureUnsealed();
        ValidatePreparedRemoval(plan);
        if (context.CompanyCode != plan.Anchor.CompanyCode || context.BranchCode != plan.Anchor.BranchCode
            || context.CostMethod != StockCostMethods.MovingAverage)
            throw Stale("The receipt removal plan changed.");
        await StockReplayWacRemovalPlanner.ValidateWithinRootAsync(context, plan.Anchor, ct);
        foreach (var physical in plan.PhysicalAnchors.Skip(1))
            await StockReplayPhysicalPlanner.ValidateWithinRootAsync(context, physical, ct);
        await ValidatePileItemsAsync(context, plan.Anchor.ItemCode, plan.Piles.Select(p => p.BalLocId).ToArray(), ct);
        var refreshed = ComposeRemoval(plan.Anchor, plan.PhysicalAnchors, histories, owner);
        if (refreshed.Hash != plan.Hash) throw Stale("The receipt removal dependencies changed.");
        var permit = new StockReplayWacWritePermit(context, plan.Anchor.ItemCode, plan.Hash,
            plan.Tail.CurrentQuantity, plan.Tail.CurrentValue, plan.Projection.FinalQuantity, plan.Projection.FinalValue,
            plan.Piles.ToImmutableDictionary(p => p.BalLocId, p => p.Projection.FinalQuantity));
        return (refreshed, permit);
    }

    private static async Task ValidatePileItemsAsync(StockPostingContext context, string item, int[] ids, CancellationToken ct)
    {
        var piles = await context.Db.IvBalLocs.AsNoTracking().Where(p => p.CompanyCode == context.CompanyCode
            && p.BranchCode == context.BranchCode && ids.Contains(p.Id)).Select(p => new { p.Id, p.ICode }).ToArrayAsync(ct);
        if (piles.Length != ids.Length || piles.Any(p => p.ICode != item))
            throw Stale("A receipt pile changed its item.");
    }

    private static object TailEvidence(StockReplayWacFinancialTail tail) => new
    {
        tail.CurrentQuantity, tail.CurrentValue, tail.LaterEvents,
        piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
            .Select(p => new { p.Key, p.Value, Events = tail.PhysicalByPile[p.Key] }).ToArray()
    };

    private static string InsertionHash(StockReplayWacMultiPileReceiptInsertionPlan plan) =>
        StockPostingFingerprint.Create(new { plan.Company, plan.Branch, plan.Item,
            piles = plan.Piles.OrderBy(p => p.BalLocId).Select(p => new { p.PlanHash, p.Projection }).ToArray(),
            plan.Candidate, Tail = TailEvidence(plan.Tail), plan.Projection }, new { }).RequestFingerprint;

    private static string RemovalHash(StockReplayWacMultiPileReceiptRemovalPlan plan) =>
        StockPostingFingerprint.Create(new { plan.Anchor.PlanHash,
            anchors = plan.PhysicalAnchors.Select(p => new { p.PlanHash, p.Projection }).ToArray(),
            piles = plan.Piles.Select(p => new { p.PlanHash, p.RemovedActiveEvent, p.LaterEvents, p.Projection }).ToArray(),
            source = plan.SourceEvents.OrderBy(p => p.Key).ToArray(), plan.Removed,
            Tail = TailEvidence(plan.Tail), plan.Projection }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument, message, Retryable: true));
}
