using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using PoCdnStatus = ErpWeb.Core.Purchase.PoCdnStatuses;
using PoCdnType = ErpWeb.Core.Purchase.PoCdnTypes;

namespace ErpWeb.Tests.Procurement.Inquiry;
/// <summary>
/// Purchase Inquiry Phase 1 acceptance matrix (plans/Procurement-Inquiry-Assessment.md Step 0.5).
/// </summary>
[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseOrder)]
public class PoPurchaseInquiryServiceTests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoPurchaseInquiryServiceTests()
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

    // ============================ Security ============================

    [Fact]
    public async Task AccessDenied_And_UnknownMenu_Fail()
    {
        var denied = await CreateSut(canAccess: false).GetPoOutstandingAsync(
            MenuCodes.PurchaseOrderOutstanding, Range("2026-09-01", "2026-09-30"));
        Assert.False(denied.Succeeded);

        var unknown = await CreateSut().GetPoOutstandingAsync("PO_NOT_A_MENU", Range("2026-09-01", "2026-09-30"));
        Assert.False(unknown.Succeeded);
    }

    [Fact]
    public async Task BranchIsolation_ExcludesOtherBranch()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open, rel: 1, branch: "OTHER"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 10m, branch: "OTHER"));
        });

        var result = await CreateSut().GetPoOutstandingAsync(
            MenuCodes.PurchaseOrderOutstanding, Range("2026-09-01", "2026-09-30"));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0, result.Data!.TotalCount);
    }

    // ============================ PO Outstanding ============================

    [Fact]
    public async Task PoOutstanding_LatestRevisionOnly_BalanceGreaterThanZero()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open, rel: 1));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 10m));
            db.PoOrders.Add(Po("PO1", "2026-09-11", PoOrderStatuses.Open, rel: 2));
            db.PoOrderDetails.Add(PoLine("PO1", 2, 1, 10m, balance: 5m));
            db.PoOrders.Add(Po("PO2", "2026-09-12", PoOrderStatuses.Open, rel: 1));
            db.PoOrderDetails.Add(PoLine("PO2", 1, 1, 10m, balance: 0m));
            db.PoOrders.Add(Po("PO3", "2026-09-13", PoOrderStatuses.Cancelled, rel: 1));
            db.PoOrderDetails.Add(PoLine("PO3", 1, 1, 10m, balance: 10m));
        });

        var result = await CreateSut().GetPoOutstandingAsync(
            MenuCodes.PurchaseOrderOutstanding, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.TotalCount);
        var row = Assert.Single(result.Data.Rows);
        Assert.Equal("PO1", row.PoNo);
        Assert.Equal(2, row.PoRelNo);
        Assert.Equal(5m, row.BalanceQty);
    }

    [Fact]
    public async Task PoOutstanding_SupplierFilter_And_HalfOpenDates()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 10m));
            db.PoOrders.Add(Po("PO2", "2026-09-10", PoOrderStatuses.Open, vend: "V2"));
            db.PoOrderDetails.Add(PoLine("PO2", 1, 1, 10m, balance: 10m));
            db.PoOrders.Add(Po("PO3", "2026-10-01", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO3", 1, 1, 10m, balance: 10m));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.SuppCode = "V1";
        var result = await CreateSut().GetPoOutstandingAsync(MenuCodes.PurchaseOrderOutstanding, q);
        Assert.Equal(1, result.Data!.TotalCount);
        Assert.Equal("PO1", result.Data.Rows[0].PoNo);
    }

    // ============================ PR Status ============================

    [Fact]
    public async Task PrStatus_SumsLivePoConsumption_ExcludesCancelledPo()
    {
        await SeedAsync(db =>
        {
            db.PoPrs.Add(Pr("PR1", "2026-09-01"));
            db.PoPrDetails.Add(PrLine("PR1", 1, 100m));

            db.PoOrders.Add(Po("PO1", "2026-09-05", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 40m, balance: 40m, prNo: "PR1", prLine: 1));

            db.PoOrders.Add(Po("PO2", "2026-09-06", PoOrderStatuses.Cancelled));
            db.PoOrderDetails.Add(PoLine("PO2", 1, 1, 30m, balance: 30m, prNo: "PR1", prLine: 1));

            db.PoOrders.Add(Po("PO3", "2026-09-07", PoOrderStatuses.Open));
            db.PoOrderDetails.Add(PoLine("PO3", 1, 1, 25m, balance: 25m, prNo: "PR1", prLine: 1));
        });

        var result = await CreateSut().GetPrStatusAsync(
            MenuCodes.PurchasePrStatus, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(65m, row.ConsumedQty); // 40 + 25; cancelled 30 excluded
        Assert.Equal(35m, row.RemainingQty);
    }

    // ============================ Supplier Transactions ============================

    [Fact]
    public async Task SupplierTransactions_UnionIncludesAllDocKinds()
    {
        await SeedAsync(db =>
        {
            db.PoPrs.Add(Pr("PR1", "2026-09-01"));
            db.PoPrDetails.Add(PrLine("PR1", 1, 10m, amount: 100m, vendor: "V1"));
            db.PoOrders.Add(Po("PO1", "2026-09-02", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 10m, net: 200m, tax: 0m));
            db.PoInvoices.Add(Inv("INV1", "2026-09-03", 300m, vend: "V1"));
            db.PoCdns.Add(Cdn("CN1", "2026-09-04", 50m, PoCdnType.CreditNote, vend: "V1"));
            db.PoSbInvoices.Add(SbInv("SBI1", "2026-09-05", 400m, vend: "V1"));
            db.PoSbCdns.Add(SbCdn("SBC1", "2026-09-06", 60m, PoCdnType.CreditNote, vend: "V1"));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.SuppCode = "V1";
        var result = await CreateSut().GetSupplierTransactionsAsync(MenuCodes.PurchaseSupplierTransaction, q);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(6, result.Data!.TotalCount);
        Assert.Contains(result.Data.Rows, x => x.DocType == "PR");
        Assert.Contains(result.Data.Rows, x => x.DocType == "PO");
        Assert.Contains(result.Data.Rows, x => x.DocType == "INV");
        Assert.Contains(result.Data.Rows, x => x.DocType == "CN");
        Assert.Contains(result.Data.Rows, x => x.DocType == "SBI");
        Assert.Contains(result.Data.Rows, x => x.DocType == "SBC");
    }

    [Fact]
    public async Task SupplierHistory_PostedOnly_NetsInvPlusDnMinusCn_OmitsQtyCnAndSb()
    {
        await SeedAsync(db =>
        {
            db.PoInvoices.Add(Inv("INV1", "2026-09-10", 1000m, status: PoInvoiceStatuses.Posted, vend: "V1"));
            db.PoInvoices.Add(Inv("INV_NEW", "2026-09-11", 500m, status: PoInvoiceStatuses.New, vend: "V1"));
            db.PoInvoices.Add(Inv("CNQ", "2026-09-12", 100m, status: PoInvoiceStatuses.Posted, type: PoInvoiceTypes.CreditNote, vend: "V1"));
            db.PoCdns.Add(Cdn("CN1", "2026-09-13", 50m, PoCdnType.CreditNote, status: PoCdnStatus.Posted, vend: "V1"));
            db.PoCdns.Add(Cdn("DN1", "2026-09-14", 25m, PoCdnType.DebitNote, status: PoCdnStatus.Posted, vend: "V1"));
            db.PoSbInvoices.Add(SbInv("SBI1", "2026-09-15", 999m, vend: "V1"));
        });

        var q = Range("2026-09-01", "2026-09-30");
        q.SuppCode = "V1";
        var result = await CreateSut().GetSupplierPurchaseHistoryAsync(MenuCodes.PurchaseSupplierTransaction, q);
        Assert.True(result.Succeeded, result.Message);
        var month = Assert.Single(result.Data!);
        Assert.Equal(1, month.InvoiceCount);
        Assert.Equal(1000m, month.InvoiceTotal);
        Assert.Equal(1, month.CreditNoteCount);
        Assert.Equal(50m, month.CreditNoteTotal);
        Assert.Equal(1, month.DebitNoteCount);
        Assert.Equal(25m, month.DebitNoteTotal);
        Assert.Equal(975m, month.NetPurchase); // 1000 + 25 - 50
    }

    // ============================ Invoice / CDN ============================

    [Fact]
    public async Task InvoiceInquiry_FiltersByType_NoApClaims()
    {
        await SeedAsync(db =>
        {
            db.PoInvoices.Add(Inv("INV1", "2026-09-10", 100m));
            db.PoInvoices.Add(Inv("CN1", "2026-09-11", 20m, type: PoInvoiceTypes.CreditNote));
        });

        var onlyInv = Range("2026-09-01", "2026-09-30");
        onlyInv.Type = PoInvoiceTypes.Invoice;
        var result = await CreateSut().GetInvoiceInquiryAsync(MenuCodes.PurchaseInvoiceInquiry, onlyInv);
        Assert.Equal(1, result.Data!.TotalCount);
        Assert.Equal("INV1", result.Data.Rows[0].DocNo);
    }

    [Fact]
    public async Task CdnInquiry_PoCdnOnly_FiltersType()
    {
        await SeedAsync(db =>
        {
            db.PoCdns.Add(Cdn("CN1", "2026-09-10", 50m, PoCdnType.CreditNote));
            db.PoCdns.Add(Cdn("DN1", "2026-09-11", 60m, PoCdnType.DebitNote));
            db.PoInvoices.Add(Inv("CNQ", "2026-09-12", 20m, type: PoInvoiceTypes.CreditNote));
        });

        var onlyCn = Range("2026-09-01", "2026-09-30");
        onlyCn.Type = PoCdnType.CreditNote;
        var result = await CreateSut().GetCdnInquiryAsync(MenuCodes.PurchaseCdnInquiry, onlyCn);
        Assert.Equal(1, result.Data!.TotalCount);
        Assert.Equal("CN1", result.Data.Rows[0].DocNo);
    }

    // ============================ Doc Relationships ============================

    [Fact]
    public async Task DocRelationships_EmitsPrPoGrInvEdges()
    {
        await SeedAsync(db =>
        {
            db.PoOrders.Add(Po("PO1", "2026-09-10", PoOrderStatuses.Open, vend: "V1"));
            db.PoOrderDetails.Add(PoLine("PO1", 1, 1, 10m, balance: 5m, prNo: "PR1", prLine: 1));

            var batch = new IvTrxBatch
            {
                CompanyCode = Company,
                BranchCode = Branch,
                BatchNo = 1,
                TrxDtTime = DateTime.Parse("2026-09-12"),
                TrxType = IvTrxTypes.GoodsReceive,
                BatchStatus = "POSTED"
            };
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = Company,
                BranchCode = Branch,
                BatchNo = 1,
                TrxLineNo = 1,
                TrxType = IvTrxTypes.GoodsReceive,
                ToStdQty = 5m,
                ToPurQty = 5m,
                PoNo = "PO1",
                PoRelNo = 1,
                PoLineNo = 1
            });
            db.IvTrxBatches.Add(batch);

            db.PoInvoices.Add(Inv("INV1", "2026-09-15", 100m, vend: "V1"));
            db.PoInvoiceDetails.Add(new PoInvoiceDetail
            {
                CompanyCode = Company,
                BranchCode = Branch,
                DocNo = "INV1",
                Line = 1,
                ICode = "ITM1",
                Qty = 5m,
                PoNo = "PO1",
                PoRelNo = 1,
                PoLineNo = 1
            });
            db.PoCdns.Add(Cdn("CN1", "2026-09-16", 10m, PoCdnType.CreditNote, invNo: "INV1"));
        });

        var result = await CreateSut().GetDocumentRelationshipAsync(
            MenuCodes.PurchaseDocRelationship, Range("2026-09-01", "2026-09-30"));
        Assert.True(result.Succeeded, result.Message);
        var rels = result.Data!.Rows.Select(x => x.Relation).ToHashSet();
        Assert.Contains("PR→PO", rels);
        Assert.Contains("PO→GR", rels);
        Assert.Contains("PO→INV", rels);
        Assert.Contains("INV→CDN", rels);
    }

    // ============================ Self-billed e-Invoice ============================

    [Fact]
    public async Task SbEInvoice_UsesHighestSubmissionId_AndExcludesRegularInvoice()
    {
        await SeedAsync(db =>
        {
            db.PoSbInvoices.Add(SbInv("SBI1", "2026-09-10", 100m, irbmStatus: EInvoiceStatuses.Submitted));
            db.PoInvoices.Add(Inv("INV1", "2026-09-10", 100m));
            db.EInvDocSubmissions.Add(Submission(EInvoiceDocumentTypes.SelfBilledInvoice, "SBI1", "Submitted", "U1", "SUB-1"));
            db.EInvDocSubmissions.Add(Submission(EInvoiceDocumentTypes.SelfBilledInvoice, "SBI1", "Valid", "U2", "SUB-2"));
        });

        var result = await CreateSut().GetSbEInvoiceStatusAsync(
            MenuCodes.PurchaseSbEInvoiceInquiry, Range("2026-09-01", "2026-09-30"));
        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal("SBI1", row.DocNo);
        Assert.Equal(EInvoiceStatuses.Valid, row.LatestSubmissionStatus);

        var history = await CreateSut().GetSbEInvoiceSubmissionHistoryAsync(
            MenuCodes.PurchaseSbEInvoiceInquiry, EInvoiceDocumentTypes.SelfBilledInvoice, "SBI1");
        Assert.Equal(2, history.Data!.Count);
        Assert.Equal(EInvoiceStatuses.Valid, history.Data[^1].Status);
    }

    // ============================ Export cap smoke ============================

    [Fact]
    public async Task EmptyResult_ReturnsZeroTotal()
    {
        var result = await CreateSut().GetPoOutstandingAsync(
            MenuCodes.PurchaseOrderOutstanding, Range("2026-01-01", "2026-01-31"));
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Data!.TotalCount);
        Assert.Empty(result.Data.Rows);
    }

    // ============================ Plumbing ============================

    private PoPurchaseInquiryService CreateSut(string company = Company, bool canAccess = true)
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
        string? branch = Branch) => new()
    {
        CompanyCode = Company,
        BranchCode = branch!,
        PoNo = poNo,
        PoRelNo = rel,
        PoDate = DateTime.Parse(poDate),
        Status = status,
        VendCode = vend,
        VendName = "Vendor " + vend,
        RowVersion = [1]
    };

    private static PoOrderDetail PoLine(
        string poNo,
        short rel,
        short line,
        decimal qty,
        decimal balance,
        string? prNo = null,
        short? prLine = null,
        decimal net = 0m,
        decimal tax = 0m,
        string? branch = Branch,
        decimal? recvQty = null,
        decimal returnQty = 0m,
        decimal invoicedQty = 0m,
        DateTime? etaDate = null) => new()
    {
        CompanyCode = Company,
        BranchCode = branch!,
        PoNo = poNo,
        PoRelNo = rel,
        Line = line,
        ICode = "ITM1",
        PoPurQty = qty,
        BalanceQty = balance,
        RecvQty = recvQty ?? (qty - balance),
        ReturnQty = returnQty,
        InvoicedQty = invoicedQty,
        PrNo = prNo,
        PrLineNo = prLine,
        NetAmount = net,
        TaxAmount = tax,
        Amount = net,
        EtaDate = etaDate
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

    private static PoPrDetail PrLine(
        string prNo,
        short line,
        decimal purchaseQty,
        decimal amount = 0m,
        string? vendor = "V1") => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        PrNo = prNo,
        Line = line,
        ICode = "ITM1",
        PurchaseQty = purchaseQty,
        Qty = purchaseQty,
        Amount = amount,
        TaxAmount = 0m,
        VendorCd = vendor,
        Status = PoPrStatuses.New
    };

    private static PoInvoice Inv(
        string docNo,
        string docDate,
        decimal total,
        string status = PoInvoiceStatuses.Posted,
        string type = PoInvoiceTypes.Invoice,
        string? vend = "V1") => new()
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
        RowVersion = [1]
    };

    private static PoCdn Cdn(
        string docNo,
        string docDate,
        decimal total,
        string type,
        string status = PoCdnStatus.Posted,
        string? vend = "V1",
        string? invNo = null) => new()
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
        GrossAmnt = total,
        Taxes = 0m,
        InvNo = invNo,
        RowVersion = [1]
    };

    private static PoSbInvoice SbInv(
        string docNo,
        string docDate,
        decimal total,
        string? vend = "V1",
        string? irbmStatus = null) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        DocDate = DateTime.Parse(docDate),
        Status = PoInvoiceStatuses.Posted,
        VendorCode = vend!,
        VendorName = "Vendor " + vend,
        TotAmnt = total,
        GrossAmnt = total,
        Taxes = 0m,
        IrbmStatus = irbmStatus,
        RowVersion = [1]
    };

    private static PoSbCdn SbCdn(
        string docNo,
        string docDate,
        decimal total,
        string type,
        string? vend = "V1") => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DocNo = docNo,
        DocDate = DateTime.Parse(docDate),
        Status = PoInvoiceStatuses.Posted,
        Type = type,
        VendorCode = vend!,
        VendorName = "Vendor " + vend,
        TotAmnt = total,
        GrossAmnt = total,
        Taxes = 0m,
        OriginSbInvNo = "SBI0",
        RowVersion = [1]
    };

    private static EInvDocSubmission Submission(
        string docType,
        string docNo,
        string status,
        string? uuid = null,
        string submissionUuid = "SUB-1") => new()
    {
        CompanyId = Company,
        DocumentType = docType,
        DocumentNo = docNo,
        Status = status,
        Uuid = uuid,
        SubmissionUuid = submissionUuid
    };
}
