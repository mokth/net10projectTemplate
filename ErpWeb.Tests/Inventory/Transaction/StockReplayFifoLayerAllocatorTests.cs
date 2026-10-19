using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class StockReplayFifoLayerAllocatorTests
{
    private static readonly DateTime Day = new(2026, 10, 1);

    [Fact]
    public void Issue_allocates_oldest_layers_and_preserves_exact_remainder()
    {
        var first = Layer(1, Day, 1, 2m, 4m);
        var second = Layer(2, Day.AddDays(1), 2, 5m, 15m);
        var key = IssueKey(Day.AddDays(2), 3);
        var result = StockReplayFifoLayerAllocator.AllocateIssue(
            [second, first], key, 4m);

        Assert.Equal(new[] { 1L, 2L },
            result.Allocations.Select(a => a.LayerId));
        Assert.Equal(new[] { 2m, 2m },
            result.Allocations.Select(a => a.Quantity));
        Assert.Equal(new[] { 4m, 6m },
            result.Allocations.Select(a => a.Value));
        Assert.Equal(10m, result.TotalValue);
        Assert.Equal(0m, result.Layers.Single(l => l.LayerId == 1)
            .RemainingQuantity);
        Assert.Equal(3m, result.Layers.Single(l => l.LayerId == 2)
            .RemainingQuantity);
        Assert.Equal(9m, result.Layers.Single(l => l.LayerId == 2)
            .RemainingValue);
        Assert.Equal(2m, first.RemainingQuantity);
        Assert.Equal(5m, second.RemainingQuantity);
    }

    [Fact]
    public void Final_split_takes_exact_six_decimal_value()
    {
        var layer = Layer(1, Day, 1, 3m, 1.000001m);
        var first = StockReplayFifoLayerAllocator.AllocateIssue(
            [layer], IssueKey(Day.AddDays(1), 2), 1m);
        Assert.Equal(0.333334m, Assert.Single(first.Allocations).Value);
        Assert.Equal(0.666667m, Assert.Single(first.Layers).RemainingValue);

        var final = StockReplayFifoLayerAllocator.AllocateIssue(
            first.Layers, IssueKey(Day.AddDays(2), 3), 2m);
        Assert.Equal(0.666667m, Assert.Single(final.Allocations).Value);
        Assert.Equal(0m, Assert.Single(final.Layers).RemainingValue);
        Assert.Equal(StockFifoLayerStatuses.Closed,
            Assert.Single(final.Layers).Status);
    }

    [Fact]
    public void Issue_cannot_consume_a_later_receipt_at_the_same_time()
    {
        var earlier = Layer(1, Day, 1, 2m, 4m);
        var later = Layer(2, Day.AddDays(1), 4, 10m, 30m);
        var refusal = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoLayerAllocator.AllocateIssue(
                [earlier, later], IssueKey(Day.AddDays(1), 3), 3m));
        Assert.Equal(StockLedgerErrorCodes.InsufficientBaseQty,
            refusal.Error.Code);
        Assert.Equal(10m, later.RemainingQuantity);
    }

    [Fact]
    public void Rewind_restores_later_issue_and_removes_later_receipt()
    {
        var first = Layer(1, Day, 1, 5m, 15m) with
        {
            RemainingQuantity = 2m,
            RemainingValue = 6m,
            ReceiptKey = IssueKey(Day, 1)
        };
        var second = Layer(2, Day.AddDays(2), 3, 4m, 20m) with
        {
            RemainingQuantity = 2m,
            RemainingValue = 10m,
            ReceiptKey = IssueKey(Day.AddDays(2), 3)
        };
        var issueKey = IssueKey(Day.AddDays(3), 4);
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            4m, 16m, [first, second],
            [
                new(10, 20, 1, 3m, 9m, 0, true, issueKey),
                new(11, 21, 2, 2m, 10m, 1, true, issueKey)
            ], "verified");

        var before = StockReplayFifoLayerRewinder.RewindBefore(snapshot,
            IssueKey(Day.AddDays(1), 2));

        Assert.Equal(5m, before.Quantity);
        Assert.Equal(15m, before.Value);
        Assert.Single(before.Layers);
        Assert.Equal(5m, before.Layers[0].RemainingQuantity);
        Assert.Single(before.LaterReceipts);
        Assert.Equal(4m, before.LaterReceipts[0].RemainingQuantity);
        Assert.Equal(2, before.LaterConsumptions.Count);
        Assert.Equal(2m, first.RemainingQuantity);
    }

    [Fact]
    public void Rewind_ignores_transfer_allocations_without_changing_pool()
    {
        var layer = Layer(1, Day, 1, 5m, 15m) with
        {
            ReceiptKey = IssueKey(Day, 1)
        };
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            5m, 15m, [layer],
            [new(10, 20, 1, 3m, 9m, 0, false,
                IssueKey(Day.AddDays(2), 3))], "verified");

        var before = StockReplayFifoLayerRewinder.RewindBefore(snapshot,
            IssueKey(Day.AddDays(1), 2));

        Assert.Equal(5m, before.Quantity);
        Assert.Equal(15m, before.Value);
        Assert.Single(before.LaterConsumptions);
    }

    [Fact]
    public void Earlier_issue_reallocates_a_later_issue_across_receipt_layers()
    {
        var first = Layer(1, Day, 1, 5m, 15m) with
        {
            RemainingQuantity = 0m,
            RemainingValue = 0m,
            CurrentUnitCost = 0m,
            Status = StockFifoLayerStatuses.Closed,
            ReceiptKey = IssueKey(Day, 1)
        };
        var second = Layer(2, Day.AddDays(2), 3, 10m, 40m) with
        {
            RemainingQuantity = 7m,
            RemainingValue = 28m,
            ReceiptKey = IssueKey(Day.AddDays(2), 3)
        };
        var laterKey = IssueKey(Day.AddDays(3), 4);
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            7m, 28m, [first, second],
            [
                new(10, 20, 1, 5m, 15m, 0, true, laterKey),
                new(11, 21, 2, 3m, 12m, 1, true, laterKey)
            ], "verified");

        var result = StockReplayFifoTailProjector.InsertIssue(snapshot,
            IssueKey(Day.AddDays(1), 2), 2m);

        Assert.Equal(2, result.Issues.Count);
        Assert.Equal(6m, result.Issues[0].Value);
        Assert.Equal(29m, result.Issues[1].Value);
        Assert.Equal(new[] { 3m, 5m },
            result.Issues[1].Allocations.Select(a => a.Quantity));
        Assert.Equal(5m, result.FinalQuantity);
        Assert.Equal(20m, result.FinalValue);
        Assert.Equal(0m, first.RemainingQuantity);
        Assert.Equal(7m, second.RemainingQuantity);
    }

    [Fact]
    public void Earlier_issue_reprices_transfer_allocation_without_depleting_it()
    {
        var layer = Layer(1, Day, 1, 5m, 15m) with
        {
            ReceiptKey = IssueKey(Day, 1)
        };
        var transferKey = IssueKey(Day.AddDays(2), 3);
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            5m, 15m, [layer],
            [new(10, 20, 1, 4m, 12m, 0, false, transferKey)],
            "verified");

        var result = StockReplayFifoTailProjector.InsertIssue(snapshot,
            IssueKey(Day.AddDays(1), 2), 1m);

        Assert.Equal(2, result.Issues.Count);
        Assert.False(result.Issues[1].DepletesPool);
        Assert.Equal(12m, result.Issues[1].Value);
        Assert.Equal(4m, result.FinalQuantity);
        Assert.Equal(12m, result.FinalValue);
    }

    [Fact]
    public void Inserted_and_removed_transfer_preserve_the_fifo_pool_and_later_issue()
    {
        var first = Layer(1, Day, 1, 5m, 15m) with
        {
            RemainingQuantity = 0m,
            RemainingValue = 0m,
            CurrentUnitCost = 0m,
            Status = StockFifoLayerStatuses.Closed,
            ReceiptKey = IssueKey(Day, 1)
        };
        var second = Layer(2, Day.AddDays(1), 2, 10m, 40m) with
        {
            RemainingQuantity = 7m,
            RemainingValue = 28m,
            ReceiptKey = IssueKey(Day.AddDays(1), 2)
        };
        var transferKey = IssueKey(Day.AddDays(2), 3);
        var issueKey = IssueKey(Day.AddDays(3), 4);
        StockReplayFifoConsumptionPosition[] issue =
        [
            new(10, 20, 1, 5m, 15m, 0, true, issueKey),
            new(11, 21, 2, 3m, 12m, 1, true, issueKey)
        ];
        var before = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            7m, 28m, [first, second], issue, "verified");

        var inserted = StockReplayFifoTailProjector.InsertTransfer(
            before, transferKey, 8m);
        var candidate = Assert.Single(inserted.Issues.Where(i => i.IsCandidate));
        Assert.False(candidate.DepletesPool);
        Assert.Equal(27m, candidate.Value);
        Assert.Equal([5m, 3m], candidate.Allocations.Select(a => a.Quantity));
        Assert.Equal(27m, Assert.Single(inserted.Issues.Where(i => !i.IsCandidate)).Value);
        Assert.Equal(7m, inserted.FinalQuantity);
        Assert.Equal(28m, inserted.FinalValue);

        StockReplayFifoConsumptionPosition[] transfer =
        [
            new(12, 22, 1, 5m, 15m, 0, false, transferKey),
            new(13, 23, 2, 3m, 12m, 1, false, transferKey)
        ];
        var withTransfer = before with
        {
            ActiveConsumptions = transfer.Concat(issue).ToArray()
        };
        var removed = StockReplayFifoTailProjector.RemoveTransfer(
            withTransfer, transferKey);
        Assert.True(removed.IsRemoval);
        Assert.Equal(27m, Assert.Single(removed.Issues).Value);
        Assert.Equal(7m, removed.FinalQuantity);
        Assert.Equal(28m, removed.FinalValue);
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            Assert.Throws<StockLedgerException>(() =>
                StockReplayFifoTailProjector.RemoveIssue(
                    withTransfer, transferKey)).Error.Code);
    }

    [Fact]
    public void Projection_refuses_a_tail_that_did_not_follow_fifo_order()
    {
        var first = Layer(1, Day, 1, 5m, 15m) with
        {
            ReceiptKey = IssueKey(Day, 1)
        };
        var second = Layer(2, Day.AddDays(1), 2, 10m, 40m) with
        {
            RemainingQuantity = 8m,
            RemainingValue = 32m,
            ReceiptKey = IssueKey(Day.AddDays(1), 2)
        };
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            13m, 47m, [first, second],
            [new(10, 20, 2, 2m, 8m, 0, true,
                IssueKey(Day.AddDays(3), 4))], "verified");

        var refusal = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoTailProjector.InsertIssue(snapshot,
                IssueKey(Day.AddDays(2), 3), 1m));

        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            refusal.Error.Code);
    }

    [Fact]
    public void Earlier_receipt_reallocates_a_later_issue_and_retains_its_origin()
    {
        var first = Layer(1, Day, 1, 5m, 15m) with
        {
            RemainingQuantity = 0m,
            RemainingValue = 0m,
            CurrentUnitCost = 0m,
            Status = StockFifoLayerStatuses.Closed,
            ReceiptKey = IssueKey(Day, 1)
        };
        var second = Layer(2, Day.AddDays(2), 3, 10m, 40m) with
        {
            RemainingQuantity = 7m,
            RemainingValue = 28m,
            ReceiptKey = IssueKey(Day.AddDays(2), 3)
        };
        var issueKey = IssueKey(Day.AddDays(3), 4);
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            7m, 28m, [first, second],
            [
                new(10, 20, 1, 5m, 15m, 0, true, issueKey),
                new(11, 21, 2, 3m, 12m, 1, true, issueKey)
            ], "verified");

        var result = StockReplayFifoTailProjector.InsertReceipt(snapshot,
            IssueKey(Day.AddDays(1), 2), 2m, 4m, "EA");

        var projectedIssue = Assert.Single(result.Issues);
        Assert.Equal(23m, projectedIssue.Value);
        Assert.Equal(new[] { 1L, long.MaxValue, 2L },
            projectedIssue.Allocations.Select(a => a.LayerId));
        Assert.Equal(new[] { 5m, 2m, 1m },
            projectedIssue.Allocations.Select(a => a.Quantity));
        Assert.Equal(9m, result.FinalQuantity);
        Assert.Equal(36m, result.FinalValue);
        Assert.NotNull(result.CandidateReceipt);
        Assert.Equal(0m, result.FinalLayers.Single(l =>
            l.LayerId == result.CandidateReceipt.LayerId).RemainingQuantity);
        Assert.Equal(7m, second.RemainingQuantity);
        var wrongUom = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoTailProjector.InsertReceipt(snapshot,
                IssueKey(Day.AddDays(1), 2), 2m, 4m, "BOX"));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            wrongUom.Error.Code);
    }

    [Fact]
    public void Removing_earlier_receipt_reallocates_later_issue_or_refuses_shortage()
    {
        var first = Layer(1, Day, 1, 5m, 15m) with
        {
            RemainingQuantity = 0m,
            RemainingValue = 0m,
            CurrentUnitCost = 0m,
            Status = StockFifoLayerStatuses.Closed,
            ReceiptKey = IssueKey(Day, 1)
        };
        var second = Layer(2, Day.AddDays(1), 2, 10m, 40m) with
        {
            RemainingQuantity = 7m,
            RemainingValue = 28m,
            ReceiptKey = IssueKey(Day.AddDays(1), 2)
        };
        var issueKey = IssueKey(Day.AddDays(2), 3);
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            7m, 28m, [first, second],
            [
                new(10, 20, 1, 5m, 15m, 0, true, issueKey),
                new(11, 21, 2, 3m, 12m, 1, true, issueKey)
            ], "verified");

        var removal = StockReplayFifoTailProjector.RemoveReceipt(
            snapshot, first.LayerId);
        Assert.True(removal.IsRemoval);
        Assert.Equal(first.LayerId, removal.CandidateReceipt?.LayerId);
        Assert.Equal(32m, Assert.Single(removal.Issues).Value);
        Assert.Equal(2m, removal.FinalQuantity);
        Assert.Equal(8m, removal.FinalValue);
        Assert.DoesNotContain(removal.FinalLayers, l => l.LayerId == first.LayerId);

        var shortage = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoTailProjector.RemoveReceipt(snapshot, second.LayerId));
        Assert.Equal(StockLedgerErrorCodes.InsufficientBaseQty,
            shortage.Error.Code);
    }

    [Fact]
    public void Removing_earlier_issue_reallocates_later_issue_to_oldest_layer()
    {
        var first = Layer(1, Day, 1, 5m, 15m) with
        {
            RemainingQuantity = 0m,
            RemainingValue = 0m,
            CurrentUnitCost = 0m,
            Status = StockFifoLayerStatuses.Closed,
            ReceiptKey = IssueKey(Day, 1)
        };
        var second = Layer(2, Day.AddDays(1), 2, 10m, 40m) with
        {
            RemainingQuantity = 7m,
            RemainingValue = 28m,
            ReceiptKey = IssueKey(Day.AddDays(1), 2)
        };
        var firstIssue = IssueKey(Day.AddDays(2), 3);
        var laterIssue = IssueKey(Day.AddDays(3), 4);
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            7m, 28m, [first, second],
            [
                new(10, 20, 1, 4m, 12m, 0, true, firstIssue),
                new(11, 21, 1, 1m, 3m, 0, true, laterIssue),
                new(12, 22, 2, 3m, 12m, 1, true, laterIssue)
            ], "verified");

        var removal = StockReplayFifoTailProjector.RemoveIssue(
            snapshot, firstIssue);

        Assert.True(removal.IsRemoval);
        Assert.Equal(12m, Assert.Single(removal.Issues).Value);
        Assert.Equal(11m, removal.FinalQuantity);
        Assert.Equal(43m, removal.FinalValue);
        Assert.Equal(1m, removal.FinalLayers.Single(l =>
            l.LayerId == first.LayerId).RemainingQuantity);
    }

    [Fact]
    public void Count_adjustments_reallocate_later_fifo_issue_in_both_directions()
    {
        var first = Layer(1, Day, 1, 5m, 15m) with
        {
            RemainingQuantity = 0m, RemainingValue = 0m,
            CurrentUnitCost = 0m, Status = StockFifoLayerStatuses.Closed,
            ReceiptKey = IssueKey(Day, 1)
        };
        var receiptAt = Day.AddDays(2);
        var second = Layer(2, receiptAt, 3, 5m, 20m) with
        {
            RemainingQuantity = 4m, RemainingValue = 16m,
            ReceiptKey = IssueKey(receiptAt, 3)
        };
        var laterKey = IssueKey(Day.AddDays(3), 4);
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            4m, 16m, [first, second],
            [
                new(10, 20, 1, 5m, 15m, 0, true, laterKey),
                new(11, 21, 2, 1m, 4m, 1, true, laterKey)
            ], "verified");
        var key = CountKey(Day.AddDays(1), 2);

        var inbound = StockReplayFifoTailProjector.InsertCountAdjustment(
            snapshot, key, 2m, 9m, "EA");
        Assert.Equal(5m, inbound.BeforeCandidate.Quantity);
        Assert.Equal(15m, inbound.BeforeCandidate.Value);
        Assert.Equal(6m, inbound.CandidateReceipt?.OriginalValue);
        Assert.Equal(18m, Assert.Single(inbound.Issues).Value);
        Assert.Equal([1L, long.MaxValue],
            Assert.Single(inbound.Issues).Allocations.Select(a => a.LayerId));
        Assert.Equal(6m, inbound.FinalQuantity);
        Assert.Equal(23m, inbound.FinalValue);
        Assert.Equal(16m, snapshot.CurrentValue);
        var postedLayers = inbound.FinalLayers.Select(l =>
            l.LayerId == long.MaxValue
                ? l with { LayerId = 3, OriginFactId = 3, OriginPostingId = 3 }
                : l).ToArray();
        var postedInbound = snapshot with
        {
            CurrentQuantity = inbound.FinalQuantity,
            CurrentValue = inbound.FinalValue,
            Layers = postedLayers,
            ActiveConsumptions =
            [
                new(10, 20, 1, 5m, 15m, 0, true, laterKey),
                new(11, 21, 3, 1m, 3m, 1, true, laterKey)
            ]
        };
        var removedInbound = StockReplayFifoTailProjector.RemoveReceipt(
            postedInbound, 3);
        Assert.Equal(4m, removedInbound.FinalQuantity);
        Assert.Equal(16m, removedInbound.FinalValue);
        Assert.Equal(19m, Assert.Single(removedInbound.Issues).Value);

        var outbound = StockReplayFifoTailProjector.InsertCountAdjustment(
            snapshot, key, -2m, null, "EA");
        var countIssue = Assert.Single(outbound.Issues.Where(i => i.IsCandidate));
        Assert.Equal(6m, countIssue.Value);
        Assert.Equal([1L], countIssue.Allocations.Select(a => a.LayerId));
        Assert.Equal(21m, Assert.Single(outbound.Issues.Where(i => !i.IsCandidate)).Value);
        Assert.Equal(2m, outbound.FinalQuantity);
        Assert.Equal(8m, outbound.FinalValue);
        var postedOutbound = snapshot with
        {
            CurrentQuantity = outbound.FinalQuantity,
            CurrentValue = outbound.FinalValue,
            Layers = outbound.FinalLayers,
            ActiveConsumptions =
            [
                new(12, 30, 1, 2m, 6m, 0, true, key),
                new(13, 20, 1, 3m, 9m, 0, true, laterKey),
                new(14, 21, 2, 3m, 12m, 1, true, laterKey)
            ]
        };
        var removedOutbound = StockReplayFifoTailProjector.RemoveIssue(
            postedOutbound, key);
        Assert.Equal(4m, removedOutbound.FinalQuantity);
        Assert.Equal(16m, removedOutbound.FinalValue);
        Assert.Equal(19m, Assert.Single(removedOutbound.Issues).Value);
    }

    [Fact]
    public void Empty_pool_count_uses_frozen_cost_and_zero_adjustment_preserves_tail()
    {
        var empty = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            0m, 0m, [], [], "verified");
        var key = CountKey(Day, 1);
        var inbound = StockReplayFifoTailProjector.InsertCountAdjustment(
            empty, key, 2m, 3.5m, "EA");
        Assert.Equal(7m, inbound.CandidateReceipt?.OriginalValue);
        Assert.Equal(2m, inbound.FinalQuantity);
        Assert.Equal(7m, inbound.FinalValue);
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            Assert.Throws<StockLedgerException>(() =>
                StockReplayFifoTailProjector.InsertCountAdjustment(
                    empty, key, 2m, null, "EA")).Error.Code);
        var zero = StockReplayFifoTailProjector.InsertCountAdjustment(
            empty, key, 0m, null, "EA");
        Assert.Empty(zero.Issues);
        Assert.Empty(zero.FinalLayers);
        Assert.Equal(0m, zero.FinalQuantity);
        Assert.Equal(0m, zero.FinalValue);
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            Assert.Throws<StockLedgerException>(() =>
                StockReplayFifoTailProjector.InsertCountAdjustment(
                    empty, IssueKey(Day, 1), 0m, null, "EA")).Error.Code);
    }

    [Fact]
    public void Count_depletion_sees_ordinary_receipt_at_final_tick_even_with_later_sequence()
    {
        var at = StockReplayEventKey.CountEndOfDay(Day);
        var receipt = Layer(1, at, 10, 3m, 9m) with
        {
            ReceiptKey = IssueKey(at, 10)
        };
        var count = CountKey(Day, 5);
        var allocation = StockReplayFifoLayerAllocator.AllocateIssue(
            [receipt], count, 2m);
        Assert.Equal(6m, allocation.TotalValue);
        Assert.Equal(1m, Assert.Single(allocation.Layers).RemainingQuantity);
        var forged = receipt with { ReceiptKey = IssueKey(at, 9) };
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            Assert.Throws<StockLedgerException>(() =>
                StockReplayFifoLayerAllocator.AllocateIssue(
                    [forged], count, 2m)).Error.Code);
    }

    [Fact]
    public void Ordinary_final_tick_receipt_precedes_count_layer_in_fifo_allocation()
    {
        var at = StockReplayEventKey.CountEndOfDay(Day);
        var ordinary = Layer(1, at, 10, 1m, 4m) with
        {
            ReceiptKey = IssueKey(at, 10)
        };
        var countLayer = Layer(2, at, 5, 1m, 3m) with
        {
            ReceiptKey = CountKey(Day, 5)
        };

        var allocation = StockReplayFifoLayerAllocator.AllocateIssue(
            [countLayer, ordinary], IssueKey(Day.AddDays(1), 11), 1m);

        Assert.Equal(1L, Assert.Single(allocation.Allocations).LayerId);
        Assert.Equal(4m, allocation.TotalValue);
        Assert.Equal(1m, allocation.Layers.Single(l => l.LayerId == 2)
            .RemainingQuantity);
    }

    [Fact]
    public void Multiple_count_lines_share_one_fifo_pool_before_later_issue()
    {
        var first = Layer(1, Day, 1, 10m, 30m) with
        {
            RemainingQuantity = 0m, RemainingValue = 0m,
            CurrentUnitCost = 0m, Status = StockFifoLayerStatuses.Closed,
            ReceiptKey = IssueKey(Day, 1)
        };
        var laterReceiptAt = Day.AddDays(2);
        var second = Layer(2, laterReceiptAt, 3, 5m, 20m) with
        {
            RemainingQuantity = 4m, RemainingValue = 16m,
            ReceiptKey = IssueKey(laterReceiptAt, 3)
        };
        var laterIssueKey = IssueKey(Day.AddDays(3), 4);
        var snapshot = new StockReplayFifoActiveLayerSnapshot("C", "B", "A",
            4m, 16m, [first, second],
            [
                new(10, 20, 1, 10m, 30m, 0, true, laterIssueKey),
                new(11, 21, 2, 1m, 4m, 1, true, laterIssueKey)
            ], "verified");
        var firstCountKey = CountKey(Day.AddDays(1), 2);
        var secondCountKey = firstCountKey with
        {
            LineIdentity = "C0000000002"
        };
        var zeroCountKey = firstCountKey with
        {
            LineIdentity = "C0000000003"
        };
        StockReplayFifoCountCandidate[] counts =
        [
            new(firstCountKey, 2m, 9m, "EA"),
            new(secondCountKey, -1m, null, "EA"),
            new(zeroCountKey, 0m, null, "EA")
        ];

        var projection = StockReplayFifoCountSheetProjector.Insert(
            snapshot, counts.Reverse().ToArray());

        Assert.Equal(10m, projection.BeforeFirstCount.Quantity);
        Assert.Equal(30m, projection.BeforeFirstCount.Value);
        Assert.Equal(6m, projection.Counts[0].Receipt?.OriginalValue);
        Assert.Equal(3m, projection.Counts[1].Issue?.Value);
        Assert.Null(projection.Counts[2].Issue);
        Assert.Equal(33m, Assert.Single(projection.LaterIssues).Value);
        Assert.Equal([9m, 2m], Assert.Single(projection.LaterIssues)
            .Allocations.Select(a => a.Quantity));
        Assert.Equal(5m, projection.FinalQuantity);
        Assert.Equal(20m, projection.FinalValue);
        Assert.Equal(16m, snapshot.CurrentValue);

        var duplicate = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoCountSheetProjector.Insert(snapshot,
                [counts[0], counts[0]]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            duplicate.Error.Code);
        var missingBasis = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoCountSheetProjector.Insert(snapshot,
                [counts[0] with { FrozenFallbackUnitCost = null }]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            missingBasis.Error.Code);
        var mixedAnchor = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoCountSheetProjector.Insert(snapshot,
                [counts[0], counts[1] with
                {
                    Key = secondCountKey with { OriginalPostingSequence = 9 }
                }]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            mixedAnchor.Error.Code);
        var invalidQuantity = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoCountSheetProjector.Insert(snapshot,
                [counts[0] with { SignedAdjustmentQuantity = 0.000001m }]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            invalidQuantity.Error.Code);
    }

    private static StockReplayEventKey CountKey(DateTime day, long sequence) =>
        new(StockReplayEventKey.CountEndOfDay(day),
            StockReplayEventKey.CountClass, sequence, "C0000000001", 0);

    private static StockReplayFifoLayerPosition Layer(
        long id, DateTime at, long sequence, decimal quantity,
        decimal value) => new(id, id, id, sequence, at,
            quantity, value, 0m, quantity, value,
            decimal.Round(value / quantity, 6,
                MidpointRounding.AwayFromZero),
            "EA", StockFifoLayerStatuses.Open, "01");

    private static StockReplayEventKey IssueKey(
        DateTime at, long sequence) => new(at,
            StockReplayEventKey.OrdinaryClass, sequence, "H0000000001", 0);
}
