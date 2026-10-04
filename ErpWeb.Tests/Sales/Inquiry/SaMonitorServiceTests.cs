using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Sales Monitor Phase A acceptance matrix (plan-salesDecisionSupport.prompt.md). Each test gets a fresh
/// SQLite database.
///
/// <para>
/// The boundaries under test are the ones the plan calls out by name: the ageing bucket edges
/// (30/31, 60/61, 90/91), "a delivery due today is <b>not</b> overdue", the SO-age column never being
/// derived from the delivery date, the four delivery invoice states, days-since-delivered falling back
/// to <c>DoDate</c>, the quotation expiry window (≤ 7), <c>ACCEPTED</c> never expiring, the three-day
/// e-Invoice monitoring threshold, and menu isolation per screen.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesShared)]
public class SaMonitorServiceTests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";
    private static readonly DateTime AsOf = new(2026, 9, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaMonitorServiceTests()
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

    // ============================ A1 · SO ageing ============================

    [Theory]
    [InlineData(0, "0-30")]
    [InlineData(30, "0-30")]
    [InlineData(31, "31-60")]
    [InlineData(60, "31-60")]
    [InlineData(61, "61-90")]
    [InlineData(90, "61-90")]
    [InlineData(91, ">90")]
    public void AgeBucket_HasTheDocumentedEdges(int ageDays, string expected) =>
        Assert.Equal(expected, SaMonitorBuckets.AgeBucket(ageDays));

    [Fact]
    public async Task SoAgeing_AgeIsTheOrderAge_AndIsNeverDerivedFromTheDeliveryDate()
    {
        await SeedAsync(db =>
        {
            // Order placed 100 days ago; delivery due tomorrow. Age must be 100 and the line must NOT be
            // overdue — the two columns answer different questions.
            db.SaSos.Add(So("SO-OLD", AsOf.AddDays(-100)));
            db.SaSoDetails.Add(SoLine("SO-OLD", 1, order: 10m, balance: 10m, deliveryDate: AsOf.AddDays(1)));
        });

        var result = await CreateSut().GetSoAgeingAsync(MenuCodes.SalesSoAgeing, Query());

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(100, row.AgeDays);
        Assert.Equal(">90", row.AgeBucket);
        Assert.False(row.IsOverdueDelivery);
        Assert.Equal(0, row.OverdueDays);
        Assert.Equal(AsOf.AddDays(1), row.DeliveryDate);
    }

    [Fact]
    public async Task SoAgeing_DeliveryDueToday_IsNotOverdue_ButYesterdayIs()
    {
        await SeedAsync(db =>
        {
            db.SaSos.Add(So("SO-TODAY", AsOf.AddDays(-5)));
            db.SaSoDetails.Add(SoLine("SO-TODAY", 1, order: 1m, balance: 1m, deliveryDate: AsOf));

            db.SaSos.Add(So("SO-LATE", AsOf.AddDays(-5)));
            db.SaSoDetails.Add(SoLine("SO-LATE", 1, order: 1m, balance: 1m, deliveryDate: AsOf.AddDays(-1)));
        });

        var result = await CreateSut().GetSoAgeingAsync(MenuCodes.SalesSoAgeing, Query());
        var rows = result.Data!.Rows;

        var dueToday = rows.Single(x => x.SoNo == "SO-TODAY");
        Assert.False(dueToday.IsOverdueDelivery);
        Assert.Equal(0, dueToday.OverdueDays);

        var late = rows.Single(x => x.SoNo == "SO-LATE");
        Assert.True(late.IsOverdueDelivery);
        Assert.Equal(1, late.OverdueDays);
    }

    [Fact]
    public async Task SoAgeing_OverdueOnlyFilter_ExcludesTodayAndUndated()
    {
        await SeedAsync(db =>
        {
            db.SaSos.Add(So("SO-LATE", AsOf.AddDays(-5)));
            db.SaSoDetails.Add(SoLine("SO-LATE", 1, 1m, 1m, AsOf.AddDays(-3)));

            db.SaSos.Add(So("SO-TODAY", AsOf.AddDays(-5)));
            db.SaSoDetails.Add(SoLine("SO-TODAY", 1, 1m, 1m, AsOf));

            db.SaSos.Add(So("SO-NODATE", AsOf.AddDays(-5)));
            db.SaSoDetails.Add(SoLine("SO-NODATE", 1, 1m, 1m, null));
        });

        var query = Query();
        query.OverdueOnly = true;
        var result = await CreateSut().GetSoAgeingAsync(MenuCodes.SalesSoAgeing, query);

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal("SO-LATE", row.SoNo);
    }

    [Theory]
    [InlineData("0-30", 10)]
    [InlineData("31-60", 45)]
    [InlineData("61-90", 70)]
    [InlineData(">90", 120)]
    public async Task SoAgeing_BucketFilter_ReturnsOnlyThatBucket(string bucket, int ageDays)
    {
        await SeedAsync(db =>
        {
            foreach (var age in new[] { 10, 45, 70, 120 })
            {
                var soNo = $"SO-{age}";
                db.SaSos.Add(So(soNo, AsOf.AddDays(-age)));
                db.SaSoDetails.Add(SoLine(soNo, 1, 1m, 1m, null));
            }
        });

        var query = Query();
        query.Bucket = bucket;
        var result = await CreateSut().GetSoAgeingAsync(MenuCodes.SalesSoAgeing, query);

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(ageDays, row.AgeDays);
    }

    [Fact]
    public async Task SoAgeing_SupersededRevisionsAndOtherTenants_AreExcluded()
    {
        await SeedAsync(db =>
        {
            db.SaSos.Add(So("SO-MINE", AsOf.AddDays(-3)));
            db.SaSoDetails.Add(SoLine("SO-MINE", 1, 1m, 1m, null));

            var superseded = So("SO-SUPERSEDED", AsOf.AddDays(-3));
            superseded.IsCurrent = false;
            db.SaSos.Add(superseded);
            db.SaSoDetails.Add(SoLine("SO-SUPERSEDED", 1, 1m, 1m, null));

            var otherCompany = So("SO-OTHERCO", AsOf.AddDays(-3));
            otherCompany.CompanyCode = "OTHER";
            db.SaSos.Add(otherCompany);
            db.SaSoDetails.Add(SoLine("SO-OTHERCO", 1, 1m, 1m, null, "OTHER"));

            var otherBranch = So("SO-OTHERBR", AsOf.AddDays(-3));
            otherBranch.BranchCode = "KL";
            db.SaSos.Add(otherBranch);
            db.SaSoDetails.Add(SoLine("SO-OTHERBR", 1, 1m, 1m, null, branch: "KL"));
        });

        var result = await CreateSut().GetSoAgeingAsync(MenuCodes.SalesSoAgeing, Query());

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal("SO-MINE", row.SoNo);
    }

    [Fact]
    public async Task SoAgeingSummary_CountsEachOrderOnce_NotOncePerLine()
    {
        await SeedAsync(db =>
        {
            // One order, three lines. The header total must be counted ONCE (1000, not 3000).
            db.SaSos.Add(So("SO-MULTI", AsOf.AddDays(-5), total: 1000m));
            db.SaSoDetails.Add(SoLine("SO-MULTI", 1, order: 10m, balance: 4m, deliveryDate: null, netAmount: 400m));
            db.SaSoDetails.Add(SoLine("SO-MULTI", 2, order: 10m, balance: 3m, deliveryDate: null, netAmount: 300m));
            db.SaSoDetails.Add(SoLine("SO-MULTI", 3, order: 10m, balance: 3m, deliveryDate: null, netAmount: 300m));
        });

        var result = await CreateSut().GetSoAgeingSummaryAsync(MenuCodes.SalesSoAgeing, Query());

        Assert.True(result.Succeeded, result.Message);
        var summary = result.Data!;
        Assert.Equal(1, summary.OpenCount);
        Assert.Equal(1000m, summary.OriginalValue);
        Assert.Equal(10m, summary.PendingQty);

        // Derived: 400*4/10 + 300*3/10 + 300*3/10 = 160 + 90 + 90 = 340.
        Assert.Equal(340m, summary.OutstandingValue);

        var bucket = summary.Buckets.Single(x => x.Label == "0-30");
        Assert.Equal(1, bucket.Count);
        Assert.Equal(1000m, bucket.Value);
        Assert.Equal(10m, bucket.Qty);
    }

    [Fact]
    public async Task SoAgeingSummary_OverdueQuantityUsesTheDeliveryDateOnly()
    {
        await SeedAsync(db =>
        {
            db.SaSos.Add(So("SO1", AsOf.AddDays(-10), 500m));
            db.SaSoDetails.Add(SoLine("SO1", 1, 10m, 4m, AsOf.AddDays(-2)));
            db.SaSoDetails.Add(SoLine("SO1", 2, 10m, 6m, AsOf.AddDays(5)));
        });

        var result = await CreateSut().GetSoAgeingSummaryAsync(MenuCodes.SalesSoAgeing, Query());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(10m, result.Data!.PendingQty);
        Assert.Equal(4m, result.Data.OverdueQty);
    }

    // ============================ A2 · Delivered not fully invoiced ============================

    [Theory]
    [InlineData(SaDualStatuses.None, null, SaDoInvoiceStates.NoInvoice, true)]
    [InlineData(SaDualStatuses.Partial, null, SaDoInvoiceStates.Partial, true)]
    [InlineData(SaDualStatuses.None, "INV-9", SaDoInvoiceStates.Invoiced, false)]
    [InlineData(SaDualStatuses.Full, "INV-9", SaDoInvoiceStates.Invoiced, false)]
    [InlineData(SaDualStatuses.WrittenOff, null, SaDoInvoiceStates.WrittenOff, false)]
    public async Task NotFullyInvoiced_DerivesTheLineState(
        string billingStatus,
        string? lineInvNo,
        string expectedState,
        bool expectedPending)
    {
        await SeedAsync(db =>
        {
            var doDoc = Do("DO1", AsOf.AddDays(-5), billingStatus, postedDate: AsOf.AddDays(-4));
            db.SaDos.Add(doDoc);
            db.SaDoDetails.Add(DoLine("DO1", 1, qty: 7m, invNo: lineInvNo));
        });

        // Include every state so the assertion is about derivation, not about the default scope.
        var query = Query();
        query.PendingOnly = false;
        var result = await CreateSut().GetDeliveredNotFullyInvoicedAsync(MenuCodes.SalesDoNotFullyInvoiced, query);

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(expectedState, row.InvoiceState);
        Assert.Equal(expectedPending, row.IsPendingInvoice);
    }

    [Fact]
    public async Task NotFullyInvoiced_PartialOrderWithSomeInvoiceNumbers_IsNotReportedAsMissingAnInvoice()
    {
        await SeedAsync(db =>
        {
            var doDoc = Do("DO1", AsOf.AddDays(-5), SaDualStatuses.Partial, postedDate: AsOf.AddDays(-4));
            db.SaDos.Add(doDoc);

            // Line 1 was billed; line 2 was not. This is exactly the case the screen was renamed for.
            db.SaDoDetails.Add(DoLine("DO1", 1, qty: 5m, invNo: "INV-100"));
            db.SaDoDetails.Add(DoLine("DO1", 2, qty: 5m, invNo: null));
        });

        var result = await CreateSut().GetDeliveredNotFullyInvoicedAsync(
            MenuCodes.SalesDoNotFullyInvoiced, PendingQuery());

        Assert.True(result.Succeeded, result.Message);
        var page = result.Data!;

        // The screen's default scope is "still owes billing", so only the unbilled line is listed — and
        // the result must never claim the whole order is uninvoiced.
        var row = Assert.Single(page.Rows);
        Assert.Equal(2, row.Line);
        Assert.Equal(SaDoInvoiceStates.Partial, row.InvoiceState);
        Assert.Null(row.LineInvNo);
    }

    [Fact]
    public async Task NotFullyInvoiced_PendingScopeExcludesFullAndWrittenOff()
    {
        await SeedAsync(db =>
        {
            db.SaDos.Add(Do("DO-PENDING", AsOf.AddDays(-5), SaDualStatuses.None));
            db.SaDoDetails.Add(DoLine("DO-PENDING", 1, 5m, null));

            db.SaDos.Add(Do("DO-FULL", AsOf.AddDays(-5), SaDualStatuses.Full));
            db.SaDoDetails.Add(DoLine("DO-FULL", 1, 5m, "INV-1"));

            db.SaDos.Add(Do("DO-WRITTEN", AsOf.AddDays(-5), SaDualStatuses.WrittenOff));
            db.SaDoDetails.Add(DoLine("DO-WRITTEN", 1, 5m, null));
        });

        var result = await CreateSut().GetDeliveredNotFullyInvoicedAsync(
            MenuCodes.SalesDoNotFullyInvoiced, PendingQuery());

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal("DO-PENDING", row.DoNo);
    }

    [Fact]
    public async Task NotFullyInvoiced_DraftDeliveryOrdersAreNeverListed()
    {
        await SeedAsync(db =>
        {
            var draft = Do("DO-DRAFT", AsOf.AddDays(-5), SaDualStatuses.None);
            draft.Status = SaDoStatuses.New;
            db.SaDos.Add(draft);
            db.SaDoDetails.Add(DoLine("DO-DRAFT", 1, 5m, null));
        });

        var result = await CreateSut().GetDeliveredNotFullyInvoicedAsync(
            MenuCodes.SalesDoNotFullyInvoiced, Query());

        Assert.True(result.Succeeded, result.Message);
        Assert.Empty(result.Data!.Rows);
    }

    [Fact]
    public async Task NotFullyInvoiced_DaysSinceDelivered_FallsBackToTheDocumentDate()
    {
        await SeedAsync(db =>
        {
            db.SaDos.Add(Do("DO-POSTED", AsOf.AddDays(-10), SaDualStatuses.None, postedDate: AsOf.AddDays(-8)));
            db.SaDoDetails.Add(DoLine("DO-POSTED", 1, 5m, null));

            // A legacy row with no posted stamp must fall back to DoDate, never to 0 or an exception.
            db.SaDos.Add(Do("DO-LEGACY", AsOf.AddDays(-12), SaDualStatuses.None, postedDate: null));
            db.SaDoDetails.Add(DoLine("DO-LEGACY", 1, 5m, null));
        });

        var result = await CreateSut().GetDeliveredNotFullyInvoicedAsync(
            MenuCodes.SalesDoNotFullyInvoiced, Query());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(8, result.Data!.Rows.Single(x => x.DoNo == "DO-POSTED").DaysSinceDelivered);
        Assert.Equal(12, result.Data.Rows.Single(x => x.DoNo == "DO-LEGACY").DaysSinceDelivered);
    }

    [Fact]
    public async Task NotFullyInvoicedSummary_ReportsTheFourStateSplitAndThePendingHalf()
    {
        await SeedAsync(db =>
        {
            db.SaDos.Add(Do("DO1", AsOf.AddDays(-5), SaDualStatuses.None));
            db.SaDoDetails.Add(DoLine("DO1", 1, 6m, null, netAmount: 600m));

            db.SaDos.Add(Do("DO2", AsOf.AddDays(-5), SaDualStatuses.Partial));
            db.SaDoDetails.Add(DoLine("DO2", 1, 4m, "INV-1", netAmount: 400m));
            db.SaDoDetails.Add(DoLine("DO2", 2, 6m, null, netAmount: 600m));
        });

        var query = Query();
        query.PendingOnly = false;
        var result = await CreateSut().GetDeliveredNotFullyInvoicedSummaryAsync(
            MenuCodes.SalesDoNotFullyInvoiced, query);

        Assert.True(result.Succeeded, result.Message);
        var summary = result.Data!;
        Assert.Equal(2, summary.DoCount);
        Assert.Equal(3, summary.LineCount);

        Assert.Equal(1, summary.States.Single(x => x.State == SaDoInvoiceStates.NoInvoice).Count);
        Assert.Equal(1, summary.States.Single(x => x.State == SaDoInvoiceStates.Partial).Count);
        Assert.Equal(1, summary.States.Single(x => x.State == SaDoInvoiceStates.Invoiced).Count);
        Assert.Equal(0, summary.States.Single(x => x.State == SaDoInvoiceStates.WrittenOff).Count);

        // The pending half is the unbilled line only: DO1 line 1 (6) + DO2 line 2 (6).
        Assert.Equal(12m, summary.NotFullyInvoicedQty);
        Assert.Equal(1200m, summary.NotFullyInvoicedValue);
    }

    // ============================ A3 · Quotation expiry ============================

    [Theory]
    [InlineData(0, "0-7")]
    [InlineData(7, "0-7")]
    [InlineData(8, "8-30")]
    [InlineData(30, "8-30")]
    [InlineData(31, ">30")]
    public void ExpiryBucket_HasTheDocumentedEdges(int daysToExpiry, string expected) =>
        Assert.Equal(expected, SaMonitorBuckets.ExpiryBucket(daysToExpiry, expired: false));

    [Fact]
    public void ExpiryBucket_ExpiredWinsOverTheDayCount() =>
        Assert.Equal("EXPIRED", SaMonitorBuckets.ExpiryBucket(-1, expired: true));

    [Fact]
    public async Task QtExpiry_ExpiringSoonWindowIsInclusive()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT-TODAY", AsOf.AddDays(-10), SaQtStatuses.New, 100m, validUntil: AsOf));
            db.SaQts.Add(Qt("QT-7", AsOf.AddDays(-10), SaQtStatuses.Sent, 100m, validUntil: AsOf.AddDays(7)));
            db.SaQts.Add(Qt("QT-8", AsOf.AddDays(-10), SaQtStatuses.New, 100m, validUntil: AsOf.AddDays(8)));
            db.SaQts.Add(Qt("QT-PAST", AsOf.AddDays(-30), SaQtStatuses.New, 100m, validUntil: AsOf.AddDays(-1)));
        });

        var result = await CreateSut().GetQtExpiryAsync(MenuCodes.SalesQtExpiry, Query());

        Assert.True(result.Succeeded, result.Message);
        var rows = result.Data!.Rows;

        Assert.True(rows.Single(x => x.QtNo == "QT-TODAY").IsExpiringSoon);
        Assert.True(rows.Single(x => x.QtNo == "QT-7").IsExpiringSoon);
        Assert.False(rows.Single(x => x.QtNo == "QT-8").IsExpiringSoon);
        Assert.False(rows.Single(x => x.QtNo == "QT-PAST").IsExpiringSoon);

        Assert.True(rows.Single(x => x.QtNo == "QT-PAST").IsExpired);
        Assert.Equal("EXPIRED", rows.Single(x => x.QtNo == "QT-PAST").ExpiryBucket);
    }

    [Fact]
    public async Task QtExpiry_AcceptedQuotationPastItsDate_IsNeverFlaggedAsExpired()
    {
        await SeedAsync(db =>
        {
            var accepted = Qt("QT-ACC", AsOf.AddDays(-10), SaQtStatuses.Accepted, 500m, validUntil: AsOf.AddDays(-2));
            db.SaQts.Add(accepted);

            var sent = Qt("QT-SENT", AsOf.AddDays(-10), SaQtStatuses.Sent, 300m, validUntil: AsOf.AddDays(-2));
            db.SaQts.Add(sent);
        });

        var result = await CreateSut().GetQtExpiryAsync(MenuCodes.SalesQtExpiry, Query());

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Data!.Rows.Single(x => x.QtNo == "QT-ACC").IsExpired);
        Assert.True(result.Data.Rows.Single(x => x.QtNo == "QT-SENT").IsExpired);
    }

    [Fact]
    public async Task QtExpiry_NotYetDueExpirableStatus_IsNotFlaggedAtAll()
    {
        await SeedAsync(db =>
        {
            var newQt = Qt("QT-NEW", AsOf.AddDays(-1), SaQtStatuses.New, 100m, validUntil: AsOf.AddDays(20));
            db.SaQts.Add(newQt);

            var expired = Qt("QT-ALREADY", AsOf.AddDays(-40), SaQtStatuses.Expired, 100m, validUntil: AsOf.AddDays(-5));
            db.SaQts.Add(expired);
        });

        var result = await CreateSut().GetQtExpiryAsync(MenuCodes.SalesQtExpiry, Query());

        Assert.True(result.Succeeded, result.Message);
        var rows = result.Data!.Rows;

        // A current revision holding EXPIRED is the normal post-sweep state, not an exception.
        var already = rows.Single(x => x.QtNo == "QT-ALREADY");
        Assert.True(already.IsExpired);
        Assert.False(already.IsExpiringSoon);
        Assert.False(rows.Single(x => x.QtNo == "QT-NEW").IsExpiringSoon);
    }

    [Fact]
    public async Task QtExpirySummary_SeparatesOpenFromExpiringSoonAndPastValidity()
    {
        await SeedAsync(db =>
        {
            var open = Qt("QT-OPEN", AsOf.AddDays(-5), SaQtStatuses.New, 100m, validUntil: AsOf.AddDays(60));
            db.SaQts.Add(open);

            var soon = Qt("QT-SOON", AsOf.AddDays(-5), SaQtStatuses.Sent, 200m, validUntil: AsOf.AddDays(3));
            db.SaQts.Add(soon);

            var past = Qt("QT-PAST", AsOf.AddDays(-30), SaQtStatuses.New, 400m, validUntil: AsOf.AddDays(-2));
            db.SaQts.Add(past);

            // A superseded revision is historical and must never be counted.
            db.SaQts.Add(Qt("QT-SUP", AsOf.AddDays(-5), SaQtStatuses.New, 999m, isCurrent: false));
        });

        var result = await CreateSut().GetQtExpirySummaryAsync(MenuCodes.SalesQtExpiry, Query());

        Assert.True(result.Succeeded, result.Message);
        var summary = result.Data!;
        Assert.Equal(3, summary.OpenCount);
        Assert.Equal(700m, summary.OpenValue);
        Assert.Equal(1, summary.ExpiringSoonCount);
        Assert.Equal(200m, summary.ExpiringSoonValue);
        Assert.Equal(1, summary.ExpiredCount);
        Assert.Equal(400m, summary.ExpiredValue);
        Assert.Single(summary.BySalesRep);
    }

    // ============================ A4 · e-Invoice action queue ============================

    [Fact]
    public async Task EInvoiceAction_NotSubmitted_IsTheReason()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV-1", AsOf.AddDays(-5)));
        });

        var result = await CreateSut().GetEInvoiceActionQueueAsync(MenuCodes.SalesEInvoiceAction, Query());

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(SaEInvoiceActionReasons.NotSubmitted, row.ActionReason);
        Assert.Null(row.DaysSinceSubmitted);
    }

    [Theory]
    [InlineData(3, null)]
    [InlineData(4, "Pending > 3 days (monitoring)")]
    public async Task EInvoiceAction_PendingThreshold_IsStrictlyGreaterThanThreeDays(
        int daysSinceSent,
        string? expected)
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv(
                "INV-1",
                AsOf.AddDays(-20),
                irbmStatus: EInvoiceStatuses.Submitted,
                irbmSentOn: AsOf.AddDays(-daysSinceSent)));
        });

        var result = await CreateSut().GetEInvoiceActionQueueAsync(MenuCodes.SalesEInvoiceAction, Query());

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(daysSinceSent, row.DaysSinceSubmitted);
        Assert.Equal(expected, row.ActionReason);
    }

    [Fact]
    public async Task EInvoiceAction_InvalidAndFailed_AreReported()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV-INVALID", AsOf.AddDays(-5), irbmStatus: EInvoiceStatuses.Invalid));
            db.SaInvoices.Add(Inv("INV-FAILED", AsOf.AddDays(-5), irbmStatus: EInvoiceStatuses.Failed));
        });

        var result = await CreateSut().GetEInvoiceActionQueueAsync(MenuCodes.SalesEInvoiceAction, Query());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            SaEInvoiceActionReasons.Invalid,
            result.Data!.Rows.Single(x => x.DocNo == "INV-INVALID").ActionReason);
        Assert.Equal(
            SaEInvoiceActionReasons.Failed,
            result.Data.Rows.Single(x => x.DocNo == "INV-FAILED").ActionReason);
    }

    [Fact]
    public async Task EInvoiceAction_StatusMismatch_UsesTheRegistryAsAuthoritative()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV-1", AsOf.AddDays(-5), irbmStatus: EInvoiceStatuses.Valid));
            // The registry's latest row disagrees with the ERP column.
            db.EInvDocSubmissions.Add(Submission("INV", "INV-1", "Cancelled"));
        });

        var result = await CreateSut().GetEInvoiceActionQueueAsync(MenuCodes.SalesEInvoiceAction, Query());

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(EInvoiceStatuses.Cancelled, row.StatusLabel);
        Assert.Equal(SaEInvoiceActionReasons.StatusMismatch, row.ActionReason);
    }

    [Fact]
    public async Task EInvoiceAction_LegacyLowercaseStatus_IsNormalised()
    {
        await SeedAsync(db =>
        {
            db.EInvDocSubmissions.Add(Submission("INV", "INV-1", "invalid"));
            db.SaInvoices.Add(Inv("INV-1", AsOf.AddDays(-5)));
        });

        var result = await CreateSut().GetEInvoiceActionQueueAsync(MenuCodes.SalesEInvoiceAction, Query());

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(EInvoiceStatuses.Invalid, row.LatestSubmissionStatus);
        Assert.Equal(SaEInvoiceActionReasons.Invalid, row.ActionReason);
    }

    [Fact]
    public async Task EInvoiceAction_HealthyDocument_HasNoReason()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv(
                "INV-1",
                AsOf.AddDays(-5),
                irbmStatus: EInvoiceStatuses.Valid,
                irbmSentOn: AsOf.AddDays(-1)));
        });

        var result = await CreateSut().GetEInvoiceActionQueueAsync(MenuCodes.SalesEInvoiceAction, Query());

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(Assert.Single(result.Data!.Rows).ActionReason);
    }

    [Fact]
    public async Task EInvoiceBreakdown_CountsEveryDocumentAndTheOnesNeedingAction()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV-OK", AsOf.AddDays(-5), irbmStatus: EInvoiceStatuses.Valid));
            db.SaInvoices.Add(Inv("INV-NONE", AsOf.AddDays(-5)));
            db.SaCdns.Add(Cdn("DN-1", AsOf.AddDays(-4), 50m, SaCdnTypes.DebitNote, irbmStatus: EInvoiceStatuses.Failed));
        });

        var result = await CreateSut().GetEInvoiceStatusBreakdownAsync(MenuCodes.SalesEInvoiceAction, Query());

        Assert.True(result.Succeeded, result.Message);
        var summary = result.Data!;
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(2, summary.NeedsActionCount);
        Assert.Equal(3, summary.Statuses.Sum(x => x.Count));
    }

    // ============================ Menu isolation ============================

    [Fact]
    public async Task MonitorScreens_ServeOnlyTheirOwnMenu()
    {
        // Each screen carries its own grant, so holding one monitor menu must not open another screen.
        Assert.True((await CreateSut().GetSoAgeingAsync(MenuCodes.SalesSoAgeing, Query())).Succeeded);
        Assert.True((await CreateSut()
            .GetDeliveredNotFullyInvoicedAsync(MenuCodes.SalesDoNotFullyInvoiced, Query())).Succeeded);
        Assert.True((await CreateSut().GetQtExpiryAsync(MenuCodes.SalesQtExpiry, Query())).Succeeded);
        Assert.True((await CreateSut()
            .GetEInvoiceActionQueueAsync(MenuCodes.SalesEInvoiceAction, Query())).Succeeded);

        Assert.Equal(
            IvMasterErrorCode.Validation,
            (await CreateSut().GetSoAgeingAsync(MenuCodes.SalesQtExpiry, Query())).ErrorCode);
        Assert.Equal(
            IvMasterErrorCode.Validation,
            (await CreateSut().GetQtExpiryAsync(MenuCodes.SalesSoAgeing, Query())).ErrorCode);
        Assert.Equal(
            IvMasterErrorCode.Validation,
            (await CreateSut()
                .GetDeliveredNotFullyInvoicedAsync(MenuCodes.SalesEInvoiceAction, Query())).ErrorCode);
        Assert.Equal(
            IvMasterErrorCode.Validation,
            (await CreateSut().GetEInvoiceStatusBreakdownAsync(MenuCodes.SalesDoNotFullyInvoiced, Query())).ErrorCode);
    }

    [Fact]
    public async Task MonitorScreens_RefuseAStrangerMenu()
    {
        // A Phase-1 inquiry menu is NOT a monitor menu: holding SA_SO_OUTSTANDING must not open the
        // monitor screens.
        var result = await CreateSut().GetSoAgeingAsync(MenuCodes.SalesSoOutstanding, Query());
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);

        var notAMenu = await CreateSut().GetSoAgeingAsync("SA_NOT_A_MENU", Query());
        Assert.False(notAMenu.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, notAMenu.ErrorCode);
    }

    [Fact]
    public async Task MonitorScreens_AreDeniedWithoutAccess()
    {
        Assert.False((await CreateSut(canAccess: false).GetSoAgeingAsync(MenuCodes.SalesSoAgeing, Query())).Succeeded);
        Assert.False((await CreateSut(canAccess: false)
            .GetDeliveredNotFullyInvoicedAsync(MenuCodes.SalesDoNotFullyInvoiced, Query())).Succeeded);
        Assert.False((await CreateSut(canAccess: false).GetQtExpiryAsync(MenuCodes.SalesQtExpiry, Query())).Succeeded);
        Assert.False((await CreateSut(canAccess: false)
            .GetEInvoiceActionQueueAsync(MenuCodes.SalesEInvoiceAction, Query())).Succeeded);
    }

    [Fact]
    public async Task MonitorScreens_NeedAMenuCodeAndRejectAnUnknownOne()
    {
        // An unknown menu is refused before any data is read — never a partial result.
        var denied = await CreateSut().GetEInvoiceActionQueueAsync("SA_SOMETHING_ELSE", Query());
        Assert.False(denied.Succeeded);
    }

    [Fact]
    public async Task MonitorScreens_FailClosedWhenTheTenantScopeCannotBeResolved()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new SaSalesInquiryService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(string.Empty, Branch, "SITE"),
            access.Object);

        var result = await sut.GetSoAgeingAsync(MenuCodes.SalesSoAgeing, Query());
        Assert.False(result.Succeeded);
        access.Verify(
            x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================ Shared plumbing ============================

    private SaSalesInquiryService CreateSut(string company = Company, bool canAccess = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);

        return new SaSalesInquiryService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, Branch, "SITE"),
            access.Object);
    }

    private static SaInquiryQuery Query() => new()
    {
        DateFrom = AsOf.AddDays(-365),
        DateTo = AsOf,
        AsOfDate = AsOf
    };

    /// <summary>The scope the Delivered-Not-Fully-Invoiced screen defaults to: lines that still owe billing.</summary>
    private static SaInquiryQuery PendingQuery()
    {
        var query = Query();
        query.PendingOnly = true;
        return query;
    }

    private async Task SeedAsync(Action<AppDbContext> seed)
    {
        await using var db = await _factory.CreateDbContextAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static SaSo So(string soNo, DateTime soDate, decimal total = 100m) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        SoNo = soNo,
        CustRel = 1,
        IsCurrent = true,
        SoDate = soDate,
        Status = SaSoStatuses.New,
        FulfillmentStatus = SaDualStatuses.None,
        BillingStatus = SaDualStatuses.None,
        CustCode = "C1",
        CustName = "Customer One",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaSoDetail SoLine(
        string soNo,
        short line,
        decimal order,
        decimal balance,
        DateTime? deliveryDate,
        string company = Company,
        string branch = Branch,
        decimal netAmount = 0m) => new()
    {
        CompanyCode = company,
        BranchCode = branch,
        SoNo = soNo,
        CustRel = 1,
        Line = line,
        ICode = "ITM1",
        IDesc = "Item one",
        OrderQty = order,
        ShippedQty = 0m,
        DeliveredQty = 0m,
        InvoicedQty = 0m,
        BalanceQty = balance,
        DeliveryDate = deliveryDate,
        NetAmount = netAmount
    };

    private static SaQt Qt(
        string qtNo,
        DateTime qtDate,
        string status,
        decimal total,
        bool isCurrent = true,
        DateTime? validUntil = null) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        QtNo = qtNo,
        CustRel = 1,
        IsCurrent = isCurrent,
        QtDate = qtDate,
        ValidUntil = validUntil ?? qtDate.AddDays(30),
        Status = status,
        CustCode = "C1",
        CustName = "Customer One",
        SalesRep = "REP1",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaDo Do(
        string doNo,
        DateTime doDate,
        string billingStatus,
        DateTime? postedDate = null) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DoNo = doNo,
        DoDate = doDate,
        Status = SaDoStatuses.Posted,
        BillingStatus = billingStatus,
        PostedDate = postedDate,
        CustCode = "C1",
        CustName = "Customer One",
        GrossAmnt = 100m,
        Taxes = 0m,
        TotAmnt = 100m
    };

    private static SaDoDetail DoLine(
        string doNo,
        short line,
        decimal qty,
        string? invNo,
        decimal netAmount = 0m) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DoNo = doNo,
        Line = line,
        SoNo = "SO1",
        ICode = "ITM1",
        IDesc = "Item one",
        Qty = qty,
        InvNo = invNo,
        NetAmount = netAmount
    };

    private static SaInvoice Inv(
        string invNo,
        DateTime invDate,
        string status = SaInvoiceStatuses.Posted,
        string? irbmStatus = null,
        DateTime? irbmSentOn = null) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        InvNo = invNo,
        InvDate = invDate,
        Status = status,
        DoNo = invNo,
        CustCode = "C1",
        CustName = "Customer One",
        GrossAmnt = 100m,
        Taxes = 0m,
        TotAmnt = 100m,
        IrbmStatus = irbmStatus,
        IrbmSentOn = irbmSentOn
    };

    private static SaCdn Cdn(
        string docNo,
        DateTime docDate,
        decimal total,
        string type,
        string? irbmStatus = null) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        DocDate = docDate,
        Status = SaCdnStatuses.Posted,
        Type = type,
        CustCode = "C1",
        CustName = "Customer One",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total,
        IrbmStatus = irbmStatus
    };

    private static EInvDocSubmission Submission(string docType, string docNo, string status) => new()
    {
        CompanyId = Company,
        DocumentType = docType,
        DocumentNo = docNo,
        Status = status,
        SubmissionUuid = "SUB-1"
    };
}
