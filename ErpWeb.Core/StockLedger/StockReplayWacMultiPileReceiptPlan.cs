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
    StockReplayWacRemovalPlan Anchor, IReadOnlyList<StockReplayPhysicalRemovalPlan> Piles,
    IReadOnlyDictionary<int, StockReplayWacEvent> SourceEvents, StockReplayWacEvent Removed,
    StockReplayWacFinancialTail Tail, StockReplayWacRemovalResult Projection, string Hash);

internal static class StockReplayWacMultiPileReceiptPlanner
{
    internal static void ValidatePreparedInsertion(StockReplayWacMultiPileReceiptInsertionPlan plan)
    {
        if (plan.Piles.Count != 2 || plan.Hash != InsertionHash(plan))
            throw Stale("The prepared receipt insertion plan changed.");
    }

    internal static void ValidatePreparedRemoval(StockReplayWacMultiPileReceiptRemovalPlan plan)
    {
        if (plan.Piles.Count != 2 || plan.Hash != RemovalHash(plan))
            throw Stale("The prepared receipt removal plan changed.");
    }

    internal static async Task<StockReplayWacMultiPileReceiptInsertionPlan> BuildInsertionAsync(
        AppDbContext db, string company, string branch,
        IReadOnlyList<StockReplayFifoMrReceiptAuthority> lines, CancellationToken ct)
    {
        if (lines.Count != 2 || lines.Select(l => l.ItemCode).Distinct(StringComparer.Ordinal).Count() != 1
            || lines.Select(l => l.BalLocId).Distinct().Count() != 2
            || lines.Any(l => l.EffectiveAt != lines[0].EffectiveAt || l.Quantity <= 0m || l.Value < 0m))
            throw Invalid("The receipt must contain two approved lines for one item in two different piles.");
        var piles = new List<StockReplayPhysicalPlan>(2);
        foreach (var line in lines)
            piles.Add(await StockReplayInventoryCandidatePlanner.BuildSingleAsync(db, company, branch,
                line.BalLocId, line.EffectiveAt, line.Quantity, ct));
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
        if (originals.Length != 2 || originals.Any(h => h.StockPostingId != owner.Id
                || h.ICode != originals[0].ICode || h.ToBalLocId is not > 0 || h.ToStdQty is not > 0m)
            || originals.Select(h => h.ToBalLocId).Distinct().Count() != 2)
            throw Invalid("The posted receipt does not have two exact item and pile histories.");
        var piles = new List<StockReplayPhysicalRemovalPlan>(2);
        foreach (var history in originals)
        {
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
        StockReplayWacRemovalPlan anchor, IReadOnlyList<StockReplayPhysicalRemovalPlan> piles,
        IReadOnlyList<IvTrxHistory> histories, StockPosting owner)
    {
        var originals = histories.OrderBy(h => h.Id).ToArray();
        if (originals.Length != 2 || piles.Count != 2 || originals[0].Id != anchor.RemovedActiveEvent.EventId
            || piles[0].PlanHash != anchor.PhysicalPlan.PlanHash
            || originals.Any(h => h.ICode != anchor.ItemCode || h.StockPostingId != owner.Id
                || h.ToBalLocId is not > 0 || h.ToStdQty is not > 0m)
            || originals.Select(h => h.ToBalLocId).Distinct().Count() != 2
            || originals.Where((h, i) => h.Id != piles[i].RemovedActiveEvent.EventId
                || h.ToBalLocId != piles[i].BalLocId || h.ToStdQty != piles[i].RemovedActiveEvent.SignedDelta).Any())
            throw Invalid("The receipt removal does not match its original histories.");
        var second = anchor.Tail.LaterEvents.Where(e => e.EventId == originals[1].Id).ToArray();
        if (second.Length != 1 || second[0].Key != piles[1].RemovedActiveEvent.Key
            || anchor.Tail.LaterEvents.Any(e => e.EventId != originals[1].Id && e.Key.CompareTo(second[0].Key) <= 0))
            throw Invalid("Another movement overlaps the receipt lines.");
        var source = new[] { anchor.RemovedActiveEvent, second[0] };
        if (source.Where((e, i) => e.Key != piles[i].RemovedActiveEvent.Key
                || e.OldSignedQuantity != originals[i].ToStdQty || e.ReplaySignedQuantity != originals[i].ToStdQty
                || e.OldSignedValue < 0m || e.FixedSignedValue != e.OldSignedValue || e.AbsoluteCountQty is not null).Any()
            || anchor.Tail.PhysicalByPile.Any(p => p.Key != piles[1].BalLocId
                && p.Value.Any(e => e.EventId == originals[1].Id))
            || !anchor.Tail.PhysicalByPile.TryGetValue(piles[1].BalLocId, out var secondPileTail)
            || secondPileTail.Count(e => e == piles[1].RemovedActiveEvent) != 1)
            throw Invalid("The receipt does not have exact active physical and value events.");
        var quantity = IvQty.Round(source.Sum(e => e.OldSignedQuantity));
        var value = StockLedgerPrecision.Money(source.Sum(e => e.OldSignedValue));
        var removed = source[0] with { OldSignedQuantity = quantity, OldSignedValue = value,
            ReplaySignedQuantity = quantity, FixedSignedValue = value };
        var tail = anchor.Tail with
        {
            LaterEvents = anchor.Tail.LaterEvents.Where(e => e.EventId != originals[1].Id).ToArray(),
            PhysicalByPile = anchor.Tail.PhysicalByPile.ToDictionary(p => p.Key,
                p => (IReadOnlyList<StockReplayPhysicalEvent>)p.Value.Where(e => e.EventId != originals[1].Id).ToArray())
        };
        var projection = StockReplayWacTailProjector.SimulateMultiPileReceiptRemoval(tail, piles, removed);
        var plan = new StockReplayWacMultiPileReceiptRemovalPlan(anchor, piles,
            originals.ToDictionary(h => h.Id, h => source.Single(e => e.EventId == h.Id)),
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
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(context, plan.Piles[1], ct);
        await ValidatePileItemsAsync(context, plan.Anchor.ItemCode, plan.Piles.Select(p => p.BalLocId).ToArray(), ct);
        var refreshed = ComposeRemoval(plan.Anchor, plan.Piles, histories, owner);
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
            piles = plan.Piles.Select(p => new { p.PlanHash, p.Projection }).ToArray(),
            source = plan.SourceEvents.OrderBy(p => p.Key).ToArray(), plan.Removed,
            Tail = TailEvidence(plan.Tail), plan.Projection }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument, message, Retryable: true));
}
