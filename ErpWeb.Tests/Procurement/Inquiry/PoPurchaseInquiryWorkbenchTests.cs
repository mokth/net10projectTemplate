using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using PoCdnStatus = ErpWeb.Core.Purchase.PoCdnStatuses;
using PoCdnType = ErpWeb.Core.Purchase.PoCdnTypes;

namespace ErpWeb.Tests;

/// <summary>Purchase Inquiry Workbench — presets, summaries, calculation integrity, ACCESS.</summary>
[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseOrder)]
public class PoPurchaseInquiryWorkbenchTests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoPurchaseInquiryWorkbenchTests()
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
    public async Task PoSummary_And_Presets_Overdue_Partial_Unbilled_Value()
    {
        var asOf = new DateTime(2026, 9, 26);
        await SeedAsync(db =>
        {
            // Open + overdue + partial: ordered 10, recv 4, balance 6, net 100 → outstanding 60
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 6m, net: 100m,
                recvQty: 4m, etaDate: asOf.AddDays(-3)));

            // Open, ETA today — not overdue
            db.PoOrders.Add(Po("PO2", "2026-09-11", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO2", 1, 1, 10m, balance: 10m, net: 50m,
                recvQty: 0m, etaDate: asOf));

            // Null ETA — not overdue
            db.PoOrders.Add(Po("PO3", "2026-09-12", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO3", 1, 1, 5m, balance: 5m, net: 20m, recvQty: 0m));

            // Unbilled with BalanceQty 0 (fully received, not invoiced)
            db.PoOrders.Add(Po("PO4", "2026-09-13", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO4", 1, 1, 8m, balance: 0m, net: 80m,
                recvQty: 8m, invoicedQty: 0m));

            // Cancelled — excluded
            db.PoOrders.Add(Po("PO5", "2026-09-14", PoOrderStatuses.Cancelled));
            db.PoOrderDetails.Add(PoLine("PO5", 1, 1, 10m, balance: 10m, net: 10m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.AsOfDate = asOf;

        var summary = await CreateSut().GetPoOutstandingSummaryAsync(MenuCodes.PurchaseOrderOutstanding, q);
        Assert.True(summary.Succeeded, summary.Message);
        Assert.Equal(3, summary.Data!.OpenLineCount); // PO1,2,3 (PO4 balance 0)
        Assert.Equal(1, summary.Data.OverdueLineCount); // PO1 only
        Assert.Equal(1, summary.Data.PartialLineCount); // PO1 recv>0 and balance>0
        Assert.Equal(2, summary.Data.UnbilledLineCount); // PO1 invoiceable 4, PO4 invoiceable 8
        Assert.Equal(130m, summary.Data.OutstandingValue); // 60+50+20

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Overdue;
        var overdue = await CreateSut().GetPoOutstandingAsync(MenuCodes.PurchaseOrderOutstanding, q);
        Assert.Equal(1, overdue.Data!.TotalCount);
        Assert.Equal(3, overdue.Data.Rows[0].DaysOverdue);
        Assert.Equal(60m, overdue.Data.Rows[0].OutstandingValue);
        Assert.Equal(4m, overdue.Data.Rows[0].InvoiceableQty);

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Partial;
        Assert.Equal(1, (await CreateSut().GetPoOutstandingAsync(MenuCodes.PurchaseOrderOutstanding, q)).Data!.TotalCount);

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Unbilled;
        var unbilled = await CreateSut().GetPoOutstandingAsync(MenuCodes.PurchaseOrderOutstanding, q);
        Assert.Equal(2, unbilled.Data!.TotalCount);

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.AllOpen;
        Assert.Equal(3, (await CreateSut().GetPoOutstandingAsync(MenuCodes.PurchaseOrderOutstanding, q)).Data!.TotalCount);
    }

    [Fact]
    public async Task PoSummary_AccessDenied_MatchesGrid()
    {
        var denied = await CreateSut(canAccess: false).GetPoOutstandingSummaryAsync(
            MenuCodes.PurchaseOrderOutstanding, Range("2026-09-01", "2026-09-30"));
        Assert.False(denied.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, denied.ErrorCode);
    }

    [Fact]
    public async Task PrUnconverted_Preset_And_Summary_And_LinkedPos()
    {
        await SeedAsync(db =>
        {
            db.PoPrs.Add(Pr("PR1", "2026-09-01"));
            db.PoPrDetails.Add(PrLine("PR1", 1, 100m));
            db.PoPrDetails.Add(PrLine("PR1", 2, 50m));

            db.PoOrders.Add(Po("PO1", "2026-09-05", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 100m, balance: 100m, prNo: "PR1", prLine: 1));
            db.PoOrders.Add(Po("PO2", "2026-09-06", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO2", 1, 1, 20m, balance: 20m, prNo: "PR1", prLine: 2));
        });

        var q = Range("2026-09-01", "2026-09-30");
        var summary = await CreateSut().GetPrStatusSummaryAsync(MenuCodes.PurchasePrStatus, q);
        Assert.True(summary.Succeeded, summary.Message);
        Assert.Equal(1, summary.Data!.UnconvertedLineCount); // line 2 remaining 30
        Assert.Equal(30m, summary.Data.TotalRemainingQty);

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.Unconverted;
        var page = await CreateSut().GetPrStatusAsync(MenuCodes.PurchasePrStatus, q);
        Assert.Equal(1, page.Data!.TotalCount);
        Assert.Equal(2, page.Data.Rows[0].Line);
        Assert.Equal("PO2", page.Data.Rows[0].LinkedPoNos);

        q.WorkbenchPreset = null;
        var all = await CreateSut().GetPrStatusAsync(MenuCodes.PurchasePrStatus, q);
        Assert.Equal(2, all.Data!.TotalCount);
        Assert.Equal("PO1", all.Data.Rows.Single(x => x.Line == 1).LinkedPoNos);
    }

    [Fact]
    public async Task SupplierHistoryTotals_MatchMonthlyNetRules()
    {
        await SeedAsync(db =>
        {
            db.PoInvoices.Add(Inv("INV1", "2026-09-10", 100m));
            db.PoInvoices.Add(Inv("INV2", "2026-09-15", 50m));
            db.PoCdns.Add(Cdn("CN1", "2026-09-12", 20m, PoCdnType.CreditNote));
            db.PoCdns.Add(Cdn("DN1", "2026-09-18", 10m, PoCdnType.DebitNote));
        });

        var q = Range("2026-09-01", "2026-09-30");
        var totals = await CreateSut().GetSupplierPurchaseHistoryTotalsAsync(
            MenuCodes.PurchaseSupplierTransaction, q);
        Assert.True(totals.Succeeded, totals.Message);
        Assert.Equal(2, totals.Data!.InvoiceCount);
        Assert.Equal(150m, totals.Data.InvoiceTotal);
        Assert.Equal(1, totals.Data.CreditNoteCount);
        Assert.Equal(20m, totals.Data.CreditNoteTotal);
        Assert.Equal(1, totals.Data.DebitNoteCount);
        Assert.Equal(10m, totals.Data.DebitNoteTotal);
        Assert.Equal(140m, totals.Data.NetPurchase); // 150 + 10 - 20
    }

    [Fact]
    public async Task SbSummary_And_Presets_ReuseDecoration()
    {
        await SeedAsync(db =>
        {
            db.PoSbInvoices.Add(SbInv("SBI1", "2026-09-10", 100m)); // not submitted
            db.PoSbInvoices.Add(SbInv("SBI2", "2026-09-11", 100m, irbmStatus: EInvoiceStatuses.Invalid));
            db.EInvDocSubmissions.Add(Submission(
                EInvoiceDocumentTypes.SelfBilledInvoice, "SBI2", EInvoiceStatuses.Invalid));
            db.PoSbInvoices.Add(SbInv("SBI3", "2026-09-12", 100m, irbmStatus: EInvoiceStatuses.Valid));
            db.EInvDocSubmissions.Add(Submission(
                EInvoiceDocumentTypes.SelfBilledInvoice, "SBI3", EInvoiceStatuses.Valid));
        });

        var q = Range("2026-09-01", "2026-09-30");
        var summary = await CreateSut().GetSbEInvoiceSummaryAsync(MenuCodes.PurchaseSbEInvoiceInquiry, q);
        Assert.True(summary.Succeeded, summary.Message);
        Assert.Equal(3, summary.Data!.TotalCount);
        Assert.Equal(1, summary.Data.NotSubmittedCount);
        Assert.Equal(1, summary.Data.InvalidCount);

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.SbNotSubmitted;
        Assert.Equal(1, (await CreateSut().GetSbEInvoiceStatusAsync(
            MenuCodes.PurchaseSbEInvoiceInquiry, q)).Data!.TotalCount);

        q.WorkbenchPreset = PoInquiryWorkbenchPresets.SbInvalid;
        Assert.Equal(1, (await CreateSut().GetSbEInvoiceStatusAsync(
            MenuCodes.PurchaseSbEInvoiceInquiry, q)).Data!.TotalCount);
    }

    private PoPurchaseInquiryService CreateSut(bool canAccess = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);

        return new PoPurchaseInquiryService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(Company, Branch, "SITE"),
            access.Object);
    }

    private async Task SeedAsync(Action<AppDbContext> seed)
    {
        await using var db = await _factory.CreateDbContextAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static PoInquiryQuery Range(string from, string to) => new()
    {
        DateFrom = DateTime.Parse(from),
        DateTo = DateTime.Parse(to)
    };

    private static PoOrder Po(string poNo, string poDate, string status, short rel = 1, string? vend = "V1") => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        PoNo = poNo,
        PoRelNo = rel,
        PoDate = DateTime.Parse(poDate),
        Status = status,
        VendCode = vend,
        VendName = "Vendor " + vend,
        RowVersion = [1]
    };

    private static PoOrderDetail PoLine(
        string poNo, short rel, short line, decimal qty, decimal balance,
        decimal net = 0m, decimal? recvQty = null, decimal returnQty = 0m,
        decimal invoicedQty = 0m, DateTime? etaDate = null,
        string? prNo = null, short? prLine = null) => new()
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
        EtaDate = etaDate,
        PrNo = prNo,
        PrLineNo = prLine
    };

    private static PoPr Pr(string prNo, string createDt) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        PrNo = prNo,
        CreateDt = DateTime.Parse(createDt),
        Status = PoPrStatuses.New,
        Requester = "U1",
        RowVersion = [1]
    };

    private static PoPrDetail PrLine(string prNo, short line, decimal purchaseQty) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        PrNo = prNo,
        Line = line,
        ICode = "ITM1",
        PurchaseQty = purchaseQty,
        Qty = purchaseQty,
        VendorCd = "V1",
        Status = PoPrStatuses.New
    };

    private static PoInvoice Inv(string docNo, string docDate, decimal total) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        DocDate = DateTime.Parse(docDate),
        Status = PoInvoiceStatuses.Posted,
        Type = PoInvoiceTypes.Invoice,
        VendorCode = "V1",
        VendorName = "Vendor V1",
        TotAmnt = total,
        Taxes = 0m,
        GrossAmnt = total,
        RowVersion = [1]
    };

    private static PoCdn Cdn(string docNo, string docDate, decimal total, string type) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        DocDate = DateTime.Parse(docDate),
        Status = PoCdnStatus.Posted,
        Type = type,
        VendorCode = "V1",
        VendorName = "Vendor V1",
        TotAmnt = total,
        GrossAmnt = total,
        Taxes = 0m,
        RowVersion = [1]
    };

    private static PoSbInvoice SbInv(string docNo, string docDate, decimal total, string? irbmStatus = null) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        DocDate = DateTime.Parse(docDate),
        Status = PoInvoiceStatuses.Posted,
        VendorCode = "V1",
        VendorName = "Vendor V1",
        TotAmnt = total,
        GrossAmnt = total,
        Taxes = 0m,
        IrbmStatus = irbmStatus,
        RowVersion = [1]
    };

    private static EInvDocSubmission Submission(string docType, string docNo, string status) => new()
    {
        CompanyId = Company,
        DocumentType = docType,
        DocumentNo = docNo,
        Status = status,
        SubmissionUuid = "SUB-" + docNo
    };
}
