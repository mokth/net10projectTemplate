using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Sales.Inquiry;
/// <summary>
/// Sales Dashboard Phase 3 acceptance matrix (plan-salesReportsAndInquiries.prompt.md). Locked rules
/// under test: the KPI chip definitions (POSTED-only sales windows, open QT/SO status sets, pending
/// delivery = BalanceQty over open SOs, CN/DN grouped by type), company isolation and menu gating.
/// The chart payloads are asserted through the analysis-service seam.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesShared)]
public class SaSalesDashboardServiceTests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";
    private static readonly DateTime Today = new(2026, 9, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaSalesDashboardServiceTests()
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
    public async Task SalesChips_ArePostedOnly_AndUseTheThreeDateWindows()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_TODAY", "2026-09-26", 100m));
            db.SaInvoices.Add(Inv("INV_MONTH", "2026-09-10", 200m));
            db.SaInvoices.Add(Inv("INV_YEAR", "2026-03-01", 400m));
            db.SaInvoices.Add(Inv("INV_NEW", "2026-09-26", 999m, status: SaInvoiceStatuses.New));
        });

        var result = await CreateSut().GetDashboardAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(100m, result.Data!.SalesToday);
        Assert.Equal(300m, result.Data.SalesMonth);
        Assert.Equal(700m, result.Data.SalesYear);
    }

    [Fact]
    public async Task OpenQtChip_CountsCurrentOpenStatusesOnly()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT_NEW", SaQtStatuses.New, 100m));
            db.SaQts.Add(Qt("QT_SENT", SaQtStatuses.Sent, 200m));
            db.SaQts.Add(Qt("QT_CLOSED", SaQtStatuses.Closed, 300m));
            db.SaQts.Add(Qt("QT_OLD", SaQtStatuses.New, 400m, isCurrent: false));
        });

        var result = await CreateSut().GetDashboardAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.OpenQtCount);
        Assert.Equal(300m, result.Data.OpenQtValue);
    }

    [Fact]
    public async Task OpenSoChips_ComputeOriginalAndOutstandingValue()
    {
        await SeedAsync(db =>
        {
            var so = So("SO1", SaSoStatuses.New, 1000m);
            so.Details.Add(SoLine("SO1", 1, balance: 5m));
            db.SaSos.Add(so);
            db.SaSos.Add(So("SO2", SaSoStatuses.Closed, 500m));

            // Invoiced against SO1 via the invoice detail's SoNo link.
            db.SaInvoices.Add(Inv("INV1", "2026-09-20", 300m));
            db.SaInvoiceDetails.Add(InvLine("INV1", "SO1", 300m));
        });

        var result = await CreateSut().GetDashboardAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.OpenSoCount);
        Assert.Equal(1000m, result.Data.OpenSoValue);
        Assert.Equal(700m, result.Data.OpenSoOutstandingValue); // 1000 − 300
        Assert.Equal(5m, result.Data.PendingDeliveryQty);
    }

    [Fact]
    public async Task CdnChips_ArePostedOnly_AndGroupedByType()
    {
        await SeedAsync(db =>
        {
            db.SaCdns.Add(Cdn("CN1", "2026-09-05", 100m, SaCdnTypes.CreditNote));
            db.SaCdns.Add(Cdn("DN1", "2026-09-06", 50m, SaCdnTypes.DebitNote));
            db.SaCdns.Add(Cdn("CN_DRAFT", "2026-09-07", 999m, SaCdnTypes.CreditNote, status: SaCdnStatuses.New));
        });

        var result = await CreateSut().GetDashboardAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(100m, result.Data!.CreditNoteTotal);
        Assert.Equal(50m, result.Data.DebitNoteTotal);
    }

    [Fact]
    public async Task Dashboard_IsCompanyIsolated()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_MINE", "2026-09-10", 100m));
            db.SaInvoices.Add(Inv("INV_OTHER", "2026-09-10", 900m, company: "OTHER"));
        });

        var result = await CreateSut().GetDashboardAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(100m, result.Data!.SalesMonth);
    }

    [Fact]
    public async Task Dashboard_DeniesAccess_WhenTheMenuIsNotGranted()
    {
        var result = await CreateSut(canAccess: false).GetDashboardAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Charts_AreFilledFromTheAnalysisService()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV1", "2026-09-10", 100m));
        });

        var analysis = new Mock<ISaSalesAnalysisService>();
        analysis.Setup(x => x.GetSalesSummaryAsync(
                It.Is<SaSalesAnalysisQuery>(q => q.Dimension == SaSalesSummaryDimension.Customer),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<SaSalesSummaryResult>.Ok(new SaSalesSummaryResult
            {
                Rows = [new SaSalesSummaryRow { Key = "C1", InvoiceTotal = 100m }]
            }));
        analysis.Setup(x => x.GetSalesSummaryAsync(
                It.Is<SaSalesAnalysisQuery>(q => q.Dimension == SaSalesSummaryDimension.Salesman),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<SaSalesSummaryResult>.Ok(new SaSalesSummaryResult()));
        analysis.Setup(x => x.GetSalesDetailAsync(
                It.IsAny<string>(), It.IsAny<SaSalesAnalysisQuery>(),
                It.IsAny<SaSalesDetailDimension>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<IReadOnlyList<SaSalesDetailRow>>.Ok(new List<SaSalesDetailRow>()));

        var result = await CreateSut(analysis: analysis).GetDashboardAsync();

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.ByCustomer);
        Assert.Equal("C1", row.Key);
        Assert.Equal(100m, row.InvoiceTotal);
    }

    // ============================ Shared plumbing ============================

    private SaSalesDashboardService CreateSut(
        bool canAccess = true,
        Mock<ISaSalesAnalysisService>? analysis = null)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);

        var dates = new Mock<ICurrentDateService>();
        dates.Setup(x => x.Today).Returns(Today);
        dates.Setup(x => x.Now).Returns(Today);

        analysis ??= CreateEmptyAnalysis();
        return new SaSalesDashboardService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(Company, Branch, "SITE"),
            access.Object,
            dates.Object,
            analysis.Object);
    }

    private static Mock<ISaSalesAnalysisService> CreateEmptyAnalysis()
    {
        var analysis = new Mock<ISaSalesAnalysisService>();
        analysis.Setup(x => x.GetSalesSummaryAsync(It.IsAny<SaSalesAnalysisQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<SaSalesSummaryResult>.Ok(new SaSalesSummaryResult()));
        analysis.Setup(x => x.GetSalesDetailAsync(
                It.IsAny<string>(), It.IsAny<SaSalesAnalysisQuery>(),
                It.IsAny<SaSalesDetailDimension>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<IReadOnlyList<SaSalesDetailRow>>.Ok(new List<SaSalesDetailRow>()));
        return analysis;
    }

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
        string status = SaInvoiceStatuses.Posted,
        string company = Company) => new()
    {
        CompanyCode = company,
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

    private static SaQt Qt(string qtNo, string status, decimal total, bool isCurrent = true) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        QtNo = qtNo,
        CustRel = 1,
        IsCurrent = isCurrent,
        QtDate = new DateTime(2026, 9, 1),
        ValidUntil = new DateTime(2026, 10, 1),
        Status = status,
        CustCode = "C1",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaSo So(string soNo, string status, decimal total) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        SoNo = soNo,
        CustRel = 1,
        IsCurrent = true,
        SoDate = new DateTime(2026, 9, 1),
        Status = status,
        FulfillmentStatus = "NONE",
        BillingStatus = "NONE",
        CustCode = "C1",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaSoDetail SoLine(string soNo, short line, decimal balance) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        SoNo = soNo,
        CustRel = 1,
        Line = line,
        ICode = "ITM1",
        OrderQty = balance,
        BalanceQty = balance
    };

    private static SaInvoiceDetail InvLine(string invNo, string soNo, decimal net) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        InvNo = invNo,
        Line = 1,
        SoNo = soNo,
        Qty = 1m,
        NetAmount = net
    };

    private static SaCdn Cdn(
        string docNo,
        string docDate,
        decimal total,
        string type,
        string status = SaCdnStatuses.Posted) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        DocDate = DateTime.Parse(docDate),
        Status = status,
        Type = type,
        CustCode = "C1",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };
}
