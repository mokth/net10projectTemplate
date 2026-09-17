using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Sales-analysis Phase 1 acceptance matrix. Each test gets a fresh SQLite database, so no test
/// depends on another test's rows.
///
/// The locked rules under test: R1 (half-open dates, no target proration), M2 (missing/zero target is
/// N/A), R2 (company-wide attainment ignores the branch filter), R3 (CN/DN netted once, positively),
/// R4/M4 (Won = CLOSED + CONVERTED), M5 (header totals), R5 (Source is live), R7 (company isolation).
/// </summary>
public class SaSalesAnalysisServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaSalesAnalysisServiceTests()
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

    // ============================== Sales Summary ==============================

    [Fact]
    public async Task Summary_IncludesPostedAndExcludesNew()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Invoice("INV001", "2026-09-10", 1000m, salesman: "SM1"));
            db.SaInvoices.Add(Invoice("INV002", "2026-09-11", 500m, salesman: "SM1",
                status: SaInvoiceStatuses.New));
        });

        var result = await CreateSut().GetSalesSummaryAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var data = result.Data!;
        Assert.Equal(1, data.Totals.InvoiceCount);
        Assert.Equal(1000m, data.Totals.InvoiceTotal);
        var row = Assert.Single(data.Rows);
        Assert.Equal(1, row.InvoiceCount);
        Assert.Equal(1000m, row.InvoiceTotal);
    }

    [Fact]
    public async Task Summary_DateRange_IsInclusiveOfBothEndDays_AndExcludesTheNextDay()
    {
        await SeedAsync(db =>
        {
            // M1: >= DateFrom.Date and < DateTo.Date.AddDays(1).
            db.SaInvoices.Add(Invoice("INV_FROM", "2026-09-01 00:00:00", 100m, salesman: "SM1"));
            db.SaInvoices.Add(Invoice("INV_TO", "2026-09-30 23:59:00", 200m, salesman: "SM1"));
            db.SaInvoices.Add(Invoice("INV_AFTER", "2026-10-01 00:00:00", 400m, salesman: "SM1"));
            db.SaInvoices.Add(Invoice("INV_BEFORE", "2026-08-31 23:59:00", 800m, salesman: "SM1"));
        });

        var result = await CreateSut().GetSalesSummaryAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.Totals.InvoiceCount);
        Assert.Equal(300m, result.Data.Totals.InvoiceTotal);
    }

    [Fact]
    public async Task Summary_IsCompanyIsolated()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Invoice("INV_DEMO", "2026-09-10", 100m, salesman: "SM1"));
            db.SaInvoices.Add(Invoice("INV_OTHER", "2026-09-10", 900m, salesman: "SM1", company: "OTHER"));
        });

        var result = await CreateSut("DEMO").GetSalesSummaryAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(100m, result.Data!.Totals.InvoiceTotal);
    }

    [Fact]
    public async Task Summary_RequiresABoundedDateRange()
    {
        var sut = CreateSut();

        var missing = await sut.GetSalesSummaryAsync(new SaSalesAnalysisQuery());
        Assert.False(missing.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, missing.ErrorCode);

        var inverted = await sut.GetSalesSummaryAsync(Range("2026-09-30", "2026-09-01"));
        Assert.False(inverted.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, inverted.ErrorCode);
    }

    [Fact]
    public async Task Summary_DeniesAccess_WhenTheMenuIsNotGranted()
    {
        var result = await CreateSut(canAccess: false)
            .GetSalesSummaryAsync(Range("2026-09-01", "2026-09-30"));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Summary_BranchFilter_AppliesToThisScreenOnly()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Invoice("INV_HQ", "2026-09-10", 100m, salesman: "SM1", branch: "HQ"));
            db.SaInvoices.Add(Invoice("INV_KL", "2026-09-10", 250m, salesman: "SM1", branch: "KL"));
        });

        var all = await CreateSut().GetSalesSummaryAsync(Range("2026-09-01", "2026-09-30"));
        Assert.Equal(350m, all.Data!.Totals.InvoiceTotal);

        var hq = Range("2026-09-01", "2026-09-30");
        hq.BranchCode = "HQ";
        var filtered = await CreateSut().GetSalesSummaryAsync(hq);
        Assert.Equal(100m, filtered.Data!.Totals.InvoiceTotal);
    }

    [Fact]
    public async Task Summary_GroupsByTheInvoiceSegmentationSnapshot()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Invoice("INV1", "2026-09-10", 100m, salesman: "SM1", custType: "RETAIL"));
            db.SaInvoices.Add(Invoice("INV2", "2026-09-10", 300m, salesman: "SM1", custType: "RETAIL"));
            db.SaInvoices.Add(Invoice("INV3", "2026-09-10", 50m, salesman: "SM1", custType: "WHOLESALE"));
        });

        var query = Range("2026-09-01", "2026-09-30");
        query.Dimension = SaSalesSummaryDimension.CustType;

        var result = await CreateSut().GetSalesSummaryAsync(query);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.Rows.Count);
        var retail = result.Data.Rows.Single(x => x.Key == "RETAIL");
        Assert.Equal(400m, retail.InvoiceTotal);
        Assert.Equal(2, retail.InvoiceCount);
        // Share is of the filtered invoice total, so the two rows add up to 100%.
        Assert.Equal(88.89m, retail.SharePercent);
        Assert.Equal(100m, result.Data.Rows.Sum(x => x.SharePercent));
    }

    [Fact]
    public async Task Summary_SourceDimension_ReadsTheLiveCustomerMaster()
    {
        await SeedAsync(db =>
        {
            db.SaCusts.Add(new SaCust { CompanyCode = "DEMO", CustCode = "C1", CustName = "One", CustSource = "WEB" });
            db.SaInvoices.Add(Invoice("INV1", "2026-09-10", 100m, salesman: "SM1", cust: "C1"));
        });

        var query = Range("2026-09-01", "2026-09-30");
        query.Dimension = SaSalesSummaryDimension.Source;

        var first = await CreateSut().GetSalesSummaryAsync(query);
        var web = Assert.Single(first.Data!.Rows);
        Assert.Equal("WEB", web.Key);

        // R5: the dimension is live attribution, so re-labelling the customer moves past periods too.
        await SeedAsync(db =>
        {
            var cust = db.SaCusts.Single(x => x.CustCode == "C1");
            cust.CustSource = "EXPO";
        });

        var second = await CreateSut().GetSalesSummaryAsync(query);
        Assert.Equal("EXPO", Assert.Single(second.Data!.Rows).Key);

        var filtered = Range("2026-09-01", "2026-09-30");
        filtered.Dimension = SaSalesSummaryDimension.Source;
        filtered.CustSource = "WEB";
        var none = await CreateSut().GetSalesSummaryAsync(filtered);
        Assert.Empty(none.Data!.Rows);
    }

    [Fact]
    public async Task Summary_PeriodChips_NetPostedCnAndDnOnce_AndIgnoreDrafts()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Invoice("INV1", "2026-09-10", 1000m, salesman: "SM1"));
            db.SaCdns.Add(Cdn("CN1", "2026-09-11", 100m, SaCdnTypes.CreditNote));
            db.SaCdns.Add(Cdn("DN1", "2026-09-12", 50m, SaCdnTypes.DebitNote));
            db.SaCdns.Add(Cdn("CN_DRAFT", "2026-09-13", 999m, SaCdnTypes.CreditNote, SaCdnStatuses.New));
        });

        var result = await CreateSut().GetSalesSummaryAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var totals = result.Data!.Totals;
        Assert.Equal(1000m, totals.InvoiceTotal);
        Assert.Equal(1, totals.CreditNoteCount);
        Assert.Equal(100m, totals.CreditNoteTotal);
        Assert.Equal(1, totals.DebitNoteCount);
        Assert.Equal(50m, totals.DebitNoteTotal);
        // R3: positively stored, netted exactly once, never negated twice.
        Assert.Equal(950m, totals.NetSalesAmount);

        // Dimensional rows stay posted-invoice-only: the CN/DN never leak into a salesman row.
        var row = Assert.Single(result.Data.Rows);
        Assert.Equal(1000m, row.InvoiceTotal);
    }

    // ========================== Sales Rep Attainment ==========================

    [Fact]
    public async Task Attainment_ShowsSalesOnlyAndTargetOnlySalesmen()
    {
        await SeedAsync(db =>
        {
            db.SaSalesReps.Add(Rep("SM1", "Sales One"));
            db.SaSalesReps.Add(Rep("SM2", "Sales Two"));
            db.SaInvoices.Add(Invoice("INV1", "2026-09-10", 100m, salesman: "SM1"));
            db.SaSalesRepTargets.Add(Target("SM2", 2026, 9, 200m));
        });

        var result = await CreateSut().GetSalesRepAttainmentAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.Count);

        var salesOnly = result.Data.Single(x => x.Code == "SM1");
        Assert.Equal(100m, salesOnly.ActualAmount);
        Assert.Equal(0m, salesOnly.TargetAmount);
        Assert.Null(salesOnly.AttainmentPercent); // M2: no target to measure against.
        Assert.Equal("Sales One", salesOnly.Name);

        var targetOnly = result.Data.Single(x => x.Code == "SM2");
        Assert.Equal(0m, targetOnly.ActualAmount);
        Assert.Equal(200m, targetOnly.TargetAmount);
        Assert.Equal(0m, targetOnly.AttainmentPercent);
        Assert.Equal(1, targetOnly.MonthsWithTarget);
    }

    [Fact]
    public async Task Attainment_PartialMonthUsesTheFullMonthlyTarget()
    {
        await SeedAsync(db =>
        {
            db.SaSalesReps.Add(Rep("SM1", "Sales One"));
            db.SaSalesRepTargets.Add(Target("SM1", 2026, 9, 3000m));
            db.SaInvoices.Add(Invoice("INV_IN", "2026-09-18", 600m, salesman: "SM1"));
            db.SaInvoices.Add(Invoice("INV_OUT", "2026-09-05", 400m, salesman: "SM1"));
        });

        var result = await CreateSut().GetSalesRepAttainmentAsync(Range("2026-09-15", "2026-09-20"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!);
        // R1: the target is the whole month (3000), only the actual is partial (600).
        Assert.Equal(600m, row.ActualAmount);
        Assert.Equal(3000m, row.TargetAmount);
        Assert.Equal(20m, row.AttainmentPercent);
    }

    [Fact]
    public async Task Attainment_MultiMonthSumsEveryIntersectingMonth_TargetsOnly()
    {
        await SeedAsync(db =>
        {
            db.SaSalesReps.Add(Rep("SM1", "Sales One"));
            db.SaSalesRepTargets.Add(Target("SM1", 2026, 7, 700m));
            db.SaSalesRepTargets.Add(Target("SM1", 2026, 8, 1000m));
            db.SaSalesRepTargets.Add(Target("SM1", 2026, 9, 2000m));
            db.SaSalesRepTargets.Add(Target("SM1", 2026, 10, 3000m));
        });

        // 15 Aug → 10 Sep touches August and September only; July and October are outside.
        var result = await CreateSut().GetSalesRepAttainmentAsync(Range("2026-08-15", "2026-09-10"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!);
        Assert.Equal(3000m, row.TargetAmount);
        Assert.Equal(2, row.MonthsWithTarget);
        // Both months carry a target, so no sales legitimately reports 0% rather than N/A.
        Assert.Equal(0m, row.AttainmentPercent);
    }

    [Fact]
    public async Task Attainment_MissingMonthCountsAsZero_AndZeroTargetIsNa()
    {
        await SeedAsync(db =>
        {
            db.SaSalesReps.Add(Rep("SM1", "Sales One"));
            // September only — October is absent and must count as zero, not as "unknown".
            db.SaSalesRepTargets.Add(Target("SM1", 2026, 9, 0m));
        });

        var result = await CreateSut().GetSalesRepAttainmentAsync(Range("2026-09-01", "2026-10-31"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!);
        Assert.Equal(0m, row.TargetAmount);
        Assert.Null(row.AttainmentPercent);
        Assert.Equal(1, row.MonthsWithTarget);
    }

    [Fact]
    public async Task Attainment_IsCompanyWide_AndTheBranchFilterCannotChangeIt()
    {
        await SeedAsync(db =>
        {
            db.SaSalesReps.Add(Rep("SM1", "Sales One"));
            db.SaSalesRepTargets.Add(Target("SM1", 2026, 9, 1000m));
            db.SaInvoices.Add(Invoice("INV_HQ", "2026-09-10", 100m, salesman: "SM1", branch: "HQ"));
            db.SaInvoices.Add(Invoice("INV_KL", "2026-09-10", 200m, salesman: "SM1", branch: "KL"));
        });

        var withBranch = Range("2026-09-01", "2026-09-30");
        withBranch.BranchCode = "HQ";

        var result = await CreateSut().GetSalesRepAttainmentAsync(withBranch);

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!);
        // R2: attainment ignores the branch filter on purpose — both branches' sales count.
        Assert.Equal(300m, row.ActualAmount);
        Assert.Equal(1000m, row.TargetAmount);
        Assert.Equal(30m, row.AttainmentPercent);
    }

    // ========================= Quotation Conversion =========================

    [Fact]
    public async Task Qt_CountsOnlyTheCurrentRevision()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT1", "2026-09-05", SaQtStatuses.Closed, 500m,
                isCurrent: true, closedReason: SaQtClosedReasons.Converted, rep: "SM1", custRel: 2));
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.Superseded, 400m,
                isCurrent: false, rep: "SM1", custRel: 1));
        });

        var result = await CreateSut().GetQtConversionAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.Total.Count);
        Assert.Equal(500m, result.Data.Total.Amount);
        Assert.Equal(1, result.Data.Won.Count);
        Assert.Equal(500m, result.Data.Won.Amount);
    }

    [Fact]
    public async Task Qt_BucketsFollowTheLockedStatusRules_AndWinRateExcludesTheRest()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT_NEW", "2026-09-01", SaQtStatuses.New, 100m, rep: "SM1"));
            db.SaQts.Add(Qt("QT_SENT", "2026-09-02", SaQtStatuses.Sent, 200m, rep: "SM1"));
            db.SaQts.Add(Qt("QT_ACC", "2026-09-03", SaQtStatuses.Accepted, 300m, rep: "SM2"));
            db.SaQts.Add(Qt("QT_WON", "2026-09-04", SaQtStatuses.Closed, 700m,
                closedReason: SaQtClosedReasons.Converted, rep: "SM2", conversionStatus: "FULL"));
            db.SaQts.Add(Qt("QT_LOST", "2026-09-05", SaQtStatuses.Lost, 400m,
                rep: "SM2", lostReason: "Price"));
            db.SaQts.Add(Qt("QT_EXP", "2026-09-06", SaQtStatuses.Expired, 500m, rep: "SM1"));
            db.SaQts.Add(Qt("QT_CAN", "2026-09-07", SaQtStatuses.Cancelled, 600m, rep: "SM1"));
        });

        var result = await CreateSut().GetQtConversionAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var data = result.Data!;
        Assert.Equal(7, data.Total.Count);
        Assert.Equal(2800m, data.Total.Amount);

        Assert.Equal(3, data.Open.Count);
        Assert.Equal(600m, data.Open.Amount);
        Assert.Equal(1, data.Won.Count);
        Assert.Equal(700m, data.Won.Amount);
        Assert.Equal(1, data.Lost.Count);
        Assert.Equal(400m, data.Lost.Amount);
        Assert.Equal(1, data.Expired.Count);
        Assert.Equal(500m, data.Expired.Amount);
        Assert.Equal(1, data.Cancelled.Count);
        Assert.Equal(600m, data.Cancelled.Amount);

        // Win rate divides by won + lost only: 1 / (1 + 1).
        Assert.Equal(50m, data.WinRatePercent);

        var reason = Assert.Single(data.LostReasons);
        Assert.Equal("Price", reason.Reason);
        Assert.Equal(400m, reason.Amount);
    }

    [Fact]
    public async Task Qt_ClosedWithoutConversion_IsNotCountedAsWon()
    {
        await SeedAsync(db =>
        {
            // M4: ConversionStatus alone is NOT the Won path — the converter writes CLOSED + CONVERTED.
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.Closed, 900m,
                closedReason: null, conversionStatus: "FULL", rep: "SM1"));
        });

        var result = await CreateSut().GetQtConversionAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.Total.Count);
        Assert.Equal(0, result.Data.Won.Count);
        Assert.Equal(0m, result.Data.Won.Amount);
    }

    [Fact]
    public async Task Qt_WinRateIsNaWhenNothingWasWonOrLost()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.New, 100m, rep: "SM1"));
        });

        var result = await CreateSut().GetQtConversionAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.Data!.WinRatePercent);
    }

    [Fact]
    public async Task Qt_BlankLostReasonBecomesTheBlankLabel()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.Lost, 100m, rep: "SM1", lostReason: "   "));
            db.SaQts.Add(Qt("QT2", "2026-09-02", SaQtStatuses.Lost, 300m, rep: "SM1", lostReason: "Price"));
        });

        var result = await CreateSut().GetQtConversionAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(result.Data!.LostReasons, x => x.Reason == "(blank)" && x.Amount == 100m);
        Assert.Contains(result.Data.LostReasons, x => x.Reason == "Price" && x.Amount == 300m);
    }

    [Fact]
    public async Task Qt_AmountsComeFromTheHeaderTotal()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.New, 1234.56m, rep: "SM1"));
        });

        var result = await CreateSut().GetQtConversionAsync(Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        // M5: the header total is authoritative — this service never re-totals detail lines.
        Assert.Equal(1234.56m, result.Data!.Total.Amount);
    }

    [Fact]
    public async Task Qt_BreakdownBySalesRep_UsesTheSameWonAndWinRateRules()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.Closed, 700m,
                closedReason: SaQtClosedReasons.Converted, rep: "SM1"));
            db.SaQts.Add(Qt("QT2", "2026-09-02", SaQtStatuses.Lost, 300m, rep: "SM1", lostReason: "Price"));
            db.SaQts.Add(Qt("QT3", "2026-09-03", SaQtStatuses.New, 100m, rep: null));
        });

        var query = Range("2026-09-01", "2026-09-30");
        query.GroupQtBySalesRep = true;

        var result = await CreateSut().GetQtConversionAsync(query);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.BySalesRep.Count);

        var sm1 = result.Data.BySalesRep.Single(x => x.SalesRep == "SM1");
        Assert.Equal(1, sm1.Won.Count);
        Assert.Equal(1, sm1.Lost.Count);
        Assert.Equal(50m, sm1.WinRatePercent);

        var unassigned = result.Data.BySalesRep.Single(x => x.SalesRep == "(blank)");
        Assert.Equal(1, unassigned.Total.Count);
        Assert.Null(unassigned.WinRatePercent);
    }

    [Fact]
    public async Task Qt_SalesmanFilter_LimitsTheUniverse()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.New, 100m, rep: "SM1"));
            db.SaQts.Add(Qt("QT2", "2026-09-02", SaQtStatuses.New, 900m, rep: "SM2"));
        });

        var query = Range("2026-09-01", "2026-09-30");
        query.SalesmanCode = "SM1";

        var result = await CreateSut().GetQtConversionAsync(query);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.Total.Count);
        Assert.Equal(100m, result.Data.Total.Amount);
    }

    // ============================ CustSource master ============================

    [Fact]
    public async Task Lookups_ListAndValidateCustomerSources()
    {
        await SeedAsync(db =>
        {
            db.IvMsCodes.Add(new IvMsCode { Code = "WEB", Name = "Website", CodeType = IvMsCodeTypes.Source });
            db.IvMsCodes.Add(new IvMsCode { Code = "WALK", Name = "Walk-in", CodeType = IvMsCodeTypes.Source });
            db.IvMsCodes.Add(new IvMsCode { Code = "ELEC", Name = "Electronics", CodeType = IvMsCodeTypes.Industry });
        });

        var lookups = new SaCustLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext());

        var options = await lookups.ListSourcesForAssignmentAsync();
        Assert.Equal(2, options.Count);
        Assert.Equal(["WALK", "WEB"], options.Select(x => x.Code).ToArray());

        Assert.True(await lookups.ValidateSourceAssignmentAsync("WEB", null));
        Assert.True(await lookups.ValidateSourceAssignmentAsync(null, null));
        Assert.False(await lookups.ValidateSourceAssignmentAsync("NOPE", null));
        // A legacy value already on the row is tolerated so it cannot block an unrelated edit.
        Assert.True(await lookups.ValidateSourceAssignmentAsync("LEGACY", "LEGACY"));
    }

    // ============================ Shared plumbing ============================

    private SaSalesAnalysisService CreateSut(string company = "DEMO", bool canAccess = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);

        return new SaSalesAnalysisService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, "HQ", "SITE"),
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

    private static SaInvoice Invoice(
        string invNo,
        string invDate,
        decimal total,
        string? salesman = null,
        string cust = "C1",
        string branch = "HQ",
        string status = SaInvoiceStatuses.Posted,
        string? custType = null,
        string company = "DEMO") => new()
        {
            CompanyCode = company,
            InvNo = invNo,
            BranchCode = branch,
            CustCode = cust,
            InvDate = DateTime.Parse(invDate),
            Status = status,
            DoNo = invNo,
            SalesmanCode = salesman,
            CustType = custType,
            GrossAmnt = total,
            Taxes = 0m,
            TotAmnt = total
        };

    private static SaCdn Cdn(
        string docNo,
        string docDate,
        decimal total,
        string type,
        string status = SaCdnStatuses.Posted,
        string cust = "C1",
        string branch = "HQ") => new()
        {
            CompanyCode = "DEMO",
            BranchCode = branch,
            DocNo = docNo,
            DocDate = DateTime.Parse(docDate),
            Status = status,
            Type = type,
            CustCode = cust,
            GrossAmnt = total,
            Taxes = 0m,
            TotAmnt = total
        };

    private static SaQt Qt(
        string qtNo,
        string qtDate,
        string status,
        decimal total,
        bool isCurrent = true,
        string? closedReason = null,
        string? lostReason = null,
        string? rep = null,
        string conversionStatus = "NONE",
        short custRel = 1) => new()
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            QtNo = qtNo,
            CustRel = custRel,
            IsCurrent = isCurrent,
            QtDate = DateTime.Parse(qtDate),
            ValidUntil = DateTime.Parse(qtDate).AddDays(30),
            Status = status,
            ClosedReason = closedReason,
            ConversionStatus = conversionStatus,
            LostReason = lostReason,
            SalesRep = rep,
            CustCode = "C1",
            GrossAmnt = total,
            Taxes = 0m,
            TotAmnt = total
        };

    private static SaSalesRep Rep(string code, string name) => new()
    {
        CompanyCode = "DEMO",
        SrepCode = code,
        SrepName = name,
        IsActive = true
    };

    private static SaSalesRepTarget Target(string rep, int year, int month, decimal amount) => new()
    {
        CompanyCode = "DEMO",
        SrepCode = rep,
        Year = year,
        Month = month,
        TargetAmount = amount
    };
}
