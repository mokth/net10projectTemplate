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
/// the root. Bounded writers handle independent sheets and supported repriced
/// issues after positive, negative or mixed adjustments.
/// </summary>
public static class StockReplayFifoCountSheetRemovalPlanner
{
    /// <summary>
    /// The bounded FIFO removal writer accepts one unchanged adjustment and
    /// zero-adjustment lines whose later physical movements are unchanged.
    /// Later proven MR receipts and unchanged MI/Scrap issues may remain when
    /// they do not depend on a removed count-created layer.
    /// </summary>
    public static StockReplayFifoCountSheetRemovalItem RequireIsolatedAdjustment(
        StockReplayFifoCountSheetRemovalPlan plan)
        => RequireIsolatedAdjustment(plan, repricedSign: 0);

    public static StockReplayFifoCountSheetRemovalItem
        RequireRepricedNegativeAdjustment(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequireIsolatedAdjustment(plan, repricedSign: -1);

    public static StockReplayFifoCountSheetRemovalItem
        RequireRepricedPositiveAdjustment(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequireIsolatedAdjustment(plan, repricedSign: 1);

    internal static bool NeedsIssueRepricing(
        StockReplayFifoProjectedIssue issue) =>
        !issue.PreviousConsumptions.Select(c =>
                (c.LayerId, c.Quantity, c.Value))
            .OrderBy(c => c.LayerId)
            .SequenceEqual(issue.Allocations.Select(a =>
                    (a.LayerId, a.Quantity, a.Value))
                .OrderBy(a => a.LayerId));

    private static StockReplayFifoCountSheetRemovalItem
        RequireIsolatedAdjustment(
            StockReplayFifoCountSheetRemovalPlan plan,
            int repricedSign)
    {
        ValidatePreparedPlan(plan);
        var adjustedLines = plan.Sheet.Lines.Where(l =>
            l.Physical.RemovedActiveEvent.SignedDelta != 0m).ToArray();
        if (adjustedLines.Length != 1
            || plan.Sheet.Lines.Any(l =>
                l.Physical.RemovedActiveEvent.SignedDelta == 0m
                && (l.Physical.Projection.FinalQuantity
                        != l.Physical.CurrentQuantity
                    || l.Physical.LaterEvents.Count
                        != l.Physical.Projection.LaterSteps.Count
                    || l.Physical.LaterEvents.Zip(
                        l.Physical.Projection.LaterSteps).Any(pair =>
                            pair.First.EventId != pair.Second.EventId
                            || pair.First.SignedDelta
                                != pair.Second.SignedDelta))))
            throw Invalid("The isolated FIFO reversal needs one adjustment and unchanged zero lines.");
        var line = adjustedLines[0];
        var item = plan.Items.Single(i => i.CountLineIds.Contains(line.CountLineId));
        var removed = item.Projection?.RemovedCounts;
        if (plan.Items.Any(i => i != item && i.Projection is not null)
            || removed is null || removed.Count != 1
            || removed[0].SignedAdjustmentQuantity == 0m
            || repricedSign != 0
                && Math.Sign(removed[0].SignedAdjustmentQuantity)
                    != repricedSign
            || repricedSign != 0
                && !item.Projection!.LaterIssues.Any(NeedsIssueRepricing)
            || item.Projection!.LaterIssues.Any(issue =>
                !issue.DepletesPool
                || repricedSign == 0
                    && NeedsIssueRepricing(issue))
            || item.Projection.FinalQuantity
                != item.Snapshot.CurrentQuantity
                    - removed[0].SignedAdjustmentQuantity)
            throw Invalid("The FIFO count has a tail or an adjustment that needs a broader reversal writer.");
        var count = removed[0];
        if (count.SignedAdjustmentQuantity > 0m)
        {
            var receipt = count.Receipt;
            var changedKeys = item.Projection.LaterIssues
                .Where(NeedsIssueRepricing).Select(i => i.Key).ToHashSet();
            if (receipt is null || count.Consumptions.Count != 0
                || item.Snapshot.ActiveConsumptions.Any(c =>
                    c.LayerId == receipt.LayerId
                    && (repricedSign != 1
                        || !changedKeys.Contains(c.IssueKey)))
                || item.Snapshot.Layers.Count(l =>
                    l.LayerId == receipt.LayerId
                    && l.OriginalQuantity == receipt.OriginalQuantity
                    && l.OriginalValue == receipt.OriginalValue
                    && l.RemainingQuantity
                        + item.Snapshot.ActiveConsumptions.Where(c =>
                            c.LayerId == receipt.LayerId)
                            .Sum(c => c.Quantity) == l.OriginalQuantity
                    && l.RemainingValue
                        + item.Snapshot.ActiveConsumptions.Where(c =>
                            c.LayerId == receipt.LayerId)
                            .Sum(c => c.Value) == l.OriginalValue) != 1
                || receipt.ReceiptKey != count.Key
                || receipt.OriginalQuantity != receipt.RemainingQuantity
                || receipt.OriginalValue != receipt.RemainingValue
                || item.Snapshot.Layers.Any(l =>
                    l.LayerId != receipt.LayerId
                    && l.ReceiptKey is { } key
                    && key.CompareTo(count.Key) >= 0
                    && key.ClassPriority != StockReplayEventKey.OrdinaryClass)
                || item.Projection.FinalValue
                    != StockLedgerPrecision.Money(
                        item.Snapshot.CurrentValue - receipt.OriginalValue
                        - item.Projection.LaterIssues.Sum(i =>
                            i.Value - i.PreviousConsumptions.Sum(c =>
                                c.Value))))
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
                        + count.Consumptions.Sum(c => c.Value)
                        - item.Projection.LaterIssues.Sum(i =>
                            i.Value - i.PreviousConsumptions.Sum(c =>
                                c.Value)))
                throw Invalid("The negative FIFO count has dependent allocations or a later event.");
        }
        return item;
    }

    /// <summary>
    /// Multiple positive adjustments can be removed together when their
    /// original receipts are intact. Supported changed later issues may have
    /// consumed count layers; ordinary receipts retain their exact ownership.
    /// Other counted items may have only zero-adjustment lines and proven tails.
    /// </summary>
    public static StockReplayFifoCountSheetRemovalItem
        RequireIndependentPositiveAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequirePositiveAdjustments(plan, allowRepricedIssues: false);

    public static StockReplayFifoCountSheetRemovalItem
        RequireRepricedPositiveAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequirePositiveAdjustments(plan, allowRepricedIssues: true);

    /// <summary>
    /// Removes bounded adjustments on multiple items in one sheet. An item may
    /// have several signed lines with exact count-owned layers and consumption
    /// splits. Zero-adjustment lines on an adjusted item retain their physical
    /// proof and removal generations. Later ordinary issue allocations must
    /// remain unchanged. Zero-only items may retain proven tails.
    /// </summary>
    public static IReadOnlyList<StockReplayFifoCountSheetRemovalItem>
        RequireIndependentMultiItemPositiveAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequireMultiItemAdjustments(plan,
            allowNegative: false, allowRepricedIssues: false);

    public static IReadOnlyList<StockReplayFifoCountSheetRemovalItem>
        RequireIndependentMultiItemSignedAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequireMultiItemAdjustments(plan,
            allowNegative: true, allowRepricedIssues: false);

    /// <summary>
    /// Reprices later ordinary issues for any affected item only when their
    /// count-layer ownership and each item's final value are proven. Zero-only
    /// counted items retain their original ordinary tails.
    /// </summary>
    public static IReadOnlyList<StockReplayFifoCountSheetRemovalItem>
        RequireRepricedMultiItemAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequireMultiItemAdjustments(plan,
            allowNegative: plan.Sheet.Lines.Any(l =>
                l.Physical.RemovedActiveEvent.SignedDelta < 0m),
            allowRepricedIssues: true);

    private static IReadOnlyList<StockReplayFifoCountSheetRemovalItem>
        RequireMultiItemAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan,
            bool allowNegative, bool allowRepricedIssues)
    {
        ValidatePreparedPlan(plan);
        var adjustedItems = plan.Items.Where(i => i.Projection is not null)
            .ToArray();
        var zeroLineIds = plan.Items.Where(i => i.Projection is null)
            .SelectMany(i => i.CountLineIds).ToHashSet();
        if (adjustedItems.Length < 2
            || adjustedItems.Length > plan.Sheet.Lines.Count(l =>
                l.Physical.RemovedActiveEvent.SignedDelta != 0m)
            || plan.Sheet.Lines.Any(l =>
                zeroLineIds.Contains(l.CountLineId)
                    && l.Physical.RemovedActiveEvent.SignedDelta != 0m
                || l.Physical.Projection.FinalQuantity
                    != l.Physical.CurrentQuantity
                        - l.Physical.RemovedActiveEvent.SignedDelta
                || !allowNegative
                    && l.Physical.RemovedActiveEvent.SignedDelta < 0m
                || l.Physical.LaterEvents.Count
                    != l.Physical.Projection.LaterSteps.Count
                || l.Physical.LaterEvents.Zip(
                    l.Physical.Projection.LaterSteps).Any(pair =>
                        pair.First.EventId != pair.Second.EventId
                        || pair.First.SignedDelta != pair.Second.SignedDelta))
            || allowNegative && !plan.Sheet.Lines.Any(l =>
                l.Physical.RemovedActiveEvent.SignedDelta < 0m)
            || allowRepricedIssues && !adjustedItems.Any(i =>
                i.Projection?.LaterIssues.Any(NeedsIssueRepricing) == true))
            throw Invalid("The multi-item FIFO reversal has unsupported adjusted or zero lines.");
        foreach (var item in adjustedItems)
        {
            var lineIds = item.CountLineIds.ToHashSet();
            var selected = plan.Sheet.Lines.Where(l =>
                lineIds.Contains(l.CountLineId)
                && l.Physical.RemovedActiveEvent.SignedDelta != 0m)
                .ToArray();
            if (selected.Length > 1)
            {
                var itemRepriced = allowRepricedIssues
                    && item.Projection!.LaterIssues.Any(NeedsIssueRepricing);
                if (selected.Any(l =>
                        l.Physical.RemovedActiveEvent.SignedDelta < 0m))
                {
                    if (!allowNegative)
                        throw Invalid("A multi-item FIFO count has a negative adjustment in a positive-only sheet.");
                    RequireSignedAdjustmentItem(plan, item, selected,
                        requirePositive: selected.Any(l =>
                            l.Physical.RemovedActiveEvent.SignedDelta > 0m),
                        allowRepricedIssues: itemRepriced);
                }
                else
                    RequirePositiveAdjustmentItem(plan, item, selected,
                        itemRepriced);
                continue;
            }
            if (selected.Length != 1 || item.Projection is null)
                throw Invalid("The multi-item FIFO reversal has an incomplete item.");
            var line = selected[0];
            var removed = item.Projection.RemovedCounts;
            var count = removed.Count == 1 ? removed[0] : null;
            var receipt = count?.Receipt;
            var changedKeys = item.Projection.LaterIssues
                .Where(NeedsIssueRepricing).Select(i => i.Key).ToHashSet();
            var changedValue = item.Projection.LaterIssues.Sum(i =>
                i.Value - i.PreviousConsumptions.Sum(c => c.Value));
            if (count is null
                || count.Key != line.Physical.RemovedActiveEvent.Key
                || count.SignedAdjustmentQuantity
                    != line.Physical.RemovedActiveEvent.SignedDelta
                || item.Projection.LaterIssues.Any(i =>
                    !i.DepletesPool
                    || !allowRepricedIssues && NeedsIssueRepricing(i))
                || line.Physical.Projection.FinalQuantity
                    != line.Physical.CurrentQuantity
                        - count.SignedAdjustmentQuantity
                || line.Physical.LaterEvents.Count
                    != line.Physical.Projection.LaterSteps.Count
                || line.Physical.LaterEvents.Zip(
                    line.Physical.Projection.LaterSteps).Any(pair =>
                        pair.First.EventId != pair.Second.EventId
                        || pair.First.SignedDelta != pair.Second.SignedDelta)
                || item.Projection.FinalQuantity
                    != StockLedgerPrecision.Quantity(
                        item.Snapshot.CurrentQuantity
                        - count.SignedAdjustmentQuantity))
                throw Invalid("A multi-item FIFO count has dependent layers or a later event.");
            if (count.SignedAdjustmentQuantity > 0m)
            {
                if (receipt is null || count.Consumptions.Count != 0
                    || receipt.ReceiptKey != count.Key
                    || receipt.OriginalQuantity
                        != count.SignedAdjustmentQuantity
                    || receipt.OriginalQuantity != receipt.RemainingQuantity
                    || receipt.OriginalValue != receipt.RemainingValue
                    || item.Projection.BeforeFirstCount.LaterReceipts.Count(r =>
                        r.ReceiptKey?.ClassPriority
                            == StockReplayEventKey.CountClass) != 1
                    || !item.Snapshot.Layers.Any(l =>
                        l.LayerId == receipt.LayerId
                        && l.OriginalQuantity == receipt.OriginalQuantity
                        && l.OriginalValue == receipt.OriginalValue
                        && l.RemainingQuantity
                            + item.Snapshot.ActiveConsumptions.Where(c =>
                                c.LayerId == receipt.LayerId)
                                .Sum(c => c.Quantity) == l.OriginalQuantity
                        && l.RemainingValue
                            + item.Snapshot.ActiveConsumptions.Where(c =>
                                c.LayerId == receipt.LayerId)
                                .Sum(c => c.Value) == l.OriginalValue)
                    || item.Snapshot.ActiveConsumptions.Any(c =>
                        c.LayerId == receipt.LayerId
                        && (!allowRepricedIssues
                            || !changedKeys.Contains(c.IssueKey)))
                    || item.Snapshot.Layers.Any(l =>
                        l.ReceiptKey is { } key
                        && key.CompareTo(count.Key) >= 0
                        && key.ClassPriority
                            != StockReplayEventKey.OrdinaryClass
                        && l.LayerId != receipt.LayerId)
                    || item.Projection.FinalValue
                        != StockLedgerPrecision.Money(
                            item.Snapshot.CurrentValue
                            - receipt.OriginalValue - changedValue))
                    throw Invalid("A multi-item FIFO count has a dependent receipt.");
            }
            else
            {
                var consumptions = count.Consumptions;
                if (!allowNegative || receipt is not null
                    || consumptions.Count == 0
                    || consumptions.Any(c => !c.DepletesPool
                        || c.IssueKey != count.Key)
                    || consumptions.Sum(c => c.Quantity)
                        != -count.SignedAdjustmentQuantity
                    || !consumptions.Select(c => c.ConsumptionId)
                        .Order().SequenceEqual(item.Snapshot
                            .ActiveConsumptions.Where(c =>
                                c.IssueKey == count.Key)
                            .Select(c => c.ConsumptionId).Order())
                    || item.Projection.BeforeFirstCount.LaterReceipts.Any(r =>
                        r.ReceiptKey?.ClassPriority
                            == StockReplayEventKey.CountClass)
                    || item.Snapshot.Layers.Any(l =>
                        l.ReceiptKey is not { } key
                        || key.CompareTo(count.Key) >= 0
                            && key.ClassPriority
                                != StockReplayEventKey.OrdinaryClass)
                    || item.Projection.FinalValue
                        != StockLedgerPrecision.Money(
                            item.Snapshot.CurrentValue
                            + consumptions.Sum(c => c.Value)
                            - changedValue))
                    throw Invalid("A multi-item FIFO count has dependent negative allocations.");
            }
        }
        return adjustedItems;
    }

    private static StockReplayFifoCountSheetRemovalItem
        RequirePositiveAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan,
            bool allowRepricedIssues)
    {
        ValidatePreparedPlan(plan);
        if (plan.Items.Count(i => i.Projection is not null) != 1
            || plan.Sheet.Lines.Count < 2)
            throw Invalid("The bounded FIFO sheet reversal needs one adjusted item and multiple lines.");
        var item = plan.Items.Single(i => i.Projection is not null);
        var adjusted = plan.Sheet.Lines.Where(l =>
            l.Physical.RemovedActiveEvent.SignedDelta != 0m).ToArray();
        RequirePositiveAdjustmentItem(plan, item, adjusted,
            allowRepricedIssues);
        return item;
    }

    private static void RequirePositiveAdjustmentItem(
        StockReplayFifoCountSheetRemovalPlan plan,
        StockReplayFifoCountSheetRemovalItem item,
        StockReplayCountSheetRemovalLine[] adjusted,
        bool allowRepricedIssues)
    {
        var removed = item.Projection?.RemovedCounts;
        var changedKeys = item.Projection?.LaterIssues
            .Where(NeedsIssueRepricing).Select(i => i.Key).ToHashSet();
        if (adjusted.Length < 2 || removed is null
            || allowRepricedIssues && (changedKeys is null
                || changedKeys.Count == 0)
            || removed.Count != adjusted.Length
            || adjusted.Any(l => l.Physical.RemovedActiveEvent.SignedDelta <= 0m)
            || plan.Sheet.Lines.Any(l =>
                l.Physical.Projection.FinalQuantity
                    != l.Physical.CurrentQuantity
                        - l.Physical.RemovedActiveEvent.SignedDelta
                || l.Physical.LaterEvents.Count
                    != l.Physical.Projection.LaterSteps.Count
                || l.Physical.LaterEvents.Zip(
                    l.Physical.Projection.LaterSteps).Any(pair =>
                        pair.First.EventId != pair.Second.EventId
                        || pair.First.SignedDelta
                            != pair.Second.SignedDelta))
            || item.Projection!.LaterIssues.Any(issue =>
                !issue.DepletesPool
                || !allowRepricedIssues && NeedsIssueRepricing(issue))
            || item.Projection.BeforeFirstCount.LaterReceipts.Count(r =>
                r.ReceiptKey?.ClassPriority
                    == StockReplayEventKey.CountClass) != adjusted.Length
            || removed.Any(c => c.Receipt is null
                || c.Consumptions.Count != 0
                || c.Receipt.ReceiptKey != c.Key
                || c.Receipt.OriginalQuantity != c.Receipt.RemainingQuantity
                || c.Receipt.OriginalValue != c.Receipt.RemainingValue
                || !item.Snapshot.Layers.Any(l =>
                    l.LayerId == c.Receipt.LayerId
                    && l.OriginalQuantity == c.Receipt.OriginalQuantity
                    && l.OriginalValue == c.Receipt.OriginalValue
                    && l.RemainingQuantity
                        + item.Snapshot.ActiveConsumptions.Where(x =>
                            x.LayerId == c.Receipt.LayerId)
                            .Sum(x => x.Quantity) == l.OriginalQuantity
                    && l.RemainingValue
                        + item.Snapshot.ActiveConsumptions.Where(x =>
                            x.LayerId == c.Receipt.LayerId)
                            .Sum(x => x.Value) == l.OriginalValue)
                || item.Snapshot.ActiveConsumptions.Any(x =>
                    x.LayerId == c.Receipt.LayerId
                    && (!allowRepricedIssues
                        || changedKeys is null
                        || !changedKeys.Contains(x.IssueKey))))
            || !adjusted.Select(l => l.Physical.RemovedActiveEvent.Key)
                .Order().SequenceEqual(removed.Select(c => c.Key).Order())
            || item.Snapshot.Layers.Any(l =>
                l.ReceiptKey is { } key
                && key.CompareTo(removed[0].Key) >= 0
                && key.ClassPriority
                    != StockReplayEventKey.OrdinaryClass
                && !removed.Any(c => c.Key == key))
            || item.Projection.FinalQuantity != StockLedgerPrecision.Quantity(
                item.Snapshot.CurrentQuantity
                - removed.Sum(c => c.SignedAdjustmentQuantity))
            || item.Projection.FinalValue != StockLedgerPrecision.Money(
                item.Snapshot.CurrentValue
                - removed.Sum(c => c.Receipt!.OriginalValue)
                - item.Projection.LaterIssues.Sum(i =>
                    i.Value - i.PreviousConsumptions.Sum(c => c.Value))))
            throw Invalid("The FIFO sheet has dependent adjustments or later item activity.");
    }

    /// <summary>
    /// An independent mixed sheet keeps positive layers unconsumed and later
    /// ordinary issue allocations unchanged. Repriced mixed removal also
    /// accepts count-layer consumptions owned by corrected later issues.
    /// Other counted items may have only zero-adjustment lines.
    /// </summary>
    public static StockReplayFifoCountSheetRemovalItem
        RequireIndependentMixedAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequireIndependentSignedAdjustments(plan, requirePositive: true,
            allowRepricedIssues: false);

    public static StockReplayFifoCountSheetRemovalItem
        RequireRepricedMixedAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequireIndependentSignedAdjustments(plan, requirePositive: true,
            allowRepricedIssues: true);

    public static StockReplayFifoCountSheetRemovalItem
        RequireIndependentNegativeAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan)
        => RequireIndependentSignedAdjustments(plan, requirePositive: false,
            allowRepricedIssues: false);

    private static StockReplayFifoCountSheetRemovalItem
        RequireIndependentSignedAdjustments(
            StockReplayFifoCountSheetRemovalPlan plan,
            bool requirePositive, bool allowRepricedIssues)
    {
        ValidatePreparedPlan(plan);
        if (plan.Items.Count(i => i.Projection is not null) != 1
            || plan.Sheet.Lines.Count < 2)
            throw Invalid("The mixed FIFO reversal needs one adjusted item and multiple lines.");
        var item = plan.Items.Single(i => i.Projection is not null);
        var adjusted = plan.Sheet.Lines.Where(l =>
            l.Physical.RemovedActiveEvent.SignedDelta != 0m).ToArray();
        RequireSignedAdjustmentItem(plan, item, adjusted, requirePositive,
            allowRepricedIssues);
        return item;
    }

    private static void RequireSignedAdjustmentItem(
        StockReplayFifoCountSheetRemovalPlan plan,
        StockReplayFifoCountSheetRemovalItem item,
        StockReplayCountSheetRemovalLine[] adjusted,
        bool requirePositive, bool allowRepricedIssues)
    {
        var removed = item.Projection?.RemovedCounts;
        var changedKeys = item.Projection?.LaterIssues
            .Where(NeedsIssueRepricing).Select(i => i.Key).ToHashSet();
        var positiveLayerIds = removed?.Where(c => c.Receipt is not null)
            .Select(c => c.Receipt!.LayerId).ToHashSet();
        if (adjusted.Length < 2 || removed is null
            || (requirePositive
                && !removed.Any(c => c.SignedAdjustmentQuantity > 0m))
            || (!requirePositive
                && removed.Any(c => c.SignedAdjustmentQuantity > 0m))
            || !removed.Any(c => c.SignedAdjustmentQuantity < 0m)
            || allowRepricedIssues && (changedKeys is null
                || changedKeys.Count == 0)
            || removed.Count != adjusted.Length
            || !adjusted.Select(l => l.Physical.RemovedActiveEvent.Key)
                .Order().SequenceEqual(removed.Select(c => c.Key).Order())
            || plan.Sheet.Lines.Any(l =>
                l.Physical.Projection.FinalQuantity
                    != l.Physical.CurrentQuantity
                        - l.Physical.RemovedActiveEvent.SignedDelta
                || l.Physical.LaterEvents.Count
                    != l.Physical.Projection.LaterSteps.Count
                || l.Physical.LaterEvents.Zip(
                    l.Physical.Projection.LaterSteps).Any(pair =>
                        pair.First.EventId != pair.Second.EventId
                        || pair.First.SignedDelta
                            != pair.Second.SignedDelta))
            || item.Projection!.LaterIssues.Any(issue =>
                !issue.DepletesPool
                || !allowRepricedIssues && NeedsIssueRepricing(issue))
            || item.Projection.BeforeFirstCount.LaterReceipts.Count(r =>
                r.ReceiptKey?.ClassPriority
                    == StockReplayEventKey.CountClass)
                != removed.Count(c => c.SignedAdjustmentQuantity > 0m)
            || removed.Any(c => c.SignedAdjustmentQuantity > 0m
                ? c.Receipt is null || c.Consumptions.Count != 0
                    || c.Receipt.ReceiptKey != c.Key
                    || c.Receipt.OriginalQuantity
                        != c.Receipt.RemainingQuantity
                    || c.Receipt.OriginalValue
                        != c.Receipt.RemainingValue
                    || !item.Snapshot.Layers.Any(l =>
                        l.LayerId == c.Receipt.LayerId
                        && l.OriginalQuantity == c.Receipt.OriginalQuantity
                        && l.OriginalValue == c.Receipt.OriginalValue
                        && l.RemainingQuantity
                            + item.Snapshot.ActiveConsumptions.Where(x =>
                                x.LayerId == c.Receipt.LayerId)
                                .Sum(x => x.Quantity) == l.OriginalQuantity
                        && l.RemainingValue
                            + item.Snapshot.ActiveConsumptions.Where(x =>
                                x.LayerId == c.Receipt.LayerId)
                                .Sum(x => x.Value) == l.OriginalValue)
                    || item.Snapshot.ActiveConsumptions.Any(x =>
                        x.LayerId == c.Receipt.LayerId
                        && (!allowRepricedIssues
                            || changedKeys is null
                            || !changedKeys.Contains(x.IssueKey)))
                : c.Receipt is not null || c.Consumptions.Count == 0
                    || c.Consumptions.Any(x => !x.DepletesPool
                        || x.IssueKey != c.Key
                        || positiveLayerIds!.Contains(x.LayerId))
                    || c.Consumptions.Sum(x => x.Quantity)
                        != -c.SignedAdjustmentQuantity
                    || !c.Consumptions.Select(x => x.ConsumptionId)
                        .Order().SequenceEqual(item.Snapshot
                            .ActiveConsumptions.Where(x =>
                                x.IssueKey == c.Key)
                            .Select(x => x.ConsumptionId).Order()))
            || item.Snapshot.Layers.Any(l =>
                l.ReceiptKey is { } key
                && key.CompareTo(removed[0].Key) >= 0
                && key.ClassPriority
                    != StockReplayEventKey.OrdinaryClass
                && !removed.Any(c => c.Receipt?.ReceiptKey == key))
            || item.Projection.FinalQuantity != StockLedgerPrecision.Quantity(
                item.Snapshot.CurrentQuantity
                - removed.Sum(c => c.SignedAdjustmentQuantity))
            || item.Projection.FinalValue != StockLedgerPrecision.Money(
                item.Snapshot.CurrentValue
                - removed.Sum(c => c.Receipt?.OriginalValue ?? 0m)
                + removed.Sum(c => c.Consumptions.Sum(x => x.Value))
                - item.Projection.LaterIssues.Sum(i =>
                    i.Value - i.PreviousConsumptions.Sum(c => c.Value))))
            throw Invalid("The mixed FIFO sheet has dependent adjustments or repriced issues.");
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
        var repostedIds = await ReadRepostedGenerationIdsAsync(db,
            sheet.CompanyCode, sheet.BranchCode, active.Values,
            cancellationToken);
        if (active.Count != lineIds.Length || sheet.Lines.Any(l =>
                !active.TryGetValue(l.CountLineId, out var generation)
                || generation.IsRemoval
                || generation.PreviousGenerationId is not null
                    && !repostedIds.Contains(generation.Id)
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

    internal static async Task<HashSet<long>> ReadRepostedGenerationIdsAsync(
        AppDbContext db, string companyCode, string branchCode,
        IEnumerable<StockCountReplayGeneration> generations,
        CancellationToken cancellationToken)
    {
        var rows = generations.Where(g => g.PreviousGenerationId.HasValue)
            .ToArray();
        var parentIds = rows.Select(g => g.PreviousGenerationId!.Value)
            .Distinct().ToArray();
        if (parentIds.Length == 0)
            return [];
        var parents = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.CompanyCode == companyCode
                && g.BranchCode == branchCode && parentIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, cancellationToken);
        return rows.Where(g => parents.TryGetValue(
                g.PreviousGenerationId!.Value, out var parent)
                && parent.IsRemoval
                && parent.StockCountLineId == g.StockCountLineId)
            .Select(g => g.Id).ToHashSet();
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
