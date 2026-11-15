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

    [Fact]
    public async Task Editing_receipt_draft_does_not_double_count_own_existing_qty()
    {
        var svc = _h.CreateService();
        var dispatch = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch(qty: 5m));
        Assert.True(dispatch.Succeeded, dispatch.ErrorMessage);

        var created = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
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
                    Qty = 3m
                }
            ]
        });
        Assert.True(created.Succeeded, created.ErrorMessage);

        var edited = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            DocNo = created.Receipt!.DocNo,
            TrxDate = DateTime.Today,
            SupplierCode = "SUP01",
            RowVersion = created.Receipt.RowVersion,
            Lines =
            [
                new SubconReceiptLineSaveCommand
                {
                    LineNo = 1,
                    DispatchDocNo = dispatch.Dispatch.DocNo,
                    DispatchLineNo = 1,
                    Qty = 5m
                }
            ]
        });
        Assert.True(edited.Succeeded, edited.ErrorMessage);
        Assert.Equal(5m, edited.Receipt!.Lines[0].Qty);

        var over = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            DocNo = edited.Receipt.DocNo,
            TrxDate = DateTime.Today,
            SupplierCode = "SUP01",
            RowVersion = edited.Receipt.RowVersion,
            Lines =
            [
                new SubconReceiptLineSaveCommand
                {
                    LineNo = 1,
                    DispatchDocNo = dispatch.Dispatch.DocNo,
                    DispatchLineNo = 1,
                    Qty = 6m
                }
            ]
        });
        Assert.False(over.Succeeded);
        Assert.Contains("receive only 5", over.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Receipt_with_duplicate_lines_to_same_dispatch_line_sums_combined_qty()
    {
        var svc = _h.CreateService();
        var dispatch = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch(qty: 5m));
        Assert.True(dispatch.Succeeded, dispatch.ErrorMessage);

        var over = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = DateTime.Today,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = dispatch.Dispatch!.DocNo, DispatchLineNo = 1, Qty = 3m },
                new SubconReceiptLineSaveCommand { LineNo = 2, DispatchDocNo = dispatch.Dispatch.DocNo, DispatchLineNo = 1, Qty = 3m }
            ]
        });
        Assert.False(over.Succeeded);
        Assert.Contains("receive only 5", over.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var valid = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = DateTime.Today,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = dispatch.Dispatch.DocNo, DispatchLineNo = 1, Qty = 3m },
                new SubconReceiptLineSaveCommand { LineNo = 2, DispatchDocNo = dispatch.Dispatch.DocNo, DispatchLineNo = 1, Qty = 2m }
            ]
        });
        Assert.True(valid.Succeeded, valid.ErrorMessage);
    }

    [Fact]
    public async Task Dispatch_edit_rejects_modifying_or_removing_line_referenced_by_receipt()
    {
        var svc = _h.CreateService();
        var dispatch = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch(qty: 5m));
        Assert.True(dispatch.Succeeded, dispatch.ErrorMessage);

        var receipt = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = DateTime.Today,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = dispatch.Dispatch!.DocNo, DispatchLineNo = 1, Qty = 3m }
            ]
        });
        Assert.True(receipt.Succeeded, receipt.ErrorMessage);

        var lowerQty = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            DocNo = dispatch.Dispatch.DocNo,
            TrxDate = DateTime.Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            RowVersion = dispatch.Dispatch.RowVersion,
            Lines =
            [
                new SubconDispatchLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "RM1",
                    Qty = 2m,
                    WarehouseCode = "MAIN",
                    LocationCode = "BIN1",
                    Uom = "PCS"
                }
            ]
        });
        Assert.False(lowerQty.Succeeded);
        Assert.Equal(SubconMessages.DispatchLineReferenced, lowerQty.ErrorMessage);

        var addSecondLine = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            DocNo = dispatch.Dispatch.DocNo,
            TrxDate = DateTime.Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            RowVersion = dispatch.Dispatch.RowVersion,
            Lines =
            [
                new SubconDispatchLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "RM1",
                    Qty = 5m,
                    WarehouseCode = "MAIN",
                    LocationCode = "BIN1",
                    Uom = "PCS"
                },
                new SubconDispatchLineSaveCommand
                {
                    LineNo = 2,
                    ItemCode = "RM1",
                    Qty = 4m,
                    WarehouseCode = "MAIN",
                    LocationCode = "SITE",
                    Uom = "PCS"
                }
            ]
        });
        Assert.True(addSecondLine.Succeeded, addSecondLine.ErrorMessage);

        var reloadedReceipt = await svc.GetReceiptAsync(receipt.Receipt!.DocNo);
        Assert.True(reloadedReceipt.Succeeded, reloadedReceipt.ErrorMessage);
        Assert.Equal(dispatch.Dispatch.DocNo, reloadedReceipt.Receipt!.Lines[0].DispatchDocNo);
        Assert.Equal(3m, reloadedReceipt.Receipt.Lines[0].Qty);
    }

    [Fact]
    public async Task Dispatch_validates_warehouse_location_uom_and_po()
    {
        var svc = _h.CreateService();

        var cmdBadWh = new SubconDispatchSaveCommand
        {
            TrxDate = DateTime.Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "RM1", Qty = 5m, WarehouseCode = "BADWH", LocationCode = "BIN1", Uom = "PCS" }
            ]
        };
        var resWh = await svc.SaveDispatchDraftAsync(cmdBadWh);
        Assert.False(resWh.Succeeded);
        Assert.Equal(SubconMessages.WarehouseInvalid, resWh.ErrorMessage);

        var cmdBadLoc = new SubconDispatchSaveCommand
        {
            TrxDate = DateTime.Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "RM1", Qty = 5m, WarehouseCode = "MAIN", LocationCode = "BADLOC", Uom = "PCS" }
            ]
        };
        var resLoc = await svc.SaveDispatchDraftAsync(cmdBadLoc);
        Assert.False(resLoc.Succeeded);
        Assert.Equal(SubconMessages.LocationInvalid, resLoc.ErrorMessage);

        var cmdBadUom = new SubconDispatchSaveCommand
        {
            TrxDate = DateTime.Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "RM1", Qty = 5m, WarehouseCode = "MAIN", LocationCode = "BIN1", Uom = "BADUOM" }
            ]
        };
        var resUom = await svc.SaveDispatchDraftAsync(cmdBadUom);
        Assert.False(resUom.Succeeded);
        Assert.Equal(SubconMessages.UomInvalid, resUom.ErrorMessage);

        var badPo = SubconTestHarness.GoodDispatch();
        var cmdBadPo = new SubconDispatchSaveCommand
        {
            TrxDate = badPo.TrxDate,
            Mode = badPo.Mode,
            SupplierCode = badPo.SupplierCode,
            PoNo = "PO-MISSING",
            Lines = badPo.Lines
        };
        var resPo = await svc.SaveDispatchDraftAsync(cmdBadPo);
        Assert.False(resPo.Succeeded);
        Assert.Equal(SubconMessages.PoInvalid, resPo.ErrorMessage);
    }

    [Fact]
    public async Task Workbench_kpis_aggregate_across_all_drafts_beyond_first_page()
    {
        var svc = _h.CreateService();
        Assert.True((await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch(qty: 5m))).Succeeded);
        Assert.True((await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch(qty: 5m))).Succeeded);
        Assert.True((await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch(qty: 5m))).Succeeded);

        var page = await svc.GetWorkbenchAsync(new SubconListQuery { Take = 1 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Single(page.Workbench!.Rows);
        Assert.Equal(3, page.Workbench.TotalCount);
        Assert.Equal(3, page.Workbench.DraftCount);
        Assert.Equal(3, page.Workbench.OpenLineCount);
        Assert.Contains("15 PCS", page.Workbench.OutstandingByUom);
    }

    [Fact]
    public async Task SearchDispatchLinesAsync_returns_remaining_qty_and_excludes_self_on_edit()
    {
        var svc = _h.CreateService();
        var dispatch = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch(qty: 5m));
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
                    Qty = 3m
                }
            ]
        });
        Assert.True(receipt.Succeeded, receipt.ErrorMessage);

        var newLookup = await svc.SearchDispatchLinesAsync(new SubconDispatchLineLookupQuery
        {
            SupplierCode = "SUP01"
        });
        Assert.True(newLookup.Succeeded, newLookup.ErrorMessage);
        var newRow = Assert.Single(newLookup.DispatchLineLookup!.Rows);
        Assert.Equal(dispatch.Dispatch.DocNo, newRow.DispatchDocNo);
        Assert.Equal(2m, newRow.RemainingQty);

        var editLookup = await svc.SearchDispatchLinesAsync(new SubconDispatchLineLookupQuery
        {
            SupplierCode = "SUP01",
            ExcludeReceiptDocNo = receipt.Receipt!.DocNo
        });
        Assert.True(editLookup.Succeeded, editLookup.ErrorMessage);
        var editRow = Assert.Single(editLookup.DispatchLineLookup!.Rows);
        Assert.Equal(5m, editRow.RemainingQty);
    }

    [Fact]
    public async Task SaveJobDraftAsync_creates_assembly_job_from_bom_and_linked_dispatch_without_moving_stock()
    {
        var before = await _h.SnapshotAsync();
        var bom = await _h.CreateBomReadService().ExplodeForSubconAsync(new SubconBomExplosionRequest
        {
            ProdCode = "FG1",
            Quantity = 4m
        });
        Assert.True(bom.Succeeded, bom.ErrorMessage);

        var svc = _h.CreateService();
        var saved = await svc.SaveJobDraftAsync(new SubconJobSaveCommand
        {
            TrxDate = DateTime.Today,
            JobKind = SubconJobKinds.Assembly,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            TargetItemCode = "FG1",
            TargetUom = bom.Snapshot!.Uom,
            TargetQty = 4m,
            DefinitionCode = bom.Snapshot.DefinitionCode,
            BomVersion = bom.Snapshot.BomVersion,
            IsManualKit = false,
            Materials = bom.Snapshot.Materials.Select(c => new SubconJobMaterialSaveCommand
            {
                LineNo = c.LineNo,
                ItemCode = c.ItemCode,
                Uom = c.Uom,
                RequiredQty = c.RequiredQty,
                SendNowQty = c.RequiredQty,
                WarehouseCode = c.WarehouseCode,
                LocationCode = c.LocationCode
            }).ToArray()
        });

        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.NotNull(saved.Job);
        Assert.StartsWith("SJ", saved.Job!.JobNo, StringComparison.Ordinal);
        Assert.Equal(SubconJobKinds.Assembly, saved.Job.JobKind);
        Assert.Equal("FG1", saved.Job.TargetItemCode);
        Assert.Equal(4m, saved.Job.TargetQty);
        Assert.Single(saved.Job.Materials);
        Assert.Equal(8m, saved.Job.Materials[0].RequiredQty);
        Assert.Single(saved.Job.Outputs);
        Assert.Equal("FG1", saved.Job.Outputs[0].ItemCode);
        Assert.False(string.IsNullOrWhiteSpace(saved.Job.PrimaryDispatchDocNo));

        var dispatch = await svc.GetDispatchAsync(saved.Job.PrimaryDispatchDocNo!);
        Assert.True(dispatch.Succeeded, dispatch.ErrorMessage);
        Assert.Equal(8m, dispatch.Dispatch!.Lines[0].Qty);

        var after = await _h.SnapshotAsync();
        AssertUnchangedStock(before, after);
    }

    [Fact]
    public async Task SaveJobDraftAsync_supports_manual_kit_snapshot_without_bom_master()
    {
        var before = await _h.SnapshotAsync();
        var svc = _h.CreateService();
        var saved = await svc.SaveJobDraftAsync(new SubconJobSaveCommand
        {
            TrxDate = DateTime.Today,
            JobKind = SubconJobKinds.Assembly,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            TargetItemCode = "FG1",
            TargetUom = "PCS",
            TargetQty = 2m,
            IsManualKit = true,
            Materials =
            [
                new SubconJobMaterialSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "RM1",
                    Uom = "PCS",
                    RequiredQty = 6m,
                    SendNowQty = 6m,
                    WarehouseCode = "MAIN",
                    LocationCode = "BIN1"
                }
            ]
        });

        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.True(saved.Job!.IsManualKit);
        var loaded = await svc.GetJobAsync(saved.Job.JobNo);
        Assert.True(loaded.Succeeded, loaded.ErrorMessage);
        Assert.True(loaded.Job!.IsManualKit);
        Assert.Equal(6m, loaded.Job.Materials[0].SendNowQty);

        var after = await _h.SnapshotAsync();
        AssertUnchangedStock(before, after);
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
