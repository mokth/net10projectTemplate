using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Subcon;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Quality;
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
                IsActive = true,
                RowVersion = [1]
            },
            new IvWarehouse
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                WarehouseCode = "SUBCON",
                WarehouseDesc = "Subcontractor Custody",
                WarehouseType = SubconCustodyConstants.CustodyWarehouseType,
                IsActive = true,
                RowVersion = [1]
            });
        db.IvLocations.AddRange(
            new IvLocation
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                WarehouseCode = "MAIN",
                LocCode = "BIN1",
                LocDesc = "Main Bin 1",
                IsActive = true,
                RowVersion = [1]
            },
            new IvLocation
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                WarehouseCode = "SUBCON",
                LocCode = "SUP01",
                LocDesc = "SUP01 Custody Bin",
                IsActive = true,
                RowVersion = [1]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backdated_wac_send_out_before_later_issue_posts_rolls_back_and_reposts(bool existingDestination)
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        if (existingDestination)
        {
            // Create a real, empty custody pile before the later issue.
            var previous = await SaveAndPostDispatchAtAsync(svc, Today.AddDays(-8), 1m);
            var undone = await svc.RollbackDispatchAsync(new SubconKeyCommand
            {
                DocNo = previous.DocNo, RowVersion = previous.RowVersion
            });
            Assert.True(undone.Succeeded, undone.ErrorMessage);
        }
        var issueBatch = await PostLaterIssueForSubconReplayAsync();
        var draft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today.AddDays(-5), Mode = SubconModes.Inventory, SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA",
                Qty = 20m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var current = draft.Dispatch!;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var posted = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = current.DocNo, RowVersion = current.RowVersion });
            Assert.True(posted.Succeeded, posted.ErrorMessage);
            await AssertSubconReplayStateAsync(70m, 20m, issueBatch, 20m);
            var rolled = await svc.RollbackDispatchAsync(new SubconKeyCommand { DocNo = posted.Dispatch!.DocNo, RowVersion = posted.Dispatch.RowVersion });
            Assert.True(rolled.Succeeded, rolled.ErrorMessage);
            await AssertSubconReplayStateAsync(90m, 0m, issueBatch, 0m);
            current = rolled.Dispatch!;
        }
    }

    [Fact]
    public async Task Backdated_wac_receive_back_before_later_issue_posts_rolls_back_and_reposts()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        var dispatch = await SaveAndPostDispatchAtAsync(svc, Today.AddDays(-8), 20m);
        var issueBatch = await PostLaterIssueForSubconReplayAsync();
        var draft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today.AddDays(-5), SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = dispatch.DocNo,
                DispatchLineNo = 1, Qty = 10m }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var current = draft.Receipt!;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var posted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = current.DocNo, RowVersion = current.RowVersion });
            Assert.True(posted.Succeeded, posted.ErrorMessage);
            await AssertSubconReplayStateAsync(80m, 10m, issueBatch, 10m);
            var rolled = await svc.RollbackReceiptAsync(new SubconKeyCommand { DocNo = posted.Receipt!.DocNo, RowVersion = posted.Receipt.RowVersion });
            Assert.True(rolled.Succeeded, rolled.ErrorMessage);
            await AssertSubconReplayStateAsync(70m, 20m, issueBatch, 20m);
            current = rolled.Receipt!;
        }
    }

    [Fact]
    public async Task Backdated_wac_receive_back_to_new_destination_posts_rolls_back_and_reposts()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLocations.Add(new IvLocation { CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!,
                WarehouseCode = "MAIN", LocCode = "BIN2", LocDesc = "Main Bin 2", IsActive = true, RowVersion = [1] });
            await db.SaveChangesAsync();
        }
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        var dispatch = await SaveAndPostDispatchAtAsync(svc, Today.AddDays(-8), 20m);
        var issueBatch = await PostLaterIssueForSubconReplayAsync();
        var draft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today.AddDays(-5), SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = dispatch.DocNo,
                DispatchLineNo = 1, Qty = 10m, ToWarehouseCode = "MAIN", ToLocationCode = "BIN2" }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var current = draft.Receipt!;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var posted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = current.DocNo, RowVersion = current.RowVersion });
            Assert.True(posted.Succeeded, posted.ErrorMessage);
            await AssertStateAsync(10m, 10m);
            var rolled = await svc.RollbackReceiptAsync(new SubconKeyCommand { DocNo = posted.Receipt!.DocNo, RowVersion = posted.Receipt.RowVersion });
            Assert.True(rolled.Succeeded, rolled.ErrorMessage);
            await AssertStateAsync(0m, 20m);
            current = rolled.Receipt!;
        }

        async Task AssertStateAsync(decimal newLocationQty, decimal custodyQty)
        {
            await using var db = await _factory.CreateDbContextAsync();
            Assert.Equal(70m, (await db.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN" && b.LocCode == "BIN1")).StdQty);
            Assert.Equal(newLocationQty, (await db.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN" && b.LocCode == "BIN2")).StdQty);
            Assert.Equal(custodyQty, (await db.IvBalLocs.SingleAsync(b => b.WhCode == "SUBCON")).StdQty);
            var state = await db.StockCostStates.SingleAsync();
            Assert.Equal((90m, 450m), (state.OnHandBaseQty, state.InventoryValue));
            var alloc = await db.SubconCustodyAllocations.SingleAsync(a => a.IsActive);
            Assert.Equal(custodyQty, alloc.DispatchedBaseQty - alloc.ReturnedBaseQty);
            var issue = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == issueBatch && h.EntryRole == "ORIGINAL");
            var facts = await StockReplayValuationGenerationResolver.ResolveAsync(db, _scope.CompanyCode, _scope.BranchCode!, [issue]);
            Assert.Equal(50m, facts[issue.Id].Sum(f => f.CostAmount));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backdated_subcon_replay_refuses_closed_period_or_cross_month_tail_atomically(bool crossMonth)
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        var later = crossMonth ? new DateTime(Today.Year, Today.Month, 1).AddMonths(1) : Today;
        await PostLaterIssueForSubconReplayAsync(later);
        var draft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today.AddDays(-5), Mode = SubconModes.Inventory, SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA",
                Qty = 20m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        if (!crossMonth)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var first = new DateTime(Today.Year, Today.Month, 1);
            db.IvPeriodCloseHdrs.Add(new IvPeriodCloseHdr
            {
                CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!,
                PeriodFrom = first, PeriodTo = first.AddMonths(1).AddDays(-1), Status = IvPeriodCloseStatuses.Closed
            });
            await db.SaveChangesAsync();
        }
        async Task<(int Roots, int Facts, int Links, int Allocations, decimal Main, decimal Value)> SnapshotAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            return (await db.StockPostings.CountAsync(), await db.StockValuationFacts.CountAsync(),
                await db.SubconPostingLinks.CountAsync(), await db.SubconCustodyAllocations.CountAsync(),
                await db.IvBalLocs.Where(b => b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync(),
                (await db.StockCostStates.SingleAsync()).InventoryValue);
        }
        var before = await SnapshotAsync();
        var blocked = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = draft.Dispatch!.DocNo, RowVersion = draft.Dispatch.RowVersion
        });
        Assert.False(blocked.Succeeded);
        Assert.Equal(before, await SnapshotAsync());
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await check.IvBalLocs.CountAsync(b => b.WhCode == "SUBCON"));
        Assert.Equal(SubconStatuses.New, (await check.SubconDispatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task Backdated_subcon_receive_back_rollback_respects_quality_holds()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true, qaPhysical: new QaPhysicalHoldAuthorizationService());
        await EnableLiveCustodyPolicyAsync(svc);
        var dispatch = await SaveAndPostDispatchAtAsync(svc, Today.AddDays(-8), 20m);
        var issueBatch = await PostLaterIssueForSubconReplayAsync();
        var draft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today.AddDays(-5), SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = dispatch.DocNo,
                DispatchLineNo = 1, Qty = 10m }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var posted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = draft.Receipt!.DocNo, RowVersion = draft.Receipt.RowVersion });
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        int roots, facts, links;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var balance = await db.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN");
            db.QaPolicies.Add(new QaPolicy { CompanyCode = _scope.CompanyCode, Version = 1, Enabled = true,
                IsPublished = true, PhysicalHoldEnabled = true, CreatedDate = DateTime.UtcNow, CreatedBy = "tester" });
            var line = new QaCaseLine { LineNo = 1, ItemCode = balance.ICode, Uom = "EA", Qty = 75m };
            var qaCase = new QaCase { CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!,
                CaseNo = "QA-SUBCON-REPLAY", CaseDate = Today, Status = QaCaseStatuses.Approved,
                SourceType = QaSourceTypes.InventoryStock, CreatedDate = DateTime.UtcNow, CreatedBy = "tester", Lines = [line] };
            qaCase.PhysicalHolds.Add(new QaPhysicalHold { CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!,
                CaseLine = line, BalLocId = balance.Id, ItemCode = balance.ICode, WarehouseCode = balance.WhCode,
                LocationCode = balance.LocCode, LotNo = balance.LotNo ?? string.Empty, ItemStatus = balance.IStatus,
                ReservedBaseQty = 75m, Status = QaLogicalHoldStatuses.Active, Cause = QaLogicalHoldCauses.KeepHold,
                CreatedAtUtc = DateTime.UtcNow });
            db.QaCases.Add(qaCase);
            await db.SaveChangesAsync();
            roots = await db.StockPostings.CountAsync();
            facts = await db.StockValuationFacts.CountAsync();
            links = await db.SubconPostingLinks.CountAsync();
        }
        var blocked = await svc.RollbackReceiptAsync(new SubconKeyCommand { DocNo = posted.Receipt!.DocNo, RowVersion = posted.Receipt.RowVersion });
        Assert.False(blocked.Succeeded);
        Assert.Contains("quality hold", blocked.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        await AssertSubconReplayStateAsync(80m, 10m, issueBatch, 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(roots, await db.StockPostings.CountAsync());
            Assert.Equal(facts, await db.StockValuationFacts.CountAsync());
            Assert.Equal(links, await db.SubconPostingLinks.CountAsync());
            (await db.QaPhysicalHolds.SingleAsync()).ReservedBaseQty = 70m;
            await db.SaveChangesAsync();
        }
        var rolled = await svc.RollbackReceiptAsync(new SubconKeyCommand { DocNo = posted.Receipt.DocNo, RowVersion = posted.Receipt.RowVersion });
        Assert.True(rolled.Succeeded, rolled.ErrorMessage);
        await AssertSubconReplayStateAsync(70m, 20m, issueBatch, 20m);
    }

    [Fact]
    public async Task Backdated_subcon_send_out_rejects_historical_shortage_even_when_current_stock_is_sufficient()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        await PostLaterIssueForSubconReplayAsync(qty: 90m);
        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, Today);
        var received = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = Today,
            Lines = [new IvMiscReceiptLineRequest { ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                Quantity = 100m, UnitPrice = 5m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW",
                IStatus = "ACTIVE", Reason = "ADJ" }]
        });
        Assert.True(received.Succeeded, received.ErrorMessage);
        var postedReceipt = await receipt.PostAsync([received.BatchNo]);
        Assert.True(postedReceipt.Succeeded, postedReceipt.ErrorMessage);
        var draft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today.AddDays(-5), Mode = SubconModes.Inventory, SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA",
                Qty = 105m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        int roots, facts;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            roots = await db.StockPostings.CountAsync();
            facts = await db.StockValuationFacts.CountAsync();
        }
        var blocked = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = draft.Dispatch!.DocNo, RowVersion = draft.Dispatch.RowVersion });
        Assert.False(blocked.Succeeded);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(roots, await check.StockPostings.CountAsync());
        Assert.Equal(facts, await check.StockValuationFacts.CountAsync());
        Assert.Equal(0, await check.SubconPostingLinks.CountAsync());
        Assert.Equal(0, await check.SubconCustodyAllocations.CountAsync());
        Assert.Equal(0, await check.IvBalLocs.CountAsync(b => b.WhCode == "SUBCON"));
        Assert.Equal(110m, (await check.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN")).StdQty);
        Assert.Equal((110m, 550m), ((await check.StockCostStates.SingleAsync()).OnHandBaseQty,
            (await check.StockCostStates.SingleAsync()).InventoryValue));
    }

    [Fact]
    public async Task Backdated_subcon_send_out_uses_historical_wac_when_later_receipt_changes_the_average()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, Today);
        var later = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = Today,
            Lines = [new IvMiscReceiptLineRequest { ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                Quantity = 100m, UnitPrice = 10m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW",
                IStatus = "ACTIVE", Reason = "ADJ" }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        var receiptPost = await receipt.PostAsync([later.BatchNo]);
        Assert.True(receiptPost.Succeeded, receiptPost.ErrorMessage);
        var issueBatch = await PostLaterIssueForSubconReplayAsync();
        var draft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today.AddDays(-5), Mode = SubconModes.Inventory, SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA",
                Qty = 20m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var posted = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = draft.Dispatch!.DocNo, RowVersion = draft.Dispatch.RowVersion });
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(170m, (await db.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN")).StdQty);
            Assert.Equal(20m, (await db.IvBalLocs.SingleAsync(b => b.WhCode == "SUBCON")).StdQty);
            var state = await db.StockCostStates.SingleAsync();
            Assert.Equal((190m, 1425m), (state.OnHandBaseQty, state.InventoryValue));
            var transfer = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == posted.Dispatch!.PostedTrxBatchNo && h.EntryRole == "ORIGINAL");
            var issue = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == issueBatch && h.EntryRole == "ORIGINAL");
            var facts = await StockReplayValuationGenerationResolver.ResolveAsync(db, _scope.CompanyCode, _scope.BranchCode!, [transfer, issue]);
            Assert.Equal(2, facts[transfer.Id].Count);
            Assert.All(facts[transfer.Id], fact => Assert.Equal(100m, fact.CostAmount));
            Assert.Equal(75m, facts[issue.Id].Sum(f => f.CostAmount));
        }
        var rolled = await svc.RollbackDispatchAsync(new SubconKeyCommand { DocNo = posted.Dispatch!.DocNo, RowVersion = posted.Dispatch.RowVersion });
        Assert.True(rolled.Succeeded, rolled.ErrorMessage);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(190m, (await check.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN")).StdQty);
        Assert.Equal(0m, (await check.IvBalLocs.SingleAsync(b => b.WhCode == "SUBCON")).StdQty);
        Assert.Equal(1425m, (await check.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Backdated_subcon_rollback_refuses_a_closed_original_period_without_mutation()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        var issueBatch = await PostLaterIssueForSubconReplayAsync();
        var posted = await SaveAndPostDispatchAtAsync(svc, Today.AddDays(-5), 20m);
        int roots, facts, links;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var first = new DateTime(Today.Year, Today.Month, 1);
            db.IvPeriodCloseHdrs.Add(new IvPeriodCloseHdr { CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!,
                PeriodFrom = first, PeriodTo = first.AddMonths(1).AddDays(-1), Status = IvPeriodCloseStatuses.Closed });
            await db.SaveChangesAsync();
            roots = await db.StockPostings.CountAsync();
            facts = await db.StockValuationFacts.CountAsync();
            links = await db.SubconPostingLinks.CountAsync();
        }
        var blocked = await svc.RollbackDispatchAsync(new SubconKeyCommand { DocNo = posted.DocNo, RowVersion = posted.RowVersion });
        Assert.False(blocked.Succeeded);
        await AssertSubconReplayStateAsync(70m, 20m, issueBatch, 20m);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(roots, await check.StockPostings.CountAsync());
        Assert.Equal(facts, await check.StockValuationFacts.CountAsync());
        Assert.Equal(links, await check.SubconPostingLinks.CountAsync());
        Assert.Equal(SubconStatuses.Posted, (await check.SubconDispatches.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backdated_subcon_rollback_rejects_changed_link_or_custody_allocation(bool changeLink)
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        var issueBatch = await PostLaterIssueForSubconReplayAsync();
        var posted = await SaveAndPostDispatchAtAsync(svc, Today.AddDays(-5), 20m);
        int roots, facts, links;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.SubconPostingLinks.SingleAsync(l => l.IsActive);
            if (changeLink)
                link.StockPostingId = (await db.IvTrxHistories.SingleAsync(h => h.BatchNo == issueBatch && h.EntryRole == "ORIGINAL")).StockPostingId;
            else
                (await db.SubconCustodyAllocations.SingleAsync(a => a.IsActive)).DispatchedBaseQty = 19m;
            await db.SaveChangesAsync();
            roots = await db.StockPostings.CountAsync();
            facts = await db.StockValuationFacts.CountAsync();
            links = await db.SubconPostingLinks.CountAsync();
        }
        var blocked = await svc.RollbackDispatchAsync(new SubconKeyCommand { DocNo = posted.DocNo, RowVersion = posted.RowVersion });
        Assert.False(blocked.Succeeded);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(roots, await check.StockPostings.CountAsync());
        Assert.Equal(facts, await check.StockValuationFacts.CountAsync());
        Assert.Equal(links, await check.SubconPostingLinks.CountAsync());
        Assert.Equal(70m, (await check.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN")).StdQty);
        Assert.Equal(20m, (await check.IvBalLocs.SingleAsync(b => b.WhCode == "SUBCON")).StdQty);
        Assert.Equal(450m, (await check.StockCostStates.SingleAsync()).InventoryValue);
        Assert.True((await check.SubconCustodyAllocations.SingleAsync()).IsActive);
        Assert.Equal(SubconStatuses.Posted, (await check.SubconDispatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task Backdated_subcon_receive_back_rollback_rejects_changed_custody_allocation()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-10));
        var (svc, _, _) = CreateServices(enableBackdatedReplay: true);
        await EnableLiveCustodyPolicyAsync(svc);
        var dispatch = await SaveAndPostDispatchAtAsync(svc, Today.AddDays(-8), 20m);
        await PostLaterIssueForSubconReplayAsync();
        var draft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today.AddDays(-5), SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = dispatch.DocNo,
                DispatchLineNo = 1, Qty = 10m }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var posted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = draft.Receipt!.DocNo, RowVersion = draft.Receipt.RowVersion });
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        int roots, facts, links;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var mainId = (await db.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN")).Id;
            (await db.SubconCustodyAllocations.SingleAsync(a => a.IsActive)).CustodyBalLocId = mainId;
            await db.SaveChangesAsync();
            roots = await db.StockPostings.CountAsync();
            facts = await db.StockValuationFacts.CountAsync();
            links = await db.SubconPostingLinks.CountAsync();
        }
        var blocked = await svc.RollbackReceiptAsync(new SubconKeyCommand { DocNo = posted.Receipt!.DocNo, RowVersion = posted.Receipt.RowVersion });
        Assert.False(blocked.Succeeded);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(roots, await check.StockPostings.CountAsync());
        Assert.Equal(facts, await check.StockValuationFacts.CountAsync());
        Assert.Equal(links, await check.SubconPostingLinks.CountAsync());
        Assert.Equal(80m, (await check.IvBalLocs.SingleAsync(b => b.WhCode == "MAIN")).StdQty);
        Assert.Equal(10m, (await check.IvBalLocs.SingleAsync(b => b.WhCode == "SUBCON")).StdQty);
        Assert.Equal(450m, (await check.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(10m, (await check.SubconCustodyAllocations.SingleAsync()).ReturnedBaseQty);
        Assert.Equal(SubconStatuses.Posted, (await check.SubconReceipts.SingleAsync()).Status);
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

    [Fact]
    public async Task H01_Warehouse_and_location_master_mutations_are_blocked_when_custody_stock_or_references_exist()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);
        var refService = CreateInventoryRefService();

        var d1 = await PostSimpleDispatchAsync(svc, qty: 30m);

        // Fetch current RowVersions for SUBCON, MAIN, and SUBCON/SUP01.
        var subconWh = await refService.GetWarehouseAsync("SUBCON");
        Assert.True(subconWh.Succeeded, subconWh.Message);
        var mainWh = await refService.GetWarehouseAsync("MAIN");
        Assert.True(mainWh.Succeeded, mainWh.Message);
        var subconLoc = await refService.GetLocationAsync("SUBCON", "SUP01");
        Assert.True(subconLoc.Succeeded, subconLoc.Message);

        // 1. Demoting SUBCON -> NORMAL while stock/allocations/policy exist MUST fail.
        var demote = await refService.SaveWarehouseAsync(new IvWarehouseEditVm
        {
            Code = "SUBCON",
            Desc = subconWh.Data!.Desc,
            WarehouseType = "NORMAL",
            IsActive = true,
            RowVersion = subconWh.Data.RowVersion
        }, isNew: false);
        Assert.False(demote.Succeeded);
        Assert.Contains("subcontractor custody", demote.Message!, StringComparison.OrdinalIgnoreCase);

        // 2. Deactivating SUBCON via SaveWarehouseAsync MUST fail.
        var deactivateViaSave = await refService.SaveWarehouseAsync(new IvWarehouseEditVm
        {
            Code = "SUBCON",
            Desc = subconWh.Data.Desc,
            WarehouseType = SubconCustodyConstants.CustodyWarehouseType,
            IsActive = false,
            RowVersion = subconWh.Data.RowVersion
        }, isNew: false);
        Assert.False(deactivateViaSave.Succeeded);

        // 3. Deactivating SUBCON via SetWarehouseActiveAsync MUST fail.
        var deactivateViaToggle = await refService.SetWarehouseActiveAsync(
            [new IvMasterKeyToken { Code = "SUBCON", RowVersion = subconWh.Data.RowVersion ?? [] }],
            isActive: false);
        Assert.False(deactivateViaToggle.Succeeded);

        // 4. Promoting occupied MAIN (70 EA on hand) -> SUBCON MUST fail.
        var promoteOccupied = await refService.SaveWarehouseAsync(new IvWarehouseEditVm
        {
            Code = "MAIN",
            Desc = mainWh.Data!.Desc,
            WarehouseType = SubconCustodyConstants.CustodyWarehouseType,
            IsActive = true,
            RowVersion = mainWh.Data.RowVersion
        }, isNew: false);
        Assert.False(promoteOccupied.Succeeded);
        Assert.Contains("stock", promoteOccupied.Message!, StringComparison.OrdinalIgnoreCase);

        // 5. Deactivating SUBCON/SUP01 location via SaveLocationAsync & SetLocationActiveAsync MUST fail.
        var deactivateLocSave = await refService.SaveLocationAsync(new IvLocationEditVm
        {
            WarehouseCode = "SUBCON",
            Code = "SUP01",
            Desc = subconLoc.Data!.Desc,
            IsActive = false,
            RowVersion = subconLoc.Data.RowVersion
        }, isNew: false);
        Assert.False(deactivateLocSave.Succeeded);

        var deactivateLocToggle = await refService.SetLocationActiveAsync(
            [new IvMasterKeyToken { Code = "SUP01", ParentCode = "SUBCON", RowVersion = subconLoc.Data.RowVersion ?? [] }],
            isActive: false);
        Assert.False(deactivateLocToggle.Succeeded);

        // 6. After receiving all 30 EA back and clearing policy reference, demoting SUBCON -> NORMAL succeeds.
        var rDraft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = d1.DocNo, DispatchLineNo = 1, Qty = 30m }]
        });
        Assert.True(rDraft.Succeeded, rDraft.ErrorMessage);
        Assert.True((await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = rDraft.Receipt!.DocNo, RowVersion = rDraft.Receipt.RowVersion })).Succeeded);

        var curPolicy = await svc.GetPolicyAsync();
        var disablePolicy = await svc.SavePolicyAsync(new SubconPolicySaveCommand
        {
            SubconEnabled = false,
            InventoryModeEnabled = false,
            InventoryCustodyPostEnabled = false,
            DefaultCustodyWarehouseCode = null,
            RowVersion = curPolicy.Policy!.RowVersion
        });
        Assert.True(disablePolicy.Succeeded, disablePolicy.ErrorMessage);

        var subconAfter = await refService.GetWarehouseAsync("SUBCON");
        var demoteClean = await refService.SaveWarehouseAsync(new IvWarehouseEditVm
        {
            Code = "SUBCON",
            Desc = subconAfter.Data!.Desc,
            WarehouseType = "NORMAL",
            IsActive = true,
            RowVersion = subconAfter.Data.RowVersion
        }, isNew: false);
        Assert.True(demoteClean.Succeeded, demoteClean.Message);
    }

    [Fact]
    public async Task H02_Posting_and_rollback_fail_closed_when_V2_ledger_epoch_or_coordinator_is_inactive()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        var draft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 10m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        // Sub-case A: transition StockLedgerEpoch out of ACTIVE -> Post MUST fail closed with zero mutations.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var epoch = await db.StockLedgerEpochs.SingleAsync();
            epoch.Status = StockLedgerEpochStatuses.Prepared;
            await db.SaveChangesAsync();
        }

        var blockedPost = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = draft.Dispatch!.DocNo,
            RowVersion = draft.Dispatch.RowVersion
        });
        Assert.False(blockedPost.Succeeded);
        Assert.Equal(SubconBlockedReasons.V2LedgerRequired, blockedPost.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(100m, await db.IvBalLocs.Where(b => b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync());
            Assert.Equal(0, await db.IvTrxBatches.CountAsync());
            Assert.Equal(0, await db.SubconPostingLinks.CountAsync());
            Assert.Equal(SubconStatuses.New, await db.SubconDispatches.Where(d => d.DocNo == draft.Dispatch.DocNo).Select(d => d.Status).SingleAsync());

            // Reactivate epoch so we can post and test Sub-case B (null coordinator on rollback).
            var epoch = await db.StockLedgerEpochs.SingleAsync();
            epoch.Status = StockLedgerEpochStatuses.Active;
            await db.SaveChangesAsync();
        }

        var posted = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = draft.Dispatch.DocNo,
            RowVersion = draft.Dispatch.RowVersion
        });
        Assert.True(posted.Succeeded, posted.ErrorMessage);

        // Sub-case B: posting service wired WITHOUT V2 coordinator -> Rollback MUST fail closed.
        var noCoordPosting = new IvInventoryPostingService(
            _factory, _tenant.Object, _access.Object,
            new IvStockPostingRepository(), new IvStockCommonRepository(_factory),
            new ErpWeb.Model.Repositories.Purchase.PoOrderRepository(), NullLogger<IvInventoryPostingService>.Instance,
            new FixedCurrentDateService(Today),
            historyWriter: null,
            coordinator: null,
            approval: ErpWeb.Tests.Procurement.Transaction.TestDisabledProcurementApprovalGuard.Instance);
        var noCoordAdapter = new SubconInventoryTransferAdapter(
            _factory, noCoordPosting, new RunningNumberService(), new IvUomConversionService(_factory), new InventorySoftReservationReader());
        var noCoordSvc = new SubconApplicationService(
            _factory, _tenant.Object, _access.Object, new RunningNumberService(), noCoordAdapter, new SubconBlockedProductionSourceAdapter());

        var blockedRollback = await noCoordSvc.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = posted.Dispatch!.DocNo,
            RowVersion = posted.Dispatch.RowVersion
        });
        Assert.False(blockedRollback.Succeeded);
        Assert.Equal(SubconBlockedReasons.V2LedgerRequired, blockedRollback.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(SubconStatuses.Posted, await db.SubconDispatches.Where(d => d.DocNo == posted.Dispatch.DocNo).Select(d => d.Status).SingleAsync());
            Assert.Equal(10m, await db.IvBalLocs.Where(b => b.WhCode == "SUBCON" && b.LocCode == "SUP01").Select(b => b.StdQty).SingleAsync());
        }
    }

    [Fact]
    public async Task M01_Long_supplier_code_requires_explicit_map_prevents_bin_collision_and_blocks_remap_while_outstanding()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PoSuppliers.AddRange(
                new PoSupplier { CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!, SuppCode = "LONG_SUPPLIER_123456", SuppName = "Twenty Char Subcon", IsActive = true },
                new PoSupplier { CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!, SuppCode = "SUP02", SuppName = "Second Subcon", IsActive = true });
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                WarehouseCode = "SUBCON_EXTENDED_20CH",
                WarehouseDesc = "Extended 20-char Custody WH",
                WarehouseType = SubconCustodyConstants.CustodyWarehouseType,
                IsActive = true
            });
            db.IvLocations.AddRange(
                new IvLocation { CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!, WarehouseCode = "SUBCON_EXTENDED_20CH", LocCode = "LSUP01", LocDesc = "Long Supplier Bin 1", IsActive = true },
                new IvLocation { CompanyCode = _scope.CompanyCode, BranchCode = _scope.BranchCode!, WarehouseCode = "SUBCON_EXTENDED_20CH", LocCode = "LSUP02", LocDesc = "Long Supplier Bin 2", IsActive = true });
            await db.SaveChangesAsync();
        }

        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        // 1. Unmapped 20-char supplier MUST fail closed (never truncate to 10 chars).
        var longDraft = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "LONG_SUPPLIER_123456",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 25m, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(longDraft.Succeeded, longDraft.ErrorMessage);

        var unmappedPost = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = longDraft.Dispatch!.DocNo,
            RowVersion = longDraft.Dispatch.RowVersion
        });
        Assert.False(unmappedPost.Succeeded);
        Assert.Contains("10-character", unmappedPost.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // 2. Map 20-char supplier to 20-char SUBCON warehouse + 10-char location -> succeeds.
        var map1 = await svc.SaveSupplierCustodyMapAsync(new SubconSupplierCustodyMapSaveCommand
        {
            SupplierCode = "LONG_SUPPLIER_123456",
            CustodyWarehouseCode = "SUBCON_EXTENDED_20CH",
            CustodyLocationCode = "LSUP01",
            IsActive = true
        });
        Assert.True(map1.Succeeded, map1.ErrorMessage);

        // 3. Mapping another supplier to the same active bin MUST fail with collision.
        var collision = await svc.SaveSupplierCustodyMapAsync(new SubconSupplierCustodyMapSaveCommand
        {
            SupplierCode = "SUP02",
            CustodyWarehouseCode = "SUBCON_EXTENDED_20CH",
            CustodyLocationCode = "LSUP01",
            IsActive = true
        });
        Assert.False(collision.Succeeded);
        Assert.Contains("already mapped", collision.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // 4. Posting Send-Out now routes into SUBCON_EXTENDED_20CH / LSUP01.
        var postedLong = await svc.PostDispatchAsync(new SubconKeyCommand
        {
            DocNo = longDraft.Dispatch.DocNo,
            RowVersion = longDraft.Dispatch.RowVersion
        });
        Assert.True(postedLong.Succeeded, postedLong.ErrorMessage);
        Assert.Equal("SUBCON_EXTENDED_20CH", postedLong.Dispatch!.Lines[0].CustodyWarehouseCode);
        Assert.Equal("LSUP01", postedLong.Dispatch.Lines[0].CustodyLocationCode);

        // 5. Remapping or deactivating while stock is outstanding MUST be blocked.
        var blockedRemap = await svc.SaveSupplierCustodyMapAsync(new SubconSupplierCustodyMapSaveCommand
        {
            SupplierCode = "LONG_SUPPLIER_123456",
            CustodyWarehouseCode = "SUBCON_EXTENDED_20CH",
            CustodyLocationCode = "LSUP02",
            IsActive = true,
            RowVersion = map1.SupplierCustodyMap!.RowVersion
        });
        Assert.False(blockedRemap.Succeeded);
        Assert.Equal(SubconMessages.SupplierCustodyRemapBlocked, blockedRemap.ErrorMessage);

        // 6. Receive back all 25 EA -> remapping now succeeds.
        var rDraft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "LONG_SUPPLIER_123456",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = postedLong.Dispatch.DocNo, DispatchLineNo = 1, Qty = 25m }]
        });
        Assert.True(rDraft.Succeeded, rDraft.ErrorMessage);
        Assert.True((await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = rDraft.Receipt!.DocNo, RowVersion = rDraft.Receipt.RowVersion })).Succeeded);

        var allowedRemap = await svc.SaveSupplierCustodyMapAsync(new SubconSupplierCustodyMapSaveCommand
        {
            SupplierCode = "LONG_SUPPLIER_123456",
            CustodyWarehouseCode = "SUBCON_EXTENDED_20CH",
            CustodyLocationCode = "LSUP02",
            IsActive = true,
            RowVersion = map1.SupplierCustodyMap.RowVersion
        });
        Assert.True(allowedRemap.Succeeded, allowedRemap.ErrorMessage);
    }

    [Fact]
    public async Task M02_Rollback_dispatch_and_receipt_recover_idempotently_on_retry_with_same_RequestId()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        var d1 = await PostSimpleDispatchAsync(svc, qty: 20m);
        var d2 = await PostSimpleDispatchAsync(svc, qty: 10m);

        var r1Draft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = d1.DocNo, DispatchLineNo = 1, Qty = 10m }]
        });
        var r1Posted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = r1Draft.Receipt!.DocNo, RowVersion = r1Draft.Receipt.RowVersion });
        Assert.True(r1Posted.Succeeded, r1Posted.ErrorMessage);

        var r2Draft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = d2.DocNo, DispatchLineNo = 1, Qty = 5m }]
        });
        var r2Posted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = r2Draft.Receipt!.DocNo, RowVersion = r2Draft.Receipt.RowVersion });
        Assert.True(r2Posted.Succeeded, r2Posted.ErrorMessage);

        // 1. Roll back tail receipt r2 + timeout-after-commit retry with SAME RequestId & stale pre-rollback RowVersion.
        var revReceiptReqId = Guid.NewGuid();
        var rbR2 = await svc.RollbackReceiptAsync(new SubconKeyCommand
        {
            DocNo = r2Posted.Receipt!.DocNo,
            RowVersion = r2Posted.Receipt.RowVersion,
            RequestId = revReceiptReqId
        });
        Assert.True(rbR2.Succeeded, rbR2.ErrorMessage);
        Assert.Equal(SubconStatuses.New, rbR2.Receipt!.Status);
        Assert.Equal(1, rbR2.Receipt.DocumentRevision);

        var rbR2Retry = await svc.RollbackReceiptAsync(new SubconKeyCommand
        {
            DocNo = r2Posted.Receipt.DocNo,
            RowVersion = r2Posted.Receipt.RowVersion,
            RequestId = revReceiptReqId
        });
        Assert.True(rbR2Retry.Succeeded, rbR2Retry.ErrorMessage);
        Assert.Equal(SubconStatuses.New, rbR2Retry.Receipt!.Status);
        Assert.Equal(1, rbR2Retry.Receipt.DocumentRevision);

        // Reusing revReceiptReqId on a DIFFERENT receipt (r1) MUST fail with RequestIdConflict.
        var rbR1Conflict = await svc.RollbackReceiptAsync(new SubconKeyCommand
        {
            DocNo = r1Posted.Receipt!.DocNo,
            RowVersion = r1Posted.Receipt.RowVersion,
            RequestId = revReceiptReqId
        });
        Assert.False(rbR1Conflict.Succeeded);
        Assert.Equal(SubconMessages.RequestIdConflict, rbR1Conflict.ErrorMessage);

        // Roll back r1 (now tail!) and delete both receipt drafts so d2 is tail & unreferenced.
        var rbR1 = await svc.RollbackReceiptAsync(new SubconKeyCommand
        {
            DocNo = r1Posted.Receipt.DocNo,
            RowVersion = r1Posted.Receipt.RowVersion,
            RequestId = Guid.NewGuid()
        });
        Assert.True(rbR1.Succeeded, rbR1.ErrorMessage);
        Assert.True((await svc.DeleteReceiptDraftAsync(new SubconKeyCommand { DocNo = rbR2.Receipt.DocNo, RowVersion = rbR2.Receipt.RowVersion })).Succeeded);
        Assert.True((await svc.DeleteReceiptDraftAsync(new SubconKeyCommand { DocNo = rbR1.Receipt.DocNo, RowVersion = rbR1.Receipt.RowVersion })).Succeeded);

        // 2. Roll back tail dispatch d2 + timeout-after-commit retry with SAME RequestId & stale pre-rollback RowVersion.
        var revDispatchReqId = Guid.NewGuid();
        var rbD2 = await svc.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = d2.DocNo,
            RowVersion = d2.RowVersion,
            RequestId = revDispatchReqId
        });
        Assert.True(rbD2.Succeeded, rbD2.ErrorMessage);
        Assert.Equal(SubconStatuses.New, rbD2.Dispatch!.Status);
        Assert.Equal(1, rbD2.Dispatch.DocumentRevision);

        var rbD2Retry = await svc.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = d2.DocNo,
            RowVersion = d2.RowVersion,
            RequestId = revDispatchReqId
        });
        Assert.True(rbD2Retry.Succeeded, rbD2Retry.ErrorMessage);
        Assert.Equal(SubconStatuses.New, rbD2Retry.Dispatch!.Status);
        Assert.Equal(1, rbD2Retry.Dispatch.DocumentRevision);

        // Reusing revDispatchReqId on a DIFFERENT dispatch (d1) MUST fail with RequestIdConflict.
        var rbD1Conflict = await svc.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = d1.DocNo,
            RowVersion = d1.RowVersion,
            RequestId = revDispatchReqId
        });
        Assert.False(rbD1Conflict.Succeeded);
        Assert.Equal(SubconMessages.RequestIdConflict, rbD1Conflict.ErrorMessage);

        // Confirm exactly one REVERSAL link exists for r2 and one for d2 (no duplicates).
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.SubconPostingLinks.CountAsync(x => x.DocNo == r2Posted.Receipt.DocNo && x.PostingRole == SubconPostingRoles.Reversal));
        Assert.Equal(1, await db.SubconPostingLinks.CountAsync(x => x.DocNo == d2.DocNo && x.PostingRole == SubconPostingRoles.Reversal));
    }

    [Fact]
    public async Task M03_Interleaved_multi_dispatch_FIFO_receipts_and_rollback_preserve_branch_pooled_FIFO_layers()
    {
        // Opening FIFO pool: Layer 1 = 40 @ 5.00 (200), Layer 2 = 60 @ 8.00 (480) => Total = 100 EA, Value = 680.00.
        await SeedCostMethodAndOpeningStockAsync("FIFO");
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);

        var d1 = await PostSimpleDispatchAsync(svc, qty: 35m);
        var d2 = await PostSimpleDispatchAsync(svc, qty: 45m);

        // Interleaved partial return from d2 (15 EA), tail rollback & repost of d2's receipt, then return from d1 (20 EA).
        var rD2Draft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = d2.DocNo, DispatchLineNo = 1, Qty = 15m }]
        });
        var rD2Posted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = rD2Draft.Receipt!.DocNo, RowVersion = rD2Draft.Receipt.RowVersion });
        Assert.True(rD2Posted.Succeeded, rD2Posted.ErrorMessage);

        var rD2Rolled = await svc.RollbackReceiptAsync(new SubconKeyCommand { DocNo = rD2Posted.Receipt!.DocNo, RowVersion = rD2Posted.Receipt.RowVersion });
        Assert.True(rD2Rolled.Succeeded, rD2Rolled.ErrorMessage);

        var rD2Reposted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = rD2Rolled.Receipt!.DocNo, RowVersion = rD2Rolled.Receipt.RowVersion });
        Assert.True(rD2Reposted.Succeeded, rD2Reposted.ErrorMessage);

        var rD1Draft = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = d1.DocNo, DispatchLineNo = 1, Qty = 20m }]
        });
        var rD1Posted = await svc.PostReceiptAsync(new SubconKeyCommand { DocNo = rD1Draft.Receipt!.DocNo, RowVersion = rD1Draft.Receipt.RowVersion });
        Assert.True(rD1Posted.Succeeded, rD1Posted.ErrorMessage);

        // Non-tail rollback of rD2 while rD1 is posted AFTER it MUST be refused by V2 chronology guard.
        var nonTailBlocked = await svc.RollbackReceiptAsync(new SubconKeyCommand { DocNo = rD2Reposted.Receipt!.DocNo, RowVersion = rD2Reposted.Receipt.RowVersion });
        Assert.False(nonTailBlocked.Succeeded);

        // Verify exact stock split (MAIN = 55, SUBCON = 45), branch-pooled FIFO layers unchanged, and zero allocation variance.
        await AssertStockAndValueAsync(mainQty: 55m, subconQty: 45m, expectedValue: 680m, method: "FIFO");

        await using var db = await _factory.CreateDbContextAsync();
        var layers = await db.StockFifoLayers.AsNoTracking()
            .Where(l => l.CompanyCode == _scope.CompanyCode && l.BranchCode == _scope.BranchCode && l.ItemCode == "A100")
            .OrderBy(l => l.CurrentUnitCost)
            .ToListAsync();
        Assert.Equal(2, layers.Count);
        Assert.Equal(40m, layers[0].RemainingQty);
        Assert.Equal(5m, layers[0].CurrentUnitCost);
        Assert.Equal(60m, layers[1].RemainingQty);
        Assert.Equal(8m, layers[1].CurrentUnitCost);

        var recon = await svc.GetCustodyReconciliationAsync("SUP01");
        Assert.True(recon.Succeeded, recon.ErrorMessage);
        var row = Assert.Single(recon.Reconciliation!);
        Assert.Equal(80m, row.AllocatedDispatchedBaseQty);
        Assert.Equal(35m, row.AllocatedReturnedBaseQty);
        Assert.Equal(45m, row.AllocatedOutstandingBaseQty);
        Assert.Equal(45m, row.PhysicalCustodyBaseQty);
        Assert.True(row.IsReconciled);
    }

    [Fact]
    public async Task Backdated_MR_targeting_subcon_custody_warehouse_is_blocked_even_with_replay_enabled()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-3));
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);
        var dispatch = await PostSimpleDispatchAsync(svc, qty: 20m);

        var mr = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, Today, enableBackdatedReplay: true);
        var draft = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = Today.AddDays(-1),
            RefNo = "BAD-SUBCON-MR",
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "SUBCON", ToLocation = "SUP01",
                Quantity = 5m, UnitPrice = 6m, PriceConfirmed = true,
                Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ"
            }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        var posted = await mr.PostAsync([draft.BatchNo]);
        Assert.False(posted.Succeeded);
        Assert.Contains("custody", posted.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await AssertStockAndValueAsync(mainQty: 80m, subconQty: 20m, expectedValue: 500m, method: "WAC");

        // Subcon dispatch remains cleanly rollback-able after the blocked MR.
        var rolled = await svc.RollbackDispatchAsync(new SubconKeyCommand { DocNo = dispatch.DocNo, RowVersion = dispatch.RowVersion });
        Assert.True(rolled.Succeeded, rolled.ErrorMessage);
        await AssertStockAndValueAsync(mainQty: 100m, subconQty: 0m, expectedValue: 500m, method: "WAC");
    }

    [Fact]
    public async Task Receipt_before_count_does_not_cross_or_strand_later_subcon_dispatch()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC", openingDate: Today.AddDays(-3));
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);
        var count = InventoryLedgerTestFixture.CreateStockCount(_factory, Today, enableBackdatedReplay: true);
        var savedCount = await count.SaveAsync(new IvStockCountSaveRequest
        {
            CountDate = Today.AddDays(-1), WHCode = "MAIN", IncludeZeroQty = true
        });
        Assert.True(savedCount.Succeeded, savedCount.ErrorMessage);
        Assert.True((await count.GenerateAsync(savedCount.Id, discardCounts: false)).Succeeded);
        var line = Assert.Single((await count.GetAsync(savedCount.CountNo!)).Document!.Lines);
        Assert.True((await count.SaveCountsAsync(savedCount.Id,
            [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 90m }], null)).Succeeded);
        var counted = await count.PostAsync(savedCount.Id);
        Assert.True(counted.Succeeded, counted.ErrorMessage);
        var dispatch = await PostSimpleDispatchAsync(svc, qty: 20m);

        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, Today, enableBackdatedReplay: true);
        var draft = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = Today.AddDays(-2), RefNo = "COUNT-BEFORE-SUBCON",
            Lines = [new IvMiscReceiptLineRequest { ICode = "A100", ToWarehouse = "MAIN",
                ToLocation = "BIN1", Quantity = 10m, UnitPrice = 8m, PriceConfirmed = true,
                Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ" }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var blocked = await receipt.PostAsync([draft.BatchNo]);
        Assert.False(blocked.Succeeded);
        await AssertCountedStockAsync(mainQty: 70m, subconQty: 20m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == draft.BatchNo)).BatchStatus);
            var active = (await StockCountGenerationResolver.ResolveActiveAsync(db, "DEMO", "HQ", [line.Id]))[line.Id];
            Assert.Equal(-10m, active.SignedAdjustmentQty);
        }
        var rolled = await svc.RollbackDispatchAsync(new SubconKeyCommand { DocNo = dispatch.DocNo, RowVersion = dispatch.RowVersion });
        Assert.True(rolled.Succeeded, rolled.ErrorMessage);
        await AssertCountedStockAsync(mainQty: 90m, subconQty: 0m);

        async Task AssertCountedStockAsync(decimal mainQty, decimal subconQty)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var main = await db.IvBalLocs.SingleAsync(b => b.ICode == "A100" && b.WhCode == "MAIN");
            var custody = await db.IvBalLocs.SingleOrDefaultAsync(b => b.ICode == "A100" && b.WhCode == "SUBCON");
            Assert.Equal(mainQty, main.StdQty);
            Assert.Equal(subconQty, custody?.StdQty ?? 0m);
            var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
            Assert.Equal((90m, 450m), (state.OnHandBaseQty, state.InventoryValue));
        }
    }

    [Theory]
    [InlineData("WAC")]
    [InlineData("STANDARD")]
    public async Task Backdated_MR_and_StockCount_cannot_reprice_or_strand_later_subcon_custody_transfer(string method)
    {
        await SeedCostMethodAndOpeningStockAsync(method, openingDate: Today.AddDays(-3));
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);
        var dispatch = await PostSimpleDispatchAsync(svc, qty: 20m);

        var mr = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, Today, enableBackdatedReplay: true);
        var draft = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = Today.AddDays(-1),
            RefNo = "BACKDATED-BEFORE-SUBCON",
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                Quantity = 10m, UnitPrice = 8m, PriceConfirmed = true,
                Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ"
            }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        var posted = await mr.PostAsync([draft.BatchNo]);
        Assert.False(posted.Succeeded);
        if (method == "WAC")
            Assert.Contains("subcontractor custody transfer", posted.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains("unsupported descendant", posted.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        await AssertStockAndValueAsync(mainQty: 80m, subconQty: 20m, expectedValue: 500m, method: method);

        if (method == "WAC")
        {
            var count = InventoryLedgerTestFixture.CreateStockCount(_factory, Today, enableBackdatedReplay: true);
            var savedCount = await count.SaveAsync(new IvStockCountSaveRequest
            {
                CountDate = Today.AddDays(-1), WHCode = "MAIN", IncludeZeroQty = true
            });
            Assert.True(savedCount.Succeeded, savedCount.ErrorMessage);
            Assert.True((await count.GenerateAsync(savedCount.Id, discardCounts: false)).Succeeded);
            var line = Assert.Single((await count.GetAsync(savedCount.CountNo!)).Document!.Lines);
            Assert.True((await count.SaveCountsAsync(savedCount.Id,
                [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 105m }], null)).Succeeded);

            var countPost = await count.PostAsync(savedCount.Id);
            Assert.False(countPost.Succeeded);
            Assert.Contains("subcontractor custody transfer", countPost.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            await AssertStockAndValueAsync(mainQty: 80m, subconQty: 20m, expectedValue: 500m, method: "WAC");
        }

        // Subcon Send-Out rollback must NOT be stranded because no foreign replay repriced its TR.
        var rolled = await svc.RollbackDispatchAsync(new SubconKeyCommand { DocNo = dispatch.DocNo, RowVersion = dispatch.RowVersion });
        Assert.True(rolled.Succeeded, rolled.ErrorMessage);
        await AssertStockAndValueAsync(mainQty: 100m, subconQty: 0m, expectedValue: 500m, method: method);
    }

    [Fact]
    public async Task Warehouse_and_location_delete_are_blocked_when_referenced_by_subcon_policy_or_map()
    {
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);
        var mapSave = await svc.SaveSupplierCustodyMapAsync(new SubconSupplierCustodyMapSaveCommand
        {
            SupplierCode = "SUP01",
            CustodyWarehouseCode = "SUBCON",
            CustodyLocationCode = "SUP01",
            IsActive = true
        });
        Assert.True(mapSave.Succeeded, mapSave.ErrorMessage);

        var refSvc = CreateInventoryRefService();
        byte[] whRv;
        byte[] locRv;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            whRv = (await db.IvWarehouses.AsNoTracking().SingleAsync(w => w.WarehouseCode == "SUBCON")).RowVersion;
            locRv = (await db.IvLocations.AsNoTracking().SingleAsync(l => l.WarehouseCode == "SUBCON" && l.LocCode == "SUP01")).RowVersion;
        }

        var delWh = await refSvc.DeleteWarehousesAsync([new IvMasterKeyToken { Code = "SUBCON", RowVersion = whRv }]);
        Assert.False(delWh.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, delWh.ErrorCode);

        var delLoc = await refSvc.DeleteLocationsAsync([new IvMasterKeyToken { ParentCode = "SUBCON", Code = "SUP01", RowVersion = locRv }]);
        Assert.False(delLoc.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, delLoc.ErrorCode);
    }

    [Fact]
    public async Task Sales_shipment_allocation_excludes_subcon_custody_warehouse()
    {
        await SeedCostMethodAndOpeningStockAsync("WAC");
        var (svc, _, _) = CreateServices();
        await EnableLiveCustodyPolicyAsync(svc);
        await PostSimpleDispatchAsync(svc, qty: 20m);

        var sp = new IvSpShipmentService(new IvStockPostingRepository(), new IvStockTransactionRepository(), new RunningNumberService());
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var result = await sp.CreateOrReplaceShipmentAsync(
            db,
            new IvSpCreateOrReplaceCommand
            {
                CompanyCode = _scope.CompanyCode,
                BranchCode = _scope.BranchCode!,
                LocationCode = _scope.LocationCode ?? "HQ",
                UserId = "TEST",
                DocumentNo = "DO-SUBCON-TEST",
                DocumentDate = Today,
                DoNo = "DO-SUBCON-TEST",
                OverwriteExisting = true,
                RequiredLines =
                [
                    new IvSpRequiredLine
                    {
                        Line = 1, ICode = "A100", IDesc = "Item A100",
                        FrWarehouse = "SUBCON", StdQty = 5m, StdUom = "EA",
                        UnitPrice = 10m, StockControl = true, LinkDo = false
                    }
                ]
            });
        await tx.RollbackAsync();
        Assert.True(result.HasIncompleteLines || !result.Succeeded);
        Assert.Empty(result.Lots);
        if (result.Lines.Count > 0)
            Assert.Equal(IvSpLineStatus.NoStock, Assert.Single(result.Lines).Status);
    }

    private IvInventoryRefService CreateInventoryRefService()
    {
        var lookups = new Mock<ErpWeb.Core.Admin.IAdLookupService>();
        lookups.Setup(x => x.ValidateAssignmentAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new IvInventoryRefService(
            _factory,
            _tenant.Object,
            _access.Object,
            new FixedCurrentDateService(Today),
            new IvStockCommonRepository(_factory),
            lookups.Object);
    }

    private (SubconApplicationService Service, IvInventoryPostingService Posting, SubconInventoryTransferAdapter Adapter) CreateServices(
        bool enableBackdatedReplay = false, IQaPhysicalHoldAuthorization? qaPhysical = null)
    {
        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory, _scope.CompanyCode, _scope.BranchCode!, _access.Object,
            enableBackdatedReplay: enableBackdatedReplay, qaPhysical: qaPhysical);
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

    private async Task<SubconDispatchDto> SaveAndPostDispatchAtAsync(SubconApplicationService svc, DateTime date, decimal qty)
    {
        var saved = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = date, Mode = SubconModes.Inventory, SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA",
                Qty = qty, WarehouseCode = "MAIN", LocationCode = "BIN1" }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = saved.Dispatch!.DocNo, RowVersion = saved.Dispatch.RowVersion });
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        return posted.Dispatch!;
    }

    private async Task<int> PostLaterIssueForSubconReplayAsync(DateTime? issueDate = null, decimal qty = 10m)
    {
        var date = issueDate ?? Today;
        int mainId;
        await using (var db = await _factory.CreateDbContextAsync())
            mainId = await db.IvBalLocs.Where(b => b.ICode == "A100" && b.WhCode == "MAIN").Select(b => b.Id).SingleAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, date);
        var draft = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = date,
            Lines = [new IvMiscIssueLineRequest { ICode = "A100", FromBalLocId = mainId,
                FrWarehouse = "MAIN", FrLocation = "BIN1", IStatus = "ACTIVE", Quantity = qty, Uom = "EA" }]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var posted = await issue.PostAsync([draft.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        return draft.BatchNo;
    }

    private async Task AssertSubconReplayStateAsync(decimal mainQty, decimal custodyQty, int issueBatch, decimal outstanding)
    {
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(mainQty, await db.IvBalLocs.Where(b => b.ICode == "A100" && b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync());
        Assert.Equal(custodyQty, await db.IvBalLocs.Where(b => b.ICode == "A100" && b.WhCode == "SUBCON").Select(b => b.StdQty).SingleAsync());
        var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
        Assert.Equal((90m, 450m), (state.OnHandBaseQty, state.InventoryValue));
        var allocations = await db.SubconCustodyAllocations.Where(a => a.IsActive).ToArrayAsync();
        Assert.Equal(outstanding, allocations.Sum(a => a.DispatchedBaseQty - a.ReturnedBaseQty - a.ConsumedBaseQty - a.ScrapBaseQty));
        var history = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == issueBatch && h.EntryRole == "ORIGINAL");
        var facts = await StockReplayValuationGenerationResolver.ResolveAsync(db, _scope.CompanyCode, _scope.BranchCode!, [history]);
        Assert.Equal(50m, facts[history.Id].Sum(f => f.CostAmount));
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
