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
