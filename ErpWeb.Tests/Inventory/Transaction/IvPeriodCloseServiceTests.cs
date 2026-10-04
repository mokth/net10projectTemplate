using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Inventory.Transaction;
/// <summary>
/// Close / reopen workflow and the stored-snapshot generation (plan-inventoryPeriodClose Phase 2).
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPeriodClose)]
public class IvPeriodCloseServiceTests : IAsyncLifetime
{
    private static readonly DateTime Today = new(2026, 9, 24);
    private static readonly DateTime JulyFrom = new(2026, 7, 1);
    private static readonly DateTime JulyTo = new(2026, 7, 31);
    private static readonly DateTime AugustFrom = new(2026, 8, 1);
    private static readonly DateTime AugustTo = new(2026, 8, 31);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvPeriodCloseServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvWarehouses.Add(new IvWarehouse { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true });
        db.IvLocations.Add(new IvLocation { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", LocCode = "BIN1", IsActive = true });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = 5m
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ── First close establishes the baseline ─────────────────────────────────────────────────────

    [Fact]
    public async Task First_close_accepts_any_start_and_records_the_plug()
    {
        await SeedPileAsync("A100", 100m);

        var result = await CreateService().CloseAsync(new IvPeriodCloseRequest
        {
            PeriodFrom = JulyFrom,
            PeriodTo = JulyTo
        });

        Assert.True(result.Succeeded, result.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var header = await db.IvPeriodCloseHdrs.SingleAsync();
        Assert.Equal(IvPeriodCloseStatuses.Closed, header.Status);
        Assert.Equal(1, header.LineCount);
        Assert.Equal(1, header.OpeningAdjustSlices);
        Assert.Equal(0, header.CarryForwardMismatchSlices);
        Assert.Equal(0, header.SkippedZeroSlices);

        var line = await db.IvPeriodCloseBals.SingleAsync();
        Assert.Equal(0m, line.OpeningQty);
        Assert.Equal(100m, line.OpeningAdjustQty);
        Assert.Equal(0m, line.InQty);
        Assert.Equal(0m, line.OutQty);
        Assert.Equal(100m, line.ClosingQty);
        Assert.True(line.CarryForwardOk);
    }

    [Fact]
    public async Task First_close_invariant_closing_equals_opening_plus_plug_plus_in_minus_out()
    {
        // Pile 100 with a July MR of 100: ledger agrees, so no plug is needed.
        var balId = await SeedPileAsync("A100", 100m);
        await SeedHistoryAsync(balId, trxType: IvTrxTypes.MiscellaneousReceipt, date: new DateTime(2026, 7, 15), inQty: 100m);

        var result = await CreateService().CloseAsync(new IvPeriodCloseRequest
        {
            PeriodFrom = JulyFrom,
            PeriodTo = JulyTo
        });

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.IvPeriodCloseBals.SingleAsync();
        Assert.Equal(0m, line.OpeningQty);
        Assert.Equal(0m, line.OpeningAdjustQty);
        Assert.Equal(100m, line.InQty);
        Assert.Equal(0m, line.OutQty);
        Assert.Equal(100m, line.ClosingQty);
        // Invariant: ClosingQty = OpeningQty + OpeningAdjustQty + InQty − OutQty
        Assert.Equal(
            line.OpeningQty + line.OpeningAdjustQty + line.InQty - line.OutQty,
            line.ClosingQty);
    }

    // ── Sequential / contiguous rule (D6) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Second_close_must_be_contiguous()
    {
        await SeedPileAsync("A100", 0m);
        var svc = CreateService();
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);

        // Skipping August: closing a period that starts mid-August must be refused, naming the expected start.
        var result = await svc.CloseAsync(new IvPeriodCloseRequest
        {
            PeriodFrom = new DateTime(2026, 8, 15),
            PeriodTo = new DateTime(2026, 8, 31)
        });

        Assert.False(result.Succeeded);
        Assert.Contains("2026-08-01", result.ErrorMessage);
    }

    [Fact]
    public async Task Duplicate_period_refused()
    {
        await SeedPileAsync("A100", 0m);
        var svc = CreateService();
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);

        var again = await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo });
        Assert.False(again.Succeeded);
        Assert.Contains("already been closed", again.ErrorMessage);
    }

    [Fact]
    public async Task Future_period_refused()
    {
        await SeedPileAsync("A100", 0m);
        var result = await CreateService().CloseAsync(new IvPeriodCloseRequest
        {
            PeriodFrom = new DateTime(2026, 10, 1),
            PeriodTo = new DateTime(2026, 10, 31)
        });

        Assert.False(result.Succeeded);
        Assert.Contains("future", result.ErrorMessage);
    }

    // ── Carry-forward and D11 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Carry_forward_matches_across_two_closes()
    {
        var balId = await SeedPileAsync("A100", 100m);
        await SeedHistoryAsync(balId, trxType: IvTrxTypes.MiscellaneousReceipt, date: new DateTime(2026, 7, 15), inQty: 100m);

        var svc = CreateService();
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);

        var august = await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugustFrom, PeriodTo = AugustTo });
        Assert.True(august.Succeeded, august.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var augustLine = await db.IvPeriodCloseBals
            .Where(x => x.Header.PeriodFrom == AugustFrom)
            .SingleAsync();
        Assert.Equal(100m, augustLine.OpeningQty);
        Assert.Equal(0m, augustLine.OpeningAdjustQty);
        Assert.Equal(100m, augustLine.ClosingQty);
        Assert.True(augustLine.CarryForwardOk);
    }

    [Fact]
    public async Task Carry_forward_mismatch_refuses()
    {
        var balId = await SeedPileAsync("A100", 100m);
        await SeedHistoryAsync(balId, trxType: IvTrxTypes.MiscellaneousReceipt, date: new DateTime(2026, 7, 15), inQty: 100m);

        var svc = CreateService();
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);

        // Simulate a mutated July history: an extra +50 leg dated in July, pile bumped to match.
        await SeedHistoryAsync(balId, trxType: IvTrxTypes.MiscellaneousReceipt, date: new DateTime(2026, 7, 20), inQty: 50m, batchNo: 2, lineNo: 2);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
            bal.StdQty = 150m;
            await db.SaveChangesAsync();
        }

        var august = await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugustFrom, PeriodTo = AugustTo });
        Assert.False(august.Succeeded);
        Assert.Contains("carry-forward", august.ErrorMessage);
    }

    [Fact]
    public async Task D11_pile_ledger_divergence_refuses_on_a_later_close()
    {
        var balId = await SeedPileAsync("A100", 100m);
        await SeedHistoryAsync(balId, trxType: IvTrxTypes.MiscellaneousReceipt, date: new DateTime(2026, 7, 15), inQty: 100m);

        var svc = CreateService();
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);

        // Pile now disagrees with the ledger (still says July 100) while the pile was edited to 95.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
            bal.StdQty = 95m;
            await db.SaveChangesAsync();
        }

        // Reconciliation must report the MISMATCH (pile 95 vs ledger net 100) and block the close.
        var august = await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugustFrom, PeriodTo = AugustTo });
        Assert.False(august.Succeeded);
    }

    // ── D4 blocking batches and D9 findings ───────────────────────────────────────────────────────

    [Fact]
    public async Task New_batch_dated_inside_the_period_blocks_and_is_listed()
    {
        await SeedPileAsync("A100", 0m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 77,
                TrxDtTime = new DateTime(2026, 7, 10),
                TrxType = IvTrxTypes.MiscellaneousReceipt,
                BatchStatus = IvBatchStatuses.New,
                RefNo = "G77"
            });
            await db.SaveChangesAsync();
        }

        var result = await CreateService().CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo });
        Assert.False(result.Succeeded);
        Assert.Contains("77", result.ErrorMessage);
    }

    [Fact]
    public async Task Mismatch_blocks_the_close()
    {
        var balId = await SeedPileAsync("A100", 100m);
        await SeedHistoryAsync(balId, trxType: IvTrxTypes.MiscellaneousReceipt, date: new DateTime(2026, 7, 15), inQty: 90m);

        var result = await CreateService().CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo });
        Assert.False(result.Succeeded);
        Assert.Contains("reconciliation finding", result.ErrorMessage);
    }

    [Fact]
    public async Task History_slice_mismatch_blocks_the_close()
    {
        var balId = await SeedPileAsync("A100", 100m);
        await SeedHistoryAsync(
            balId,
            trxType: IvTrxTypes.MiscellaneousReceipt,
            date: new DateTime(2026, 7, 15),
            inQty: 100m,
            toLoc: "BIN2");

        var result = await CreateService().CloseAsync(
            new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo });

        Assert.False(result.Succeeded);
        Assert.Contains("HISTORY_SLICE_MISMATCH", result.ErrorMessage);
    }

    // ── Reopen ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reopen_clears_lines_and_reclose_regenerates()
    {
        await SeedPileAsync("A100", 100m);
        var svc = CreateService();
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);

        var reopen = await svc.ReopenAsync(new IvPeriodCloseRequest
        {
            Id = 1,
            RowVersion = string.Empty,
            ReopenReason = "Wrong period"
        });
        Assert.True(reopen.Succeeded, reopen.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.IvPeriodCloseHdrs.SingleAsync();
            Assert.Equal(IvPeriodCloseStatuses.Reopened, header.Status);
            Assert.Equal(1, header.ReopenCount);
            Assert.Equal(1, header.LastReopenLineCount);
            Assert.Equal(0, await db.IvPeriodCloseBals.CountAsync());
        }

        // Re-close regenerates the snapshot on the same header row.
        var reclose = await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo });
        Assert.True(reclose.Succeeded, reclose.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.IvPeriodCloseHdrs.SingleAsync();
            Assert.Equal(IvPeriodCloseStatuses.Closed, header.Status);
            Assert.Equal(1, await db.IvPeriodCloseBals.CountAsync());
        }
    }

    [Fact]
    public async Task Reopen_requires_a_reason()
    {
        await SeedPileAsync("A100", 100m);
        var svc = CreateService();
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);

        var reopen = await svc.ReopenAsync(new IvPeriodCloseRequest { Id = 1, RowVersion = string.Empty });
        Assert.False(reopen.Succeeded);
        Assert.Contains("reason", reopen.ErrorMessage);
    }

    [Fact]
    public async Task Reopen_out_of_order_refused()
    {
        await SeedPileAsync("A100", 0m);
        var svc = CreateService();
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugustFrom, PeriodTo = AugustTo })).Succeeded);

        var reopen = await svc.ReopenAsync(new IvPeriodCloseRequest { Id = 1, RowVersion = string.Empty, ReopenReason = "Wrong" });
        Assert.False(reopen.Succeeded);
        Assert.Contains("later period", reopen.ErrorMessage);
    }

    // ── Menu gating and tenant isolation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Inquiry_requires_the_inquiry_menu_not_the_action_menu()
    {
        await SeedPileAsync("A100", 100m);
        var svc = CreateService(denyInquiry: true);
        Assert.True((await svc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo })).Succeeded);

        var inquiry = await svc.InquiryAsync(1);
        Assert.False(inquiry.Succeeded);
        Assert.Contains("Not authorized", inquiry.ErrorMessage);
    }

    [Fact]
    public async Task Tenant_isolation_another_company_pile_is_excluded()
    {
        await SeedPileAsync("A100", 100m);
        await using (var seedDb = await _factory.CreateDbContextAsync())
        {
            seedDb.IvWarehouses.Add(new IvWarehouse { CompanyCode = "OTHER", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true });
            seedDb.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "OTHER",
                ICode = "X",
                IClassCode = "RAW",
                StdUom = "EA",
                StockControl = true,
                IsActive = true
            });
            await seedDb.SaveChangesAsync();
        }

        var result = await CreateService().CloseAsync(new IvPeriodCloseRequest { PeriodFrom = JulyFrom, PeriodTo = JulyTo });
        Assert.True(result.Succeeded, result.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var lines = await db.IvPeriodCloseBals.ToListAsync();
        Assert.All(lines, l => Assert.Equal("A100", l.ICode));
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────

    private IvPeriodCloseService CreateService(bool denyInquiry = false)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        if (denyInquiry)
        {
            access.Setup(x => x.CanAsync(MenuCodes.InventoryPeriodCloseInq, PermissionCodes.Access, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        }

        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var reconciliation = new IvInventoryReconciliationService(_factory, tenant, access.Object);

        return new IvPeriodCloseService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(Today),
            reconciliation,
            NullLogger<IvPeriodCloseService>.Instance);
    }

    private async Task<int> SeedPileAsync(string iCode, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = iCode,
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA",
            TransDate = Today.AddDays(-1),
            UnitPrice = 5m
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private async Task SeedHistoryAsync(
        int balLocId,
        string trxType,
        DateTime date,
        decimal inQty,
        int batchNo = 1,
        short lineNo = 1,
        string toLoc = "BIN1")
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvTrxHistories.Add(new IvTrxHistory
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = batchNo,
            TrxLineNo = lineNo,
            TrxDtTime = date,
            TrxType = trxType,
            BatchStatus = IvBatchStatuses.Posted,
            ICode = "A100",
            ToBalLocId = balLocId,
            ToWarehouse = "MAIN",
            ToLocation = toLoc,
            ToStdQty = inQty,
            ToStdUom = "EA",
            IStatus = "ACTIVE"
        });
        await db.SaveChangesAsync();
    }
}
