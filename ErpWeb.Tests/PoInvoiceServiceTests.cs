using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests;

internal sealed class FakePoInvoiceDocumentNumberingService : IDocumentNumberingService
{
    private int _seq;

    public int IssuedCount => _seq;

    public Task<DocumentNumberResult> NextAsync(
        AppDbContext db,
        string module,
        string extraPrefix,
        DateTime documentDate,
        DocumentNumberRequestMode requestMode,
        string currentDocNo,
        CancellationToken ct)
    {
        if (requestMode == DocumentNumberRequestMode.Edit
            && !string.Equals(currentDocNo, "AUTO", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(currentDocNo))
        {
            return Task.FromResult(new DocumentNumberResult(currentDocNo.Trim(), null));
        }

        if (!string.Equals(module, PoInvoiceLimits.NumberingModule, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected module '{module}'.");
        }

        var n = Interlocked.Increment(ref _seq);
        return Task.FromResult(new DocumentNumberResult($"PINV{documentDate:yy}{documentDate:MM}-{n:D4}", "PINV"));
    }
}

public class PoOrderCalcPriceToleranceTests
{
    [Fact]
    public void ValidatePriceTolerance_exact_at_tolerance_accepted()
    {
        Assert.True(PoOrderCalc.ValidatePriceTolerance(100m, 105m, 5m, 6, out _));
    }

    [Fact]
    public void ValidatePriceTolerance_over_tolerance_rejected()
    {
        Assert.False(PoOrderCalc.ValidatePriceTolerance(100m, 106m, 5m, 6, out var err));
        Assert.Contains("varies", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePriceTolerance_zero_po_requires_zero_inv()
    {
        Assert.True(PoOrderCalc.ValidatePriceTolerance(0m, 0m, 0m, 6, out _));
        Assert.False(PoOrderCalc.ValidatePriceTolerance(0m, 1m, 100m, 6, out _));
    }

    [Fact]
    public void RecalculateFinClosed_sets_and_clears_with_exact_match()
    {
        var po = new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = "PO1",
            PoRelNo = 0,
            Status = PoOrderStatuses.Received,
            FinClosed = false
        };
        var line = new PoOrderDetail
        {
            PoPurQty = 100m,
            RecvQty = 100m,
            ReturnQty = 0m,
            BalanceQty = 0m,
            OverRecvQty = 0m,
            InvoicedQty = 100m
        };

        PoOrderCalc.RecalculateFinClosed(po, [line], "user", DateTime.UtcNow);
        Assert.True(po.FinClosed);
        Assert.NotNull(po.FinClosedOn);
        Assert.Equal("user", po.FinClosedBy);

        line.InvoicedQty = 90m;
        PoOrderCalc.RecalculateFinClosed(po, [line], "user", DateTime.UtcNow);
        Assert.False(po.FinClosed);
        Assert.Null(po.FinClosedOn);
        Assert.Null(po.FinClosedBy);
    }
}

public class PoInvoiceServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoInvoiceServiceTests()
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
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", CurrDesc = "Ringgit", IsActive = true });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "SR",
            TaxGrDesc = "Standard",
            Percentage = 0m,
            TaxGlCode = "GLTAX"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock A",
            IClassCode = "RAW",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 10m,
            PurchaseGlCode = "GLPUR",
            PurchaseTaxGroup = "SR",
            IsActive = true,
            DefWarehouse = "MAIN",
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SuppCode = "SUP01",
            SuppName = "Alpha Supplier",
            Currency = "MYR",
            GlCode = "AP001",
            Country = "MY",
            IsActive = true,
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        db.PoVendorByItems.Add(new PoVendorByItem
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Vendor = "SUP01",
            ICode = "A100",
            PurUom = "EA",
            UnitPrice = 10m,
            Tolerance = 10m,
            PriceTolerance = 5m,
            OrdLevel = 0m,
            Status = "A",
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        await db.SaveChangesAsync();
        await SeedPoAsync(db, ordered: 100m, recv: 100m, returned: 0m, invoiced: 0m);
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Save_and_Post_full_invoice_sets_FinClosed()
    {
        var numbering = new FakePoInvoiceDocumentNumberingService();
        var sut = CreateSut(numbering);
        var save = await sut.SaveNewAsync(InvRequest(qty: 100m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(1, numbering.IssuedCount);
        Assert.Equal(PoInvoiceStatuses.New, save.Document!.Status);

        var post = await sut.PostAsync(
        [
            new PoInvoiceKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document.RowVersion }
        ]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var po = await db.PoOrders.Include(x => x.Details)
            .FirstAsync(x => x.PoNo == "PO-TEST");
        Assert.Equal(100m, po.Details.Single().InvoicedQty);
        Assert.True(po.FinClosed);
        Assert.NotNull(po.FinClosedBy);
    }

    [Fact]
    public async Task Invoice_above_net_received_rejected()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(InvRequest(qty: 101m, price: 10m));
        Assert.False(save.Succeeded);
        Assert.Contains("invoiceable", string.Join(" ", save.ValidationErrors.Values), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Price_over_tolerance_rejected_exact_at_tolerance_accepted()
    {
        var sut = CreateSut();
        var over = await sut.SaveNewAsync(InvRequest(qty: 10m, price: 10.60m));
        Assert.False(over.Succeeded);

        var ok = await sut.SaveNewAsync(InvRequest(qty: 10m, price: 10.50m));
        Assert.True(ok.Succeeded, ok.ErrorMessage);
    }

    [Fact]
    public async Task Invoice_UOM_mismatch_rejected()
    {
        var sut = CreateSut();
        var req = InvRequest(qty: 10m, price: 10m);
        req.Lines = [new PoInvoiceLineRequest
        {
            ICode = "A100",
            Qty = 10m,
            UnitPrice = 10m,
            SellingUom = "KG",
            PoNo = "PO-TEST",
            PoRelNo = 0,
            PoLineNo = 1,
            TaxGroup = "SR",
            ItemGlCode = "GLPUR"
        }];
        var save = await sut.SaveNewAsync(req);
        Assert.False(save.Succeeded);
    }

    [Fact]
    public async Task Invoice_before_GR_rejected()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.PoOrderDetails.FirstAsync(x => x.PoNo == "PO-TEST");
            line.RecvQty = 0m;
            line.BalanceQty = 100m;
            line.OverRecvQty = 0m;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var save = await sut.SaveNewAsync(InvRequest(qty: 10m, price: 10m));
        Assert.False(save.Succeeded);
        Assert.Contains("receipt", string.Join(" ", save.ValidationErrors.Values), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_then_balance_invoice_closes_FinClosed()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(InvRequest(qty: 40m, price: 10m, supplierInvNo: "S-1"));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(first)])).Succeeded);

        var second = await sut.SaveNewAsync(InvRequest(qty: 60m, price: 10m, supplierInvNo: "S-2"));
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(second)])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var po = await db.PoOrders.Include(x => x.Details).FirstAsync(x => x.PoNo == "PO-TEST");
        Assert.Equal(100m, po.Details.Single().InvoicedQty);
        Assert.True(po.FinClosed);
    }

    [Fact]
    public async Task CN_exceeding_INV_remaining_rejected_within_accepted()
    {
        var sut = CreateSut();
        var inv = await sut.SaveNewAsync(InvRequest(qty: 40m, price: 10m, supplierInvNo: "S-40"));
        Assert.True(inv.Succeeded, inv.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(inv)])).Succeeded);

        var over = await sut.SaveNewAsync(CnRequest(inv.DocNo!, qty: 41m, price: 10m));
        Assert.False(over.Succeeded);

        var ok = await sut.SaveNewAsync(CnRequest(inv.DocNo!, qty: 20m, price: 10m));
        Assert.True(ok.Succeeded, ok.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(ok)])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var po = await db.PoOrders.Include(x => x.Details).FirstAsync(x => x.PoNo == "PO-TEST");
        Assert.Equal(20m, po.Details.Single().InvoicedQty);
        Assert.False(po.FinClosed);
    }

    [Fact]
    public async Task CN_rollback_restores_InvoicedQty_and_FinClosed()
    {
        var sut = CreateSut();
        var inv = await sut.SaveNewAsync(InvRequest(qty: 100m, price: 10m));
        Assert.True(inv.Succeeded, inv.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(inv)])).Succeeded);

        var cn = await sut.SaveNewAsync(CnRequest(inv.DocNo!, qty: 20m, price: 10m));
        Assert.True(cn.Succeeded, cn.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(cn)])).Succeeded);

        var cnDoc = (await sut.GetAsync(cn.DocNo!)).Document!;
        Assert.True((await sut.RollbackAsync([new PoInvoiceKeyedRequest { DocNo = cnDoc.DocNo, RowVersion = cnDoc.RowVersion }])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var po = await db.PoOrders.Include(x => x.Details).FirstAsync(x => x.PoNo == "PO-TEST");
        Assert.Equal(100m, po.Details.Single().InvoicedQty);
        Assert.True(po.FinClosed);
    }

    [Fact]
    public async Task Cannot_rollback_INV_while_posted_CN_references_it()
    {
        var sut = CreateSut();
        var inv = await sut.SaveNewAsync(InvRequest(qty: 50m, price: 10m));
        Assert.True(inv.Succeeded, inv.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(inv)])).Succeeded);

        var cn = await sut.SaveNewAsync(CnRequest(inv.DocNo!, qty: 10m, price: 10m));
        Assert.True(cn.Succeeded, cn.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(cn)])).Succeeded);

        var invDoc = (await sut.GetAsync(inv.DocNo!)).Document!;
        var rb = await sut.RollbackAsync([new PoInvoiceKeyedRequest { DocNo = invDoc.DocNo, RowVersion = invDoc.RowVersion }]);
        Assert.False(rb.Succeeded);
        Assert.Contains("credit notes", rb.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Double_post_does_not_change_qty()
    {
        var sut = CreateSut();
        var inv = await sut.SaveNewAsync(InvRequest(qty: 30m, price: 10m));
        Assert.True(inv.Succeeded, inv.ErrorMessage);
        var first = await sut.PostAsync([Keyed(inv)]);
        Assert.True(first.Succeeded, first.ErrorMessage);

        var posted = (await sut.GetAsync(inv.DocNo!)).Document!;
        var second = await sut.PostAsync([new PoInvoiceKeyedRequest { DocNo = posted.DocNo, RowVersion = posted.RowVersion }]);
        Assert.False(second.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.PoOrderDetails.FirstAsync(x => x.PoNo == "PO-TEST");
        Assert.Equal(30m, line.InvoicedQty);
    }

    [Fact]
    public async Task VR_after_full_invoice_creates_OverInvoiced_CN_restores()
    {
        var sut = CreateSut();
        var inv = await sut.SaveNewAsync(InvRequest(qty: 100m, price: 10m));
        Assert.True(inv.Succeeded, inv.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(inv)])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.PoOrderDetails.FirstAsync(x => x.PoNo == "PO-TEST");
            line.ReturnQty = 10m;
            PoOrderCalc.ApplyComputedQtyFields(line);
            var po = await db.PoOrders.Include(x => x.Details).FirstAsync(x => x.PoNo == "PO-TEST");
            PoOrderCalc.RecalculateFinClosed(po, po.Details, "user", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.PoOrderDetails.FirstAsync(x => x.PoNo == "PO-TEST");
            Assert.Equal(10m, PoOrderCalc.ComputeOverInvoiced(line.RecvQty, line.ReturnQty, line.InvoicedQty));
            var po = await db.PoOrders.FirstAsync(x => x.PoNo == "PO-TEST");
            Assert.False(po.FinClosed);
        }

        var cn = await sut.SaveNewAsync(CnRequest(inv.DocNo!, qty: 10m, price: 10m));
        Assert.True(cn.Succeeded, cn.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(cn)])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.PoOrderDetails.FirstAsync(x => x.PoNo == "PO-TEST");
            Assert.Equal(90m, line.InvoicedQty);
            Assert.Equal(0m, PoOrderCalc.ComputeOverInvoiced(line.RecvQty, line.ReturnQty, line.InvoicedQty));
            Assert.Equal(10m, line.BalanceQty);
            var po = await db.PoOrders.FirstAsync(x => x.PoNo == "PO-TEST");
            Assert.False(po.FinClosed);
        }
    }

    private static PoInvoiceKeyedRequest Keyed(PoInvoiceOperationResult result) =>
        new() { DocNo = result.DocNo!, RowVersion = result.Document!.RowVersion };

    private static PoInvoiceSaveRequest InvRequest(decimal qty, decimal price, string supplierInvNo = "SUP-INV-1") =>
        new()
        {
            Type = PoInvoiceTypes.Invoice,
            DocDate = FixedToday,
            VendorCode = "SUP01",
            InvNo = supplierInvNo,
            Currency = "MYR",
            CurrRate = 1m,
            Lines =
            [
                new PoInvoiceLineRequest
                {
                    ICode = "A100",
                    IDesc = "Stock A",
                    Qty = qty,
                    UnitPrice = price,
                    SellingUom = "EA",
                    PoNo = "PO-TEST",
                    PoRelNo = 0,
                    PoLineNo = 1,
                    TaxGroup = "SR",
                    ItemGlCode = "GLPUR"
                }
            ]
        };

    private static PoInvoiceSaveRequest CnRequest(string invDocNo, decimal qty, decimal price) =>
        new()
        {
            Type = PoInvoiceTypes.CreditNote,
            DocDate = FixedToday,
            VendorCode = "SUP01",
            InvNo = invDocNo,
            Currency = "MYR",
            CurrRate = 1m,
            Lines =
            [
                new PoInvoiceLineRequest
                {
                    ICode = "A100",
                    IDesc = "Stock A",
                    Qty = qty,
                    UnitPrice = price,
                    SellingUom = "EA",
                    PoNo = "PO-TEST",
                    PoRelNo = 0,
                    PoLineNo = 1,
                    TaxGroup = "SR",
                    ItemGlCode = "GLPUR"
                }
            ]
        };

    private static async Task SeedPoAsync(
        AppDbContext db,
        decimal ordered,
        decimal recv,
        decimal returned,
        decimal invoiced)
    {
        var po = new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = "PO-TEST",
            PoRelNo = 0,
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Supplier",
            CurCode = "MYR",
            Status = PoOrderStatuses.Received,
            TaxGrpCode = "SR",
            LocationCode = "MAIN",
            CreatedDate = FixedToday,
            CreatedBy = "user",
            RowVersion = Guid.NewGuid().ToByteArray(),
            Details =
            [
                new PoOrderDetail
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    PoNo = "PO-TEST",
                    PoRelNo = 0,
                    Line = 1,
                    ICode = "A100",
                    IDesc = "Stock A",
                    PoUnitPrice = 10m,
                    PoPurQty = ordered,
                    PoQty = ordered,
                    PackSz = 1m,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    RecvQty = recv,
                    ReturnQty = returned,
                    InvoicedQty = invoiced,
                    BalanceQty = PoOrderCalc.ComputeBalance(ordered, recv, returned),
                    OverRecvQty = PoOrderCalc.ComputeOverRecv(ordered, recv, returned),
                    TaxGroup = "SR",
                    ToWarehouse = "MAIN"
                }
            ]
        };
        db.PoOrders.Add(po);
        await db.SaveChangesAsync();
    }

    private PoInvoiceService CreateSut(FakePoInvoiceDocumentNumberingService? numbering = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: "DEMO",
            branch: "HQ",
            location: "MAIN",
            userId: "user");
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PurchaseInvoice,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new PoInvoiceService(
            _factory,
            tenant,
            access.Object,
            numbering ?? new FakePoInvoiceDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new PoInvoiceRepository(),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoInvoiceService>.Instance);
    }
}
