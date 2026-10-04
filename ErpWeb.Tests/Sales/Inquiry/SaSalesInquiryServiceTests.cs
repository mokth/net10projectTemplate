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
/// Sales Inquiry Phase 1 acceptance matrix (plan-salesReportsAndInquiries.prompt.md). Each test gets a
/// fresh SQLite database. Locked rules under test: tenant + branch isolation, half-open dates,
/// POSTED-only where relevant, CN/DN netted once (positive), the customer-transaction union, menu
/// gating (denied / unknown menu refused) and <see cref="EInvoiceStatuses.Normalize"/> on legacy rows.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesShared)]
public class SaSalesInquiryServiceTests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";
    private const string CustomerTransactionMenu = "SA_CUST_TRX";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaSalesInquiryServiceTests()
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

    // ============================ Customer Transaction ============================

    [Fact]
    public async Task CustomerTransactions_UnionEveryDocumentKind_OrderedNewestFirst()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.New, 100m));
            db.SaSos.Add(So("SO1", "2026-09-02", 200m));
            db.SaDos.Add(Do("DO1", "2026-09-03", 300m));
            db.SaInvoices.Add(Inv("INV1", "2026-09-04", 400m));
            db.SaCdns.Add(Cdn("CN1", "2026-09-05", 50m, SaCdnTypes.CreditNote));
            db.SaCdns.Add(Cdn("DN1", "2026-09-06", 60m, SaCdnTypes.DebitNote));
        });

        var result = await CreateSut().GetCustomerTransactionsAsync(
            CustomerTransactionMenu, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var page = result.Data!;
        Assert.Equal(6, page.TotalCount);
        Assert.Equal(["DN1", "CN1", "INV1", "DO1", "SO1", "QT1"], page.Rows.Select(x => x.DocNo).ToArray());
        Assert.Equal(["DN", "CN", "INV", "DO", "SO", "QT"], page.Rows.Select(x => x.DocType).ToArray());
    }

    [Fact]
    public async Task CustomerTransactions_IncludeDrafts_ButFilterByStatus()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_POSTED", "2026-09-10", 100m));
            db.SaInvoices.Add(Inv("INV_DRAFT", "2026-09-11", 200m, status: SaInvoiceStatuses.New));
        });

        var all = await CreateSut().GetCustomerTransactionsAsync(
            CustomerTransactionMenu, Range("2026-09-01", "2026-09-30"));
        Assert.Equal(2, all.Data!.TotalCount);

        var onlyPosted = Range("2026-09-01", "2026-09-30");
        onlyPosted.Status = SaInvoiceStatuses.Posted;
        var filtered = await CreateSut().GetCustomerTransactionsAsync(CustomerTransactionMenu, onlyPosted);
        Assert.Equal(1, filtered.Data!.TotalCount);
        Assert.Equal("INV_POSTED", filtered.Data.Rows[0].DocNo);
    }

    [Fact]
    public async Task CustomerTransactions_IsCompanyAndBranchIsolated()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_MINE", "2026-09-10", 100m));
            db.SaInvoices.Add(Inv("INV_OTHER_CO", "2026-09-10", 900m, company: "OTHER"));
            db.SaInvoices.Add(Inv("INV_OTHER_BR", "2026-09-10", 700m, branch: "KL"));
        });

        var result = await CreateSut().GetCustomerTransactionsAsync(
            CustomerTransactionMenu, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.TotalCount);
        Assert.Equal("INV_MINE", result.Data.Rows[0].DocNo);
    }

    [Fact]
    public async Task CustomerTransactions_DeniesAccessAndUnknownMenu()
    {
        var denied = await CreateSut(canAccess: false).GetCustomerTransactionsAsync(
            CustomerTransactionMenu, Range("2026-09-01", "2026-09-30"));
        Assert.False(denied.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, denied.ErrorCode);

        var unknown = await CreateSut().GetCustomerTransactionsAsync("SA_NOT_A_MENU", Range("2026-09-01", "2026-09-30"));
        Assert.False(unknown.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, unknown.ErrorCode);
    }

    // ============================ Customer Sales History ============================

    [Fact]
    public async Task SalesHistory_IsPostedOnly_AndNetsCnDnOnce()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV1", "2026-09-10", 1000m));
            db.SaInvoices.Add(Inv("INV_DRAFT", "2026-09-11", 999m, status: SaInvoiceStatuses.New));
            db.SaCdns.Add(Cdn("CN1", "2026-09-12", 100m, SaCdnTypes.CreditNote));
            db.SaCdns.Add(Cdn("DN1", "2026-09-13", 50m, SaCdnTypes.DebitNote));
            db.SaCdns.Add(Cdn("CN_DRAFT", "2026-09-14", 500m, SaCdnTypes.CreditNote, SaCdnStatuses.New));
        });

        var result = await CreateSut().GetCustomerSalesHistoryAsync(
            CustomerTransactionMenu, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!);
        Assert.Equal(2026, row.Year);
        Assert.Equal(9, row.Month);
        Assert.Equal(1, row.InvoiceCount);
        Assert.Equal(1000m, row.InvoiceTotal);
        Assert.Equal(1, row.CreditNoteCount);
        Assert.Equal(100m, row.CreditNoteTotal);
        Assert.Equal(1, row.DebitNoteCount);
        Assert.Equal(50m, row.DebitNoteTotal);
        // Net = Invoice + DN − CN, netted exactly once.
        Assert.Equal(950m, row.NetSales);
    }

    [Fact]
    public async Task SalesHistory_GroupsByCalendarMonth()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_SEP", "2026-09-10", 100m));
            db.SaInvoices.Add(Inv("INV_OCT", "2026-10-05", 200m));
        });

        var result = await CreateSut().GetCustomerSalesHistoryAsync(
            CustomerTransactionMenu, Range("2026-09-01", "2026-10-31"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.Count);
        Assert.Equal(["2026-09", "2026-10"], result.Data.Select(x => x.Period).ToArray());
        Assert.Equal(100m, result.Data[0].InvoiceTotal);
        Assert.Equal(200m, result.Data[1].InvoiceTotal);
    }

    // ============================ Quotation Status ============================

    [Fact]
    public async Task QtStatus_CurrentRevisionsOnly_AndComputesExpiry()
    {
        await SeedAsync(db =>
        {
            db.SaQts.Add(Qt("QT1", "2026-09-01", SaQtStatuses.New, 100m, isCurrent: true, validUntil: DateTime.Today.AddDays(-1)));
            db.SaQts.Add(Qt("QT2", "2026-09-02", SaQtStatuses.Sent, 200m, isCurrent: true, validUntil: DateTime.Today.AddDays(1)));
            db.SaQts.Add(Qt("QT1", "2026-08-01", SaQtStatuses.Superseded, 50m, isCurrent: false, custRel: 2));
        });

        var result = await CreateSut().GetQtStatusAsync(MenuCodes.SalesQtStatus, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.TotalCount);
        var expired = result.Data.Rows.Single(x => x.QtNo == "QT1");
        Assert.True(expired.IsExpired);
        var live = result.Data.Rows.Single(x => x.QtNo == "QT2");
        Assert.False(live.IsExpired);
    }

    // ============================ Sales Order Outstanding ============================

    [Fact]
    public async Task SoOutstanding_ShowsPersistedRollups_CurrentOnly()
    {
        await SeedAsync(db =>
        {
            var so = So("SO1", "2026-09-02", 500m);
            so.Details.Add(SoLine("SO1", 1, order: 10m, shipped: 4m, delivered: 4m, invoiced: 3m, balance: 6m));
            db.SaSos.Add(so);
        });

        var result = await CreateSut().GetSoOutstandingAsync(
            MenuCodes.SalesSoOutstanding, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal("SO1", row.SoNo);
        Assert.Equal(10m, row.OrderQty);
        Assert.Equal(4m, row.ShippedQty);
        Assert.Equal(4m, row.DeliveredQty);
        Assert.Equal(3m, row.InvoicedQty);
        Assert.Equal(6m, row.BalanceQty);
    }

    // ============================ Invoice Inquiry ============================

    [Fact]
    public async Task InvoiceInquiry_IncludesNewAndPosted_SeparatesEInvoiceLabel()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_NEW", "2026-09-10", 100m, status: SaInvoiceStatuses.New));
            db.SaInvoices.Add(Inv("INV_POSTED", "2026-09-11", 200m, irbmStatus: EInvoiceStatuses.Valid));
            db.EInvDocSubmissions.Add(Submission("INV", "INV_POSTED", "Valid"));
        });

        var result = await CreateSut().GetInvoiceInquiryAsync(
            MenuCodes.SalesInvoiceInquiry, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.TotalCount);
        var draft = result.Data.Rows.Single(x => x.InvNo == "INV_NEW");
        Assert.Equal(SaInvoiceStatuses.New, draft.Status);
        Assert.Equal("Not submitted", draft.EInvoiceStatusLabel);
        var posted = result.Data.Rows.Single(x => x.InvNo == "INV_POSTED");
        Assert.Equal(SaInvoiceStatuses.Posted, posted.Status);
        Assert.Equal(EInvoiceStatuses.Valid, posted.EInvoiceStatusLabel);
        Assert.Equal(200m, posted.TotAmnt);
    }

    [Fact]
    public async Task InvoiceInquiry_SearchAndStatusFilter_AndRelatedDocs()
    {
        await SeedAsync(db =>
        {
            var inv = Inv("INV_LINK", "2026-09-12", 150m);
            inv.PoNo = "PO-99";
            inv.CustName = "Customer One";
            inv.Details.Add(new SaInvoiceDetail
            {
                CompanyCode = Company,
                BranchCode = Branch,
                InvNo = "INV_LINK",
                Line = 1,
                ICode = "ITM1",
                Qty = 1m,
                UnitPrice = 150m,
                NetAmount = 150m,
                SoNo = "SO9",
                CustRel = 2,
                DoNo = "DO9"
            });
            db.SaInvoices.Add(inv);
            db.SaInvoices.Add(Inv("INV_OTHER", "2026-09-13", 50m, status: SaInvoiceStatuses.New));
        });

        var bySearch = await CreateSut().GetInvoiceInquiryAsync(
            MenuCodes.SalesInvoiceInquiry,
            new SaInquiryQuery
            {
                DateFrom = DateTime.Parse("2026-09-01"),
                DateTo = DateTime.Parse("2026-09-30"),
                SearchText = "PO-99"
            });
        Assert.True(bySearch.Succeeded, bySearch.Message);
        var linked = Assert.Single(bySearch.Data!.Rows);
        Assert.Equal("INV_LINK", linked.InvNo);
        Assert.Equal("SO9", linked.RelatedSoNo);
        Assert.Equal((short)2, linked.RelatedSoCustRel);
        Assert.Equal("DO9", linked.RelatedDoNo);

        var onlyNew = await CreateSut().GetInvoiceInquiryAsync(
            MenuCodes.SalesInvoiceInquiry,
            new SaInquiryQuery
            {
                DateFrom = DateTime.Parse("2026-09-01"),
                DateTo = DateTime.Parse("2026-09-30"),
                Status = SaInvoiceStatuses.New
            });
        Assert.True(onlyNew.Succeeded, onlyNew.Message);
        Assert.All(onlyNew.Data!.Rows, r => Assert.Equal(SaInvoiceStatuses.New, r.Status));
    }

    // ============================ SO Transactions ============================

    [Fact]
    public async Task SoTransactions_IncludesSuperseded_CurrentOnlyFilter_OutstandingUnchanged()
    {
        await SeedAsync(db =>
        {
            var current = So("SO1", "2026-09-02", 500m);
            current.CustRel = 2;
            current.IsCurrent = true;
            current.Status = SaSoStatuses.New;
            current.Details.Add(SoLine("SO1", 1, 10m, 0m, 0m, 0m, 10m));
            current.Details.Last().CustRel = 2;

            var prior = So("SO1", "2026-09-01", 400m);
            prior.CustRel = 1;
            prior.IsCurrent = false;
            prior.Status = SaSoStatuses.Superseded;
            prior.Details.Add(SoLine("SO1", 1, 8m, 0m, 0m, 0m, 8m));

            db.SaSos.Add(current);
            db.SaSos.Add(prior);
        });

        var all = await CreateSut().GetSoTransactionsAsync(
            MenuCodes.SalesSoTransactions, Range("2026-09-01", "2026-09-30"));
        Assert.True(all.Succeeded, all.Message);
        Assert.Equal(2, all.Data!.TotalCount);
        Assert.Contains(all.Data.Rows, r => !r.IsCurrent && r.Rev == 1);
        Assert.Contains(all.Data.Rows, r => r.IsCurrent && r.Rev == 2);

        var currentOnly = await CreateSut().GetSoTransactionsAsync(
            MenuCodes.SalesSoTransactions,
            new SaInquiryQuery
            {
                DateFrom = DateTime.Parse("2026-09-01"),
                DateTo = DateTime.Parse("2026-09-30"),
                CurrentOnly = true
            });
        Assert.True(currentOnly.Succeeded, currentOnly.Message);
        var currentRow = Assert.Single(currentOnly.Data!.Rows);
        Assert.True(currentRow.IsCurrent);
        Assert.Equal(2, currentRow.Rev);

        var outstanding = await CreateSut().GetSoOutstandingAsync(
            MenuCodes.SalesSoOutstanding, Range("2026-09-01", "2026-09-30"));
        Assert.True(outstanding.Succeeded, outstanding.Message);
        var open = Assert.Single(outstanding.Data!.Rows);
        Assert.Equal(2, open.Rev);
        Assert.Equal(10m, open.OrderQty);
    }

    // ============================ Price History ============================

    [Fact]
    public async Task PriceHistory_PostedLinesOnly_NetUnitFromPersistedNetAmount()
    {
        await SeedAsync(db =>
        {
            var posted = Inv("INV_P", "2026-09-10", 100m);
            posted.Currency = "MYR";
            posted.Details.Add(new SaInvoiceDetail
            {
                CompanyCode = Company,
                BranchCode = Branch,
                InvNo = "INV_P",
                Line = 1,
                ICode = "ITM1",
                IDesc = "Item one",
                Qty = 4m,
                UnitPrice = 30m,
                ItemDiscAmount = 20m,
                NetAmount = 100m,
                StdUom = "PCS",
                FrWarehouse = "WH1"
            });
            posted.Details.Add(new SaInvoiceDetail
            {
                CompanyCode = Company,
                BranchCode = Branch,
                InvNo = "INV_P",
                Line = 2,
                ICode = "ITM2",
                Qty = 0m,
                UnitPrice = 10m,
                NetAmount = 0m,
                StdUom = "BOX"
            });
            db.SaInvoices.Add(posted);

            var draft = Inv("INV_D", "2026-09-11", 50m, status: SaInvoiceStatuses.New);
            draft.Details.Add(new SaInvoiceDetail
            {
                CompanyCode = Company,
                BranchCode = Branch,
                InvNo = "INV_D",
                Line = 1,
                ICode = "ITM1",
                Qty = 1m,
                UnitPrice = 50m,
                NetAmount = 50m
            });
            db.SaInvoices.Add(draft);
        });

        var result = await CreateSut().GetSalesPriceHistoryAsync(
            MenuCodes.SalesPriceHistory, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Data!.TotalCount);
        Assert.DoesNotContain(result.Data.Rows, r => r.InvNo == "INV_D");

        var priced = result.Data.Rows.Single(x => x.ICode == "ITM1");
        Assert.Equal(100m, priced.NetAmount);
        Assert.Equal(25m, priced.NetUnitPrice);
        Assert.Equal("MYR", priced.Currency);
        Assert.Equal("PCS", priced.Uom);

        var zeroQty = result.Data.Rows.Single(x => x.ICode == "ITM2");
        Assert.Null(zeroQty.NetUnitPrice);

        var byItem = await CreateSut().GetSalesPriceHistoryAsync(
            MenuCodes.SalesPriceHistory,
            new SaInquiryQuery
            {
                DateFrom = DateTime.Parse("2026-09-01"),
                DateTo = DateTime.Parse("2026-09-30"),
                ItemCode = "ITM1"
            });
        Assert.True(byItem.Succeeded, byItem.Message);
        Assert.Single(byItem.Data!.Rows);
    }

    // ============================ Delivery Order Status ============================

    [Fact]
    public async Task DoStatus_ShowsLinesWithSoAndInvoiceRefs()
    {
        await SeedAsync(db =>
        {
            var d = Do("DO1", "2026-09-03", 300m);
            d.Details.Add(new SaDoDetail
            {
                CompanyCode = Company,
                BranchCode = Branch,
                DoNo = "DO1",
                Line = 1,
                SoNo = "SO1",
                InvNo = "INV1",
                ICode = "ITM1",
                IDesc = "Item one",
                Qty = 5m,
                Amount = 300m
            });
            db.SaDos.Add(d);
        });

        var result = await CreateSut().GetDoStatusAsync(MenuCodes.SalesDoStatus, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal("DO1", row.DoNo);
        Assert.Equal("SO1", row.SoNo);
        Assert.Equal("INV1", row.InvNo);
        Assert.Equal(5m, row.Qty);
    }

    // ============================ Document Relationship ============================

    [Fact]
    public async Task DocumentRelationship_ReadsAllFourLinkKinds()
    {
        await SeedAsync(db =>
        {
            db.SaSos.Add(So("SO1", "2026-09-01", 100m));
            db.SaDos.Add(Do("DO1", "2026-09-02", 100m));
            db.SaInvoices.Add(Inv("INV1", "2026-09-03", 100m));
            db.SaInvoiceDetails.Add(new SaInvoiceDetail
            {
                CompanyCode = Company,
                BranchCode = Branch,
                InvNo = "INV1",
                Line = 1,
                SoNo = "SO1",
                DoNo = "DO1",
                Qty = 1m,
                Amount = 100m
            });
            db.SaDocApplications.Add(App("SO", "SO1", "DO", "DO1", 10m));
            db.SaDocApplications.Add(App("DO", "DO1", "INV", "INV1", 8m));
        });

        var result = await CreateSut().GetDocumentRelationshipAsync(
            MenuCodes.SalesInvVsDoc, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var relations = result.Data!.Rows.Select(x => x.Relation).ToHashSet();
        Assert.Contains("SO→DO", relations);
        Assert.Contains("DO→INV", relations);
        Assert.Contains("INV→DO", relations);
        Assert.Contains("INV→SO", relations);
    }

    // ============================ CN/DN Inquiry ============================

    [Fact]
    public async Task CdnInquiry_FiltersByType()
    {
        await SeedAsync(db =>
        {
            db.SaCdns.Add(Cdn("CN1", "2026-09-05", 50m, SaCdnTypes.CreditNote));
            db.SaCdns.Add(Cdn("DN1", "2026-09-06", 60m, SaCdnTypes.DebitNote));
        });

        var all = await CreateSut().GetCdnInquiryAsync(MenuCodes.SalesCdnInquiry, Range("2026-09-01", "2026-09-30"));
        Assert.Equal(2, all.Data!.TotalCount);

        var onlyCn = Range("2026-09-01", "2026-09-30");
        onlyCn.Type = SaCdnTypes.CreditNote;
        var filtered = await CreateSut().GetCdnInquiryAsync(MenuCodes.SalesCdnInquiry, onlyCn);
        Assert.Equal(1, filtered.Data!.TotalCount);
        Assert.Equal("CN1", filtered.Data.Rows[0].DocNo);
    }

    // ============================ e-Invoice ============================

    [Fact]
    public async Task EInvoiceStatus_DistinguishesNotSubmitted_FromSubmitted()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_SUB", "2026-09-10", 100m, irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-1"));
            db.SaInvoices.Add(Inv("INV_NONE", "2026-09-11", 200m));
            db.EInvDocSubmissions.Add(Submission("INV", "INV_SUB", "Valid", "UUID-1"));
        });

        var result = await CreateSut().GetEInvoiceStatusAsync(
            MenuCodes.SalesEInvoiceInquiry, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var submitted = result.Data!.Rows.Single(x => x.DocNo == "INV_SUB");
        Assert.True(submitted.IsSubmitted);
        Assert.False(submitted.IsNotSubmitted);
        Assert.Equal(EInvoiceStatuses.Valid, submitted.LatestSubmissionStatus);
        Assert.Equal("VALID", submitted.StatusLabel);

        var none = result.Data.Rows.Single(x => x.DocNo == "INV_NONE");
        Assert.False(none.IsSubmitted);
        Assert.True(none.IsNotSubmitted);
        Assert.Equal("Not submitted", none.StatusLabel);
    }

    [Fact]
    public async Task EInvoiceStatus_NormalizesLegacyTitleCaseRegistryStatus()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV1", "2026-09-10", 100m, irbmStatus: "SUBMITTED"));
            db.EInvDocSubmissions.Add(Submission("INV", "INV1", "Submitted", "UUID-1"));
        });

        var result = await CreateSut().GetEInvoiceStatusAsync(
            MenuCodes.SalesEInvoiceInquiry, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(EInvoiceStatuses.Submitted, row.LatestSubmissionStatus);
        Assert.False(row.IsUnknownStatus);
    }

    [Fact]
    public async Task EInvoiceStatus_FlagsUnknownUnmappedRegistryStatus()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV1", "2026-09-10", 100m, irbmStatus: "SUBMITTED"));
            db.EInvDocSubmissions.Add(Submission("INV", "INV1", "BogusStatus", "UUID-1"));
        });

        var result = await CreateSut().GetEInvoiceStatusAsync(
            MenuCodes.SalesEInvoiceInquiry, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var row = Assert.Single(result.Data!.Rows);
        Assert.True(row.IsSubmitted);
        Assert.True(row.IsUnknownStatus);
        Assert.Equal("Unknown (unmapped)", row.StatusLabel);
    }

    [Fact]
    public async Task EInvoiceHistory_IsChronological_AndUsesTheHighestIdAsCurrent()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV1", "2026-09-10", 100m, irbmStatus: "VALID"));
            db.EInvDocSubmissions.Add(Submission("INV", "INV1", "Submitted", "UUID-1", "SUB-1"));
            db.EInvDocSubmissions.Add(Submission("INV", "INV1", "Valid", "UUID-2", "SUB-2"));
        });

        var history = await CreateSut().GetEInvoiceSubmissionHistoryAsync(
            MenuCodes.SalesEInvoiceInquiry, "INV", "INV1");
        Assert.True(history.Succeeded, history.Message);
        Assert.Equal(2, history.Data!.Count);
        Assert.Equal([1, 2], history.Data.Select(x => x.Id).ToArray());
        Assert.Equal("SUBMITTED", history.Data[0].Status);
        Assert.Equal("VALID", history.Data[1].Status);
    }

    [Fact]
    public async Task EInvoiceReconciliation_ReportsMatchAndStatusDiffers()
    {
        await SeedAsync(db =>
        {
            db.SaInvoices.Add(Inv("INV_MATCH", "2026-09-10", 100m, irbmStatus: EInvoiceStatuses.Valid));
            db.SaInvoices.Add(Inv("INV_DIFF", "2026-09-11", 200m, irbmStatus: EInvoiceStatuses.Valid));
            db.SaInvoices.Add(Inv("INV_NONE", "2026-09-12", 300m));
            db.EInvDocSubmissions.Add(Submission("INV", "INV_MATCH", "Valid"));
            db.EInvDocSubmissions.Add(Submission("INV", "INV_DIFF", "Cancelled"));
        });

        var result = await CreateSut().GetEInvoiceReconciliationAsync(
            MenuCodes.SalesEInvoiceInquiry, Range("2026-09-01", "2026-09-30"));

        Assert.True(result.Succeeded, result.Message);
        var match = result.Data!.Rows.Single(x => x.DocNo == "INV_MATCH");
        Assert.Equal("match", match.Finding);
        var differs = result.Data.Rows.Single(x => x.DocNo == "INV_DIFF");
        Assert.Equal("status differs", differs.Finding);
        var none = result.Data.Rows.Single(x => x.DocNo == "INV_NONE");
        Assert.Equal("not submitted", none.Finding);
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

    private static SaInquiryQuery Range(string from, string to) => new()
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

    private static SaQt Qt(
        string qtNo,
        string qtDate,
        string status,
        decimal total,
        bool isCurrent = true,
        short custRel = 1,
        DateTime? validUntil = null) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        QtNo = qtNo,
        CustRel = custRel,
        IsCurrent = isCurrent,
        QtDate = DateTime.Parse(qtDate),
        ValidUntil = validUntil ?? DateTime.Parse(qtDate).AddDays(30),
        Status = status,
        CustCode = "C1",
        CustName = "Customer One",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaSo So(string soNo, string soDate, decimal total) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        SoNo = soNo,
        CustRel = 1,
        IsCurrent = true,
        SoDate = DateTime.Parse(soDate),
        Status = SaSoStatuses.New,
        FulfillmentStatus = "NONE",
        BillingStatus = "NONE",
        CustCode = "C1",
        CustName = "Customer One",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaSoDetail SoLine(string soNo, short line, decimal order, decimal shipped, decimal delivered, decimal invoiced, decimal balance) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        SoNo = soNo,
        CustRel = 1,
        Line = line,
        ICode = "ITM1",
        IDesc = "Item one",
        OrderQty = order,
        ShippedQty = shipped,
        DeliveredQty = delivered,
        InvoicedQty = invoiced,
        BalanceQty = balance
    };

    private static SaDo Do(string doNo, string doDate, decimal total) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        DoNo = doNo,
        DoDate = DateTime.Parse(doDate),
        Status = SaDoStatuses.Posted,
        BillingStatus = "NONE",
        CustCode = "C1",
        CustName = "Customer One",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaInvoice Inv(
        string invNo,
        string invDate,
        decimal total,
        string status = SaInvoiceStatuses.Posted,
        string? salesman = null,
        string? cust = "C1",
        string? branch = Branch,
        string company = Company,
        string? irbmStatus = null,
        string? irbmUuid = null) => new()
    {
        CompanyCode = company,
        BranchCode = branch,
        InvNo = invNo,
        InvDate = DateTime.Parse(invDate),
        Status = status,
        DoNo = invNo,
        CustCode = cust,
        SalesmanCode = salesman,
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total,
        IrbmStatus = irbmStatus,
        IrbmUuid = irbmUuid
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
        CustName = "Customer One",
        GrossAmnt = total,
        Taxes = 0m,
        TotAmnt = total
    };

    private static SaDocApplication App(string sourceType, string sourceId, string targetType, string targetId, decimal qty) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        SourceDocType = sourceType,
        SourceDocId = sourceId,
        SourceCustRel = 1,
        SourceLineId = 1,
        TargetDocType = targetType,
        TargetDocId = targetId,
        TargetCustRel = 1,
        TargetLineId = 1,
        RelatedSoNo = "SO1",
        RelatedCustRel = 1,
        RelatedSoLine = 1,
        AppliedQty = qty
    };

    private static EInvDocSubmission Submission(string docType, string docNo, string status, string? uuid = null, string submissionUuid = "SUB-1") => new()
    {
        CompanyId = Company,
        DocumentType = docType,
        DocumentNo = docNo,
        Status = status,
        Uuid = uuid,
        SubmissionUuid = submissionUuid
    };
}
