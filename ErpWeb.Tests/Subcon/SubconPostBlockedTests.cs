using ErpWeb.Core.Subcon;

namespace ErpWeb.Tests.Subcon;

[Trait(TestCategories.Name, TestCategories.Subcon)]
public class SubconPostBlockedTests : IAsyncLifetime
{
    private readonly SubconTestHarness _h = new();

    public async Task InitializeAsync()
    {
        await _h.SeedMastersAsync();
        await _h.SeedBalancesAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Post_dispatch_returns_blocked_reason_and_does_not_write_tr_or_wip()
    {
        var svc = _h.CreateService();
        var saved = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var before = await _h.SnapshotAsync();

        var posted = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = saved.Dispatch!.DocNo,
            RowVersion = saved.Dispatch.RowVersion
        });
        Assert.False(posted.Succeeded);
        Assert.Equal(SubconBlockedReasons.NoAtomicInventorySeam, posted.ErrorMessage);

        var after = await _h.SnapshotAsync();
        AssertUnchangedLedger(before, after);
        Assert.Equal(before.DispatchCount, after.DispatchCount);
    }

    [Fact]
    public async Task Rollback_dispatch_returns_blocked_reason_and_does_not_write_tr_or_wip()
    {
        var svc = _h.CreateService();
        var saved = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        var before = await _h.SnapshotAsync();
        var rolled = await svc.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = saved.Dispatch!.DocNo,
            RowVersion = saved.Dispatch.RowVersion
        });
        Assert.False(rolled.Succeeded);
        Assert.Equal(SubconBlockedReasons.NoAtomicInventorySeam, rolled.ErrorMessage);
        AssertUnchangedLedger(before, await _h.SnapshotAsync());
    }

    [Fact]
    public async Task Post_receipt_returns_blocked_reason_and_does_not_write_tr_or_wip()
    {
        var svc = _h.CreateService();
        var dispatch = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        var receipt = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = DateTime.Today,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconReceiptLineSaveCommand
                {
                    LineNo = 1,
                    DispatchDocNo = dispatch.Dispatch!.DocNo,
                    DispatchLineNo = 1,
                    Qty = 1m
                }
            ]
        });
        Assert.True(receipt.Succeeded, receipt.ErrorMessage);
        var before = await _h.SnapshotAsync();

        var posted = await svc.PostReceiptAsync(new SubconKeyCommand
        {
            DocNo = receipt.Receipt!.DocNo,
            RowVersion = receipt.Receipt.RowVersion
        });
        Assert.False(posted.Succeeded);
        Assert.Equal(SubconBlockedReasons.NoAtomicInventorySeam, posted.ErrorMessage);
        AssertUnchangedLedger(before, await _h.SnapshotAsync());
    }

    private static void AssertUnchangedLedger(StockSnapshot before, StockSnapshot after)
    {
        Assert.Equal(before.BalLocCount, after.BalLocCount);
        Assert.Equal(100m, after.BalLocQty);
        Assert.Equal(before.HistoryCount, after.HistoryCount);
        Assert.Equal(0, after.HistoryCount);
        Assert.Equal(0, after.BatchCount);
        Assert.Equal(0, after.BatchDetailCount);
        Assert.Equal(0, after.StockPostingCount);
        Assert.Equal(before.WipCount, after.WipCount);
        Assert.Equal(10m, after.WipQty);
    }
}
