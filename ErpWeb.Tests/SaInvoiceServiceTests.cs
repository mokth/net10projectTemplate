using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

public class SaInvoiceCalcTests
{
    [Fact]
    public void Amount_rounds_away_from_zero()
    {
        var line = new SaInvoiceLineCalcState { Qty = 3m, UnitPrice = 1.005m };
        SaInvoiceCalc.CalculateLine(line, taxPercent: 0m, decPoint: false, discMethod: null);
        Assert.Equal(3.02m, line.Amount);
        Assert.Equal(3.02m, line.NetAmount);
        Assert.Equal(0m, line.TaxAmt);
    }

    [Fact]
    public void Sequential_percent_stack_then_amounts()
    {
        var disc = SaInvoiceCalc.CalculateDiscountPerUnit(
            100m, 10m, 5m, 0, 0, 0, 0, 1m, 0.5m, discMethod: null);
        Assert.Equal(16m, disc);
    }

    [Fact]
    public void Join_replaces_stack_and_ignores_split()
    {
        var join = SaInvoiceCalc.CalculateDiscountPerUnit(
            100m, 10m, 5m, 0, 0, 0, 0, 1m, 0.5m, SaCustPaymentOptions.DiscountJoin);
        Assert.Equal(16.5m, join);

        var split = SaInvoiceCalc.CalculateDiscountPerUnit(
            100m, 10m, 5m, 0, 0, 0, 0, 1m, 0.5m, SaCustPaymentOptions.DiscountSplit);
        Assert.Equal(16m, split);
    }

    [Fact]
    public void Header_excludes_excld_dis_from_gross_then_adds_back()
    {
        var lines = new List<SaInvoiceLineCalcState>
        {
            new() { NetAmount = 10m, TaxAmt = 1m, OrderType = null },
            new() { NetAmount = 4m, TaxAmt = 0m, OrderType = SaInvoiceCalc.ExcludedDiscountOrderType }
        };
        var header = SaInvoiceCalc.CalculateHeader(lines, decPoint: false);
        Assert.Equal(14m, header.GrossAmnt);
        Assert.Equal(1m, header.Taxes);
        Assert.Equal(15m, header.TotAmnt);
    }
}

public class SaInvoiceServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaInvoiceServiceTests()
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
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "USD", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "EUR", IsActive = true });
        db.IvMsCodes.Add(new IvMsCode { Code = "NET30", Name = "Net 30", CodeType = IvMsCodeTypes.PayCode });
        db.SaTaxGroups.Add(new SaTaxGroup { CompanyCode = "DEMO", TaxGrCode = "SR", TaxGrDesc = "Standard", Percentage = 6m });
        db.SaTaxGroups.Add(new SaTaxGroup { CompanyCode = "DEMO", TaxGrCode = "ZR", TaxGrDesc = "Zero", Percentage = 0m });
        db.SaCurrRates.Add(new SaCurrRate
        {
            CurrCode = "USD",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            HomeCurPerUnit = 4.2,
            Status = true
        });
        db.SaCurrRates.Add(new SaCurrRate
        {
            CurrCode = "USD",
            StartDate = new DateTime(2026, 8, 1),
            EndDate = new DateTime(2026, 9, 30),
            HomeCurPerUnit = 4.5,
            Status = true
        });
        db.SaCurrRates.Add(new SaCurrRate
        {
            CurrCode = "EUR",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            HomeCurPerUnit = 1.0,
            Status = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            IsActive = true,
            SellingPrice = 10m,
            SellingGlCode = "GLSALE",
            DefWarehouse = "MAIN"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "SVC1",
            IDesc = "Service",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = false,
            IsActive = true,
            SellingPrice = 50m,
            SellingGlCode = "GLSVC"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "PACK1",
            IDesc = "Packed item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            IsActive = true,
            SellingPrice = 8m,
            StdPackSize = 12m,
            SellingGlCode = "GLPACK",
            DefWarehouse = "MAIN"
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST01",
            CustName = "Alpha",
            Currency = "MYR",
            PayCode = "NET30",
            Address1 = "MAIN ADDR 1",
            Address4 = "MAIN ADDR 4",
            City = "MAIN CITY",
            InvAddress1 = "INV ADDR 1",
            InvCity = "INV CITY",
            ShipAddress1 = "SHIP ADDR 1",
            ShipCity = "SHIP CITY",
            IsActive = true,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST02",
            CustName = "Beta",
            Currency = "MYR",
            PayCode = "NET30",
            InvoicePrefix = "ACME",
            DiscountMethod = SaCustPaymentOptions.DiscountJoin,
            IsActive = true,
            RowVersion = [2, 0, 0, 0, 0, 0, 0, 0]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTAPP",
            CustName = "Apply Main",
            Currency = "MYR",
            PayCode = "NET30",
            AppInvoice = true,
            AppShip = true,
            Address1 = "MAIN BILL 1",
            Address4 = "MAIN BILL 4",
            City = "MAIN BILL CITY",
            Tel = "111",
            InvName = "INV NAME",
            InvAddress1 = "SPEC INV 1",
            ShipName = "SHIP NAME",
            ShipAddress1 = "SPEC SHIP 1",
            IsActive = true,
            RowVersion = [3, 0, 0, 0, 0, 0, 0, 0]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTSPEC",
            CustName = "Special Addr",
            Currency = "MYR",
            PayCode = "NET30",
            AppInvoice = false,
            AppShip = false,
            Address1 = "MAIN IGNORE",
            InvName = "Bill To Spec",
            InvAddress1 = "SPEC BILL 1",
            InvCity = "SPEC BILL CITY",
            ShipName = "Ship To Spec",
            ShipAddress1 = "SPEC SHIP 1",
            ShipCity = "SPEC SHIP CITY",
            IsActive = true,
            RowVersion = [4, 0, 0, 0, 0, 0, 0, 0]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTTAX",
            CustName = "Taxable Co",
            Currency = "MYR",
            PayCode = "NET30",
            Taxable = true,
            TaxGrCode = "SR",
            IsActive = true,
            RowVersion = [5, 0, 0, 0, 0, 0, 0, 0]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTDEC",
            CustName = "Dec Point",
            Currency = "MYR",
            PayCode = "NET30",
            DecPoint = true,
            IsActive = true,
            RowVersion = [6, 0, 0, 0, 0, 0, 0, 0]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTUSD",
            CustName = "Usd Buyer",
            Currency = "USD",
            PayCode = "NET30",
            IsActive = true,
            RowVersion = [7, 0, 0, 0, 0, 0, 0, 0]
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveNew_allocates_period_number_inside_transaction()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("INV2609-0001", save.InvNo);

        await using var db = await _factory.CreateDbContextAsync();
        var invoice = await db.SaInvoices.Include(x => x.Details).SingleAsync();
        Assert.Equal("INV2609-0001", invoice.DoNo);
        Assert.Equal("INV", invoice.InvPrefix);
        Assert.Equal("HQ", invoice.BranchCode);
        Assert.Equal("HQ", invoice.Details.Single().BranchCode);
        Assert.Equal(20m, invoice.TotAmnt);
        Assert.Equal(SaInvoiceStatuses.New, invoice.Status);
    }

    [Fact]
    public async Task SaveNew_ignores_customer_invoice_prefix()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, cust: "CUST02"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("INV2609-0001", save.InvNo);
        Assert.Equal("INV", save.Document!.InvPrefix);
    }

    [Fact]
    public async Task Mixed_inclusive_rejected()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            Lines =
            [
                Line("A100", 1m, 10m, inclusive: false),
                Line("A100", 1m, 10m, inclusive: true)
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("ST000032", save.ErrorMessage);
    }

    [Fact]
    public async Task AddShipment_fifo_order_and_repeatable()
    {
        var older = await SeedBalLocAsync(40m, transDate: new DateTime(2026, 8, 1), lot: "L1");
        var newer = await SeedBalLocAsync(40m, transDate: new DateTime(2026, 8, 15), lot: "L2");
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 50m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var ship = await ShipAsync(sut, save.InvNo!);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        Assert.Equal(2, ship.Document!.Shipment.Count);
        Assert.Equal(older, ship.Document.Shipment[0].FromBalLocId);
        Assert.Equal(40m, ship.Document.Shipment[0].FrStdQty);
        Assert.Equal(newer, ship.Document.Shipment[1].FromBalLocId);
        Assert.Equal(10m, ship.Document.Shipment[1].FrStdQty);

        var ship2 = await ShipAsync(sut, save.InvNo!, overwriteExisting: true);
        Assert.True(ship2.Succeeded, ship2.ErrorMessage);
        Assert.Equal(
            ship.Document.Shipment.Select(x => x.FromBalLocId).ToArray(),
            ship2.Document!.Shipment.Select(x => x.FromBalLocId).ToArray());

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(40m, await db.IvBalLocs.Where(x => x.Id == older).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, await db.IvTrxBatches.CountAsync(x => x.TrxType == IvTrxTypes.SalesOut));
    }

    [Fact]
    public async Task AddShipment_unstamped_LocationCode_and_same_day_TransDate_time_are_eligible()
    {
        var balId = await SeedBalLocAsync(
            25m,
            transDate: FixedToday.AddHours(16),
            locationCode: null);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var ship = await ShipAsync(sut, save.InvNo!);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        Assert.True(ship.Document!.ShipmentComplete);
        Assert.Equal(balId, ship.Document.Shipment.Single().FromBalLocId);
        Assert.Equal(10m, ship.Document.Shipment.Single().FrStdQty);
    }

    [Fact]
    public async Task GetShipmentEdit_includes_unallocated_eligible_piles()
    {
        var older = await SeedBalLocAsync(10m, transDate: FixedToday.AddDays(-2), lot: "L-OLD");
        var newer = await SeedBalLocAsync(10m, transDate: FixedToday.AddDays(-1), lot: "L-NEW");
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 6m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        var edit = await sut.GetShipmentEditAsync(save.InvNo!, 1);
        Assert.True(edit.Succeeded, edit.ErrorMessage);
        Assert.Equal(2, edit.Document!.Shipment.Count);
        var oldRow = Assert.Single(edit.Document.Shipment, x => x.FromBalLocId == older);
        var newRow = Assert.Single(edit.Document.Shipment, x => x.FromBalLocId == newer);
        Assert.Equal(6m, oldRow.FrStdQty);
        Assert.Equal(0m, newRow.FrStdQty);
        Assert.Equal(10m, oldRow.CurrentAvailableQty);
        Assert.Equal(10m, newRow.CurrentAvailableQty);
    }

    [Fact]
    public async Task Identity_edit_deletes_sp_details_date_change_does_not()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        var dateOnly = await UpdateDocAsync(sut, save.InvNo!, Request(qty: 10m, price: 10m, date: FixedToday.AddDays(1)));
        Assert.True(dateOnly.Succeeded, dateOnly.ErrorMessage);
        var afterDate = await sut.GetAsync(save.InvNo!);
        Assert.NotEmpty(afterDate.Document!.Shipment);

        var qtyChange = await UpdateDocAsync(sut, save.InvNo!, Request(qty: 12m, price: 10m, date: FixedToday.AddDays(1)));
        Assert.True(qtyChange.Succeeded, qtyChange.ErrorMessage);
        var afterQty = await sut.GetAsync(save.InvNo!);
        Assert.Empty(afterQty.Document!.Shipment);
    }

    [Fact]
    public async Task Post_requires_add_shipment_after_date_change()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);
        Assert.True((await UpdateDocAsync(sut, save.InvNo!, Request(qty: 10m, price: 10m, date: FixedToday.AddDays(1)))).Succeeded);

        var post = await sut.PostAsync([save.InvNo!]);
        Assert.False(post.Succeeded);
        Assert.Contains("add shipment", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task End_to_end_create_ship_post_rollback()
    {
        var balId = await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 30m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        var post = await sut.PostAsync([save.InvNo!]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(SaInvoiceStatuses.Posted, (await db.SaInvoices.SingleAsync()).Status);
            Assert.Equal(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync(x => x.TrxType == IvTrxTypes.SalesOut)).BatchStatus);
            Assert.Equal(1, await db.IvTrxHistories.CountAsync());
        }

        var rollback = await sut.RollbackAsync([save.InvNo!]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(SaInvoiceStatuses.New, (await db.SaInvoices.SingleAsync()).Status);
            Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(x => x.TrxType == IvTrxTypes.SalesOut)).BatchStatus);
            Assert.Equal(0, await db.IvTrxHistories.CountAsync());
            Assert.Equal(balId, await db.IvTrxBatchDetails.Select(x => x.FromBalLocId).SingleAsync());
        }
    }

    [Fact]
    public async Task Service_only_invoice_posts_without_sp()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            Lines = [Line("SVC1", 1m, 50m)]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await sut.PostAsync([save.InvNo!]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvTrxBatches.CountAsync());
        Assert.Equal(SaInvoiceStatuses.Posted, (await db.SaInvoices.SingleAsync()).Status);
    }

    [Fact]
    public async Task Post_rejects_four_and_stops_after_first_failure()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var a = await sut.SaveNewAsync(Request(qty: 1m, price: 10m));
        var b = await sut.SaveNewAsync(Request(qty: 1m, price: 10m));
        var c = await sut.SaveNewAsync(Request(qty: 1m, price: 10m));
        var d = await sut.SaveNewAsync(Request(qty: 1m, price: 10m));
        Assert.True((await ShipAsync(sut, a.InvNo!)).Succeeded);

        var four = await sut.PostAsync([a.InvNo!, b.InvNo!, c.InvNo!, d.InvNo!]);
        Assert.False(four.Succeeded);
        Assert.Contains("at most 3", four.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var three = await sut.PostAsync([a.InvNo!, b.InvNo!, c.InvNo!]);
        Assert.False(three.Succeeded);
        Assert.Equal(3, three.Posting.Count);
        Assert.Equal("Posted", three.Posting[0].Outcome);
        Assert.StartsWith("Failed:", three.Posting[1].Outcome);
        Assert.Equal("Not attempted", three.Posting[2].Outcome);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(SaInvoiceStatuses.Posted, (await db.SaInvoices.SingleAsync(x => x.InvNo == a.InvNo)).Status);
        Assert.Equal(SaInvoiceStatuses.New, (await db.SaInvoices.SingleAsync(x => x.InvNo == b.InvNo)).Status);
        Assert.Equal(SaInvoiceStatuses.New, (await db.SaInvoices.SingleAsync(x => x.InvNo == c.InvNo)).Status);
    }

    [Fact]
    public async Task Fault_post_stock_update_rolls_back_invoice_and_stock()
    {
        var balId = await SeedBalLocAsync(100m);
        var posting = CreatePosting();
        var sut = CreateSut(posting);
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        posting.TestHookAfterMiStockUpdate = () => throw new InvalidOperationException("forced after stock");
        var post = await sut.PostAsync([save.InvNo!]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(SaInvoiceStatuses.New, (await db.SaInvoices.SingleAsync()).Status);
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Fault_before_invoice_status_rolls_back()
    {
        var balId = await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        sut.TestHookAfterStockCore = () => throw new InvalidOperationException("forced before status");
        var post = await sut.PostAsync([save.InvNo!]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(SaInvoiceStatuses.New, (await db.SaInvoices.SingleAsync()).Status);
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Fault_after_invoice_status_before_commit_rolls_back()
    {
        var balId = await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        sut.TestHookAfterInvoiceStatus = () => throw new InvalidOperationException("forced during status");
        var post = await sut.PostAsync([save.InvNo!]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(SaInvoiceStatuses.New, (await db.SaInvoices.SingleAsync()).Status);
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Fault_rollback_stock_and_status_hooks()
    {
        var balId = await SeedBalLocAsync(100m);
        var posting = CreatePosting();
        var sut = CreateSut(posting);
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);
        Assert.True((await sut.PostAsync([save.InvNo!])).Succeeded);

        posting.TestHookAfterMiRollbackStock = () => throw new InvalidOperationException("forced rollback stock");
        var rb1 = await sut.RollbackAsync([save.InvNo!]);
        Assert.False(rb1.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(90m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(SaInvoiceStatuses.Posted, (await db.SaInvoices.SingleAsync()).Status);
        }

        posting.TestHookAfterMiRollbackStock = null;
        sut.TestHookAfterInvoiceStatus = () => throw new InvalidOperationException("forced rollback status");
        var rb2 = await sut.RollbackAsync([save.InvNo!]);
        Assert.False(rb2.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(90m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(SaInvoiceStatuses.Posted, (await db.SaInvoices.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task Dispatch_still_rejects_sp()
    {
        var posting = CreatePosting();
        var result = await posting.PostAsync(IvTrxTypes.SalesOut, [1]);
        Assert.False(result.Succeeded);
        Assert.Contains("not implemented", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Header_snapshots_round_trip_uppercase_names()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            TaxGrCode = "ZR",
            SalesmanCode = "SM1",
            PoNo = "PO-9",
            Remark = "hello remark",
            InvName = "bill name",
            InvAddress1 = "bill addr",
            InvAddress4 = "line 4",
            InvCity = "kl",
            ShipName = "ship name",
            ShipAddress1 = "ship addr",
            Lines = [Line("SVC1", 1m, 10m)]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doc = save.Document!;
        Assert.Equal("ALPHA", doc.CustName);
        Assert.Equal("BILL NAME", doc.InvName);
        Assert.Equal("BILL ADDR", doc.InvAddress1);
        Assert.Equal("LINE 4", doc.InvAddress4);
        Assert.Equal("KL", doc.InvCity);
        Assert.Equal("SHIP NAME", doc.ShipName);
        Assert.Equal("SHIP ADDR", doc.ShipAddress1);
        Assert.Equal("NET30", doc.PayCode);
        Assert.Equal("ZR", doc.TaxGrCode);
        Assert.Equal("SM1", doc.SalesmanCode);
        Assert.Equal("PO-9", doc.PoNo);
        Assert.Equal("hello remark", doc.Remark);
        Assert.Equal("INV", doc.InvPrefix);
    }

    [Fact]
    public async Task GetCustomerDefaults_AppInvoice_AppShip_true_false_null()
    {
        var sut = CreateSut();
        var nullish = await sut.GetCustomerDefaultsAsync("CUST01", FixedToday);
        Assert.True(nullish.Succeeded, nullish.ErrorMessage);
        Assert.Equal("INV ADDR 1", nullish.CustomerDefaults!.InvAddress1);
        Assert.Null(nullish.CustomerDefaults.InvAddress4);
        Assert.Equal("SHIP ADDR 1", nullish.CustomerDefaults.ShipAddress1);

        var useMain = await sut.GetCustomerDefaultsAsync("CUSTAPP", FixedToday);
        Assert.True(useMain.Succeeded, useMain.ErrorMessage);
        Assert.Equal("Apply Main", useMain.CustomerDefaults!.InvName);
        Assert.Equal("MAIN BILL 1", useMain.CustomerDefaults.InvAddress1);
        Assert.Equal("MAIN BILL 4", useMain.CustomerDefaults.InvAddress4);
        Assert.Equal("Apply Main", useMain.CustomerDefaults.ShipName);
        Assert.Equal("MAIN BILL 1", useMain.CustomerDefaults.ShipAddress1);

        var useSpec = await sut.GetCustomerDefaultsAsync("CUSTSPEC", FixedToday);
        Assert.True(useSpec.Succeeded, useSpec.ErrorMessage);
        Assert.Equal("Bill To Spec", useSpec.CustomerDefaults!.InvName);
        Assert.Equal("SPEC BILL 1", useSpec.CustomerDefaults.InvAddress1);
        Assert.Null(useSpec.CustomerDefaults.InvAddress4);
        Assert.Equal("Ship To Spec", useSpec.CustomerDefaults.ShipName);
        Assert.Equal("SPEC SHIP 1", useSpec.CustomerDefaults.ShipAddress1);
    }

    [Fact]
    public async Task ResolveCurrencyRate_picks_latest_window_and_rejects_foreign_rate_one()
    {
        var sut = CreateSut();
        var usd = await sut.ResolveCurrencyRateAsync("USD", FixedToday);
        Assert.True(usd.Succeeded, usd.ErrorMessage);
        Assert.Equal(4.5m, usd.CurrRate);
        Assert.True(usd.CurrRateValid);

        var eur = await sut.ResolveCurrencyRateAsync("EUR", FixedToday);
        Assert.False(eur.Succeeded);
        Assert.Contains("cannot be 1", eur.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Customer_change_does_not_renumber_and_wipes_sp()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("INV2609-0001", save.InvNo);
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        var update = await UpdateDocAsync(sut, save.InvNo!, Request(qty: 5m, price: 10m, cust: "CUST02"));
        Assert.True(update.Succeeded, update.ErrorMessage);
        Assert.Equal("INV2609-0001", update.InvNo);
        Assert.Equal("BETA", update.Document!.CustName);
        Assert.Equal("INV", update.Document.InvPrefix);
        Assert.Empty(update.Document.Shipment);
    }

    [Fact]
    public async Task CustName_server_owned_and_prefix_from_numbering_table()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, cust: "CUST02"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("INV2609-0001", save.InvNo);
        Assert.Equal("INV", save.Document!.InvPrefix);
        Assert.Equal("BETA", save.Document.CustName);
    }

    [Fact]
    public async Task Header_tax_fallback_and_taxable_reject()
    {
        var sut = CreateSut();
        var ok = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUSTTAX",
            Currency = "MYR",
            PayCode = "NET30",
            TaxGrCode = "SR",
            Lines = [Line("SVC1", 1m, 100m)]
        });
        Assert.True(ok.Succeeded, ok.ErrorMessage);
        Assert.Null(ok.Document!.Lines[0].TaxGrCode);
        Assert.Equal(6m, ok.Document.Lines[0].TaxAmt);
        Assert.Equal(106m, ok.Document.TotAmnt);

        var bad = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUSTTAX",
            Currency = "MYR",
            PayCode = "NET30",
            Lines = [Line("SVC1", 1m, 100m)]
        });
        Assert.False(bad.Succeeded);
        Assert.Equal(SaInvoiceErrorKind.Validation, bad.ErrorKind);
        Assert.True(bad.ValidationErrors.ContainsKey("TaxGrCode"));
    }

    [Fact]
    public async Task Submitted_zero_price_and_pack_gl_from_master()
    {
        var sut = CreateSut();
        var zero = await sut.SaveNewAsync(Request(qty: 2m, price: 0m, iCode: "PACK1"));
        Assert.True(zero.Succeeded, zero.ErrorMessage);
        Assert.Equal(0m, zero.Document!.Lines[0].UnitPrice);
        Assert.Equal(24m, zero.Document.Lines[0].StdQty);
        Assert.Equal("GLPACK", zero.Document.Lines[0].SellingGlCode);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "PACK1");
            item.StdPackSize = 10m;
            item.SellingGlCode = "GLNEW";
            await db.SaveChangesAsync();
        }

        var updated = await UpdateDocAsync(sut, zero.InvNo!, Request(qty: 2m, price: 0m, iCode: "PACK1"));
        Assert.True(updated.Succeeded, updated.ErrorMessage);
        Assert.Equal(20m, updated.Document!.Lines[0].StdQty);
        Assert.Equal("GLNEW", updated.Document.Lines[0].SellingGlCode);
        Assert.Equal(0m, updated.Document.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task Discount_domain_rejects_mixed_negative_and_over_100()
    {
        var sut = CreateSut();
        var mixed = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 1m,
                    UnitPrice = 100m,
                    ItemDiscount = 10m,
                    ItemDiscAmount = 1m
                }
            ]
        });
        Assert.False(mixed.Succeeded);
        Assert.True(mixed.ValidationErrors.Keys.Any(k => k.Contains("ItemDiscount", StringComparison.OrdinalIgnoreCase)));

        var negative = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 1m,
                    UnitPrice = 100m,
                    ItemDiscAmount = -1m
                }
            ]
        });
        Assert.False(negative.Succeeded);

        var over = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 1m,
                    UnitPrice = 100m,
                    ItemDiscount = 101m
                }
            ]
        });
        Assert.False(over.Succeeded);
    }

    [Fact]
    public async Task PayCode_required_on_update_and_LocalAmount_uses_server_fx()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, cust: "CUSTUSD", currency: "USD"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(4.5m, save.Document!.CurrRate);
        Assert.Equal(45m, save.Document.Lines[0].LocalAmount);

        var missingPay = await UpdateDocAsync(sut, save.InvNo!, new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUSTUSD",
            Currency = "USD",
            PayCode = null,
            Lines = [Line("SVC1", 1m, 10m)]
        });
        Assert.False(missingPay.Succeeded);
        Assert.True(missingPay.ValidationErrors.ContainsKey("PayCode"));
    }

    [Fact]
    public async Task RequiresConfirmation_does_not_bump_rowversion_and_hook_rolls_back()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var first = await ShipAsync(sut, save.InvNo!);
        Assert.True(first.Succeeded, first.ErrorMessage);
        var version = first.Document!.RowVersion.ToArray();
        var detailCount = first.Document.Shipment.Count;
        Assert.True(detailCount > 0);

        var confirm = await ShipAsync(sut, save.InvNo!, overwriteExisting: false, rowVersion: version);
        Assert.True(confirm.RequiresConfirmation);
        Assert.Equal(SaInvoiceErrorKind.Confirmation, confirm.ErrorKind);
        Assert.True(version.SequenceEqual(confirm.Document!.RowVersion));

        sut.TestHookAfterSpDelete = () => throw new InvalidOperationException("forced mid rebuild");
        var failed = await ShipAsync(sut, save.InvNo!, overwriteExisting: true, rowVersion: version);
        Assert.False(failed.Succeeded);

        var after = await sut.GetAsync(save.InvNo!);
        Assert.True(version.SequenceEqual(after.Document!.RowVersion));
        Assert.Equal(detailCount, after.Document.Shipment.Count);
    }

    [Fact]
    public async Task Shared_pool_two_lines_of_6_against_lot_of_10()
    {
        var balId = await SeedBalLocAsync(10m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            Lines = [Line("A100", 6m, 10m), Line("A100", 6m, 10m)]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var ship = await ShipAsync(sut, save.InvNo!);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        Assert.False(ship.Document!.ShipmentComplete);
        Assert.Equal(6m, ship.Document.Shipment.Where(x => x.Line == 1).Sum(x => x.FrStdQty));
        Assert.Equal(4m, ship.Document.Shipment.Where(x => x.Line == 2).Sum(x => x.FrStdQty));
        Assert.Equal(balId, ship.Document.Shipment[0].FromBalLocId);
    }

    [Fact]
    public async Task No_candidate_fifo_succeeds_incomplete_with_ST000051()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var ship = await ShipAsync(sut, save.InvNo!);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        Assert.False(ship.Document!.ShipmentComplete);
        Assert.Empty(ship.Document.Shipment);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvTrxBatchDetails.CountAsync());
    }

    [Fact]
    public async Task Idempotent_CreateOrReplace_does_not_accumulate_details()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);
        var again = await ShipAsync(sut, save.InvNo!, overwriteExisting: true);
        Assert.True(again.Succeeded, again.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.IvTrxBatches.CountAsync(x => x.TrxType == IvTrxTypes.SalesOut));
        Assert.Equal(1, await db.IvTrxBatchDetails.CountAsync());
        Assert.Equal(10m, await db.IvTrxBatchDetails.SumAsync(x => x.FrStdQty ?? 0m));
    }

    [Fact]
    public async Task Two_tenant_LocationCode_reservations_do_not_cross()
    {
        await SeedBalLocAsync(10m, locationCode: "SITE");
        await SeedBalLocAsync(10m, locationCode: "OTHER", lot: "L-OTHER");

        var numbering = new FakeDocumentNumberingService();
        var site = CreateSut(location: "SITE", numbering: numbering);
        var other = CreateSut(location: "OTHER", numbering: numbering);

        var saveA = await site.SaveNewAsync(Request(qty: 6m, price: 10m));
        Assert.True(saveA.Succeeded, saveA.ErrorMessage);
        Assert.True((await ShipAsync(site, saveA.InvNo!)).Succeeded);

        var saveB = await other.SaveNewAsync(Request(qty: 6m, price: 10m));
        Assert.True(saveB.Succeeded, saveB.ErrorMessage);
        Assert.NotEqual(saveA.InvNo, saveB.InvNo);
        var shipB = await ShipAsync(other, saveB.InvNo!);
        Assert.True(shipB.Succeeded, shipB.ErrorMessage);
        Assert.Equal(6m, shipB.Document!.Shipment.Sum(x => x.FrStdQty));
        Assert.True(shipB.Document.ShipmentComplete);
    }

    [Fact]
    public async Task Edit_short_submit_fails_and_preserves_reservation()
    {
        var balId = await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);
        var version = (await sut.GetAsync(save.InvNo!)).Document!.RowVersion;

        var failed = await sut.ReplaceShipmentLineAsync(
            save.InvNo!,
            1,
            [new SaInvoiceShipmentLotRequest { FromBalLocId = balId, IssueQty = 8m }],
            version);
        Assert.False(failed.Succeeded);
        Assert.Contains("must equal", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var after = await sut.GetAsync(save.InvNo!);
        Assert.Equal(10m, after.Document!.Shipment.Sum(x => x.FrStdQty));
    }

    [Fact]
    public async Task Edit_overlapping_lot_excludes_old_reservation_from_sum()
    {
        // Lot of 10: A reserved 4, B reserved 4. APPLY A 4→6 must exclude A's old 4 from SUM
        // (without exclude, available would be 2 and APPLY would fail).
        var balId = await SeedBalLocAsync(10m);
        var numbering = new FakeDocumentNumberingService();
        var sut = CreateSut(numbering: numbering);

        var saveA = await sut.SaveNewAsync(Request(qty: 6m, price: 10m));
        Assert.True(saveA.Succeeded, saveA.ErrorMessage);
        Assert.True((await ShipAsync(sut, saveA.InvNo!)).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync(x => x.InvNo == saveA.InvNo);
            detail.FrStdQty = 4m;
            await db.SaveChangesAsync();
        }

        var saveB = await sut.SaveNewAsync(Request(qty: 4m, price: 10m));
        Assert.True(saveB.Succeeded, saveB.ErrorMessage);
        Assert.True((await ShipAsync(sut, saveB.InvNo!)).Succeeded);

        var version = (await sut.GetAsync(saveA.InvNo!)).Document!.RowVersion;
        var apply = await sut.ReplaceShipmentLineAsync(
            saveA.InvNo!,
            1,
            [new SaInvoiceShipmentLotRequest { FromBalLocId = balId, IssueQty = 6m }],
            version);
        Assert.True(apply.Succeeded, apply.ErrorMessage);
        Assert.Equal(6m, apply.Document!.Shipment.Sum(x => x.FrStdQty));
        Assert.Single(apply.Document.Shipment);
    }

    [Fact]
    public async Task Stale_APPLY_fails_with_CurrentAvailableQty_4_and_preserves_submitted_qty()
    {
        var balId = await SeedBalLocAsync(10m);
        var numbering = new FakeDocumentNumberingService();
        var sut = CreateSut(numbering: numbering);

        var saveA = await sut.SaveNewAsync(Request(qty: 8m, price: 10m));
        Assert.True((await ShipAsync(sut, saveA.InvNo!)).Succeeded);

        var edit = await sut.GetShipmentEditAsync(saveA.InvNo!, 1);
        Assert.True(edit.Succeeded, edit.ErrorMessage);
        Assert.Equal(10m, edit.Document!.Shipment.Single().CurrentAvailableQty);

        // Concurrent change after GetEdit: shrink A's reservation and let B take 6.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync(x => x.InvNo == saveA.InvNo);
            detail.FrStdQty = 4m;
            await db.SaveChangesAsync();
        }

        var saveB = await sut.SaveNewAsync(Request(qty: 6m, price: 10m));
        Assert.True((await ShipAsync(sut, saveB.InvNo!)).Succeeded);

        var version = (await sut.GetAsync(saveA.InvNo!)).Document!.RowVersion;
        var failed = await sut.ReplaceShipmentLineAsync(
            saveA.InvNo!,
            1,
            [new SaInvoiceShipmentLotRequest { FromBalLocId = balId, IssueQty = 8m }],
            version);
        Assert.False(failed.Succeeded);
        Assert.Equal(4m, failed.Document!.Shipment.Single().CurrentAvailableQty);
        Assert.Equal(8m, failed.Document.Shipment.Single().FrStdQty);

        var after = await sut.GetAsync(saveA.InvNo!);
        Assert.Equal(4m, after.Document!.Shipment.Sum(x => x.FrStdQty));
    }

    [Fact]
    public async Task Duplicate_FromBalLocId_on_one_line_rejected()
    {
        var balId = await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);
        var version = (await sut.GetAsync(save.InvNo!)).Document!.RowVersion;

        var failed = await sut.ReplaceShipmentLineAsync(
            save.InvNo!,
            1,
            [
                new SaInvoiceShipmentLotRequest { FromBalLocId = balId, IssueQty = 6m },
                new SaInvoiceShipmentLotRequest { FromBalLocId = balId, IssueQty = 4m }
            ],
            version);
        Assert.False(failed.Succeeded);
        Assert.Contains("Duplicate", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10m, (await sut.GetAsync(save.InvNo!)).Document!.Shipment.Sum(x => x.FrStdQty));
    }

    [Fact]
    public async Task Stale_Post_fails_when_other_NEW_SP_over_reserves_before_locks()
    {
        var balId = await SeedBalLocAsync(10m);
        var numbering = new FakeDocumentNumberingService();
        var sut = CreateSut(numbering: numbering);

        var saveA = await sut.SaveNewAsync(Request(qty: 10m, price: 10m));
        Assert.True((await ShipAsync(sut, saveA.InvNo!)).Succeeded);

        // Simulate another NEW SP that reserved 6 on the same slice before Post locks.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var aBatch = await db.IvTrxBatches.SingleAsync(x => x.RefNo == saveA.InvNo);
            var spoiler = new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = aBatch.BatchNo + 1,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.SalesOut,
                BatchStatus = IvBatchStatuses.New,
                RefNo = "SPOIL-INV",
                LocationCode = "SITE",
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "admin"
            };
            spoiler.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = spoiler.BatchNo,
                TrxLineNo = 1,
                TrxType = IvTrxTypes.SalesOut,
                ICode = "A100",
                FrWarehouse = "MAIN",
                FrLocation = "BIN1",
                FrLotNo = string.Empty,
                FrStdQty = 6m,
                FrStdUom = "EA",
                IStatus = "ACTIVE",
                FromBalLocId = balId,
                InvNo = "SPOIL-INV",
                SoLineNo = 1,
                LocationCode = "SITE"
            });
            db.IvTrxBatches.Add(spoiler);
            await db.SaveChangesAsync();
        }

        var post = await sut.PostAsync([saveA.InvNo!]);
        Assert.False(post.Succeeded);
        Assert.Contains(post.Posting, x => !x.Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var invoice = await db.SaInvoices.SingleAsync(x => x.InvNo == saveA.InvNo);
            Assert.Equal(SaInvoiceStatuses.New, invoice.Status);
            var batch = await db.IvTrxBatches.SingleAsync(x => x.RefNo == saveA.InvNo);
            Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
            Assert.Equal(10m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        }
    }

    [Fact]
    public async Task Overwrite_releases_ineligible_old_lot()
    {
        var oldId = await SeedBalLocAsync(10m, lot: "OLD", transDate: FixedToday.AddDays(-2));
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var old = await db.IvBalLocs.SingleAsync(x => x.Id == oldId);
            old.IStatus = "DAMAGED";
            await db.SaveChangesAsync();
        }

        var newer = await SeedBalLocAsync(20m, lot: "NEW", transDate: FixedToday.AddDays(-1));
        var rebuild = await ShipAsync(sut, save.InvNo!, overwriteExisting: true);
        Assert.True(rebuild.Succeeded, rebuild.ErrorMessage);
        Assert.DoesNotContain(rebuild.Document!.Shipment, x => x.FromBalLocId == oldId);
        Assert.Contains(rebuild.Document.Shipment, x => x.FromBalLocId == newer);
    }

    [Fact]
    public async Task DecPoint_header_rounds_to_zero_decimals()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1.005m, price: 10m, cust: "CUSTDEC"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(10.05m, save.Document!.Lines[0].Amount);
        Assert.Equal(10m, save.Document.TotAmnt);
    }

    private async Task<int> SeedBalLocAsync(
        decimal qty,
        DateTime? transDate = null,
        string lot = "",
        string? locationCode = "SITE")
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = lot,
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA",
            TransDate = transDate ?? FixedToday,
            LocationCode = locationCode
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static SaInvoiceSaveRequest Request(
        decimal qty,
        decimal price,
        string cust = "CUST01",
        DateTime? date = null,
        string? currency = null,
        string? iCode = "A100") =>
        new()
        {
            InvDate = date ?? FixedToday,
            CustCode = cust,
            Currency = currency ?? (string.Equals(cust, "CUSTUSD", StringComparison.OrdinalIgnoreCase) ? "USD" : "MYR"),
            PayCode = "NET30",
            Lines = [Line(iCode ?? "A100", qty, price)]
        };

    private static SaInvoiceLineRequest Line(string iCode, decimal qty, decimal price, bool inclusive = false) =>
        new()
        {
            ICode = iCode,
            Qty = qty,
            UnitPrice = price,
            FrWarehouse = "MAIN",
            IsInclusive = inclusive
        };

    private async Task<SaInvoiceOperationResult> ShipAsync(
        SaInvoiceService sut,
        string invNo,
        bool overwriteExisting = false,
        byte[]? rowVersion = null)
    {
        var token = rowVersion;
        if (token is null || token.Length == 0)
        {
            var current = await sut.GetAsync(invNo);
            Assert.True(current.Succeeded, current.ErrorMessage);
            token = current.Document!.RowVersion;
        }

        return await sut.AddShipmentAsync(invNo, overwriteExisting, token);
    }

    private async Task<SaInvoiceOperationResult> UpdateDocAsync(
        SaInvoiceService sut,
        string invNo,
        SaInvoiceSaveRequest request)
    {
        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            var current = await sut.GetAsync(invNo);
            Assert.True(current.Succeeded, current.ErrorMessage);
            request.RowVersion = current.Document!.RowVersion;
        }

        return await sut.UpdateAsync(invNo, request);
    }

    private SaInvoiceService CreateSut(
        IvInventoryPostingService? posting = null,
        string location = "SITE",
        IDocumentNumberingService? numbering = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: location);
        var postingRepo = new IvStockPostingRepository();
        return new(
            _factory,
            tenant,
            Access().Object,
            new RunningNumberService(),
            numbering ?? new FakeDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new SaInvoiceRepository(),
            new SaCustRepository(_factory),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting ?? CreatePosting(),
            new IvSpShipmentService(postingRepo, new IvStockTransactionRepository(), new RunningNumberService()),
            new SaCustLookupService(_factory, tenant),
            NullLogger<SaInvoiceService>.Instance);
    }

    private IvInventoryPostingService CreatePosting() =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
