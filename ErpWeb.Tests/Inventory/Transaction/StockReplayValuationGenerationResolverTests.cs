using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class StockReplayValuationGenerationResolverTests
{
    [Fact]
    public void Fifo_issue_requires_all_active_splits()
    {
        var history = History(7, 5m, 0m);
        var first = Fact(1, 7, -1, 2m, 12m, StockCostMethods.Fifo);
        var second = Fact(2, 7, -1, 3m, 21m, StockCostMethods.Fifo);

        var active = StockReplayValuationGenerationResolver.ResolveActive([history], [first, second]);

        Assert.Equal([1L, 2L], active[7].Select(x => x.Id));
        Assert.Equal(33m, active[7].Sum(x => x.CostAmount));
    }

    [Fact]
    public void Transfer_requires_both_quantity_legs_and_equal_value()
    {
        var history = History(8, 4m, 4m);
        var outbound = Fact(3, 8, -1, 4m, 24m);
        var inbound = Fact(4, 8, 1, 4m, 24m);
        Assert.Equal(2, StockReplayValuationGenerationResolver.ResolveActive(
            [history], [outbound, inbound])[8].Count);

        inbound.CostAmount = 25m;
        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayValuationGenerationResolver.ResolveActive([history], [outbound, inbound]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Fact]
    public void Reversal_without_a_complete_replacement_is_rejected()
    {
        var history = History(9, 5m, 0m);
        var original = Fact(5, 9, -1, 5m, 30m);
        var reverse = Fact(6, 10, 1, 5m, 30m);
        reverse.ValuationStatus = StockValuationStatuses.Reversed;
        reverse.ReversesValuationFactId = original.Id;

        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayValuationGenerationResolver.ResolveActive([history], [original, reverse]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Fact]
    public void Partial_replacement_after_reversing_two_Fifo_splits_is_rejected()
    {
        var history = History(11, 5m, 0m);
        var a = Fact(7, 11, -1, 2m, 12m, StockCostMethods.Fifo);
        var b = Fact(8, 11, -1, 3m, 21m, StockCostMethods.Fifo);
        var aReverse = Fact(9, 12, 1, 2m, 12m, StockCostMethods.Fifo);
        aReverse.ValuationStatus = StockValuationStatuses.Reversed;
        aReverse.ReversesValuationFactId = a.Id;
        var bReverse = Fact(10, 12, 1, 3m, 21m, StockCostMethods.Fifo);
        bReverse.ValuationStatus = StockValuationStatuses.Reversed;
        bReverse.ReversesValuationFactId = b.Id;
        var partial = Fact(11, 11, -1, 2m, 13m, StockCostMethods.Fifo);

        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayValuationGenerationResolver.ResolveActive(
                [history], [a, b, aReverse, bReverse, partial]));
        Assert.Contains("splits", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IvTrxHistory History(int id, decimal outQty, decimal inQty) => new()
    {
        Id = id, CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
        LedgerVersion = 2, EntryRole = "ORIGINAL", FrStdQty = outQty, ToStdQty = inQty
    };

    private static StockValuationFact Fact(
        long id, int historyId, int direction, decimal qty, decimal amount,
        string costMethod = StockCostMethods.MovingAverage) => new()
    {
        Id = id, InventoryHistoryId = historyId, CompanyCode = "DEMO", BranchCode = "HQ",
        ItemCode = "A100", Direction = direction, BaseQty = qty, CostAmount = amount,
        CostMethod = costMethod, ValuationStatus = StockValuationStatuses.Valued,
        StockPostingId = 1, PostingLineNo = 1, SplitOrdinal = (int)id
    };
}
