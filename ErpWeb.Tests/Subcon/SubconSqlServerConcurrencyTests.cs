using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Subcon;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Entities.Subcon;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Tests.Inventory.Transaction;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests.Subcon;

[Collection("SubconSqlServer")]
[Trait(TestCategories.Name, TestCategories.Subcon)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class SubconSqlServerConcurrencyTests : IAsyncLifetime
{
    private const string TestConnectionKey = "SqlServerTestConnection";
    private static readonly DateTime Today = new(2026, 8, 26);

    private string? _connectionString;
    private IDbContextFactory<AppDbContext>? _factory;
    private string _company = "SC" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();
    private const string Branch = "HQ";

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    public async Task InitializeAsync()
    {
        _connectionString = GetSqlServerConnectionString();
        if (_connectionString is null
            || !IsScratchDatabase(_connectionString)
            || !CanConnect(_connectionString))
        {
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        _factory = new SqlServerTestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        await InventoryLedgerTestFixture.SeedCompanyAsync(db, _company);
        await InventoryLedgerTestFixture.SeedActiveEpochAsync(db, _company, Branch);

        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = _company,
            BranchCode = Branch,
            SuppCode = "SUP01",
            SuppName = "Subcon Vendor",
            IsActive = true
        });
        db.IvWarehouses.AddRange(
            new IvWarehouse
            {
                CompanyCode = _company,
                BranchCode = Branch,
                WarehouseCode = "MAIN",
                WarehouseDesc = "Main Store",
                WarehouseType = "NORMAL",
                IsActive = true
            },
            new IvWarehouse
            {
                CompanyCode = _company,
                BranchCode = Branch,
                WarehouseCode = "SUBCON",
                WarehouseDesc = "Subcontractor Custody",
                WarehouseType = SubconCustodyConstants.CustodyWarehouseType,
                IsActive = true
            });
        db.IvLocations.AddRange(
            new IvLocation
            {
                CompanyCode = _company,
                BranchCode = Branch,
                WarehouseCode = "MAIN",
                LocCode = "BIN1",
                LocDesc = "Bin 1",
                IsActive = true
            },
            new IvLocation
            {
                CompanyCode = _company,
                BranchCode = Branch,
                WarehouseCode = "SUBCON",
                LocCode = "SUP01",
                LocDesc = "SUP01 Custody Bin",
                IsActive = true
            });
        db.MsUoms.Add(new MsUom
        {
            CompanyCode = _company,
            UomCode = "PCS",
            UomDesc = "Pieces",
            IsActive = true
        });
        db.IvClasses.Add(new IvClass { CompanyCode = _company, IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = _company, IStatus = "ACTIVE", IsActive = true });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = _company,
            ICode = "RM1",
            IDesc = "Metal plate",
            IClassCode = "RAW",
            StdUom = "PCS",
            StockControl = true,
            LotControl = false,
            DefWarehouse = "MAIN",
            DefLocation = "BIN1",
            IsActive = true,
            PurchasePrice = 5m
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is null)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync();
        var allocs = await db.SubconCustodyAllocations.Where(x => x.CompanyCode == _company).ToListAsync();
        db.SubconCustodyAllocations.RemoveRange(allocs);
        var links = await db.SubconPostingLinks.Where(x => x.CompanyCode == _company).ToListAsync();
        db.SubconPostingLinks.RemoveRange(links);
        var maps = await db.SubconSupplierCustodyMaps.Where(x => x.CompanyCode == _company).ToListAsync();
        db.SubconSupplierCustodyMaps.RemoveRange(maps);
        var policies = await db.SubconPolicies.Where(x => x.CompanyCode == _company).ToListAsync();
        db.SubconPolicies.RemoveRange(policies);

        var receipts = await db.SubconReceipts.Include(x => x.Lines)
            .Where(x => x.CompanyCode == _company)
            .ToListAsync();
        db.SubconReceiptLines.RemoveRange(receipts.SelectMany(x => x.Lines));
        db.SubconReceipts.RemoveRange(receipts);

        var dispatches = await db.SubconDispatches.Include(x => x.Lines)
            .Where(x => x.CompanyCode == _company)
            .ToListAsync();
        db.SubconDispatchLines.RemoveRange(dispatches.SelectMany(x => x.Lines));
        db.SubconDispatches.RemoveRange(dispatches);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Concurrent_receipt_saves_against_same_dispatch_line_cannot_over_allocate()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var seedSvc = CreateDraftService();
        var dispatch = await seedSvc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
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
                }
            ]
        });
        Assert.True(dispatch.Succeeded, dispatch.ErrorMessage);

        var svc1 = CreateDraftService();
        var svc2 = CreateDraftService();
        var cmd1 = new SubconReceiptSaveCommand
        {
            TrxDate = Today,
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
        };
        var cmd2 = new SubconReceiptSaveCommand
        {
            TrxDate = Today,
            SupplierCode = "SUP01",
            Lines =
            [
                new SubconReceiptLineSaveCommand
                {
                    LineNo = 1,
                    DispatchDocNo = dispatch.Dispatch.DocNo,
                    DispatchLineNo = 1,
                    Qty = 3m
                }
            ]
        };

        var results = await RunSimultaneouslyAsync(
            () => svc1.SaveReceiptDraftAsync(cmd1),
            () => svc2.SaveReceiptDraftAsync(cmd2));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => !r.Succeeded));

        await using var db = await _factory!.CreateDbContextAsync();
        var totalAllocated = await db.SubconReceiptLines
            .Where(l => l.Dispatch.CompanyCode == _company && l.Dispatch.DocNo == dispatch.Dispatch.DocNo)
            .SumAsync(l => (decimal?)l.Qty) ?? 0m;
        Assert.Equal(3m, totalAllocated);
    }

    [Fact]
    public async Task H03_Competing_send_outs_for_last_5_units_allow_only_one_winner_on_SqlServer()
    {
        if (SkipWithoutSqlServer()) return;

        await SeedOpeningStockWacAsync(openingQty: 5m, unitCost: 10m);
        var (setupSvc, _) = CreateLiveService();
        await EnableLivePolicyAsync(setupSvc);

        var d1 = await SaveDispatchDraftAsync(setupSvc, qty: 5m);
        var d2 = await SaveDispatchDraftAsync(setupSvc, qty: 5m);

        var (svc1, _) = CreateLiveService();
        var (svc2, _) = CreateLiveService();

        var results = await RunSimultaneouslyAsync(
            () => svc1.PostDispatchAsync(new SubconKeyCommand { DocNo = d1.DocNo, RowVersion = d1.RowVersion, RequestId = Guid.NewGuid() }),
            () => svc2.PostDispatchAsync(new SubconKeyCommand { DocNo = d2.DocNo, RowVersion = d2.RowVersion, RequestId = Guid.NewGuid() }));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => !r.Succeeded));

        await using var db = await _factory!.CreateDbContextAsync();
        Assert.Equal(0m, await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync());
        Assert.Equal(5m, await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "SUBCON" && b.LocCode == "SUP01").Select(b => b.StdQty).SingleAsync());
        var cost = await db.StockCostStates.SingleAsync(x => x.CompanyCode == _company && x.BranchCode == Branch && x.ItemCode == "RM1");
        Assert.Equal(5m, cost.OnHandBaseQty);
        Assert.Equal(50m, cost.InventoryValue);
        Assert.Equal(1, await db.SubconCustodyAllocations.CountAsync(x => x.CompanyCode == _company && x.IsActive));
    }

    [Fact]
    public async Task H03_Competing_receive_backs_for_last_5_units_allow_only_one_winner_on_SqlServer()
    {
        if (SkipWithoutSqlServer()) return;

        await SeedOpeningStockWacAsync(openingQty: 10m, unitCost: 10m);
        var (setupSvc, _) = CreateLiveService();
        await EnableLivePolicyAsync(setupSvc);

        var d1 = await PostDispatchAsync(setupSvc, qty: 10m);

        // First partial return of 5 EA already posted -> only 5 EA remain in custody.
        var r0 = await SaveReceiptDraftAsync(setupSvc, d1.DocNo, qty: 5m);
        Assert.True((await setupSvc.PostReceiptAsync(new SubconKeyCommand { DocNo = r0.DocNo, RowVersion = r0.RowVersion })).Succeeded);

        // Two receipts drafted for 3 EA each (bypassing draft guard via direct seed of 2 drafts of 5 EA to race PostReceiptAsync directly).
        // Draft 1 = 3 EA, Draft 2 = 2 EA would sum to 5; to force a Post-time race over the remaining 5 EA, seed two NEW receipts each asking for 5 EA.
        var (r1Doc, r1Rv, r2Doc, r2Rv) = await SeedCompetingReceiptDraftsAsync(d1.DocNo, qtyEach: 5m);

        var (svc1, _) = CreateLiveService();
        var (svc2, _) = CreateLiveService();
        var results = await RunSimultaneouslyAsync(
            () => svc1.PostReceiptAsync(new SubconKeyCommand { DocNo = r1Doc, RowVersion = r1Rv, RequestId = Guid.NewGuid() }),
            () => svc2.PostReceiptAsync(new SubconKeyCommand { DocNo = r2Doc, RowVersion = r2Rv, RequestId = Guid.NewGuid() }));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => !r.Succeeded));

        await using var db = await _factory!.CreateDbContextAsync();
        Assert.Equal(10m, await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync());
        Assert.Equal(0m, await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "SUBCON" && b.LocCode == "SUP01").Select(b => b.StdQty).SingleAsync());
        var alloc = await db.SubconCustodyAllocations.SingleAsync(x => x.CompanyCode == _company && x.IsActive);
        Assert.Equal(10m, alloc.ReturnedBaseQty);
    }

    [Fact]
    public async Task H03_Concurrent_receive_back_post_vs_send_out_rollback_preserves_consistency_on_SqlServer()
    {
        if (SkipWithoutSqlServer()) return;

        await SeedOpeningStockWacAsync(openingQty: 10m, unitCost: 10m);
        var (setupSvc, _) = CreateLiveService();
        await EnableLivePolicyAsync(setupSvc);

        var d1 = await PostDispatchAsync(setupSvc, qty: 10m);
        var r1 = await SaveReceiptDraftAsync(setupSvc, d1.DocNo, qty: 4m);

        var (svc1, _) = CreateLiveService();
        var (svc2, _) = CreateLiveService();
        var results = await RunSimultaneouslyAsync(
            () => svc1.PostReceiptAsync(new SubconKeyCommand { DocNo = r1.DocNo, RowVersion = r1.RowVersion, RequestId = Guid.NewGuid() }),
            () => svc2.RollbackDispatchAsync(new SubconKeyCommand { DocNo = d1.DocNo, RowVersion = d1.RowVersion, RequestId = Guid.NewGuid() }));

        // Rollback MUST lose because dependent draft/posted receipt exists; receipt post MUST win!
        Assert.True(results[0].Succeeded, results[0].ErrorMessage);
        Assert.False(results[1].Succeeded);

        var recon = await setupSvc.GetCustodyReconciliationAsync("SUP01");
        Assert.True(recon.Succeeded, recon.ErrorMessage);
        var row = Assert.Single(recon.Reconciliation!);
        Assert.Equal(6m, row.AllocatedOutstandingBaseQty);
        Assert.Equal(6m, row.PhysicalCustodyBaseQty);
        Assert.True(row.IsReconciled);
    }

    [Fact]
    public async Task H03_Concurrent_MI_post_vs_subcon_send_out_post_on_same_source_balance_allows_only_one_winner()
    {
        if (SkipWithoutSqlServer()) return;

        await SeedOpeningStockWacAsync(openingQty: 5m, unitCost: 10m);
        var (setupSvc, _) = CreateLiveService();
        await EnableLivePolicyAsync(setupSvc);

        var d1 = await SaveDispatchDraftAsync(setupSvc, qty: 5m);
        int mainBalId;
        await using (var db = await _factory!.CreateDbContextAsync())
        {
            mainBalId = await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "MAIN").Select(b => b.Id).SingleAsync();
        }

        var miService = InventoryLedgerTestFixture.CreateMiscIssue(_factory!, Today, _company, Branch);
        var miDraft = await miService.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = Today,
            RefNo = "RACE-MI",
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    ICode = "RM1",
                    FromBalLocId = mainBalId,
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    IStatus = "ACTIVE",
                    Quantity = 5m,
                    Uom = "PCS"
                }
            ]
        });
        Assert.True(miDraft.Succeeded, miDraft.ErrorMessage);

        var (subconSvc, _) = CreateLiveService();
        var miPostSvc = InventoryLedgerTestFixture.CreateMiscIssue(_factory!, Today, _company, Branch);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tSubcon = Task.Run(async () =>
        {
            await barrier.Task;
            var res = await subconSvc.PostDispatchAsync(new SubconKeyCommand { DocNo = d1.DocNo, RowVersion = d1.RowVersion, RequestId = Guid.NewGuid() });
            return res.Succeeded;
        });
        var tMi = Task.Run(async () =>
        {
            await barrier.Task;
            var res = await miPostSvc.PostAsync([miDraft.BatchNo]);
            return res.Posting?.Succeeded == true;
        });
        barrier.SetResult();
        var outcomes = await Task.WhenAll(tSubcon, tMi);

        Assert.Equal(1, outcomes.Count(x => x));
        Assert.Equal(1, outcomes.Count(x => !x));

        await using (var db = await _factory!.CreateDbContextAsync())
        {
            var mainQty = await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync();
            Assert.Equal(0m, mainQty);
            var subconQty = await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "SUBCON").Select(b => (decimal?)b.StdQty).SingleOrDefaultAsync() ?? 0m;
            if (outcomes[0])
                Assert.Equal(5m, subconQty);
            else
                Assert.Equal(0m, subconQty);
        }
    }

    [Fact]
    public async Task H03_Concurrent_warehouse_demotion_vs_subcon_send_out_post_cannot_corrupt_custody()
    {
        if (SkipWithoutSqlServer()) return;

        await SeedOpeningStockWacAsync(openingQty: 10m, unitCost: 10m);
        var (setupSvc, _) = CreateLiveService();
        await EnableLivePolicyAsync(setupSvc);

        var d1 = await SaveDispatchDraftAsync(setupSvc, qty: 6m);
        var refSvc = CreateInventoryRefService();
        var subconWh = await refSvc.GetWarehouseAsync("SUBCON");
        Assert.True(subconWh.Succeeded, subconWh.Message);

        var (subconSvc, _) = CreateLiveService();
        var refSvc2 = CreateInventoryRefService();

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tPost = Task.Run(async () =>
        {
            await barrier.Task;
            return await subconSvc.PostDispatchAsync(new SubconKeyCommand { DocNo = d1.DocNo, RowVersion = d1.RowVersion, RequestId = Guid.NewGuid() });
        });
        var tDemote = Task.Run(async () =>
        {
            await barrier.Task;
            return await refSvc2.SaveWarehouseAsync(new IvWarehouseEditVm
            {
                Code = "SUBCON",
                Desc = subconWh.Data!.Desc,
                WarehouseType = "NORMAL",
                IsActive = true,
                RowVersion = subconWh.Data.RowVersion
            }, isNew: false);
        });
        barrier.SetResult();
        var postRes = await tPost;
        var demoteRes = await tDemote;

        // Because SUBCON is referenced by active SubconPolicy (and/or receives stock), demotion MUST fail while post succeeds!
        Assert.True(postRes.Succeeded, postRes.ErrorMessage);
        Assert.False(demoteRes.Succeeded);

        await using var db = await _factory!.CreateDbContextAsync();
        var whType = await db.IvWarehouses
            .Where(w => w.CompanyCode == _company && w.BranchCode == Branch && w.WarehouseCode == "SUBCON")
            .Select(w => w.WarehouseType)
            .SingleAsync();
        Assert.Equal(SubconCustodyConstants.CustodyWarehouseType, whType);
    }

    [Fact]
    public async Task H03_Crash_injection_rolls_back_atomically_and_clears_SESSION_CONTEXT_on_pooled_connection()
    {
        if (SkipWithoutSqlServer()) return;

        await SeedOpeningStockWacAsync(openingQty: 10m, unitCost: 10m);
        var (svc, adapter) = CreateLiveService();
        await EnableLivePolicyAsync(svc);

        var d1 = await SaveDispatchDraftAsync(svc, qty: 6m);
        adapter.TestHookAfterSubconLinkWrite = () => throw new InvalidOperationException("Simulated SQL Server crash before seal");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PostDispatchAsync(new SubconKeyCommand { DocNo = d1.DocNo, RowVersion = d1.RowVersion, RequestId = Guid.NewGuid() }));
        adapter.TestHookAfterSubconLinkWrite = null;

        await using var db = await _factory!.CreateDbContextAsync();
        Assert.Equal(10m, await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvBalLocs.CountAsync(b => b.CompanyCode == _company && b.WhCode == "SUBCON"));
        Assert.Equal(0, await db.SubconPostingLinks.CountAsync(x => x.CompanyCode == _company));
        Assert.Equal(0, await db.SubconCustodyAllocations.CountAsync(x => x.CompanyCode == _company));
        Assert.Equal(SubconStatuses.New, await db.SubconDispatches.Where(x => x.CompanyCode == _company && x.DocNo == d1.DocNo).Select(x => x.Status).SingleAsync());

        // Verify SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID') is NULL on a connection drawn from the SQL Server pool.
        await using var sqlConn = new SqlConnection(_connectionString);
        await sqlConn.OpenAsync();
        await using var cmd = sqlConn.CreateCommand();
        cmd.CommandText = "SELECT SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID')";
        var ctxVal = await cmd.ExecuteScalarAsync();
        Assert.True(ctxVal is null or DBNull, $"Expected NULL SESSION_CONTEXT after abort/rollback, got '{ctxVal}'.");
    }

    [Fact]
    public async Task H03_Rollback_timeout_after_commit_retry_with_same_RequestId_is_idempotent_on_SqlServer()
    {
        if (SkipWithoutSqlServer()) return;

        await SeedOpeningStockWacAsync(openingQty: 10m, unitCost: 10m);
        var (svc, _) = CreateLiveService();
        await EnableLivePolicyAsync(svc);

        var d1 = await PostDispatchAsync(svc, qty: 6m);
        var revReqId = Guid.NewGuid();

        var rb1 = await svc.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = d1.DocNo,
            RowVersion = d1.RowVersion,
            RequestId = revReqId
        });
        Assert.True(rb1.Succeeded, rb1.ErrorMessage);
        Assert.Equal(SubconStatuses.New, rb1.Dispatch!.Status);
        Assert.Equal(1, rb1.Dispatch.DocumentRevision);

        // Retry with the exact same RequestId and stale pre-rollback RowVersion on a fresh service/connection.
        var (svcRetry, _) = CreateLiveService();
        var rb2 = await svcRetry.RollbackDispatchAsync(new SubconKeyCommand
        {
            DocNo = d1.DocNo,
            RowVersion = d1.RowVersion,
            RequestId = revReqId
        });
        Assert.True(rb2.Succeeded, rb2.ErrorMessage);
        Assert.Equal(SubconStatuses.New, rb2.Dispatch!.Status);
        Assert.Equal(1, rb2.Dispatch.DocumentRevision);

        await using var db = await _factory!.CreateDbContextAsync();
        Assert.Equal(1, await db.SubconPostingLinks.CountAsync(x => x.CompanyCode == _company && x.DocNo == d1.DocNo && x.PostingRole == SubconPostingRoles.Reversal));
        Assert.Equal(10m, await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "MAIN").Select(b => b.StdQty).SingleAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Backdated_wac_custody_transfer_replay_is_atomic_and_serializes_duplicates(bool receiveBack, bool existingDestination)
    {
        if (SkipWithoutSqlServer()) return;
        await SeedOpeningStockWacAsync(100m, 5m, Today.AddDays(-10));
        var (svc, adapter) = CreateLiveService(enableBackdatedReplay: true);
        await EnableLivePolicyAsync(svc);
        var previous = receiveBack || existingDestination
            ? await PostDispatchAsync(svc, receiveBack ? 20m : 1m, Today.AddDays(-8)) : null;
        if (!receiveBack && previous is not null)
        {
            var undone = await svc.RollbackDispatchAsync(new SubconKeyCommand { DocNo = previous.DocNo, RowVersion = previous.RowVersion });
            Assert.True(undone.Succeeded, undone.ErrorMessage);
        }
        int mainId;
        await using (var db = await _factory!.CreateDbContextAsync())
            mainId = await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "MAIN").Select(b => b.Id).SingleAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory!, Today, _company, Branch);
        var issueDraft = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = Today,
            Lines = [new IvMiscIssueLineRequest { ICode = "RM1", FromBalLocId = mainId,
                FrWarehouse = "MAIN", FrLocation = "BIN1", IStatus = "ACTIVE", Quantity = 10m, Uom = "PCS" }]
        });
        Assert.True(issueDraft.Succeeded, issueDraft.ErrorMessage);
        var issuePost = await issue.PostAsync([issueDraft.BatchNo]);
        Assert.True(issuePost.Succeeded, issuePost.ErrorMessage);

        if (receiveBack && !existingDestination)
        {
            await using var db = await _factory!.CreateDbContextAsync();
            db.IvLocations.Add(new IvLocation { CompanyCode = _company, BranchCode = Branch, WarehouseCode = "MAIN",
                LocCode = "BIN2", LocDesc = "Bin 2", IsActive = true });
            await db.SaveChangesAsync();
        }
        var receipt = receiveBack ? await SaveReceiptDraftAsync(svc, previous!.DocNo, 10m, Today.AddDays(-5),
            existingDestination ? null : "BIN2") : null;
        var dispatch = !receiveBack ? await SaveDispatchDraftAsync(svc, 20m, Today.AddDays(-5)) : null;
        var docNo = receiveBack ? receipt!.DocNo : dispatch!.DocNo;
        var draftVersion = receiveBack ? receipt!.RowVersion : dispatch!.RowVersion;

        async Task<(decimal Main, decimal Custody, decimal Qty, decimal Value, int MainPiles, int CustodyPiles, int Roots, int Facts, int Links, int Allocations, string Status)> SnapshotAsync()
        {
            await using var db = await _factory!.CreateDbContextAsync();
            var state = await db.StockCostStates.SingleAsync(s => s.CompanyCode == _company && s.ItemCode == "RM1");
            return (await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "MAIN").SumAsync(b => b.StdQty),
                await db.IvBalLocs.Where(b => b.CompanyCode == _company && b.WhCode == "SUBCON").Select(b => (decimal?)b.StdQty).SingleOrDefaultAsync() ?? 0m,
                state.OnHandBaseQty, state.InventoryValue,
                await db.IvBalLocs.CountAsync(b => b.CompanyCode == _company && b.WhCode == "MAIN"),
                await db.IvBalLocs.CountAsync(b => b.CompanyCode == _company && b.WhCode == "SUBCON"),
                await db.StockPostings.CountAsync(p => p.CompanyCode == _company),
                await db.StockValuationFacts.CountAsync(f => f.CompanyCode == _company),
                await db.SubconPostingLinks.CountAsync(l => l.CompanyCode == _company),
                await db.SubconCustodyAllocations.CountAsync(a => a.CompanyCode == _company),
                receiveBack ? await db.SubconReceipts.Where(r => r.CompanyCode == _company && r.DocNo == docNo).Select(r => r.Status).SingleAsync()
                    : await db.SubconDispatches.Where(d => d.CompanyCode == _company && d.DocNo == docNo).Select(d => d.Status).SingleAsync());
        }
        async Task AssertStateAsync(decimal main, decimal custody, decimal outstanding)
        {
            await using var db = await _factory!.CreateDbContextAsync();
            var snapshot = await SnapshotAsync();
            Assert.Equal((main, custody, 90m, 450m), (snapshot.Main, snapshot.Custody, snapshot.Qty, snapshot.Value));
            if (receiveBack && !existingDestination)
                Assert.Equal(main == 80m ? 10m : 0m, await db.IvBalLocs.Where(b => b.CompanyCode == _company
                    && b.WhCode == "MAIN" && b.LocCode == "BIN2").Select(b => b.StdQty).SingleAsync());
            var allocations = await db.SubconCustodyAllocations.Where(a => a.CompanyCode == _company && a.IsActive).ToArrayAsync();
            Assert.Equal(outstanding, allocations.Sum(a => a.DispatchedBaseQty - a.ReturnedBaseQty - a.ConsumedBaseQty - a.ScrapBaseQty));
            var history = await db.IvTrxHistories.SingleAsync(h => h.CompanyCode == _company && h.BatchNo == issueDraft.BatchNo && h.EntryRole == "ORIGINAL");
            var facts = await StockReplayValuationGenerationResolver.ResolveAsync(db, _company, Branch, [history]);
            Assert.Equal(50m, facts[history.Id].Sum(f => f.CostAmount));
        }
        async Task AssertSessionClearedAsync()
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID')";
            Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
        }
        Task<SubconResult> PostAsync(SubconApplicationService service, SubconKeyCommand command) =>
            receiveBack ? service.PostReceiptAsync(command) : service.PostDispatchAsync(command);
        Task<SubconResult> RollbackAsync(SubconApplicationService service, SubconKeyCommand command) =>
            receiveBack ? service.RollbackReceiptAsync(command) : service.RollbackDispatchAsync(command);

        var postRequest = Guid.NewGuid();
        var beforePost = await SnapshotAsync();
        adapter.TestHookAfterSubconLinkWrite = () => throw new InvalidOperationException("Replay failure before seal");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PostAsync(svc,
                new SubconKeyCommand { DocNo = docNo, RowVersion = draftVersion, RequestId = postRequest }));
        }
        finally { adapter.TestHookAfterSubconLinkWrite = null; }
        Assert.Equal(beforePost, await SnapshotAsync());
        await AssertSessionClearedAsync();

        var (svc1, _) = CreateLiveService(true);
        var (svc2, _) = CreateLiveService(true);
        var posts = await RunSimultaneouslyAsync(
            () => PostAsync(svc1, new SubconKeyCommand { DocNo = docNo, RowVersion = draftVersion, RequestId = postRequest }),
            () => PostAsync(svc2, new SubconKeyCommand { DocNo = docNo, RowVersion = draftVersion, RequestId = Guid.NewGuid() }))
            .WaitAsync(TimeSpan.FromSeconds(45));
        var winner = Assert.Single(posts, r => r.Succeeded);
        Assert.Single(posts, r => !r.Succeeded);
        await AssertStateAsync(receiveBack ? 80m : 70m, receiveBack ? 10m : 20m, receiveBack ? 10m : 20m);
        var postedVersion = receiveBack ? winner.Receipt!.RowVersion : winner.Dispatch!.RowVersion;
        var rollbackRequest = Guid.NewGuid();
        var beforeRollback = await SnapshotAsync();
        adapter.TestHookAfterSubconLinkWrite = () => throw new InvalidOperationException("Replay reversal failure before seal");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RollbackAsync(svc,
                new SubconKeyCommand { DocNo = docNo, RowVersion = postedVersion, RequestId = rollbackRequest }));
        }
        finally { adapter.TestHookAfterSubconLinkWrite = null; }
        Assert.Equal(beforeRollback, await SnapshotAsync());
        await AssertSessionClearedAsync();

        var rollbacks = await RunSimultaneouslyAsync(
            () => RollbackAsync(svc1, new SubconKeyCommand { DocNo = docNo, RowVersion = postedVersion, RequestId = rollbackRequest }),
            () => RollbackAsync(svc2, new SubconKeyCommand { DocNo = docNo, RowVersion = postedVersion, RequestId = Guid.NewGuid() }))
            .WaitAsync(TimeSpan.FromSeconds(45));
        var rolled = Assert.Single(rollbacks, r => r.Succeeded);
        Assert.Single(rollbacks, r => !r.Succeeded);
        await AssertStateAsync(receiveBack ? 70m : 90m, receiveBack ? 20m : 0m, receiveBack ? 20m : 0m);
        var rolledVersion = receiveBack ? rolled.Receipt!.RowVersion : rolled.Dispatch!.RowVersion;
        var reposted = await PostAsync(svc, new SubconKeyCommand { DocNo = docNo, RowVersion = rolledVersion });
        Assert.True(reposted.Succeeded, reposted.ErrorMessage);
        var repostedVersion = receiveBack ? reposted.Receipt!.RowVersion : reposted.Dispatch!.RowVersion;
        var rerolled = await RollbackAsync(svc, new SubconKeyCommand { DocNo = docNo, RowVersion = repostedVersion });
        Assert.True(rerolled.Succeeded, rerolled.ErrorMessage);
        await AssertStateAsync(receiveBack ? 70m : 90m, receiveBack ? 20m : 0m, receiveBack ? 20m : 0m);
        await AssertSessionClearedAsync();
    }

    private async Task SeedOpeningStockWacAsync(decimal openingQty, decimal unitCost, DateTime? openingDate = null)
    {
        var date = openingDate ?? Today.AddDays(-1);
        await using var db = await _factory!.CreateDbContextAsync();
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = _company,
            BranchCode = Branch,
            ICode = "RM1",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = openingQty,
            StdUom = "PCS",
            TransDate = date,
            Cost = unitCost,
            UnitPrice = unitCost
        });
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db, _company, Branch);
        await InventoryLedgerTestFixture.SeedCostStateAsync(db, "RM1", openingQty, unitCost, StockCostMethods.MovingAverage, _company, Branch);
        await InventoryLedgerTestFixture.SeedOpeningValuationFactAsync(db, "RM1", openingQty, unitCost, date, StockCostMethods.MovingAverage, _company, Branch);
    }

    private async Task EnableLivePolicyAsync(SubconApplicationService svc)
    {
        var res = await svc.SavePolicyAsync(new SubconPolicySaveCommand
        {
            SubconEnabled = true,
            InventoryModeEnabled = true,
            InventoryCustodyPostEnabled = true,
            DefaultCustodyWarehouseCode = "SUBCON"
        });
        Assert.True(res.Succeeded, res.ErrorMessage);
    }

    private async Task<SubconDispatchDto> SaveDispatchDraftAsync(SubconApplicationService svc, decimal qty, DateTime? date = null)
    {
        var saved = await svc.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            TrxDate = date ?? Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            Lines = [new SubconDispatchLineSaveCommand { LineNo = 1, ItemCode = "RM1", Qty = qty, WarehouseCode = "MAIN", LocationCode = "BIN1", Uom = "PCS" }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        return saved.Dispatch!;
    }

    private async Task<SubconDispatchDto> PostDispatchAsync(SubconApplicationService svc, decimal qty, DateTime? date = null)
    {
        var draft = await SaveDispatchDraftAsync(svc, qty, date);
        var posted = await svc.PostDispatchAsync(new SubconKeyCommand { DocNo = draft.DocNo, RowVersion = draft.RowVersion, RequestId = Guid.NewGuid() });
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        return posted.Dispatch!;
    }

    private async Task<SubconReceiptDto> SaveReceiptDraftAsync(SubconApplicationService svc, string dispatchDocNo, decimal qty,
        DateTime? date = null, string? toLocation = null)
    {
        var saved = await svc.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            TrxDate = date ?? Today,
            SupplierCode = "SUP01",
            Lines = [new SubconReceiptLineSaveCommand { LineNo = 1, DispatchDocNo = dispatchDocNo, DispatchLineNo = 1, Qty = qty,
                ToWarehouseCode = toLocation is null ? null : "MAIN", ToLocationCode = toLocation }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        return saved.Receipt!;
    }

    private async Task<(string R1Doc, byte[] R1Rv, string R2Doc, byte[] R2Rv)> SeedCompetingReceiptDraftsAsync(string dispatchDocNo, decimal qtyEach)
    {
        await using var db = await _factory!.CreateDbContextAsync();
        var dispatch = await db.SubconDispatches.Include(x => x.Lines)
            .SingleAsync(x => x.CompanyCode == _company && x.BranchCode == Branch && x.DocNo == dispatchDocNo);
        var dLine = dispatch.Lines.Single();

        var r1 = new SubconReceipt
        {
            CompanyCode = _company,
            BranchCode = Branch,
            DocNo = $"SR-{Guid.NewGuid():N}"[..15],
            TrxDate = Today,
            Status = SubconStatuses.New,
            SupplierCode = "SUP01",
            SupplierName = "Subcon Vendor",
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester",
            Lines = [new SubconReceiptLine { LineNo = 1, DispatchId = dispatch.Uid, DispatchLineId = dLine.Uid, Qty = qtyEach, Uom = "PCS" }]
        };
        var r2 = new SubconReceipt
        {
            CompanyCode = _company,
            BranchCode = Branch,
            DocNo = $"SR-{Guid.NewGuid():N}"[..15],
            TrxDate = Today,
            Status = SubconStatuses.New,
            SupplierCode = "SUP01",
            SupplierName = "Subcon Vendor",
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester",
            Lines = [new SubconReceiptLine { LineNo = 1, DispatchId = dispatch.Uid, DispatchLineId = dLine.Uid, Qty = qtyEach, Uom = "PCS" }]
        };
        db.SubconReceipts.AddRange(r1, r2);
        await db.SaveChangesAsync();
        return (r1.DocNo, r1.RowVersion, r2.DocNo, r2.RowVersion);
    }

    private static async Task<T[]> RunSimultaneouslyAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t1 = Task.Run(async () =>
        {
            await barrier.Task;
            return await first();
        });
        var t2 = Task.Run(async () =>
        {
            await barrier.Task;
            return await second();
        });
        barrier.SetResult();
        return await Task.WhenAll(t1, t2);
    }

    private (Mock<IInventoryTenantContext> Tenant, Mock<IAccessRightService> Access) CreateTenantAndAccess()
    {
        var scope = new InventoryTenantScope
        {
            CompanyCode = _company,
            BranchCode = Branch,
            LocationCode = Branch,
            UserId = "tester"
        };
        var tenant = new Mock<IInventoryTenantContext>();
        tenant.Setup(x => x.TryCompanyScope()).Returns(scope);
        tenant.Setup(x => x.TryBranchScope()).Returns(scope);
        tenant.Setup(x => x.TryWriteScope()).Returns(scope);

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return (tenant, access);
    }

    private SubconApplicationService CreateDraftService()
    {
        var (tenant, access) = CreateTenantAndAccess();
        return new SubconApplicationService(
            _factory!,
            tenant.Object,
            access.Object,
            new RunningNumberService(),
            new SubconBlockedInventorySourceAdapter(),
            new SubconBlockedProductionSourceAdapter());
    }

    private (SubconApplicationService Service, SubconInventoryTransferAdapter Adapter) CreateLiveService(bool enableBackdatedReplay = false)
    {
        var (tenant, access) = CreateTenantAndAccess();
        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory!, _company, Branch, access.Object,
            enableBackdatedReplay: enableBackdatedReplay);
        var numbers = new RunningNumberService();
        var adapter = new SubconInventoryTransferAdapter(
            _factory!, posting, numbers, new IvUomConversionService(_factory!), new InventorySoftReservationReader());
        var svc = new SubconApplicationService(
            _factory!, tenant.Object, access.Object, numbers, adapter, new SubconBlockedProductionSourceAdapter());
        return (svc, adapter);
    }

    private IvInventoryRefService CreateInventoryRefService()
    {
        var (tenant, access) = CreateTenantAndAccess();
        var lookups = new Mock<IAdLookupService>();
        lookups.Setup(x => x.ValidateAssignmentAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new IvInventoryRefService(
            _factory!,
            tenant.Object,
            access.Object,
            new FixedCurrentDateService(Today),
            new IvStockCommonRepository(_factory!),
            lookups.Object);
    }

    private bool SkipWithoutSqlServer()
    {
        if (_factory is not null)
        {
            return false;
        }

        if (RequireSqlServer)
        {
            Assert.Fail("ERPWEB_REQUIRE_SQLSERVER_TESTS=1 is set, but SqlServerTestConnection is unavailable or not a scratch test DB.");
        }

        return true;
    }

    private sealed class SqlServerTestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString(TestConnectionKey);
        return string.IsNullOrWhiteSpace(cs) ? null : cs;
    }

    private static bool IsScratchDatabase(string connectionString)
    {
        try
        {
            var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            return name.Contains("test", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanConnect(string connectionString)
    {
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
            using var db = new AppDbContext(options);
            return db.Database.IsSqlServer() && db.Database.CanConnect();
        }
        catch
        {
            return false;
        }
    }
}

[CollectionDefinition("SubconSqlServer", DisableParallelization = true)]
public sealed class SubconSqlServerCollectionDefinition;
