using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Subcon;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Tests.Inventory.Transaction;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Subcon;

[Trait(TestCategories.Name, TestCategories.Subcon)]
public sealed class SubconLiveCustodyPostingTests : IAsyncLifetime
{
    private static readonly DateTime Today = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private readonly InventoryTenantScope _scope;

    public SubconLiveCustodyPostingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = InventoryLedgerTestFixture.CreateSqliteFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _scope = new InventoryTenantScope
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            LocationCode = InventoryLedgerTestFixture.BranchCode,
            UserId = "tester"
        };
        _tenant.Setup(x => x.TryCompanyScope()).Returns(_scope);
        _tenant.Setup(x => x.TryBranchScope()).Returns(_scope);
        _tenant.Setup(x => x.TryWriteScope()).Returns(_scope);
        GrantAll();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedCompanyAsync(db);
        await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);

        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = _scope.CompanyCode,
            BranchCode = _scope.BranchCode!,
            SuppCode = "SUP01",
            SuppName = "Subcon Coating Sdn Bhd",
            IsActive = true
        });
        db.IvWarehouses.AddRange(
            new IvWarehouse
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                WarehouseCode = "MAIN",
                WarehouseDesc = "Main Store",
                WarehouseType = "NORMAL",
                IsActive = true
            },
            new IvWarehouse
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                WarehouseCode = "SUBCON",
                WarehouseDesc = "Subcontractor Custody",
                WarehouseType = SubconCustodyConstants.CustodyWarehouseType,
                IsActive = true
            });
        db.IvLocations.AddRange(
            new IvLocation
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                WarehouseCode = "MAIN",
                LocCode = "BIN1",
                LocDesc = "Main Bin 1",
                IsActive = true
            },
            new IvLocation
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                WarehouseCode = "SUBCON",
                LocCode = "SUP01",
                LocDesc = "SUP01 Custody Bin",
                IsActive = true
            });
        db.MsUoms.AddRange(
            new MsUom { CompanyCode = _scope.CompanyCode, UomCode = "EA", UomDesc = "Each", IsActive = true },
            new MsUom { CompanyCode = _scope.CompanyCode, UomCode = "BOX", UomDesc = "Box of 10", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = _scope.CompanyCode, IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = _scope.CompanyCode, IStatus = "ACTIVE", IsActive = true });
        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = _scope.CompanyCode,
                ICode = "A100",
                IDesc = "Steel Plate",
                IClassCode = "RAW",
                StdUom = "EA",
                StockControl = true,
                LotControl = false,
                DefWarehouse = "MAIN",
                DefLocation = "BIN1",
                IsActive = true,
                PurchasePrice = 5m
            },
            new IvStockMaster
            {
                CompanyCode = _scope.CompanyCode,
                ICode = "B200",
                IDesc = "Bolt Pack",
                IClassCode = "RAW",
                StdUom = "EA",
                StockControl = true,
                LotControl = false,
                DefWarehouse = "MAIN",
                DefLocation = "BIN1",
                IsActive = true,
                PurchasePrice = 2m
            });
        db.IvItemUomConversions.Add(new IvItemUomConversion
        {
            CompanyCode = _scope.CompanyCode,
            ItemCode = "A100",
            FromUom = "BOX",
            ToUom = "EA",
            FromQty = 1m,
            ToQty = 10m,
            RoundingScale = 4,
            RoundingMode = IvUomRoundingModes.AwayFromZero,
            IsActive = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "TEST"
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Theory]
    [InlineData("WAC")]
    [InlineData("FIFO")]
    [InlineData("STANDARD")]
    public async Task SendOut_ReceiveBack_Rollback_Repost_conserves_qty_and_value_across_cost_methods(string method)
    {
        await SeedCostMethodAndOpeningStockAsync(method);
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        var expectedTotalValue = method == "FIFO" ? 680m : 500m;

        // 1. Save & Post Send-Out 70 EA (7 BOX -> 70 EA via UOM conversion!).
        var dispatchDraft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconDispatchLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "A100",
                    Uom = "BOX",
                    Qty = 7m,
                    WarehouseCode = "MAIN",
                    LocationCode = "BIN1"
                }
            ]
        });
        Assert.True(dispatchDraft.Succeeded, dispatchDraft.ErrorMessage);

        var postedDispatch = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = dispatchDraft.Dispatch!.DocNo,
            RowVersion = dispatchDraft.Dispatch.RowVersion,
            RequestId = Guid.NewGuid()
        });
        Assert.True(postedDispatch.Succeeded, postedDispatch.ErrorMessage);
        Assert.Equal(SubconStatuses.Posted, postedDispatch.Dispatch!.Status);
        Assert.NotNull(postedDispatch.Dispatch.PostedTrxBatchNo);

        await AssertStockAndValueAsync(mainQty: 30m, subconQty: 70m, expectedValue: expectedTotalValue, method);

        // Verify reconciliation reports 0 variance.
        var recon1 = await svc.GetCustodyReconciliationAsync("SUP01");
        Assert.True(recon1.Succeeded, recon1.ErrorMessage);
        var r1 = Assert.Single(recon1.Reconciliation!);
        Assert.Equal(70m, r1.AllocatedDispatchedBaseQty);
        Assert.Equal(70m, r1.AllocatedOutstandingBaseQty);
        Assert.Equal(70m, r1.PhysicalCustodyBaseQty);
        Assert.True(r1.IsReconciled);

        // 2. Save & Post Partial Receive-Back of 4 BOX (= 40 EA).
        var receiptDraft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconReceiptLineSaveCommand
                {
                    LineNo = 1,
                    DispatchDocNo = postedDispatch.Dispatch.DocNo,
                    DispatchLineNo = 1,
                    Qty = 4m
                }
            ]
        });
        Assert.True(receiptDraft.Succeeded, receiptDraft.ErrorMessage);

        var postedReceipt = await svc.PostReceiptAsync(new SubconKeyCommand
        {
            DocNo = receiptDraft.Receipt!.DocNo,
            RowVersion = receiptDraft.Receipt.RowVersion,
            RequestId = Guid.NewGuid()
        });
        Assert.True(postedReceipt.Succeeded, postedReceipt.ErrorMessage);
        Assert.Equal(SubconStatuses.Posted, postedReceipt.Receipt!.Status);

        await AssertStockAndValueAsync(mainQty: 70m, subconQty: 30m, expectedValue: expectedTotalValue, method);

        // 3. T11: Rollback Send-Out MUST fail while Receive-Back is still posted.
        var blockedRollback = await svc.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = postedDispatch.Dispatch.DocNo,
            RowVersion = postedDispatch.Dispatch.RowVersion
        });
        Assert.False(blockedRollback.Succeeded);
        Assert.Contains(postedReceipt.Receipt.DocNo, blockedRollback.ErrorMessage!);

        // 4. Rollback Receive-Back first -> returns 40 EA back to SUP01 custody.
        var rolledReceipt = await svc.RollbackReceiptAsync(new SubconKeyCommand
        {
            DocNo = postedReceipt.Receipt.DocNo,
            RowVersion = postedReceipt.Receipt.RowVersion
        });
        Assert.True(rolledReceipt.Succeeded, rolledReceipt.ErrorMessage);
        Assert.Equal(SubconStatuses.New, rolledReceipt.Receipt!.Status);
        await AssertStockAndValueAsync(mainQty: 30m, subconQty: 70m, expectedValue: expectedTotalValue, method);

        // Delete draft receipt so dispatch has zero dependent receipts, then Rollback Send-Out.
        var deletedReceipt = await svc.DeleteReceiptDraftAsync(new SubconKeyCommand
        {
            DocNo = rolledReceipt.Receipt.DocNo,
            RowVersion = rolledReceipt.Receipt.RowVersion
        });
        Assert.True(deletedReceipt.Succeeded, deletedReceipt.ErrorMessage);

        var rolledDispatch = await svc.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = postedDispatch.Dispatch.DocNo,
            RowVersion = postedDispatch.Dispatch.RowVersion
        });
        Assert.True(rolledDispatch.Succeeded, rolledDispatch.ErrorMessage);
        Assert.Equal(SubconStatuses.New, rolledDispatch.Dispatch!.Status);
        Assert.Equal(1, rolledDispatch.Dispatch.DocumentRevision);
        await AssertStockAndValueAsync(mainQty: 100m, subconQty: 0m, expectedValue: expectedTotalValue, method);

        // 5. Repost Send-Out (new active generation, new TR batch).
        var reposted = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = rolledDispatch.Dispatch.DocNo,
            RowVersion = rolledDispatch.Dispatch.RowVersion,
            RequestId = Guid.NewGuid()
        });
        Assert.True(reposted.Succeeded, reposted.ErrorMessage);
        Assert.Equal(SubconStatuses.Posted, reposted.Dispatch!.Status);
        Assert.NotEqual(postedDispatch.Dispatch.PostedTrxBatchNo, reposted.Dispatch.PostedTrxBatchNo);
        await AssertStockAndValueAsync(mainQty: 30m, subconQty: 70m, expectedValue: expectedTotalValue, method);
    }

    [Fact]
    public async Task Custody_stock_is_excluded_from_MI_TR_StockCount_and_OnHandLookup_and_Subcon_TR_cannot_be_tampered_directly()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        var (svc, posting, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        var draft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconDispatchLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "A100",
                    Uom = "EA",
                    Qty = 60m,
                    WarehouseCode = "MAIN",
                    LocationCode = "BIN1"
                }
            ]
        });
        var posted = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = draft.Dispatch!.DocNo,
            RowVersion = draft.Dispatch.RowVersion
        });
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        var subconBatchNo = posted.Dispatch!.PostedTrxBatchNo!.Value;
        var custodyBalLocId = posted.Dispatch.Lines[0].CustodyBalLocId!.Value;

        // 1. On-hand lookup excludes SUBCON warehouse.
        var commonRepo = new IvStockCommonRepository(_factory);
        var (onHandRows, totalOnHand) = await commonRepo.SearchOnHandPagedAsync(
            _scope.CompanyCode, _scope.BranchCode!, "A100", null, 0, 50);
        Assert.Equal(1, totalOnHand);
        Assert.Equal("MAIN", Assert.Single(onHandRows).WhCode);

        // 2. Stock Count candidate readers (legacy & V2) exclude SUBCON warehouse.
        var (countRows, _) = await commonRepo.ListStockCountCandidatesAsync(
            _scope.CompanyCode, _scope.BranchCode!, new IvStockCountScope(), 0, 50);
        Assert.DoesNotContain(countRows, r => r.WhCode == "SUBCON");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var (v2CountRows, _) = await StockReplayCountCandidateReader.ReadAsync(
                db, _scope.CompanyCode, _scope.BranchCode!, new IvStockCountScope(), Today, 0, 50);
            Assert.DoesNotContain(v2CountRows, r => r.Balance.WhCode == "SUBCON");
        }

        // 3. Direct MI against supplier custody balance MUST be rejected under lock!
        var miService = InventoryLedgerTestFixture.CreateMiscIssue(_factory, Today);
        var savedMi = await miService.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = Today,
            RefNo = "ILLEGAL-MI",
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    ICode = "A100",
                    FromBalLocId = custodyBalLocId,
                    FrWarehouse = "SUBCON",
                    FrLocation = "SUP01",
                    IStatus = "ACTIVE",
                    Quantity = 5m,
                    Uom = "EA"
                }
            ]
        });
        if (savedMi.Succeeded)
        {
            var postMi = await miService.PostAsync([savedMi.BatchNo]);
            Assert.False(postMi.Posting!.Succeeded);
            Assert.Contains("subcontractor custody", postMi.Posting.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }

        // 4. Direct generic TR tampering with Subcon-owned TR batch MUST be rejected!
        var directRollback = await posting.RollbackAsync(IvTrxTypes.StockTransfer, [subconBatchNo]);
        Assert.False(directRollback.Succeeded);
        Assert.Contains("owned by a Subcontracting document", directRollback.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // 5. Manual generic TR into SUBCON warehouse MUST be rejected!
        var trService = new IvStockTransferService(
            _factory, _tenant.Object, _access.Object, new RunningNumberService(),
            new FixedCurrentDateService(Today),
            new IvStockMasterRepository(_factory), commonRepo,
            new IvStockTransactionRepository(), new IvStockPostingRepository(),
            posting, NullLogger<IvStockTransferService>.Instance);
        int mainBalId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            mainBalId = await db.IvBalLocs.Where(b => b.WhCode == "MAIN").Select(b => b.Id).SingleAsync();
        }
        var manualTr = await trService.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = Today,
            Lines =
            [
                new IvStockTransferLineRequest
                {
                    LineNo = 1,
                    ICode = "A100",
                    FromBalLocId = mainBalId,
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    ToWarehouse = "SUBCON",
                    ToLocation = "SUP01",
                    IStatus = "ACTIVE",
                    Quantity = 10m,
                    Uom = "EA"
                }
            ]
        });
        Assert.False(manualTr.Succeeded);
        Assert.Contains("subcontractor custody", manualTr.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_dispatches_sharing_supplier_custody_bin_cannot_cross_consume_allocations()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        var d1 = await PostSimpleDispatchAsync(svc, qty: 50m);
        var d2 = await PostSimpleDispatchAsync(svc, qty: 30m);

        // Physical SUBCON/SUP01 holds 80 EA, but d2 only owns 30 EA -> receiving 35 against d2 MUST fail!
        var overReceipt = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = d2.DocNo, DispatchLineNo = 1, Qty = 35m }]
        });
        Assert.False(overReceipt.Succeeded);

        // Partial receives 20 + 10 against d2 succeed; d1 still has 50 untouched.
        var r1 = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = d2.DocNo, DispatchLineNo = 1, Qty = 20m }]
        });
        Assert.True((await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = r1.Receipt!.DocNo, RowVersion = r1.Receipt.RowVersion })).Succeeded);

        var r2 = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = d2.DocNo, DispatchLineNo = 1, Qty = 10m }]
        });
        Assert.True((await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = r2.Receipt!.DocNo, RowVersion = r2.Receipt.RowVersion })).Succeeded);

        var recon = await svc.GetCustodyReconciliationAsync("SUP01");
        var row = Assert.Single(recon.Reconciliation!);
        Assert.Equal(80m, row.AllocatedDispatchedBaseQty);
        Assert.Equal(30m, row.AllocatedReturnedBaseQty);
        Assert.Equal(50m, row.AllocatedOutstandingBaseQty);
        Assert.Equal(50m, row.PhysicalCustodyBaseQty);
        Assert.True(row.IsReconciled);
        Assert.Equal(SubconStatuses.Posted, d1.Status);
    }

    [Fact]
    public async Task Multi_line_shortage_and_crash_after_link_write_roll_back_atomically_with_zero_mutation()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                ICode = "B200",
                WhCode = "MAIN",
                LocCode = "BIN1",
                LotNo = string.Empty,
                IStatus = "ACTIVE",
                StdQty = 5m,
                StdUom = "EA",
                TransDate = Today
            });
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "B200", 5m, 2m, StockCostMethods.MovingAverage);
            await db.SaveChangesAsync();
        }

        var (svc, _, adapter) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        // T02: Line 1 valid (A100 = 10), Line 2 short (B200 = 20 > 5) -> zero movement.
        var multiDraft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 10m, WarehouseCode = "MAIN", LocationCode = "BIN1" },
                new SubconDispatchLineSaveCommand { LineNo = 2, ItemCode = "B200", Uom = "EA", Qty = 20m, WarehouseCode = "MAIN", LocationCode = "BIN1" }
            ]
        });
        Assert.True(multiDraft.Succeeded, multiDraft.ErrorMessage);
        var failedMulti = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = multiDraft.Dispatch!.DocNo,
            RowVersion = multiDraft.Dispatch.RowVersion
        });
        Assert.False(failedMulti.Succeeded);

        // T22: Crash injection after SubconPostingLink + SubconCustodyAllocation staged -> full rollback.
        var singleDraft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 10m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        adapter.TestHookAfterSubconLinkWrite = () => throw new InvalidOperationException("Simulated SQL crash");
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = singleDraft.Dispatch!.DocNo,
            RowVersion = singleDraft.Dispatch.RowVersion
        }));
        adapter.TestHookAfterSubconLinkWrite = null;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(100m, await db.IvBalLocs.Where(b => b.ICode == "A100" && b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync());
            Assert.Equal(0, await db.IvBalLocs.CountAsync(b => b.WhCode == "SUBCON"));
            Assert.Equal(0, await db.IvTrxBatches.CountAsync());
            Assert.Equal(0, await db.IvTrxHistories.CountAsync());
            Assert.Equal(0, await db.SubconPostingLinks.CountAsync());
            Assert.Equal(0, await db.SubconCustodyAllocations.CountAsync());
            Assert.Equal(SubconStatuses.New, await db.SubconDispatches.Where(d => d.DocNo == singleDraft.Dispatch!.DocNo).Select(d => d.Status).SingleAsync());
        }
    }

    [Fact]
    public async Task Backdated_send_out_before_later_issue_is_blocked_atomically()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        // Post an MI on Today first.
        var miService = InventoryLedgerTestFixture.CreateMiscIssue(_factory, Today);
        int mainBalId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            mainBalId = await db.IvBalLocs.Where(b => b.ICode == "A100" && b.WhCode == "MAIN").Select(b => b.Id).SingleAsync();
        }

        var miSaved = await miService.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = Today,
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    ICode = "A100", FromBalLocId = mainBalId,
                    FrWarehouse = "MAIN", FrLocation = "BIN1", IStatus = "ACTIVE", Quantity = 10m, Uom = "EA"
                }
            ]
        });
        Assert.True(miSaved.Succeeded, miSaved.ErrorMessage);
        var miPosted = await miService.PostAsync([miSaved.BatchNo]);
        Assert.True(miPosted.Posting!.Succeeded, miPosted.Posting.ErrorMessage);

        // Now try to post a backdated Subcon Send-Out dated Today - 5 (RequiresReplay == true) -> MUST BLOCK!
        var backdatedDraft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today.AddDays(-5),
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 20m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(backdatedDraft.Succeeded, backdatedDraft.ErrorMessage);

        var blocked = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = backdatedDraft.Dispatch!.DocNo,
            RowVersion = backdatedDraft.Dispatch.RowVersion
        });
        Assert.False(blocked.Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(90m, await db.IvBalLocs.Where(b => b.ICode == "A100" && b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync());
            Assert.Equal(0, await db.SubconPostingLinks.CountAsync());
            Assert.Equal(SubconStatuses.New, await db.SubconDispatches.Where(d => d.DocNo == backdatedDraft.Dispatch.DocNo).Select(d => d.Status).SingleAsync());
        }
    }

    [Fact]
    public async Task Dual_permission_policy_gates_and_idempotent_RequestId_are_enforced()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        var (svc, _, _) = CreateServices();

        // Frozen capabilities stay blocked.
        Assert.False((await svc.SavePolicyAsync(new SubconPolicySaveCommand { LotReturnEnabled = true })).Succeeded);
        Assert.False((await svc.SavePolicyAsync(new SubconPolicySaveCommand { BomConversionEnabled = true })).Succeeded);
        Assert.False((await svc.SavePolicyAsync(new SubconPolicySaveCommand { ProductionWipEnabled = true })).Succeeded);
        Assert.False((await svc.SavePolicyAsync(new SubconPolicySaveCommand { FeeCapitalizationEnabled = true })).Succeeded);
        // Non-SUBCON warehouse rejected as custody warehouse.
        Assert.False((await svc.SavePolicyAsync(new SubconPolicySaveCommand
        {
            SubconEnabled = true,
            InventoryCustodyPostEnabled = true,
            DefaultCustodyWarehouseCode = "MAIN"
        })).Succeeded);

        await EnableLiveCustodyPolicyAsync(svc);

        var d1 = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today, Mode = SubconModes.Inventory, SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 10m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        var d2 = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today, Mode = SubconModes.Inventory, SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 10m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });

        // Deny INV_STOCK_TRANSFER.Post -> dual permission blocks Post!
        _access.Reset();
        _access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menu, string perm, CancellationToken _) =>
                !(menu == MenuCodes.InventoryStockTransfer && perm == PermissionCodes.Post));
        var denied = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = d1.Dispatch!.DocNo, RowVersion = d1.Dispatch.RowVersion });
        Assert.False(denied.Succeeded);
        Assert.Equal(SubconMessages.NotAuthorized, denied.ErrorMessage);

        // Restore rights & test idempotent RequestId replay vs conflict.
        GrantAll();
        var reqId = Guid.NewGuid();
        var first = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = d1.Dispatch.DocNo, RowVersion = d1.Dispatch.RowVersion, RequestId = reqId });
        Assert.True(first.Succeeded, first.ErrorMessage);

        var retrySame = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = d1.Dispatch.DocNo, RowVersion = d1.Dispatch.RowVersion, RequestId = reqId });
        Assert.True(retrySame.Succeeded, retrySame.ErrorMessage);
        Assert.Equal(first.Dispatch!.PostedTrxBatchNo, retrySame.Dispatch!.PostedTrxBatchNo);

        var conflictDifferent = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = d2.Dispatch!.DocNo, RowVersion = d2.Dispatch.RowVersion, RequestId = reqId });
        Assert.False(conflictDifferent.Succeeded);
        Assert.Equal(SubconMessages.RequestIdConflict, conflictDifferent.ErrorMessage);
    }

    private (SubconApplicationService Service, IvInventoryPostingService Posting, SubconInventoryTransferAdapter Adapter) CreateServices()
    {
        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory, _scope.CompanyCode, _scope.BranchCode!, _access.Object);
        var numbers = new RunningNumberService();
        var uom = new IvUomConversionService(_factory);
        var softRes = new InventorySoftReservationReader();
        var adapter = new SubconInventoryTransferAdapter(_factory, posting, numbers, uom, softRes);
        var svc = new SubconApplicationService(
            _factory, _tenant.Object, _access.Object, numbers, adapter, new SubconBlockedProductionSourceAdapter());
        return (svc, posting, adapter);
    }

    private async Task EnableLiveCustodyPolicyAsync(SubconApplicationService svc)
    {
        var policy = await svc.SavePolicyAsync(new SubconPolicySaveCommand
        {
            SubconEnabled = true,
            InventoryModeEnabled = true,
            InventoryCustodyPostEnabled = true,
            DefaultCustodyWarehouseCode = "SUBCON"
        });
        Assert.True(policy.Succeeded, policy.ErrorMessage);
    }

    private async Task<SubconDispatchDto> PostSimpleDispatchAsync(SubconApplicationService svc, decimal qty)
    {
        var saved = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = qty, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = saved.Dispatch!.DocNo, RowVersion = saved.Dispatch.RowVersion });
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        return posted.Dispatch!;
    }

    private async Task SeedCostMethodAndOpeningStockAsync(string method, DateTime? openingDate = null)
    {
        var dt = openingDate ?? Today.AddDays(-1);
        await using var db = await _factory.CreateDbContextAsync();
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = _scope.CompanyCode,
            BranchCode = _scope.BranchCode!,
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = 100m,
            StdUom = "EA",
            TransDate = dt,
            Cost = 5m,
            UnitPrice = 5m
        });
        await db.SaveChangesAsync();

        if (method == "WAC")
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 5m, StockCostMethods.MovingAverage);
            await InventoryLedgerTestFixture.SeedOpeningValuationFactAsync(db, "A100", 100m, 5m, dt, StockCostMethods.MovingAverage);
        }
        else if (method == "FIFO")
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(40m, 5m, dt.AddDays(-2)),
                new FifoLayerSeed(60m, 8m, dt.AddDays(-1))
            ]);
        }
        else
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 5m, StockCostMethods.Standard);
            await InventoryLedgerTestFixture.SeedOpeningValuationFactAsync(db, "A100", 100m, 5m, dt, StockCostMethods.Standard);
        }
    }

    private async Task AssertStockAndValueAsync(decimal mainQty, decimal subconQty, decimal expectedValue, string method)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var main = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.ICode == "A100" && b.WhCode == "MAIN" && b.LocCode == "BIN1")
            .Select(b => b.StdQty).SingleAsync();
        var subcon = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.ICode == "A100" && b.WhCode == "SUBCON" && b.LocCode == "SUP01")
            .Select(b => (decimal?)b.StdQty).SingleOrDefaultAsync() ?? 0m;
        Assert.Equal(mainQty, main);
        Assert.Equal(subconQty, subcon);
        Assert.Equal(100m, main + subcon);

        var costState = await db.StockCostStates.AsNoTracking()
            .SingleAsync(s => s.CompanyCode == _scope.CompanyCode && s.BranchCode == _scope.BranchCode && s.ItemCode == "A100");
        Assert.Equal(100m, costState.OnHandBaseQty);
        Assert.Equal(expectedValue, costState.InventoryValue);

        if (method == "FIFO")
        {
            var layerRemaining = await db.StockFifoLayers.AsNoTracking()
                .Where(l => l.CompanyCode == _scope.CompanyCode && l.BranchCode == _scope.BranchCode && l.ItemCode == "A100")
                .SumAsync(l => l.RemainingQty);
            Assert.Equal(100m, layerRemaining);
        }
    }

    private void GrantAll()
    {
        _access.Reset();
        _access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }
}
