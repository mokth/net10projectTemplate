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
using CdnStatuses = ErpWeb.Core.Sales.SaCdnStatuses;
using CdnTypes = ErpWeb.Core.Sales.SaCdnTypes;

namespace ErpWeb.Tests;

// ─────────────────────────── Fake numbering ───────────────────────────

/// <summary>
/// Issues CN{yy}{MM}-{seq:D4} for CN module, DN{yy}{MM}-{seq:D4} for DN module,
/// and INV{yy}{MM}-{seq:D4} for anything else (so invoice seeding also works).
/// </summary>
internal sealed class FakeCdnDocumentNumberingService : IDocumentNumberingService
{
    private int _cnSeq;
    private int _dnSeq;
    private int _invSeq;

    public int CnIssuedCount => _cnSeq;
    public int DnIssuedCount => _dnSeq;

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

        string docNo;
        string prefix;
        var mod = (module ?? string.Empty).Trim().ToUpperInvariant();
        if (mod == "CN")
        {
            var n = Interlocked.Increment(ref _cnSeq);
            docNo = $"CN{documentDate:yy}{documentDate:MM}-{n:D4}";
            prefix = "CN";
        }
        else if (mod == "DN")
        {
            var n = Interlocked.Increment(ref _dnSeq);
            docNo = $"DN{documentDate:yy}{documentDate:MM}-{n:D4}";
            prefix = "DN";
        }
        else
        {
            var n = Interlocked.Increment(ref _invSeq);
            docNo = $"INV{documentDate:yy}{documentDate:MM}-{n:D4}";
            prefix = "INV";
        }

        return Task.FromResult(new DocumentNumberResult(docNo, prefix));
    }
}

// ─────────────────────────── Calc tests (pure, no DB) ───────────────────────────

public class SaCdnCalcTests
{
    // 1. Fingerprint is deterministic and field-sensitive
    [Fact]
    public void Fingerprint_deterministic_and_field_sensitive()
    {
        static SaCdnDetail Base() => new()
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DocNo = "CN2609-0001",
            Line = 1,
            ICode = "A100",
            FrWarehouse = "MAIN",
            LocCode = "BIN1",
            IStatus = "ACTIVE",
            LotNo = string.Empty,
            ExpiryDate = null,
            StdQty = 10m,
            StdUom = "EA",
            StockControl = true
        };

        var baseline = SaCdnCalc.ComputeSourceFingerprint(new[] { Base() });

        // Each mutation changes fingerprint
        var mutICode = Base(); mutICode.ICode = "B200";
        Assert.NotEqual(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { mutICode }));

        var mutWh = Base(); mutWh.FrWarehouse = "WH2";
        Assert.NotEqual(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { mutWh }));

        var mutLoc = Base(); mutLoc.LocCode = "BIN2";
        Assert.NotEqual(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { mutLoc }));

        var mutStat = Base(); mutStat.IStatus = "DAMAGED";
        Assert.NotEqual(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { mutStat }));

        var mutLot = Base(); mutLot.LotNo = "LOT001";
        Assert.NotEqual(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { mutLot }));

        var mutExpiry = Base(); mutExpiry.ExpiryDate = new DateTime(2027, 1, 1);
        Assert.NotEqual(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { mutExpiry }));

        var mutQty = Base(); mutQty.StdQty = 11m;
        Assert.NotEqual(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { mutQty }));

        var mutUom = Base(); mutUom.StdUom = "KG";
        Assert.NotEqual(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { mutUom }));

        // Same data => same fingerprint (deterministic)
        Assert.Equal(baseline, SaCdnCalc.ComputeSourceFingerprint(new[] { Base() }));
    }

    [Fact]
    public void EvaluateRemaining_exact_equals_ok_and_cent_over_fails()
    {
        var (ok, _, _) = SaCdnCalc.EvaluateRemaining(100m, [], 100m, decPoint: false);
        Assert.True(ok);

        var (fail, _, err) = SaCdnCalc.EvaluateRemaining(100m, [], 100.01m, decPoint: false);
        Assert.False(fail);
        Assert.NotNull(err);
    }

    [Fact]
    public void EvaluateRemaining_other_cn_reduces_remaining_to_negative_fails_closed()
    {
        // Invoice = 100, other CNs = 120 => remaining negative => fail closed
        var (fail, _, err) = SaCdnCalc.EvaluateRemaining(100m, [120m], 1m, decPoint: false);
        Assert.False(fail);
        Assert.Contains("negative", err!, StringComparison.OrdinalIgnoreCase);
    }
}

// ─────────────────────────── Service tests ───────────────────────────

public class SaCdnServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly FakeCdnDocumentNumberingService _numbering = new();

    public SaCdnServiceTests()
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

        // Warehouses and locations
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", LocCode = "BIN1", IsActive = true
        });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });

        // UOM and class
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });

        // Currencies
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "USD", IsActive = true });

        // USD rate
        db.SaCurrRates.Add(new SaCurrRate
        {
            CurrCode = "USD",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            HomeCurPerUnit = 4.5,
            Status = true
        });

        // Payment terms
        db.IvMsCodes.Add(new IvMsCode { Code = "NET30", Name = "Net 30", CodeType = IvMsCodeTypes.PayCode });
        db.SaPaymentTerms.Add(new SaPaymentTerm
        {
            CompanyCode = "DEMO", PayCode = "NET30", PayDesc = "Net 30", Days = 30, IsActive = true
        });

        // Salesperson
        db.SaSalesReps.Add(new SaSalesRep
        {
            CompanyCode = "DEMO", SrepCode = "SM1", SrepName = "Sales One", IsActive = true
        });

        // Tax groups
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO", TaxGrCode = "SR", TaxGrDesc = "Standard", Percentage = 6m, TaxGlCode = "GLTAX"
        });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO", TaxGrCode = "ZR", TaxGrDesc = "Zero", Percentage = 0m, TaxGlCode = "GLTAX0"
        });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO", TaxGrCode = "NOTAXGL", TaxGrDesc = "No tax GL", Percentage = 6m
        });
        db.SaSalesReps.Add(new SaSalesRep
        {
            CompanyCode = "DEMO", SrepCode = "SMINACTIVE", SrepName = "Inactive", IsActive = false
        });

        // Stock items
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            SellingPrice = 10m,
            SellingGlCode = "GLSALE",
            Classification = "CLASS-A",
            DefWarehouse = "MAIN",
            DefLocation = "BIN1"
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
            SellingGlCode = "GLSVC",
            Classification = "CLASS-S"
        });

        // Main test customer
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST01",
            CustName = "Alpha",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            GlCode = "GLAR01",
            TinNo = "TIN01",
            CustBrn = "BRN01",
            Email = "alpha@example.com",
            Tel = "0123456789",
            Country = "MY",
            InvName = "Alpha",
            InvAddress1 = "INV ADDR 1",
            InvCity = "INV CITY",
            InvPostalCode = "50000",
            InvCountry = "MY",
            InvTel = "0123456789",
            IsActive = true,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
        });

        // Customer with DecPoint=true
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTDEC",
            CustName = "Dec Point",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            GlCode = "GLARDEC",
            Country = "MY",
            InvName = "Dec Point",
            InvAddress1 = "DEC ADDR",
            InvCity = "DEC CITY",
            InvPostalCode = "50005",
            InvCountry = "MY",
            DecPoint = true,
            IsActive = true,
            RowVersion = [6, 0, 0, 0, 0, 0, 0, 0]
        });

        // Other-company customer
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "OTHER",
            CustCode = "CUST01",
            CustName = "Other Co Cust",
            Currency = "MYR",
            GlCode = "GLAROTH",
            Country = "SG",
            IsActive = true,
            RowVersion = [9, 0, 0, 0, 0, 0, 0, 0]
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ─────────────────────────── 2. CN and DN issue different numbering modules ───────────────────────────

    [Fact]
    public async Task Save_CN_and_DN_issue_different_numbering_modules()
    {
        var sut = CreateSut();
        var cn = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(cn.Succeeded, cn.ErrorMessage);
        Assert.StartsWith("CN", cn.DocNo);

        var dn = await sut.SaveNewAsync(CdnRequest("DN"));
        Assert.True(dn.Succeeded, dn.ErrorMessage);
        Assert.StartsWith("DN", dn.DocNo);

        Assert.NotEqual(cn.DocNo, dn.DocNo);
    }

    // ─────────────────────────── 3. Save writes NEW; credit-only Post => POSTED, no IvTrxBatch ───────────────────────────

    [Fact]
    public async Task Save_writes_NEW_and_credit_only_Post_does_not_create_IvTrxBatch()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(CdnStatuses.New, save.Document!.Status);

        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var cdn = await db.SaCdns.SingleAsync();
        Assert.Equal(CdnStatuses.Posted, cdn.Status);

        var refNo = SaCdnSpRefs.ToRefNo(save.DocNo!);
        Assert.Equal(0, await db.IvTrxBatches.CountAsync(x => x.RefNo == refNo));
    }

    // ─────────────────────────── 4. Client inflated TotAmnt ignored ───────────────────────────

    [Fact]
    public async Task Client_inflated_TotAmnt_ignored_server_recalc_persisted()
    {
        var sut = CreateSut();
        var req = CdnRequest("CN");
        // Server should recalculate: 2 x 10 = 20
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(20m, save.Document!.TotAmnt);
    }

    // ─────────────────────────── 5. CopyFromInvoice does not save ───────────────────────────

    [Fact]
    public async Task CopyFromInvoice_without_save_leaves_no_CDN_row()
    {
        var invNo = await SeedPostedInvoiceAsync(100m);
        var sut = CreateSut();
        var copy = await sut.CopyFromInvoiceAsync(invNo);
        Assert.True(copy.Succeeded, copy.ErrorMessage);
        Assert.Equal(string.Empty, copy.Document!.DocNo); // not saved

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.SaCdns.CountAsync());
    }

    // ─────────────────────────── 6. CN remaining: exact OK; +0.01 over rejected ───────────────────────────

    [Fact]
    public async Task CN_remaining_exact_equals_ok_and_cent_over_rejected()
    {
        var invNo = await SeedPostedInvoiceAsync(100m);
        var sut = CreateSut();

        // Exact remaining = OK
        var exact = await sut.SaveNewAsync(CdnRequest("CN", invNo: invNo, qty: 10m, price: 10m));
        Assert.True(exact.Succeeded, exact.ErrorMessage);

        // Remove it to test again fresh
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var cdn = await db.SaCdns.Include(x => x.Details).SingleAsync();
            db.SaCdnDetails.RemoveRange(cdn.Details);
            db.SaCdns.Remove(cdn);
            await db.SaveChangesAsync();
        }

        // One cent over => rejected
        var over = await sut.SaveNewAsync(CdnRequest("CN", invNo: invNo, qty: 1m, price: 100.01m));
        Assert.False(over.Succeeded);
        Assert.Equal(SaCdnErrorKind.BusinessRule, over.ErrorKind);
    }

    // ─────────────────────────── 7. DN with InvNo does not apply remaining check ───────────────────────────

    [Fact]
    public async Task DN_with_InvNo_not_subject_to_remaining_check()
    {
        var invNo = await SeedPostedInvoiceAsync(100m);
        var sut = CreateSut();

        // DN with InvNo set and amount larger than invoice: should succeed (DN doesn't check remaining)
        var dn = await sut.SaveNewAsync(CdnRequest("DN", invNo: invNo, qty: 1m, price: 999m));
        Assert.True(dn.Succeeded, dn.ErrorMessage);
        Assert.Equal(999m, dn.Document!.TotAmnt);
    }

    // ─────────────────────────── 8. DN cannot save ReturnStock=true ───────────────────────────

    [Fact]
    public async Task DN_cannot_save_ReturnStock_true()
    {
        var sut = CreateSut();
        var req = CdnRequest("DN");
        req.ReturnStock = true; // should be forced false for DN
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.False(save.Document!.ReturnStock);
    }

    // ─────────────────────────── 9. Delete only NEW; cascade details ───────────────────────────

    [Fact]
    public async Task Delete_NEW_succeeds_and_cascades_details()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var del = await sut.DeleteAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(del.Succeeded, del.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.SaCdns.CountAsync());
        Assert.Equal(0, await db.SaCdnDetails.CountAsync());
    }

    [Fact]
    public async Task Delete_POSTED_document_fails()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        var curr = await sut.GetAsync(save.DocNo!);
        var del = await sut.DeleteAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr.Document!.RowVersion }]);
        Assert.False(del.Succeeded);
    }

    // ─────────────────────────── 10. Post already POSTED => status conflict ───────────────────────────

    [Fact]
    public async Task Post_already_POSTED_returns_status_conflict()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post1 = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post1.Succeeded, post1.ErrorMessage);

        var curr = await sut.GetAsync(save.DocNo!);
        var post2 = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr.Document!.RowVersion }]);
        Assert.False(post2.Succeeded);
        var item = post2.Posting.Single(x => x.DocNo == save.DocNo);
        Assert.Equal(SaCdnReasonCodes.StatusConflict, item.ReasonCode);
    }

    // ─────────────────────────── 11. Rollback already NEW => status conflict ───────────────────────────

    [Fact]
    public async Task Rollback_already_NEW_returns_status_conflict()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var rb = await sut.RollbackAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.False(rb.Succeeded);
        var item = rb.Posting.Single(x => x.DocNo == save.DocNo);
        Assert.Equal(SaCdnReasonCodes.StatusConflict, item.ReasonCode);
    }

    // ─────────────────────────── 12. Unauthorized POST rejected ───────────────────────────

    [Fact]
    public async Task Unauthorized_Post_rejected()
    {
        var access = new Mock<IAccessRightService>();
        // Allow Add and Access for CN
        access.Setup(x => x.CanAsync(
                It.Is<string>(m => m == MenuCodes.SalesCreditNote),
                It.Is<string>(p => p == PermissionCodes.Add),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(
                It.Is<string>(m => m == MenuCodes.SalesCreditNote),
                It.Is<string>(p => p == PermissionCodes.Access),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        // POST denied
        access.Setup(x => x.CanAsync(
                It.Is<string>(m => m == MenuCodes.SalesCreditNote),
                It.Is<string>(p => p == PermissionCodes.Post),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut(access: access);
        var save = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.False(post.Succeeded);
    }

    // ─────────────────────────── 13. Cross-company Get returns not found ───────────────────────────

    [Fact]
    public async Task Cross_company_Get_returns_not_found()
    {
        // Seed a CDN directly for OTHER company
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaCdns.Add(new SaCdn
            {
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                DocNo = "CN-OTHER-0001",
                DocDate = FixedToday,
                Status = CdnStatuses.New,
                Type = CdnTypes.CreditNote,
                CustCode = "CUST01",
                Currency = "MYR",
                CurrRate = 1m,
                RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
            });
            await db.SaveChangesAsync();
        }

        // DEMO context should not see OTHER company documents
        var sut = CreateSut();
        var result = await sut.GetAsync("CN-OTHER-0001");
        Assert.False(result.Succeeded);
        Assert.Equal(SaCdnErrorKind.NotFound, result.ErrorKind);
    }

    // ─────────────────────────── 14. Remaining < 0 fail closed ───────────────────────────

    [Fact]
    public async Task Remaining_negative_from_legacy_over_credit_fails_closed()
    {
        var invNo = await SeedPostedInvoiceAsync(100m);

        // Seed existing CN for 120 (over the invoice -- simulates legacy data)
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaCdns.Add(new SaCdn
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                DocNo = "CN-LEGACY-001",
                DocDate = FixedToday,
                Status = CdnStatuses.New,
                Type = CdnTypes.CreditNote,
                InvNo = invNo,
                CustCode = "CUST01",
                Currency = "MYR",
                CurrRate = 1m,
                TotAmnt = 120m,
                RowVersion = [99, 0, 0, 0, 0, 0, 0, 0]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        // Remaining is negative (100 - 120 = -20), even 1m CN should fail
        var result = await sut.SaveNewAsync(CdnRequest("CN", invNo: invNo, qty: 1m, price: 1m));
        Assert.False(result.Succeeded);
        Assert.Equal(SaCdnErrorKind.BusinessRule, result.ErrorKind);
    }

    // ─────────────────────────── 15. ReturnStock=true Post increases BalLoc; Rollback restores ───────────────────────────

    [Fact]
    public async Task ReturnStock_Post_increases_BalLoc_and_Rollback_restores()
    {
        var sut = CreateSut();

        var req = new SaCdnSaveRequest
        {
            Type = "CN",
            DocDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            SalesmanCode = "SM1",
            ReturnStock = true,
            Lines =
            [
                new SaCdnLineRequest
                {
                    ICode = "A100",
                    Qty = 5m,
                    UnitPrice = 10m,
                    FrWarehouse = "MAIN",
                    LocCode = "BIN1",
                    IStatus = "ACTIVE"
                }
            ]
        };

        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);

        // Post => should increase BalLoc
        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        decimal balAfterPost;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var cdn = await db.SaCdns.SingleAsync(x => x.DocNo == save.DocNo);
            Assert.Equal(CdnStatuses.Posted, cdn.Status);
            balAfterPost = await db.IvBalLocs
                .Where(x => x.ICode == "A100" && x.WhCode == "MAIN")
                .SumAsync(x => x.StdQty);
            Assert.True(balAfterPost > 0m, "BalLoc should have stock after return");
        }

        // Rollback => BalLoc should decrease
        var curr = await sut.GetAsync(save.DocNo!);
        var rb = await sut.RollbackAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr.Document!.RowVersion }]);
        Assert.True(rb.Succeeded, rb.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var cdnAfter = await db.SaCdns.SingleAsync(x => x.DocNo == save.DocNo);
            Assert.Equal(CdnStatuses.New, cdnAfter.Status);

            var balAfterRollback = await db.IvBalLocs
                .Where(x => x.ICode == "A100" && x.WhCode == "MAIN")
                .SumAsync(x => x.StdQty);
            Assert.True(balAfterRollback < balAfterPost, "Rollback should reduce stock");
        }
    }

    // ─────────────────────────── 16. Rollback credit-only Post => no orphan CR batch ───────────────────────────

    [Fact]
    public async Task Rollback_credit_only_Post_then_repost_leaves_no_orphan_CR_batch()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN")); // ReturnStock=false
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        var curr = await sut.GetAsync(save.DocNo!);
        var rb = await sut.RollbackAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr.Document!.RowVersion }]);
        Assert.True(rb.Succeeded, rb.ErrorMessage);

        // Post again
        var curr2 = await sut.GetAsync(save.DocNo!);
        var post2 = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr2.Document!.RowVersion }]);
        Assert.True(post2.Succeeded, post2.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var refNo = SaCdnSpRefs.ToRefNo(save.DocNo!);
        Assert.Equal(0, await db.IvTrxBatches.CountAsync(x =>
            x.RefNo == refNo && x.TrxType == IvTrxTypes.CustomerReturn));
    }

    // ─────────────────────────── 17. Rollback then Delete => no orphan NEW CR ───────────────────────────

    [Fact]
    public async Task Rollback_then_Delete_CN_leaves_no_orphan_NEW_CR()
    {
        var sut = CreateSut();
        var req = new SaCdnSaveRequest
        {
            Type = "CN",
            DocDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            SalesmanCode = "SM1",
            ReturnStock = true,
            Lines =
            [
                new SaCdnLineRequest
                {
                    ICode = "A100",
                    Qty = 3m,
                    UnitPrice = 10m,
                    FrWarehouse = "MAIN",
                    LocCode = "BIN1",
                    IStatus = "ACTIVE"
                }
            ]
        };
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);

        // Post (creates CR batch)
        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        // Rollback (CR batch => NEW)
        var curr = await sut.GetAsync(save.DocNo!);
        var rb = await sut.RollbackAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr.Document!.RowVersion }]);
        Assert.True(rb.Succeeded, rb.ErrorMessage);

        // Delete CN (should clean up the NEW CR batch)
        var curr2 = await sut.GetAsync(save.DocNo!);
        var del = await sut.DeleteAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr2.Document!.RowVersion }]);
        Assert.True(del.Succeeded, del.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.SaCdns.CountAsync());
        var refNo = SaCdnSpRefs.ToRefNo(save.DocNo!);
        Assert.Equal(0, await db.IvTrxBatches.CountAsync(x => x.RefNo == refNo));
    }

    // ─────────────────────────── 18. Fingerprint mismatch => Post fails ───────────────────────────

    [Fact]
    public async Task Post_after_fingerprint_mismatch_fails_closed()
    {
        var sut = CreateSut();
        var req = new SaCdnSaveRequest
        {
            Type = "CN",
            DocDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            SalesmanCode = "SM1",
            ReturnStock = true,
            Lines =
            [
                new SaCdnLineRequest
                {
                    ICode = "A100",
                    Qty = 2m,
                    UnitPrice = 10m,
                    FrWarehouse = "MAIN",
                    LocCode = "BIN1",
                    IStatus = "ACTIVE"
                }
            ]
        };
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);

        // Post (creates + posts CR batch with fingerprint)
        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        // Manually set CN back to NEW and mutate a stock line to simulate change
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var cdn = await db.SaCdns.Include(x => x.Details).SingleAsync(x => x.DocNo == save.DocNo);
            cdn.Status = CdnStatuses.New;
            cdn.RowVersion = Guid.NewGuid().ToByteArray();
            var detail = cdn.Details.First();
            detail.StdQty = 99m; // mutate => fingerprint mismatch
            await db.SaveChangesAsync();
        }

        var curr = await sut.GetAsync(save.DocNo!);
        var post2 = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr.Document!.RowVersion }]);
        Assert.False(post2.Succeeded);
        var item = post2.Posting.Single();
        Assert.Equal(SaCdnReasonCodes.FingerprintMismatch, item.ReasonCode);
    }

    // ─────────────────────────── 19. NULL SourceFingerprint on POSTED CR => Post fails ───────────────────────────

    [Fact]
    public async Task Post_with_null_SourceFingerprint_on_POSTED_CR_fails_closed()
    {
        var sut = CreateSut();
        var req = new SaCdnSaveRequest
        {
            Type = "CN",
            DocDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            SalesmanCode = "SM1",
            ReturnStock = true,
            Lines =
            [
                new SaCdnLineRequest
                {
                    ICode = "A100",
                    Qty = 2m,
                    UnitPrice = 10m,
                    FrWarehouse = "MAIN",
                    LocCode = "BIN1",
                    IStatus = "ACTIVE"
                }
            ]
        };
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        // Nullify the fingerprint on the POSTED CR batch
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var refNo = SaCdnSpRefs.ToRefNo(save.DocNo!);
            var batch = await db.IvTrxBatches.SingleAsync(x => x.RefNo == refNo);
            batch.SourceFingerprint = null;
            await db.SaveChangesAsync();
        }

        // Set CN back to NEW
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var cdn = await db.SaCdns.SingleAsync(x => x.DocNo == save.DocNo);
            cdn.Status = CdnStatuses.New;
            cdn.RowVersion = Guid.NewGuid().ToByteArray();
            await db.SaveChangesAsync();
        }

        var curr = await sut.GetAsync(save.DocNo!);
        var post2 = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr.Document!.RowVersion }]);
        Assert.False(post2.Succeeded);
        var item = post2.Posting.Single();
        Assert.Equal(SaCdnReasonCodes.FingerprintMissing, item.ReasonCode);
    }

    // ─────────────────────────── 20. Post ReturnStock=true => exactly one POSTED CR matching fingerprint ───────────────────────────

    [Fact]
    public async Task Post_ReturnStock_creates_exactly_one_POSTED_CR_matching_fingerprint()
    {
        var sut = CreateSut();
        var req = new SaCdnSaveRequest
        {
            Type = "CN",
            DocDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            SalesmanCode = "SM1",
            ReturnStock = true,
            Lines =
            [
                new SaCdnLineRequest
                {
                    ICode = "A100",
                    Qty = 4m,
                    UnitPrice = 10m,
                    FrWarehouse = "MAIN",
                    LocCode = "BIN1",
                    IStatus = "ACTIVE"
                }
            ]
        };
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await sut.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var refNo = SaCdnSpRefs.ToRefNo(save.DocNo!);
        var batches = await db.IvTrxBatches
            .Where(x => x.RefNo == refNo && x.TrxType == IvTrxTypes.CustomerReturn)
            .ToListAsync();
        Assert.Single(batches);
        Assert.Equal(IvBatchStatuses.Posted, batches[0].BatchStatus);

        var cdn = await db.SaCdns.Include(x => x.Details).SingleAsync(x => x.DocNo == save.DocNo);
        var expectedFp = SaCdnCalc.ComputeSourceFingerprint(cdn.Details);
        Assert.Equal(expectedFp, batches[0].SourceFingerprint);
    }

    // ─────────────────────────── 21. Mixed inclusive/exclusive lines rejected ───────────────────────────

    [Fact]
    public async Task Mixed_inclusive_and_exclusive_lines_rejected()
    {
        var sut = CreateSut();
        var req = new SaCdnSaveRequest
        {
            Type = "CN",
            DocDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            Lines =
            [
                new SaCdnLineRequest { ICode = "A100", Qty = 1m, UnitPrice = 10m, IsInclusive = false },
                new SaCdnLineRequest { ICode = "SVC1", Qty = 1m, UnitPrice = 50m, IsInclusive = true }
            ]
        };
        var save = await sut.SaveNewAsync(req);
        Assert.False(save.Succeeded);
        Assert.Contains("inclusive", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────── Commercial readiness (Save) ───────────────────────────

    [Fact]
    public async Task Save_request_ItemGlCode_wins_over_master()
    {
        var sut = CreateSut();
        var req = CdnRequest("CN");
        req.Lines = [new SaCdnLineRequest { ICode = "SVC1", Qty = 1m, UnitPrice = 10m, ItemGlCode = "CLIENTGL" }];
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("CLIENTGL", save.Document!.Lines[0].ItemGlCode);
    }

    [Fact]
    public async Task Save_rejects_overlength_ItemGlCode()
    {
        var sut = CreateSut();
        var req = CdnRequest("CN");
        req.Lines =
        [
            new SaCdnLineRequest
            {
                ICode = "SVC1",
                Qty = 1m,
                UnitPrice = 10m,
                ItemGlCode = new string('X', 21)
            }
        ];
        var save = await sut.SaveNewAsync(req);
        Assert.False(save.Succeeded);
        Assert.Contains(save.ValidationErrors.Keys, k => k.Contains("ItemGlCode", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, _numbering.CnIssuedCount);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.SaCdns.CountAsync());
    }

    [Fact]
    public async Task Save_master_fallback_ItemGlCode_when_request_blank()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("GLSVC", save.Document!.Lines[0].ItemGlCode);
    }

    [Fact]
    public async Task Save_fails_when_ItemGl_blank_and_amount_nonzero()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "SVC1");
            item.SellingGlCode = null;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var cn = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.False(cn.Succeeded);
        Assert.Contains(cn.ValidationErrors.Keys, k => k.Contains("ItemGlCode", StringComparison.OrdinalIgnoreCase));

        var dn = await sut.SaveNewAsync(CdnRequest("DN"));
        Assert.False(dn.Succeeded);
        Assert.Contains(dn.ValidationErrors.Keys, k => k.Contains("ItemGlCode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Save_allows_blank_ItemGl_when_amount_zero()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "SVC1");
            item.SellingGlCode = null;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN", qty: 1m, price: 0m));
        Assert.True(save.Succeeded, save.ErrorMessage);
    }

    [Fact]
    public async Task Save_salesman_missing_or_inactive_fails()
    {
        var sut = CreateSut();
        var missingReq = CdnRequest("CN");
        missingReq.SalesmanCode = null;
        var missing = await sut.SaveNewAsync(missingReq);
        Assert.False(missing.Succeeded);
        Assert.Contains(missing.ValidationErrors.Keys, k => k.Equals("SalesmanCode", StringComparison.OrdinalIgnoreCase));

        var inactiveReq = CdnRequest("CN");
        inactiveReq.SalesmanCode = "SMINACTIVE";
        var inactive = await sut.SaveNewAsync(inactiveReq);
        Assert.False(inactive.Succeeded);
        Assert.Contains(inactive.ValidationErrors.Keys, k => k.Equals("SalesmanCode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Save_pay_term_blank_and_taxable_without_tax_group_succeeds()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var cust = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST01");
            cust.Taxable = true;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var req = CdnRequest("CN");
        req.PayCode = null;
        req.TaxGrCode = null;
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(0m, save.Document!.Taxes);
    }

    [Fact]
    public async Task Save_HasMaterialTax_without_header_tax_group_fails()
    {
        var sut = CreateSut();
        var req = CdnRequest("CN", qty: 1m, price: 100m);
        req.TaxGrCode = null;
        req.Lines = [new SaCdnLineRequest { ICode = "SVC1", Qty = 1m, UnitPrice = 100m, TaxGrCode = "SR" }];
        // Line tax still produces header Taxes; blank header TaxGrCode → TaxGrCode commercial key.
        var save = await sut.SaveNewAsync(req);
        Assert.False(save.Succeeded);
        Assert.True(save.ValidationErrors.ContainsKey("TaxGrCode"));
        Assert.False(save.ValidationErrors.ContainsKey("TaxGlCode"));
    }

    [Fact]
    public async Task Save_HasMaterialTax_with_TaxGl_missing_fails()
    {
        var sut = CreateSut();
        var req = CdnRequest("CN", qty: 1m, price: 100m);
        req.TaxGrCode = "NOTAXGL";
        var save = await sut.SaveNewAsync(req);
        Assert.False(save.Succeeded);
        Assert.True(save.ValidationErrors.ContainsKey("TaxGlCode"));
        Assert.False(save.ValidationErrors.ContainsKey("TaxGrCode"));
    }

    [Fact]
    public async Task Update_commercial_fail_leaves_existing_NEW_unchanged()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(CdnRequest("CN"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var originalSalesman = save.Document!.SalesmanCode;
        var originalQty = save.Document.Lines[0].Qty;

        var updateReq = CdnRequest("CN", qty: 5m, price: 10m);
        updateReq.SalesmanCode = "SMINACTIVE";
        updateReq.RowVersion = save.Document.RowVersion;
        var update = await sut.UpdateAsync(save.DocNo!, updateReq);
        Assert.False(update.Succeeded);
        Assert.Contains(update.ValidationErrors.Keys, k => k.Equals("SalesmanCode", StringComparison.OrdinalIgnoreCase));

        var get = await sut.GetAsync(save.DocNo!);
        Assert.True(get.Succeeded, get.ErrorMessage);
        Assert.Equal(originalSalesman, get.Document!.SalesmanCode);
        Assert.Equal(originalQty, get.Document.Lines[0].Qty);
    }

    // ─────────────────────────── 22. DecPoint header rounds to 0 dp ───────────────────────────

    [Fact]
    public async Task DecPoint_customer_header_rounds_to_zero_decimals()
    {
        var sut = CreateSut();
        // 1.005 * 10 = 10.05, rounded to 0 dp => 10
        var req = CdnRequest("CN", qty: 1.005m, price: 10m, cust: "CUSTDEC");
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(10m, save.Document!.TotAmnt);
    }

    // ─────────────────────────── Helpers ───────────────────────────

    private SaCdnService CreateSut(Mock<IAccessRightService>? access = null)
    {
        access ??= AlwaysAllowed();
        var postingRepo = new IvStockPostingRepository();
        return new SaCdnService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            _numbering,
            new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new SaCdnRepository(),
            new SaInvoiceRepository(),
            postingRepo,
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            CreatePosting(access),
            NullLogger<SaCdnService>.Instance);
    }

    private IvInventoryPostingService CreatePosting(Mock<IAccessRightService>? access = null)
    {
        access ??= AlwaysAllowed();
        return new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);
    }

    private static Mock<IAccessRightService> AlwaysAllowed()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    /// <summary>Seeds a simple service-only invoice and posts it. Returns the InvNo.</summary>
    private async Task<string> SeedPostedInvoiceAsync(decimal totalAmount = 100m)
    {
        var invService = CreateInvoiceService();
        var qty = totalAmount / 10m;
        var invReq = new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = qty,
                    UnitPrice = 10m,
                    FrWarehouse = "MAIN"
                }
            ]
        };
        var save = await invService.SaveNewAsync(invReq);
        if (!save.Succeeded) throw new InvalidOperationException($"Failed to seed invoice: {save.ErrorMessage}");

        var post = await invService.PostAsync([save.InvNo!]);
        if (!post.Succeeded) throw new InvalidOperationException($"Failed to post invoice: {post.ErrorMessage}");

        return save.InvNo!;
    }

    private SaInvoiceService CreateInvoiceService()
    {
        var postingRepo = new IvStockPostingRepository();
        var access = AlwaysAllowed();
        var salesOrders = new SaSoRepository();
        var docApplication = new SaDocApplicationService(salesOrders, new SaDoRepository());
        return new SaInvoiceService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new RunningNumberService(),
            _numbering,
            new FixedCurrentDateService(FixedToday),
            new SaInvoiceRepository(),
            new SaCustRepository(_factory),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            new IvInventoryPostingService(
                _factory,
                InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
                access.Object,
                postingRepo,
                new IvStockCommonRepository(_factory),
                NullLogger<IvInventoryPostingService>.Instance),
            new IvSpShipmentService(postingRepo, new IvStockTransactionRepository(), new RunningNumberService()),
            salesOrders,
            docApplication,
            new SaCustLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext(location: "SITE")),
            NullLogger<SaInvoiceService>.Instance);
    }

    private static SaCdnSaveRequest CdnRequest(
        string type,
        string? invNo = null,
        decimal qty = 2m,
        decimal price = 10m,
        string cust = "CUST01") =>
        new()
        {
            Type = type,
            DocDate = FixedToday,
            CustCode = cust,
            Currency = "MYR",
            InvNo = invNo,
            SalesmanCode = "SM1",
            Lines = [new SaCdnLineRequest { ICode = "SVC1", Qty = qty, UnitPrice = price }]
        };
}
