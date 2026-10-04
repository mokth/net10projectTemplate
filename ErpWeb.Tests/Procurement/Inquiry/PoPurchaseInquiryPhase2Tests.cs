using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>Purchase Inquiry Phase 2 — Price History, Matching/RNI, Delivery Performance.</summary>
[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseOrder)]
public class PoPurchaseInquiryPhase2Tests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoPurchaseInquiryPhase2Tests()
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

    // ============================ ItemDiscAmount proof ============================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItemDiscAmount_MatchesComputeLineAmounts(bool isInclusive)
    {
        var qty = 10m;
        var unitPrice = 100m;
        var amount = PoOrderCalc.ComputeAmount(qty, unitPrice);
        var (net, tax, gross) = PoInvoiceCalc.ComputeLineAmounts(
            qty, unitPrice, itemDiscount: 10m, discountType: "%",
            itemDiscount1: 0m, discountType1: null,
            taxPercent: 6m, isInclusive, taxDecimals: 2);

        Assert.Equal(amount, gross);
        var discounted = isInclusive
            ? PoOrderCalc.RoundMoney(net + tax)
            : PoOrderCalc.RoundMoney(net);
        var expected = PoOrderCalc.RoundMoney(amount - discounted);
        var actual = PoPurchaseInquiryService.ComputeItemDiscAmount(amount, net, tax, isInclusive);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LocalAmount_NullWhenCurrRateNonPositive()
    {
        Assert.Null(PoPurchaseInquiryService.ComputeLocalAmount(100m, 0m));
        Assert.Null(PoPurchaseInquiryService.ComputeLocalAmount(100m, -1m));
        Assert.Equal(200m, PoPurchaseInquiryService.ComputeLocalAmount(100m, 2m));
    }

    [Fact]
    public void MatchingStatus_Priority_OverInvoiceWinsOverPriceMismatch()
    {
        var status = PoPurchaseInquiryService.ResolveMatchingStatus(
            overInvoicedQty: 1m,
            priceMismatch: true,
            invoiceableQty: 1m,
            balanceQty: 0m,
            recvQty: 5m,
            poPurQty: 10m);
        Assert.Equal(PoMatchingStatuses.InvoicedNotReceived, status);
    }

    // ============================ Price History ============================

    [Fact]
    public async Task PriceHistory_PostedInvOnly_And_NetUnit_LocalAmount()
    {
        await SeedAsync(db =>
        {
            var posted = Inv("INV1", "2026-09-10", 90m, currency: "MYR", currRate: 1m);
            db.PoInvoices.Add(posted);
            db.PoInvoiceDetails.Add(InvLine("INV1", 1, "ITM1", 10m, 10m, amount: 100m, net: 90m, tax: 0m));

            db.PoInvoices.Add(Inv("INV2", "2026-09-11", 50m, status: PoInvoiceStatuses.New));
            db.PoInvoiceDetails.Add(InvLine("INV2", 1, "ITM1", 5m, 10m, amount: 50m, net: 50m));

            db.PoInvoices.Add(Inv("CN1", "2026-09-12", 10m, type: PoInvoiceTypes.CreditNote));
            db.PoInvoiceDetails.Add(InvLine("CN1", 1, "ITM1", 1m, 10m, amount: 10m, net: 10m));

            var fx = Inv("INV3", "2026-09-13", 100m, currency: "USD", currRate: 0m);
            db.PoInvoices.Add(fx);
            db.PoInvoiceDetails.Add(InvLine("INV3", 1, "ITM2", 2m, 50m, amount: 100m, net: 100m));
        });

        var result = await CreateSut().GetPurchasePriceHistoryAsync(
            MenuCodes.PurchasePriceHistory, Range("2026-09-01", "2026-09-30"));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.TotalCount);

        var inv1 = result.Data.Rows.Single(x => x.DocNo == "INV1");
        Assert.Equal(9m, inv1.NetUnitPrice);
        Assert.Equal(10m, inv1.ItemDiscAmount);
        Assert.Equal(90m, inv1.LocalAmount);

        var inv3 = result.Data.Rows.Single(x => x.DocNo == "INV3");
        Assert.Null(inv3.LocalAmount);
    }

    [Fact]
    public async Task PriceHistory_Summary_LatestPrevious_TieBreak_SameDate()
    {
        await SeedAsync(db =>
        {
            db.PoInvoices.Add(Inv("INV-A", "2026-09-10", 10m));
            db.PoInvoiceDetails.Add(InvLine("INV-A", 1, "ITM1", 1m, 10m, amount: 10m, net: 10m));
            db.PoInvoices.Add(Inv("INV-B", "2026-09-10", 20m));
            db.PoInvoiceDetails.Add(InvLine("INV-B", 1, "ITM1", 1m, 20m, amount: 20m, net: 20m));
        });

        var summary = await CreateSut().GetPurchasePriceHistorySummaryAsync(
            MenuCodes.PurchasePriceHistory, Range("2026-09-01", "2026-09-30"));
        Assert.True(summary.Succeeded, summary.Message);
        // DocNo DESC: INV-B then INV-A
        Assert.Equal(20m, summary.Data!.LatestNetUnitPrice);
        Assert.Equal(10m, summary.Data.PreviousNetUnitPrice);
        Assert.Equal(2, summary.Data.SampleCount);
    }

    [Fact]
    public async Task PriceHistory_AccessDenied()
    {
        var denied = await CreateSut(canAccess: false).GetPurchasePriceHistoryAsync(
            MenuCodes.PurchasePriceHistory, Range("2026-09-01", "2026-09-30"));
        Assert.False(denied.Succeeded);
    }

    // ============================ Matching ============================

    [Fact]
    public async Task Matching_ReceivedNotInvoiced_DefaultPreset()
    {
        await SeedAsync(db =>
        {
            var po = Po("PO1", "2026-09-10", PoOrderStatuses.Open, cur: "MYR");
            db.PoOrders.Add(po);
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 0m, net: 100m,
                recvQty: 10m, invoicedQty: 0m, unitPrice: 10m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.ReceivedNotInvoiced;
        var result = await CreateSut().GetPoMatchingAsync(MenuCodes.PurchaseMatching, q);
        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(PoMatchingStatuses.ReceivedNotInvoiced, row.MatchingStatus);
        Assert.Equal(100m, row.ReceivedAmount);
    }

    [Fact]
    public async Task Matching_MultipleInvoices_EffectivePrice_And_LatestOpenInv()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open, cur: "MYR"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 0m, net: 100m,
                recvQty: 10m, invoicedQty: 10m, unitPrice: 10m));

            db.PoInvoices.Add(Inv("INV1", "2026-09-11", 40m, currency: "MYR"));
            db.PoInvoiceDetails.Add(InvLine("INV1", 1, "ITM1", 4m, 10m, amount: 40m, net: 40m,
                poNo: "PO1", poRel: 1, poLine: 1));

            db.PoInvoices.Add(Inv("INV2", "2026-09-12", 72m, currency: "MYR"));
            db.PoInvoiceDetails.Add(InvLine("INV2", 1, "ITM1", 6m, 12m, amount: 72m, net: 72m,
                poNo: "PO1", poRel: 1, poLine: 1));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        var result = await CreateSut().GetPoMatchingAsync(MenuCodes.PurchaseMatching, q);
        var row = Assert.Single(result.Data!.Rows);
        Assert.True(row.HasMultipleInvoices);
        Assert.True(row.HasMultipleInvoicePrices);
        Assert.Equal(112m, row.InvoiceAmount);
        Assert.Equal(10m, row.InvoiceQtyPosted);
        Assert.Equal(11.2m, row.EffectiveInvoiceUnitPrice);
        Assert.Equal("INV2", row.LatestInvoiceDocNo);
        Assert.True(row.PriceMismatch);
    }

    [Fact]
    public async Task Matching_PersistedInvoicedQty_Authoritative_WhenPostedSumDiffers()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open, cur: "MYR"));
            // Persisted rollup says 100 invoiced; posted INV only 95
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 100m, balance: 0m, net: 1000m,
                recvQty: 100m, invoicedQty: 100m, unitPrice: 10m));

            db.PoInvoices.Add(Inv("INV1", "2026-09-11", 950m, currency: "MYR"));
            db.PoInvoiceDetails.Add(InvLine("INV1", 1, "ITM1", 95m, 10m, amount: 950m, net: 950m,
                poNo: "PO1", poRel: 1, poLine: 1));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        var row = Assert.Single((await CreateSut().GetPoMatchingAsync(MenuCodes.PurchaseMatching, q)).Data!.Rows);
        Assert.Equal(100m, row.InvoicedQty);
        Assert.Equal(95m, row.InvoiceQtyPosted);
        Assert.Equal(0m, row.InvoiceableQty);
        Assert.Equal(PoMatchingStatuses.FullyMatched, row.MatchingStatus);
    }

    [Fact]
    public async Task Matching_SimultaneousExceptions_StatusPriority_FlagsIndependent()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open, cur: "MYR"));
            // Over-invoiced: recv 5, invoiced 8; also price mismatch vs PO 10
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 5m, net: 100m,
                recvQty: 5m, invoicedQty: 8m, unitPrice: 10m));

            db.PoInvoices.Add(Inv("INV1", "2026-09-11", 160m, currency: "MYR"));
            db.PoInvoiceDetails.Add(InvLine("INV1", 1, "ITM1", 8m, 20m, amount: 160m, net: 160m,
                poNo: "PO1", poRel: 1, poLine: 1));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        var row = Assert.Single((await CreateSut().GetPoMatchingAsync(MenuCodes.PurchaseMatching, q)).Data!.Rows);
        Assert.Equal(PoMatchingStatuses.InvoicedNotReceived, row.MatchingStatus);
        Assert.True(row.OverInvoiced);
        Assert.True(row.PriceMismatch);
    }

    [Fact]
    public async Task Matching_CurrencyMismatch_SkipsPriceCompare()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open, cur: "MYR"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 0m, net: 100m,
                recvQty: 10m, invoicedQty: 10m, unitPrice: 10m));

            db.PoInvoices.Add(Inv("INV1", "2026-09-11", 200m, currency: "USD"));
            db.PoInvoiceDetails.Add(InvLine("INV1", 1, "ITM1", 10m, 20m, amount: 200m, net: 200m,
                poNo: "PO1", poRel: 1, poLine: 1));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        var row = Assert.Single((await CreateSut().GetPoMatchingAsync(MenuCodes.PurchaseMatching, q)).Data!.Rows);
        Assert.False(row.PriceMismatch);
        Assert.Null(row.PriceVariance);
        Assert.NotEqual(PoMatchingStatuses.PriceMismatch, row.MatchingStatus);
    }

    [Fact]
    public async Task Matching_MultipleGRs_FlagAndNoSingleBatch()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 0m, net: 100m, recvQty: 10m, invoicedQty: 10m));

            var b1 = GrBatch(1001, "2026-09-11");
            var b2 = GrBatch(1002, "2026-09-12");
            db.IvTrxBatches.Add(b1);
            db.IvTrxBatches.Add(b2);
            db.SaveChanges();
            db.IvTrxBatchDetails.Add(GrLine(b1.Id, 1001, "PO1", 1, 1));
            db.IvTrxBatchDetails.Add(GrLine(b2.Id, 1002, "PO1", 1, 1));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        var row = Assert.Single((await CreateSut().GetPoMatchingAsync(MenuCodes.PurchaseMatching, q)).Data!.Rows);
        Assert.True(row.HasMultipleGRs);
        Assert.Null(row.SingleGrBatchNo);
    }

    [Fact]
    public async Task Matching_CancelledExcluded()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Cancelled));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 10m, recvQty: 0m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        var result = await CreateSut().GetPoMatchingAsync(MenuCodes.PurchaseMatching, q);
        Assert.Equal(0, result.Data!.TotalCount);
    }

    // ============================ Delivery ============================

    [Fact]
    public async Task Delivery_OnTime_Early_Late_Complete()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 0m, recvQty: 10m,
                etaDate: DateTime.Parse("2026-09-10")));

            db.PoOrders.Add(Po("PO2", "2026-09-02", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO2", 1, 1, 5m, balance: 0m, recvQty: 5m,
                etaDate: DateTime.Parse("2026-09-10")));

            db.PoOrders.Add(Po("PO3", "2026-09-03", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO3", 1, 1, 8m, balance: 0m, recvQty: 8m,
                etaDate: DateTime.Parse("2026-09-10")));

            var b1 = GrBatch(1001, "2026-09-10");
            var b2 = GrBatch(1002, "2026-09-08");
            var b3 = GrBatch(1003, "2026-09-12");
            db.IvTrxBatches.AddRange(b1, b2, b3);
            db.SaveChanges();
            db.IvTrxBatchDetails.Add(GrLine(b1.Id, 1001, "PO1", 1, 1, 10m));
            db.IvTrxBatchDetails.Add(GrLine(b2.Id, 1002, "PO2", 1, 1, 5m));
            db.IvTrxBatchDetails.Add(GrLine(b3.Id, 1003, "PO3", 1, 1, 8m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.OnTimeCompleted;
        q.AsOfDate = DateTime.Parse("2026-09-30");
        var onTimePage = await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q);
        Assert.Equal(2, onTimePage.Data!.TotalCount);

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.LateCompletion;
        var latePage = await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q);
        var late = Assert.Single(latePage.Data!.Rows);
        Assert.Equal("PO3", late.PoNo);
        Assert.Equal(PoDeliveryPerformanceStatuses.Late, late.DeliveryStatus);
        Assert.Equal(2, late.CompletionDaysLate);
    }

    [Fact]
    public async Task Delivery_PartialThenLateComplete_FullyReceivedAndDays()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 100m, balance: 0m, recvQty: 100m,
                etaDate: DateTime.Parse("2026-09-10")));

            var b1 = GrBatch(1, "2026-09-10");
            var b2 = GrBatch(2, "2026-09-15");
            var b3 = GrBatch(3, "2026-09-20");
            db.IvTrxBatches.AddRange(b1, b2, b3);
            db.SaveChanges();
            db.IvTrxBatchDetails.Add(GrLine(b1.Id, 1, "PO1", 1, 1, 40m));
            db.IvTrxBatchDetails.Add(GrLine(b2.Id, 2, "PO1", 1, 1, 30m));
            db.IvTrxBatchDetails.Add(GrLine(b3.Id, 3, "PO1", 1, 1, 30m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.LateCompletion;
        q.AsOfDate = DateTime.Parse("2026-09-30");
        var row = Assert.Single((await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows);
        Assert.Equal(DateTime.Parse("2026-09-20"), row.FullyReceivedDate);
        Assert.Equal(10, row.CompletionDaysLate);
        Assert.Equal(40m, row.OnTimeReceivedQty);
        Assert.Equal(60m, row.LateReceivedQty);
    }

    [Fact]
    public async Task Delivery_PartialOpen_OverduePreset_And_StatusPartial()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 100m, balance: 60m, recvQty: 40m,
                etaDate: DateTime.Parse("2026-09-10")));
            var b1 = GrBatch(10, "2026-09-10");
            db.IvTrxBatches.Add(b1);
            db.SaveChanges();
            db.IvTrxBatchDetails.Add(GrLine(b1.Id, 10, "PO1", 1, 1, 40m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.AsOfDate = DateTime.Parse("2026-09-20");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Overdue;
        var overdue = Assert.Single((await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows);
        Assert.Equal(PoDeliveryPerformanceStatuses.PartiallyReceived, overdue.DeliveryStatus);
        Assert.Equal(10, overdue.DaysLate);

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Partial;
        Assert.Single((await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows);
    }

    [Fact]
    public async Task Delivery_CancelledExcluded_And_LatestRevisionOnly()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Cancelled, rel: 1));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 10m,
                etaDate: DateTime.Parse("2026-09-05")));

            db.PoOrders.Add(Po("PO2", "2026-09-01", PoOrderStatuses.Cancelled, rel: 1));
            db.PoOrderDetails.Add(PoLine("PO2", 1, 1, 10m, balance: 0m, recvQty: 10m,
                etaDate: DateTime.Parse("2026-09-05")));
            db.PoOrders.Add(Po("PO2", "2026-09-02", PoOrderStatuses.Open, rel: 2));
            db.PoOrderDetails.Add(PoLine("PO2", 2, 1, 10m, balance: 10m,
                etaDate: DateTime.Parse("2026-09-10")));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.AllOpen;
        q.AsOfDate = DateTime.Parse("2026-09-15");
        var rows = (await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows;
        var row = Assert.Single(rows);
        Assert.Equal("PO2", row.PoNo);
        Assert.Equal(2, row.PoRelNo);
    }

    [Fact]
    public async Task Delivery_AsOfFutureGr_ExceptionStatus_WithAllPreset()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 100m, balance: 0m, recvQty: 100m,
                etaDate: DateTime.Parse("2026-09-10")));
            var b1 = GrBatch(50, "2026-09-15");
            db.IvTrxBatches.Add(b1);
            db.SaveChanges();
            db.IvTrxBatchDetails.Add(GrLine(b1.Id, 50, "PO1", 1, 1, 100m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        q.AsOfDate = DateTime.Parse("2026-09-10");
        q.Status = PoDeliveryPerformanceStatuses.Exception;
        var row = Assert.Single((await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows);
        Assert.Equal(PoDeliveryPerformanceStatuses.Exception, row.DeliveryStatus);
        Assert.Null(row.FullyReceivedDate);
    }

    [Fact]
    public async Task Delivery_ReturnAfterFull_Reopens_KeepsHistoricalFullyReceived()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 100m, balance: 20m, recvQty: 100m, returnQty: 20m,
                etaDate: DateTime.Parse("2026-09-10")));
            var b1 = GrBatch(70, "2026-09-01");
            db.IvTrxBatches.Add(b1);
            db.SaveChanges();
            db.IvTrxBatchDetails.Add(GrLine(b1.Id, 70, "PO1", 1, 1, 100m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Partial;
        q.AsOfDate = DateTime.Parse("2026-09-05");
        var row = Assert.Single((await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows);
        Assert.Equal(PoDeliveryPerformanceStatuses.PartiallyReceived, row.DeliveryStatus);
        Assert.Equal(DateTime.Parse("2026-09-01"), row.FullyReceivedDate);
        Assert.Equal(80m, row.NetReceivedQty);
    }

    [Fact]
    public async Task Delivery_Summary_OnTimePctNull_WhenNoCompleted_And_Consistency()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 100m, balance: 60m, recvQty: 40m,
                etaDate: DateTime.Parse("2026-09-10")));
            var b1 = GrBatch(80, "2026-09-10");
            db.IvTrxBatches.Add(b1);
            db.SaveChanges();
            db.IvTrxBatchDetails.Add(GrLine(b1.Id, 80, "PO1", 1, 1, 40m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Partial;
        q.AsOfDate = DateTime.Parse("2026-09-20");

        var lines = (await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows;
        var summary = Assert.Single((await CreateSut().GetDeliveryPerformanceSummaryAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows);
        Assert.Equal(0, summary.CompletedLines);
        Assert.Null(summary.OnTimePct);
        Assert.Equal(1, summary.PartialLines);
        Assert.Equal(lines.Sum(x => x.NetReceivedQty), summary.ReceivedQty);
    }

    [Fact]
    public async Task Delivery_OverReceipt_RemainingNeverNegative_InReceipts()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 100m, balance: 0m, recvQty: 120m,
                etaDate: DateTime.Parse("2026-09-10")));
            var b1 = GrBatch(90, "2026-09-08");
            db.IvTrxBatches.Add(b1);
            db.SaveChanges();
            db.IvTrxBatchDetails.Add(GrLine(b1.Id, 90, "PO1", 1, 1, 120m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.OnTimeCompleted;
        q.AsOfDate = DateTime.Parse("2026-09-30");
        var row = Assert.Single((await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.Rows);
        Assert.Equal(100m, row.OnTimeReceivedQty);
        Assert.Equal(120m, row.NetReceivedQty);

        var receipts = (await CreateSut().GetDeliveryPerformanceReceiptsAsync(
            MenuCodes.PurchaseDeliveryPerformance, "PO1", 1, 1, q.AsOfDate)).Data!;
        Assert.Equal(0m, Assert.Single(receipts).RemainingQty);
        Assert.Equal(120m, receipts[0].CumulativeQty);
    }

    [Fact]
    public async Task Delivery_GateDenied()
    {
        var result = await CreateSut(canAccess: false).GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, Range("2026-09-01", "2026-09-30"));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Delivery_ZeroPoPurQty_Excluded()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 0m, balance: 0m,
                etaDate: DateTime.Parse("2026-09-10")));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        q.AsOfDate = DateTime.Parse("2026-09-20");
        Assert.Equal(0, (await CreateSut().GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseDeliveryPerformance, q)).Data!.TotalCount);
    }


    // ============================ helpers ============================

    private PoPurchaseInquiryService CreateSut(bool canAccess = true, string company = Company)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        return new PoPurchaseInquiryService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, Branch, "SITE"),
            access.Object);
    }

    private static PoInquiryQuery Range(string from, string to) => new()
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

    private static PoOrder Po(
        string poNo,
        string poDate,
        string status,
        short rel = 1,
        string? vend = "V1",
        string? cur = "MYR") => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        PoNo = poNo,
        PoRelNo = rel,
        PoDate = DateTime.Parse(poDate),
        Status = status,
        VendCode = vend,
        VendName = "Vendor " + vend,
        CurCode = cur,
        RowVersion = [1]
    };

    private static PoOrderDetail PoLine(
        string poNo,
        short rel,
        short line,
        decimal qty,
        decimal balance,
        decimal net = 0m,
        decimal? recvQty = null,
        decimal returnQty = 0m,
        decimal invoicedQty = 0m,
        DateTime? etaDate = null,
        DateTime? recvDate = null,
        decimal unitPrice = 0m) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        PoNo = poNo,
        PoRelNo = rel,
        Line = line,
        ICode = "ITM1",
        PoPurQty = qty,
        BalanceQty = balance,
        RecvQty = recvQty ?? (qty - balance),
        ReturnQty = returnQty,
        InvoicedQty = invoicedQty,
        NetAmount = net,
        Amount = net,
        PoUnitPrice = unitPrice,
        PurchaseUom = "PCS",
        EtaDate = etaDate,
        RecvDate = recvDate
    };

    private static PoInvoice Inv(
        string docNo,
        string docDate,
        decimal total,
        string status = PoInvoiceStatuses.Posted,
        string type = PoInvoiceTypes.Invoice,
        string? vend = "V1",
        string? currency = "MYR",
        decimal currRate = 1m) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        DocDate = DateTime.Parse(docDate),
        Status = status,
        Type = type,
        VendorCode = vend!,
        VendorName = "Vendor " + vend,
        TotAmnt = total,
        Taxes = 0m,
        GrossAmnt = total,
        Currency = currency,
        CurrRate = currRate,
        RowVersion = [1]
    };

    private static PoInvoiceDetail InvLine(
        string docNo,
        short line,
        string iCode,
        decimal qty,
        decimal unitPrice,
        decimal amount,
        decimal net,
        decimal tax = 0m,
        bool isInclusive = false,
        string? poNo = null,
        short? poRel = null,
        short? poLine = null) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        Line = line,
        ICode = iCode,
        Qty = qty,
        UnitPrice = unitPrice,
        Amount = amount,
        NetAmount = net,
        TaxAmt = tax,
        IsInclusive = isInclusive,
        StdUom = "PCS",
        SellingUom = "PCS",
        PoNo = poNo,
        PoRelNo = poRel,
        PoLineNo = poLine
    };

    private static IvTrxBatch GrBatch(int batchNo, string trxDate) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        BatchNo = batchNo,
        TrxDtTime = DateTime.Parse(trxDate),
        TrxType = IvTrxTypes.GoodsReceive,
        BatchStatus = IvBatchStatuses.Posted
    };

    private static IvTrxBatchDetail GrLine(
        int batchId,
        int batchNo,
        string poNo,
        short rel,
        short line,
        decimal qty = 1m) => new()
    {
        BatchId = batchId,
        CompanyCode = Company,
        BranchCode = Branch,
        BatchNo = batchNo,
        TrxLineNo = 1,
        TrxType = IvTrxTypes.GoodsReceive,
        PoNo = poNo,
        PoRelNo = rel,
        PoLineNo = line,
        ToPurQty = qty,
        ICode = "ITM1"
    };
}
