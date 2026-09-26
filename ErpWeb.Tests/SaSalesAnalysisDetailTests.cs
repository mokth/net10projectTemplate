using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Sales-analysis Phase 2 acceptance matrix (plan-salesReportsAndInquiries.prompt.md): the
/// item / category / warehouse detail grids over POSTED invoice lines. Locked rules under test:
/// POSTED-only, half-open dates, server-side GROUP BY, category joined live on (CompanyCode, ICode)
/// with "(unknown)" for items that have no master, and menu gating.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesPricing)]
public class SaSalesAnalysisDetailTests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaSalesAnalysisDetailTests()
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

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Item_GroupSumsQtyAmountNetTaxAndDiscount_AndComputesAsp()
    {
        await SeedStandardAsync();

        var result = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByItem, Range("2026-09-01", "2026-09-30"), SaSalesDetailDimension.Item);

        Assert.True(result.Succeeded, result.Message);
        var itemA = result.Data!.Single(x => x.Key == "ITM_A");
        Assert.Equal(15m, itemA.Qty);
        Assert.Equal(150m, itemA.Amount);
        Assert.Equal(135m, itemA.NetAmount);
        Assert.Equal(8.10m, itemA.TaxAmount);
        Assert.Equal(15m, itemA.Discount);
        Assert.Equal(9m, itemA.Asp); // 135 / 15
        Assert.Equal("Item A", itemA.Description);

        var itemB = result.Data.Single(x => x.Key == "ITM_B");
        Assert.Equal(2m, itemB.Qty);
        Assert.Equal(18m, itemB.NetAmount);
        Assert.Equal(9m, itemB.Asp); // 18 / 2
    }

    [Fact]
    public async Task Category_JoinsTheLiveItemMaster_AndUnknownsGroupTogether()
    {
        await SeedStandardAsync();

        var result = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByCategory, Range("2026-09-01", "2026-09-30"), SaSalesDetailDimension.Category);

        Assert.True(result.Succeeded, result.Message);
        var cls = result.Data!.Single(x => x.Key == "CLS1");
        Assert.Equal(15m, cls.Qty);
        Assert.Equal(135m, cls.NetAmount);
        Assert.Equal("Class One", cls.Description);

        var unknown = result.Data.Single(x => x.Key == "(unknown)");
        Assert.Equal(2m, unknown.Qty);
        Assert.Equal(18m, unknown.NetAmount);
    }

    [Fact]
    public async Task Category_IsCompanyScoped_AndNeverCrossMatches()
    {
        await SeedAsync(db =>
        {
            // Same item code exists in another company with a DIFFERENT class — it must not leak.
            db.IvStockMasters.Add(new IvStockMaster { CompanyCode = "OTHER", ICode = "ITM_A", IClassCode = "FOREIGN", IDesc = "Foreign" });
            db.SaInvoices.Add(Inv("INV1", "2026-09-10", 100m));
            db.SaInvoiceDetails.Add(Line("INV1", 1, "ITM_A", "WH1", qty: 1m, amount: 100m, net: 100m));
        });

        var result = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByCategory, Range("2026-09-01", "2026-09-30"), SaSalesDetailDimension.Category);

        Assert.True(result.Succeeded, result.Message);
        // No DEMO master row for ITM_A, so it is "(unknown)", never "FOREIGN".
        var row = Assert.Single(result.Data!);
        Assert.Equal("(unknown)", row.Key);
    }

    [Fact]
    public async Task Warehouse_GroupsByTheLineFrWarehouse()
    {
        await SeedStandardAsync();

        var result = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByWarehouse, Range("2026-09-01", "2026-09-30"), SaSalesDetailDimension.Warehouse);

        Assert.True(result.Succeeded, result.Message);
        var wh1 = result.Data!.Single(x => x.Key == "WH1");
        Assert.Equal(15m, wh1.Qty);
        Assert.Equal(135m, wh1.NetAmount);

        var wh2 = result.Data.Single(x => x.Key == "WH2");
        Assert.Equal(2m, wh2.Qty);
        Assert.Equal(18m, wh2.NetAmount);
    }

    [Fact]
    public async Task Detail_IsPostedOnly()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_POSTED", "2026-09-10", 100m));
            db.SaInvoiceDetails.Add(Line("INV_POSTED", 1, "ITM_A", "WH1", qty: 1m, amount: 100m, net: 100m));
            db.SaInvoices.Add(Inv("INV_NEW", "2026-09-11", 900m, status: SaInvoiceStatuses.New));
            db.SaInvoiceDetails.Add(Line("INV_NEW", 1, "ITM_B", "WH2", qty: 9m, amount: 900m, net: 900m));
        });

        var result = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByItem, Range("2026-09-01", "2026-09-30"), SaSalesDetailDimension.Item);

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!);
        Assert.Equal("ITM_A", row.Key);
    }

    [Fact]
    public async Task Detail_DateRange_IsInclusiveOfBothEndDays_AndExcludesTheNextDay()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_FROM", "2026-09-01 00:00:00", 100m));
            db.SaInvoiceDetails.Add(Line("INV_FROM", 1, "ITM_A", "WH1", qty: 1m, amount: 100m, net: 100m));
            db.SaInvoices.Add(Inv("INV_TO", "2026-09-30 23:59:00", 200m));
            db.SaInvoiceDetails.Add(Line("INV_TO", 1, "ITM_A", "WH1", qty: 2m, amount: 200m, net: 200m));
            db.SaInvoices.Add(Inv("INV_AFTER", "2026-10-01 00:00:00", 400m));
            db.SaInvoiceDetails.Add(Line("INV_AFTER", 1, "ITM_A", "WH1", qty: 4m, amount: 400m, net: 400m));
        });

        var result = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByItem, Range("2026-09-01", "2026-09-30"), SaSalesDetailDimension.Item);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(3m, result.Data![0].Qty);
    }

    [Fact]
    public async Task Detail_AppliesTheDimensionSpecificFilters()
    {
        await SeedStandardAsync();

        var itemQuery = Range("2026-09-01", "2026-09-30");
        itemQuery.ItemCode = "ITM_B";
        var itemResult = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByItem, itemQuery, SaSalesDetailDimension.Item);
        Assert.Equal("ITM_B", Assert.Single(itemResult.Data!).Key);

        var whQuery = Range("2026-09-01", "2026-09-30");
        whQuery.Warehouse = "WH2";
        var whResult = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByWarehouse, whQuery, SaSalesDetailDimension.Warehouse);
        Assert.Equal("WH2", Assert.Single(whResult.Data!).Key);

        var clsQuery = Range("2026-09-01", "2026-09-30");
        clsQuery.ClassCode = "CLS1";
        var clsResult = await CreateSut().GetSalesDetailAsync(
            MenuCodes.SalesByCategory, clsQuery, SaSalesDetailDimension.Category);
        Assert.Equal("CLS1", Assert.Single(clsResult.Data!).Key);
    }

    [Fact]
    public async Task Detail_DeniesAccess_WhenTheMenuIsNotGranted()
    {
        var result = await CreateSut(canAccess: false).GetSalesDetailAsync(
            MenuCodes.SalesByItem, Range("2026-09-01", "2026-09-30"), SaSalesDetailDimension.Item);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    // ============================ Shared plumbing ============================

    private async Task SeedStandardAsync() => await SeedAsync(db =>
    {
        db.IvStockMasters.Add(new IvStockMaster { CompanyCode = Company, ICode = "ITM_A", IClassCode = "CLS1", IDesc = "Item A" });
        db.IvClasses.Add(new IvClass { CompanyCode = Company, IClassCode = "CLS1", IDesc = "Class One" });

        db.SaInvoices.Add(Inv("INV1", "2026-09-10", 150m));
        db.SaInvoiceDetails.Add(Line("INV1", 1, "ITM_A", "WH1", qty: 10m, amount: 100m, net: 90m, tax: 5.40m, disc: 10m));
        db.SaInvoiceDetails.Add(Line("INV1", 2, "ITM_A", "WH1", qty: 5m, amount: 50m, net: 45m, tax: 2.70m, disc: 5m));
        db.SaInvoiceDetails.Add(Line("INV1", 3, "ITM_B", "WH2", qty: 2m, amount: 20m, net: 18m, tax: 1.08m, disc: 2m));
    });

    private SaSalesAnalysisService CreateSut(string company = Company, bool canAccess = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);

        return new SaSalesAnalysisService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, Branch, "SITE"),
            access.Object);
    }

    private static SaSalesAnalysisQuery Range(string from, string to) => new()
    {
        DateFrom = DateTime.Parse(from),
        DateTo = DateTime.Parse(to)
    };

    private async Task SeedAsync(Action<AppDbContext> seed)
    {
        await using var db = await _factory.CreateDbContextAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static SaInvoice Inv(
        string invNo,
        string invDate,
        decimal total,
        string status = SaInvoiceStatuses.Posted) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        InvNo = invNo,
        InvDate = DateTime.Parse(invDate),
        Status = status,
        DoNo = invNo,
        CustCode = "C1",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaInvoiceDetail Line(
        string invNo,
        int line,
        string iCode,
        string warehouse,
        decimal qty,
        decimal amount,
        decimal net,
        decimal tax = 0m,
        decimal disc = 0m) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        InvNo = invNo,
        Line = line,
        ICode = iCode,
        FrWarehouse = warehouse,
        Qty = qty,
        Amount = amount,
        NetAmount = net,
        TaxAmt = tax,
        ItemDiscAmount = disc
    };
}
