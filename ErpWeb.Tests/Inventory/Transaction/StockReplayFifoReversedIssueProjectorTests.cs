using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class StockReplayFifoReversedIssueProjectorTests
{
    private static readonly DateTime Start = new(2026, 10, 1);
    private static readonly StockReplayEventKey Issue = Key(Start.AddDays(2), 3, "ISSUE");
    private static readonly StockReplayEventKey Reversal = Key(Start.AddDays(3), 4, "REVERSE");

    [Fact]
    public void Inserting_earlier_receipt_projects_the_reversed_issue_splits()
    {
        var opening = Layer(1, Start.AddDays(-1), 1, 2m, 4m);
        var later = Layer(2, Start.AddDays(1), 2, 18m, 36m);
        var snapshot = Snapshot(opening, later);
        var sourceKey = Key(Start, 5, "PENDING");
        var candidate = StockReplayFifoTailProjector.InsertReceipt(snapshot, sourceKey, 5m, 50m, "EA")
            .CandidateReceipt!;

        var plan = StockReplayFifoReversedIssueProjector.InsertReceipt(
            snapshot, Issue, Reversal, [Consumption(1, 2m, 4m, 0), Consumption(2, 2m, 4m, 1)], candidate);

        Assert.Equal(8m, plan.OriginalValue);
        Assert.Equal(24m, plan.ProjectedValue);
        Assert.Equal(new[] { 1L, long.MaxValue }, plan.ProjectedSplits.Select(s => s.LayerId));
        Assert.Equal(new[] { 4m, 20m }, plan.ProjectedSplits.Select(s => s.Value));
        Assert.Equal(20m, snapshot.CurrentQuantity);
        Assert.Equal(40m, snapshot.CurrentValue);
    }

    [Fact]
    public void Removing_receipt_projects_the_reversed_issue_without_its_layer()
    {
        var snapshot = Snapshot(Layer(1, Start.AddDays(-1), 1, 2m, 4m),
            Layer(2, Start, 2, 5m, 50m), Layer(3, Start.AddDays(1), 3, 18m, 36m));
        var previous = new[] { Consumption(1, 2m, 4m, 0), Consumption(2, 2m, 20m, 1) };

        var plan = StockReplayFifoReversedIssueProjector.RemoveReceipt(snapshot, Issue, Reversal, previous, 2);

        Assert.Equal(24m, plan.OriginalValue);
        Assert.Equal(8m, plan.ProjectedValue);
        Assert.Equal(new[] { 1L, 3L }, plan.ProjectedSplits.Select(s => s.LayerId));
        Assert.Equal(25m, snapshot.CurrentQuantity);
        Assert.Equal(90m, snapshot.CurrentValue);
    }

    [Fact]
    public void Removing_receipt_refuses_a_historical_shortage()
    {
        var snapshot = Snapshot(Layer(1, Start.AddDays(-1), 1, 2m, 4m), Layer(2, Start, 2, 5m, 50m));
        var previous = new[] { Consumption(1, 2m, 4m, 0), Consumption(2, 2m, 20m, 1) };

        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoReversedIssueProjector.RemoveReceipt(snapshot, Issue, Reversal, previous, 2));
        Assert.Equal(StockLedgerErrorCodes.InsufficientBaseQty, error.Error.Code);
    }

    [Fact]
    public void Projected_split_keeps_the_exact_six_decimal_layer_value()
    {
        var snapshot = Snapshot(Layer(1, Start.AddDays(-1), 1, 3m, 1.000001m),
            Layer(2, Start.AddDays(1), 2, 18m, 36m));
        var candidate = StockReplayFifoTailProjector.InsertReceipt(snapshot,
            Key(Start, 5, "PENDING"), 5m, 50m, "EA").CandidateReceipt!;
        var previous = new[] { Consumption(1, 3m, 1.000001m, 0), Consumption(2, 1m, 2m, 1) };

        var plan = StockReplayFifoReversedIssueProjector.InsertReceipt(snapshot, Issue, Reversal, previous, candidate);

        Assert.Equal(3.000001m, plan.OriginalValue);
        Assert.Equal(11.000001m, plan.ProjectedValue);
        Assert.Equal(1.000001m, plan.ProjectedSplits[0].Value);
        Assert.Equal(10m, plan.ProjectedSplits[1].Value);
    }

    [Fact]
    public void Removing_receipt_refuses_an_unreproducible_original_split()
    {
        var snapshot = Snapshot(Layer(1, Start.AddDays(-1), 1, 2m, 4m),
            Layer(2, Start, 2, 5m, 50m), Layer(3, Start.AddDays(1), 3, 18m, 36m));
        var damaged = new[] { Consumption(1, 2m, 4m, 0), Consumption(2, 2m, 19m, 1) };

        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoReversedIssueProjector.RemoveReceipt(snapshot, Issue, Reversal, damaged, 2));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Fact]
    public void Intervening_active_issue_requires_a_wider_replay()
    {
        var snapshot = Snapshot(Layer(1, Start.AddDays(-1), 1, 2m, 4m),
            Layer(2, Start, 2, 5m, 50m), Layer(3, Start.AddDays(1), 3, 18m, 36m)) with
        {
            ActiveConsumptions = [new(20, 30, 3, 1m, 2m, 0, true, Key(Start.AddDays(2).AddHours(1), 5, "LATER"))]
        };
        var previous = new[] { Consumption(1, 2m, 4m, 0), Consumption(2, 2m, 20m, 1) };

        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoReversedIssueProjector.RemoveReceipt(snapshot, Issue, Reversal, previous, 2));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    private static StockReplayFifoActiveLayerSnapshot Snapshot(params StockReplayFifoLayerPosition[] layers) =>
        new("C", "B", "A", layers.Sum(l => l.RemainingQuantity), layers.Sum(l => l.RemainingValue),
            layers, [], "verified");

    private static StockReplayFifoLayerPosition Layer(long id, DateTime at, long sequence, decimal quantity, decimal value) =>
        new(id, id, id, sequence, at, quantity, value, 0m, quantity, value,
            decimal.Round(value / quantity, 6, MidpointRounding.AwayFromZero), "EA",
            StockFifoLayerStatuses.Open, "01", Key(at, sequence, $"L{id}"));

    private static StockReplayFifoConsumptionPosition Consumption(long layer, decimal quantity, decimal value, int ordinal) =>
        new(10 + ordinal, 20 + ordinal, layer, quantity, value, ordinal, true, Issue);

    private static StockReplayEventKey Key(DateTime at, long sequence, string line) =>
        new(at, StockReplayEventKey.OrdinaryClass, sequence, line, 0);
}
