using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class StockReplayPhysicalSimulatorTests
{
    [Fact]
    public void End_of_day_count_follows_every_ordinary_same_day_event()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 1));
        var ordinary = Key(countAt, 100);
        var count = Key(countAt, 1, StockReplayEventKey.CountClass);
        var correction = ordinary with { AttachedSuborder = 1 };

        Assert.True(ordinary.CompareTo(correction) < 0);
        Assert.True(correction.CompareTo(count) < 0);
        Assert.Equal(new DateTime(2026, 10, 1, 23, 59, 59).AddTicks(9_999_999), countAt);
    }

    [Fact]
    public void Late_receipt_replays_later_issue_and_changes_final_live_quantity()
    {
        var candidate = new StockReplayPhysicalEvent(1, Key(new DateTime(2026, 10, 1, 8, 0, 0), 3), 3m);
        var issue = new StockReplayPhysicalEvent(2, Key(new DateTime(2026, 10, 2, 8, 0, 0), 2), -4m);

        var result = StockReplayPhysicalSimulator.Simulate(6m, candidate, [issue]);

        Assert.Equal(10m, result.HistoricalQuantityBeforeCandidate);
        Assert.Equal(13m, result.Candidate.QuantityAfter);
        Assert.Equal(9m, Assert.Single(result.LaterSteps).QuantityAfter);
        Assert.Equal(9m, result.FinalQuantity);
    }

    [Fact]
    public void Later_count_recomputes_its_delta_and_preserves_frozen_physical_quantity()
    {
        var candidate = new StockReplayPhysicalEvent(1, Key(new DateTime(2026, 10, 1, 8, 0, 0), 3), 3m);
        var count = new StockReplayPhysicalEvent(2,
            Key(StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 1)), 2,
                StockReplayEventKey.CountClass), -2m, 8m);
        var issue = new StockReplayPhysicalEvent(3, Key(new DateTime(2026, 10, 2, 8, 0, 0), 4), -4m);

        var result = StockReplayPhysicalSimulator.Simulate(4m, candidate, [issue, count]);

        Assert.Equal(10m, result.HistoricalQuantityBeforeCandidate);
        Assert.Equal(-5m, result.LaterSteps[0].SignedDelta);
        Assert.Equal(8m, result.LaterSteps[0].QuantityAfter);
        Assert.Equal(4m, result.FinalQuantity);
    }

    [Theory]
    [InlineData(0, -2)]
    [InlineData(2, 0)]
    [InlineData(2, -3)]
    public void Count_replay_generation_handles_zero_and_direction_changes(
        decimal oldDelta, decimal expectedNewDelta)
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 2));
        var candidateDelta = oldDelta - expectedNewDelta;
        var candidate = new StockReplayPhysicalEvent(9,
            Key(countAt.AddDays(-1), 3), candidateDelta);
        var count = new StockReplayPhysicalEvent(-100,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                2, "C0000000001", 0), oldDelta, 10m);
        var projection = StockReplayPhysicalSimulator.Simulate(10m, candidate, [count]);
        Assert.Equal(expectedNewDelta, Assert.Single(projection.LaterSteps).SignedDelta);
        Assert.Equal(10m, projection.FinalQuantity);

        var initial = new StockCountReplayGeneration
        {
            Id = 100, CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = 1, AnchorPostingId = 1,
            WritePostingId = 1, AnchorPostingSequence = 2,
            EffectiveAt = countAt, SignedAdjustmentQty = oldDelta,
            AdjustmentBatchNo = oldDelta == 0m ? null : 40,
            AdjustmentHistoryId = oldDelta == 0m ? null : 400
        };
        var corrected = new StockCountReplayGeneration
        {
            Id = 101, CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = 1, AnchorPostingId = 1,
            WritePostingId = 3, AnchorPostingSequence = 2,
            PreviousGenerationId = initial.Id, EffectiveAt = countAt,
            SignedAdjustmentQty = expectedNewDelta,
            ReversalBatchNo = oldDelta == 0m ? null : 41,
            ReversalHistoryId = oldDelta == 0m ? null : 401,
            AdjustmentBatchNo = expectedNewDelta == 0m ? null : 42,
            AdjustmentHistoryId = expectedNewDelta == 0m ? null : 402
        };
        var active = StockCountGenerationResolver.ResolveActive([initial, corrected]);
        Assert.Same(corrected, active[1]);
    }

    [Fact]
    public void Negative_intermediate_slice_identifies_the_first_failing_event()
    {
        var candidate = new StockReplayPhysicalEvent(1, Key(new DateTime(2026, 10, 1, 8, 0, 0), 3), -3m);
        var later = new StockReplayPhysicalEvent(2, Key(new DateTime(2026, 10, 2, 8, 0, 0), 2), 4m);

        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayPhysicalSimulator.Simulate(4m, candidate, [later]));

        Assert.Equal(StockLedgerErrorCodes.InsufficientBaseQty, error.Error.Code);
        Assert.Contains("event 1", error.Message);
    }

    [Fact]
    public void Removing_a_count_reset_replays_the_later_issue_without_the_count()
    {
        var count = new StockReplayPhysicalEvent(10,
            Key(StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 1)), 5,
                StockReplayEventKey.CountClass), -2m, 8m);
        var issue = new StockReplayPhysicalEvent(11,
            Key(new DateTime(2026, 10, 2, 8, 0, 0), 6), -4m);

        var result = StockReplayPhysicalSimulator.SimulateRemoval(4m, count, [issue]);

        Assert.Equal(10m, result.HistoricalQuantityBeforeRemovedEvent);
        Assert.Equal(6m, result.FinalQuantity);
    }

    [Fact]
    public void Removing_a_count_reset_rejects_a_later_negative_slice()
    {
        var count = new StockReplayPhysicalEvent(10,
            Key(StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 1)), 5,
                StockReplayEventKey.CountClass), 6m, 8m);
        var issue = new StockReplayPhysicalEvent(11,
            Key(new DateTime(2026, 10, 2, 8, 0, 0), 6), -5m);

        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayPhysicalSimulator.SimulateRemoval(3m, count, [issue]));

        Assert.Equal(StockLedgerErrorCodes.InsufficientBaseQty, error.Error.Code);
        Assert.Contains("event 11", error.Message);
    }

    private static StockReplayEventKey Key(DateTime at, long sequence, int priority = 0) =>
        new(at, priority, sequence, "line-1", 0);
}
