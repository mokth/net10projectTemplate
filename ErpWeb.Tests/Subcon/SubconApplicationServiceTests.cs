using ErpWeb.Core.Subcon;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Subcon;

[Trait(TestCategories.Name, TestCategories.Subcon)]
public class SubconApplicationServiceTests : IAsyncLifetime
{
    private readonly SubconTestHarness _h = new();

    public async Task InitializeAsync() => await _h.SeedMastersAsync();

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task EnsureCreated_creates_subcon_tables()
    {
        await using var db = await _h.Factory.CreateDbContextAsync();
        Assert.Equal(0, await db.SubconPolicies.CountAsync());
        Assert.Equal(0, await db.SubconDispatches.CountAsync());
        Assert.Equal(0, await db.SubconDispatchLines.CountAsync());
        Assert.Equal(0, await db.SubconReceipts.CountAsync());
        Assert.Equal(0, await db.SubconReceiptLines.CountAsync());
    }

    [Fact]
    public async Task Policy_defaults_all_live_flags_off()
    {
        var result = await _h.CreateService().GetPolicyAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(result.Policy!.SubconEnabled);
        Assert.False(result.Policy.InventoryModeEnabled);
        Assert.False(result.Policy.ProductionModeEnabled);
    }

    [Fact]
    public async Task Policy_refuses_live_enablement_and_stays_off()
    {
        var svc = _h.CreateService();
        var live = await svc.SavePolicyAsync(new SubconPolicySaveCommand { SubconEnabled = true });
        Assert.False(live.Succeeded);
        Assert.Equal(SubconBlockedReasons.LiveEnablementFrozen, live.ErrorMessage);

        var inventory = await svc.SavePolicyAsync(new SubconPolicySaveCommand { InventoryModeEnabled = true });
        Assert.False(inventory.Succeeded);
        Assert.Equal(SubconBlockedReasons.InventoryModeFrozen, inventory.ErrorMessage);

        var production = await svc.SavePolicyAsync(new SubconPolicySaveCommand { ProductionModeEnabled = true });
        Assert.False(production.Succeeded);
        Assert.Equal(SubconBlockedReasons.ProductionModeFrozen, production.ErrorMessage);

        var saved = await svc.SavePolicyAsync(new SubconPolicySaveCommand());
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.False(saved.Policy!.SubconEnabled);
        Assert.False(saved.Policy.InventoryModeEnabled);
        Assert.False(saved.Policy.ProductionModeEnabled);
        Assert.True(saved.Policy.IsPublished);
    }

    [Fact]
    public async Task Save_update_delete_new_draft_uses_tenant_company_and_branch()
    {
        var svc = _h.CreateService();
        var saved = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(saved.Dispatch!.DocNo));
        Assert.StartsWith("SD", saved.Dispatch.DocNo);
        Assert.Equal(SubconStatuses.New, saved.Dispatch.Status);
        Assert.Equal(SubconModes.Inventory, saved.Dispatch.Mode);
        Assert.Equal("SUP01", saved.Dispatch.SupplierCode);
        Assert.Equal("Paint Co", saved.Dispatch.SupplierName);
        Assert.Single(saved.Dispatch.Lines);

        await using (var db = await _h.Factory.CreateDbContextAsync())
        {
            var row = await db.SubconDispatches.SingleAsync();
            Assert.Equal("DEMO", row.CompanyCode);
            Assert.Equal("HQ", row.BranchCode);
            Assert.Equal(saved.Dispatch.DocNo, row.DocNo);
        }

        var updated = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            DocNo = saved.Dispatch.DocNo,
            TrxDate = DateTime.Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Remarks = "keep draft",
            RowVersion = saved.Dispatch.RowVersion,
            Lines =
            [
                new SubconDispatchLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "RM1",
                    Qty = 8m,
                    WarehouseCode = "MAIN",
                    LocationCode = "BIN1",
                    Uom = "PCS"
                }
            ]
        });
        Assert.True(updated.Succeeded, updated.ErrorMessage);
        Assert.Equal(8m, updated.Dispatch!.Lines[0].Qty);
        Assert.Equal("keep draft", updated.Dispatch.Remarks);

        var deleted = await svc.DeleteDispatchDraftAsync(new SubconKeyCommand
        {
            DocNo = updated.Dispatch.DocNo,
            RowVersion = updated.Dispatch.RowVersion
        });
        Assert.True(deleted.Succeeded, deleted.ErrorMessage);
        await using (var db = await _h.Factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.SubconDispatches.CountAsync());
            Assert.Equal(0, await db.SubconDispatchLines.CountAsync());
        }
    }

    [Fact]
    public async Task Save_and_delete_do_not_mutate_stock_or_wip()
    {
        await _h.SeedBalancesAsync();
        var before = await _h.SnapshotAsync();
        var svc = _h.CreateService();
        var saved = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var deleted = await svc.DeleteDispatchDraftAsync(new SubconKeyCommand
        {
            DocNo = saved.Dispatch!.DocNo,
            RowVersion = saved.Dispatch.RowVersion
        });
        Assert.True(deleted.Succeeded, deleted.ErrorMessage);
        var after = await _h.SnapshotAsync();
        AssertUnchangedStock(before, after);
    }

    [Fact]
    public async Task Receipt_draft_can_allocate_to_a_send_out_draft_without_posting()
    {
        var svc = _h.CreateService();
        var dispatch = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        Assert.True(dispatch.Succeeded, dispatch.ErrorMessage);

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
                    Qty = 2m
                }
            ]
        });
        Assert.True(receipt.Succeeded, receipt.ErrorMessage);
        Assert.StartsWith("SR", receipt.Receipt!.DocNo);
        Assert.Equal(SubconStatuses.New, receipt.Receipt.Status);
        Assert.Equal(dispatch.Dispatch.DocNo, receipt.Receipt.Lines[0].DispatchDocNo);
        Assert.Equal(2m, receipt.Receipt.Lines[0].Qty);
    }

    [Fact]
    public async Task Workbench_counts_drafts_without_mixing_uom_into_one_total()
    {
        var svc = _h.CreateService();
        Assert.True((await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch())).Succeeded);
        var page = await svc.GetWorkbenchAsync(new SubconListQuery { Take = 20 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(1, page.Workbench!.DraftCount);
        Assert.Equal(1, page.Workbench.OpenLineCount);
        Assert.Contains("5 PCS", page.Workbench.OutstandingByUom);
        Assert.Equal(SubconBlockedReasons.PostingNotEnabled, page.Workbench.PostingBanner);
    }

    private static void AssertUnchangedStock(StockSnapshot before, StockSnapshot after)
    {
        Assert.Equal(before.BalLocCount, after.BalLocCount);
        Assert.Equal(before.BalLocQty, after.BalLocQty);
        Assert.Equal(before.HistoryCount, after.HistoryCount);
        Assert.Equal(before.BatchCount, after.BatchCount);
        Assert.Equal(before.BatchDetailCount, after.BatchDetailCount);
        Assert.Equal(before.StockPostingCount, after.StockPostingCount);
        Assert.Equal(before.WipCount, after.WipCount);
        Assert.Equal(before.WipQty, after.WipQty);
    }
}
