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

namespace ErpWeb.Tests;

/// <summary>
/// Issues DO numbers for the "DO" module (DO{yy}{MM}-{seq:D4} / prefix "DO").
/// Falls through to the INV pattern for any other module, so these tests can share the
/// same instance if both invoice and DO operations are needed in one test.
/// </summary>
internal sealed class FakeDoDocumentNumberingService : IDocumentNumberingService
{
    private int _invSeq;
    private int _doSeq;

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

        if (string.Equals(module, "DO", StringComparison.OrdinalIgnoreCase))
        {
            var n = Interlocked.Increment(ref _doSeq);
            var doc = $"DO{documentDate:yy}{documentDate:MM}-{n:D4}";
            return Task.FromResult(new DocumentNumberResult(doc, "DO"));
        }
        else
        {
            var n = Interlocked.Increment(ref _invSeq);
            var doc = $"INV{documentDate:yy}{documentDate:MM}-{n:D4}";
            return Task.FromResult(new DocumentNumberResult(doc, "INV"));
        }
    }
}

public class SaDoServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaDoServiceTests()
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
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "USD", IsActive = true });
        db.IvMsCodes.Add(new IvMsCode { Code = "NET30", Name = "Net 30", CodeType = IvMsCodeTypes.PayCode });
        db.SaPaymentTerms.Add(new SaPaymentTerm
        {
            CompanyCode = "DEMO",
            PayCode = "NET30",
            PayDesc = "Net 30",
            Days = 30,
            IsActive = true
        });
        db.SaSalesReps.Add(new SaSalesRep
        {
            CompanyCode = "DEMO",
            SrepCode = "SM1",
            SrepName = "Sales One",
            IsActive = true
        });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "SR",
            TaxGrDesc = "Standard",
            Percentage = 6m,
            TaxGlCode = "GLTAX"
        });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "ZR",
            TaxGrDesc = "Zero",
            Percentage = 0m,
            TaxGlCode = "GLTAX0"
        });
        db.SaCurrRates.Add(new SaCurrRate
        {
            CurrCode = "USD",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            HomeCurPerUnit = 4.5,
            Status = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            IsActive = true,
            SellingPrice = 10m,
            SellingGlCode = "GLSALE",
            Classification = "CLASS-A",
            DefWarehouse = "MAIN"
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
        // Basic customer
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
            Country = "MY",
            InvName = "Alpha",
            InvAddress1 = "INV ADDR 1",
            InvCity = "INV CITY",
            InvPostalCode = "50000",
            InvCountry = "MY",
            ShipAddress1 = "SHIP ADDR 1",
            ShipCity = "SHIP CITY",
            IsActive = true,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
        });
        // Customer with InvoicePrefix — must be ignored by DO numbering
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST02",
            CustName = "Beta",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            GlCode = "GLAR02",
            InvoicePrefix = "ACME",
            Country = "MY",
            InvName = "Beta",
            InvAddress1 = "BETA ADDR",
            InvCity = "BETA CITY",
            InvPostalCode = "50001",
            InvCountry = "MY",
            IsActive = true,
            RowVersion = [2, 0, 0, 0, 0, 0, 0, 0]
        });
        // Customer with AppInvoice=true, AppShip=true → use main address
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTAPP",
            CustName = "Apply Main",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            GlCode = "GLARAPP",
            AppInvoice = true,
            AppShip = true,
            Address1 = "MAIN BILL 1",
            Address4 = "MAIN BILL 4",
            City = "MAIN BILL CITY",
            PostalCode = "50002",
            Country = "MY",
            Tel = "111",
            InvName = "INV NAME",
            InvAddress1 = "SPEC INV 1",
            ShipName = "SHIP NAME",
            ShipAddress1 = "SPEC SHIP 1",
            IsActive = true,
            RowVersion = [3, 0, 0, 0, 0, 0, 0, 0]
        });
        db.SaCustAdds.AddRange(
            new SaCustAdd
            {
                CompanyCode = "DEMO",
                CustCode = "CUSTAPP",
                Line = 1,
                AddName = "Warehouse A",
                DeliverTo = "WH-A",
                Address1 = "SHIPTO A1",
                City = "Ship City A",
                Country = "MY"
            });
        // Customer with AppInvoice=false, AppShip=false
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTSPEC",
            CustName = "Special Addr",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            GlCode = "GLARSPEC",
            AppInvoice = false,
            AppShip = false,
            Address1 = "MAIN IGNORE",
            Country = "MY",
            InvName = "Bill To Spec",
            InvAddress1 = "SPEC BILL 1",
            InvCity = "SPEC BILL CITY",
            InvPostalCode = "50003",
            InvCountry = "MY",
            ShipName = "Ship To Spec",
            ShipAddress1 = "SPEC SHIP 1",
            ShipCity = "SPEC SHIP CITY",
            IsActive = true,
            RowVersion = [4, 0, 0, 0, 0, 0, 0, 0]
        });
        // Taxable customer
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUSTTAX",
            CustName = "Taxable Co",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            GlCode = "GLARTAX",
            Country = "MY",
            InvName = "Taxable Co",
            InvAddress1 = "TAX ADDR",
            InvCity = "TAX CITY",
            InvPostalCode = "50004",
            InvCountry = "MY",
            Taxable = true,
            TaxGrCode = "SR",
            IsActive = true,
            RowVersion = [5, 0, 0, 0, 0, 0, 0, 0]
        });
        // Other-company customer — must not be accessible from DEMO context
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "OTHER",
            CustCode = "CUST01",
            CustName = "Other Co Cust",
            Currency = "MYR",
            PayCode = "NET30",
            GlCode = "GLAROTH",
            Country = "SG",
            IsActive = true,
            RowVersion = [9, 0, 0, 0, 0, 0, 0, 0]
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ───────────────────────────────────────────────────────
    // 1. SaveNew issues DO numbers; customer prefix ignored
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task SaveNew_issues_DO_numbers_and_stamps_tenant()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        // DO number format: DO{yy}{MM}-{seq}
        Assert.Equal("DO2609-0001", save.DoNo);
        Assert.Equal(SaDoStatuses.New, save.Document!.Status);

        await using var db = await _factory.CreateDbContextAsync();
        var do_ = await db.SaDos.Include(x => x.Details).SingleAsync();
        Assert.Equal("DO2609-0001", do_.DoNo);
        Assert.Equal("DEMO", do_.CompanyCode);
        Assert.Equal("HQ", do_.BranchCode);
        Assert.Equal(20m, do_.TotAmnt);
        Assert.Equal(SaDoStatuses.New, do_.Status);
        // Detail also stamped
        Assert.Equal("DEMO", do_.Details.Single().CompanyCode);
        Assert.Equal("HQ", do_.Details.Single().BranchCode);
    }

    [Fact]
    public async Task SaveNew_round_trips_Ref1_and_ProjId_with_TruncateOptional_contract()
    {
        var sut = CreateSut();
        var overRef = new string('R', 60);
        var overProj = new string('P', 30);

        var save = await sut.SaveNewAsync(new SaDoSaveRequest
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Ref1 = "  ABC  ",
            ProjId = "  PRJ1  ",
            Lines = [Line("A100", 1m, 10m)]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("ABC", save.Document!.Ref1);
        Assert.Equal("PRJ1", save.Document.ProjId);

        var get = await sut.GetAsync(save.DoNo!);
        Assert.True(get.Succeeded, get.ErrorMessage);
        Assert.Equal("ABC", get.Document!.Ref1);
        Assert.Equal("PRJ1", get.Document.ProjId);

        var emptySave = await sut.SaveNewAsync(new SaDoSaveRequest
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Ref1 = "   ",
            ProjId = "",
            Lines = [Line("A100", 1m, 10m)]
        });
        Assert.True(emptySave.Succeeded, emptySave.ErrorMessage);
        Assert.Null(emptySave.Document!.Ref1);
        Assert.Null(emptySave.Document.ProjId);

        var longSave = await sut.SaveNewAsync(new SaDoSaveRequest
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Ref1 = overRef,
            ProjId = overProj,
            Lines = [Line("A100", 1m, 10m)]
        });
        Assert.True(longSave.Succeeded, longSave.ErrorMessage);
        Assert.Equal(overRef[..50], longSave.Document!.Ref1);
        Assert.Equal(overProj[..20], longSave.Document.ProjId);
    }

    [Fact]
    public async Task SaveNew_ignores_customer_invoice_prefix()
    {
        var sut = CreateSut();
        // CUST02 has InvoicePrefix = "ACME" — must be ignored for DO
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, cust: "CUST02"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("DO2609-0001", save.DoNo);
        // Prefix stored from numbering table, not customer
        Assert.Equal("DO", save.Document!.Prefix);
    }

    // ───────────────────────────────────────────────────────
    // 2. IDOR: other-company DoNo → not found
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task IDOR_other_company_DoNo_Get_returns_not_found()
    {
        var demo = CreateSut();
        var save = await demo.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var other = CreateSut(tenant: InventoryTenantTestHelper.CreateTenantContext(company: "OTHER"));
        var get = await other.GetAsync(save.DoNo!);
        Assert.False(get.Succeeded);
        Assert.Equal(SaDoErrorKind.NotFound, get.ErrorKind);
    }

    [Fact]
    public async Task IDOR_other_company_DoNo_Post_returns_not_found()
    {
        var demo = CreateSut();
        var save = await demo.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var other = CreateSut(tenant: InventoryTenantTestHelper.CreateTenantContext(company: "OTHER"));
        var post = await other.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.False(post.Succeeded);
        Assert.Contains("not found", post.Posting[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IDOR_other_company_DoNo_ForceClose_returns_not_found()
    {
        // Create + post in DEMO
        var demo = CreateSut();
        var save = await demo.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await demo.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)])).Succeeded);
        var posted = await demo.GetAsync(save.DoNo!);

        var other = CreateSut(tenant: InventoryTenantTestHelper.CreateTenantContext(company: "OTHER"));
        var fc = await other.ForceCloseAsync([Keyed(save.DoNo!, posted.Document!.RowVersion)]);
        Assert.False(fc.Succeeded);
        Assert.Contains("not found", fc.Posting[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IDOR_other_company_DoNo_Delete_returns_not_found()
    {
        var demo = CreateSut();
        var save = await demo.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var other = CreateSut(tenant: InventoryTenantTestHelper.CreateTenantContext(company: "OTHER"));
        var del = await other.DeleteAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.False(del.Succeeded);
        Assert.Contains("not found", del.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────
    // 3. AppInvoice/AppShip defaults on GetCustomerDefaultsAsync
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetCustomerDefaults_AppInvoice_AppShip_true_uses_main_address()
    {
        var sut = CreateSut();
        var result = await sut.GetCustomerDefaultsAsync("CUSTAPP", FixedToday);
        Assert.True(result.Succeeded, result.ErrorMessage);
        var def = result.CustomerDefaults!;
        // AppInvoice=true → main address for billing
        Assert.Equal("Apply Main", def.InvName);
        Assert.Equal("MAIN BILL 1", def.InvAddress1);
        // AppShip=true → main address for shipping
        Assert.Equal("Apply Main", def.ShipName);
        Assert.Equal("MAIN BILL 1", def.ShipAddress1);
        // Ship-to addresses scoped to company
        Assert.Single(def.ShipToAddresses);
        Assert.Equal("WH-A", def.ShipToAddresses[0].DeliverTo);
    }

    [Fact]
    public async Task GetCustomerDefaults_AppInvoice_AppShip_false_uses_dedicated_address()
    {
        var sut = CreateSut();
        var result = await sut.GetCustomerDefaultsAsync("CUSTSPEC", FixedToday);
        Assert.True(result.Succeeded, result.ErrorMessage);
        var def = result.CustomerDefaults!;
        Assert.Equal("Bill To Spec", def.InvName);
        Assert.Equal("SPEC BILL 1", def.InvAddress1);
        Assert.Equal("Ship To Spec", def.ShipName);
        Assert.Equal("SPEC SHIP 1", def.ShipAddress1);
    }

    // ───────────────────────────────────────────────────────
    // 4. Post without e-invoice/AR gates (service-only DO)
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_service_only_DO_posts_without_shipment()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 50m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        // No shipment added — service item has StockControl=false
        var post = await sut.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(SaDoStatuses.Posted, (await db.SaDos.SingleAsync()).Status);
        // No SP batch created
        Assert.Equal(0, await db.IvTrxBatches.CountAsync(x => x.TrxType == IvTrxTypes.SalesOut));
    }

    // ───────────────────────────────────────────────────────
    // 5. POST_NO_LINES: header with zero details fails post
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_no_lines_fails_POST_NO_LINES()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        // Manually delete details to simulate empty header
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var details = db.SaDoDetails.Where(x => x.DoNo == save.DoNo);
            db.SaDoDetails.RemoveRange(details);
            await db.SaveChangesAsync();
        }

        var post = await sut.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.False(post.Succeeded);
        Assert.Equal(SaDoPostReasonCodes.NoLines, post.Posting[0].ReasonCode);
    }

    // ───────────────────────────────────────────────────────
    // 6. RefNo namespace: DO SP uses "DO/{doNo}" — does not collide with invoice bare InvNo
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task RefNo_DO_SP_uses_DO_prefix_and_does_not_collide_with_invoice_refno()
    {
        var numbering = new FakeDoDocumentNumberingService();
        var sut = CreateSut(numbering: numbering);
        await SeedBalLocAsync(100m);

        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await ShipAsync(sut, save.DoNo!)).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.TrxType == IvTrxTypes.SalesOut);
        // RefNo must be "DO/{doNo}", not bare doNo
        Assert.Equal(SaDoSpRefs.ToRefNo(save.DoNo!), batch.RefNo);
        Assert.StartsWith("DO/", batch.RefNo, StringComparison.OrdinalIgnoreCase);

        // A synthetic invoice batch with RefNo = doNo (bare) should NOT be found by DO logic
        db.IvTrxBatches.Add(new IvTrxBatch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = batch.BatchNo + 100,
            TrxDtTime = FixedToday,
            TrxType = IvTrxTypes.SalesOut,
            BatchStatus = IvBatchStatuses.New,
            RefNo = save.DoNo!, // bare DoNo — must NOT collide
            LocationCode = "SITE",
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "admin"
        });
        await db.SaveChangesAsync();

        // GetAsync still works fine — finds by prefixed RefNo
        var get = await sut.GetAsync(save.DoNo!);
        Assert.True(get.Succeeded, get.ErrorMessage);
        Assert.Equal(SaDoSpRefs.ToRefNo(save.DoNo!), get.Document!.SpBatchNo.HasValue
            ? $"DO/{save.DoNo}"
            : string.Empty);
    }

    // ───────────────────────────────────────────────────────
    // 7. Rollback: stock restored; SP kept NEW
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Rollback_restores_stock_and_SP_reverts_to_NEW()
    {
        var balId = await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 30m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await ShipAsync(sut, save.DoNo!)).Succeeded);

        var post = await sut.PostAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(SaDoStatuses.Posted, (await db.SaDos.SingleAsync()).Status);
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(x => x.TrxType == IvTrxTypes.SalesOut)).BatchStatus);
        }

        var rollback = await sut.RollbackAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(SaDoStatuses.New, (await db.SaDos.SingleAsync()).Status);
            // SP batch is KEPT (not deleted) but reverted to NEW
            var batch = await db.IvTrxBatches.SingleAsync(x => x.TrxType == IvTrxTypes.SalesOut);
            Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
            // History records cleared
            Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        }
    }

    // ───────────────────────────────────────────────────────
    // 8. ForceClose: POSTED→CLOSED; SP gone; balances UNCHANGED
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task ForceClose_POSTED_to_CLOSED_SP_deleted_balance_unchanged()
    {
        var balId = await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 20m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await ShipAsync(sut, save.DoNo!)).Succeeded);

        var post = await sut.PostAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        // Balance was reduced after post
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(80m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        }

        var fc = await sut.ForceCloseAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))]);
        Assert.True(fc.Succeeded, fc.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(SaDoStatuses.Closed, (await db.SaDos.SingleAsync()).Status);
            // SP batch and its details are deleted
            Assert.Equal(0, await db.IvTrxBatches.CountAsync(x => x.TrxType == IvTrxTypes.SalesOut));
            // Balance is NOT restored — physical shipment already occurred
            Assert.Equal(80m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        }
    }

    // ───────────────────────────────────────────────────────
    // 9. Authorization: POST denied, CLOSE denied
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_denied_authz_fails()
    {
        var sut = CreateSut(access: DenyPermission(PermissionCodes.Post));
        var save = await CreateSut().SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded);

        var post = await sut.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.False(post.Succeeded);
        Assert.Contains("authorized", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Close_denied_authz_fails()
    {
        var sut = CreateSut(access: DenyPermission(PermissionCodes.Close));
        var fc = await sut.ForceCloseAsync([Keyed("DO2609-0001", [1, 2, 3, 4, 5, 6, 7, 8])]);
        Assert.False(fc.Succeeded);
        Assert.Contains("authorized", fc.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────
    // 10. CLOSED cannot edit/delete/post/rollback
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task CLOSED_DO_cannot_be_edited()
    {
        var sut = CreateSut();
        var doNo = await CreatePostedClosedDo(sut);
        var rv = GetRowVersion(doNo);

        var update = await sut.UpdateAsync(doNo, Request(qty: 2m, price: 10m, iCode: "SVC1", rowVersion: rv));
        Assert.False(update.Succeeded);
        Assert.Equal(SaDoErrorKind.BusinessRule, update.ErrorKind);
    }

    [Fact]
    public async Task CLOSED_DO_cannot_be_deleted()
    {
        var sut = CreateSut();
        var doNo = await CreatePostedClosedDo(sut);
        var rv = GetRowVersion(doNo);

        var del = await sut.DeleteAsync([Keyed(doNo, rv)]);
        Assert.False(del.Succeeded);
        Assert.Contains("not NEW", del.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CLOSED_DO_cannot_be_posted()
    {
        var sut = CreateSut();
        var doNo = await CreatePostedClosedDo(sut);
        var rv = GetRowVersion(doNo);

        var post = await sut.PostAsync([Keyed(doNo, rv)]);
        Assert.False(post.Succeeded);
        // CLOSED is not NEW → Concurrency reason code
        Assert.Equal(SaDoPostReasonCodes.Concurrency, post.Posting[0].ReasonCode);
    }

    [Fact]
    public async Task CLOSED_DO_cannot_be_rolled_back()
    {
        var sut = CreateSut();
        var doNo = await CreatePostedClosedDo(sut);
        var rv = GetRowVersion(doNo);

        var rb = await sut.RollbackAsync([Keyed(doNo, rv)]);
        Assert.False(rb.Succeeded);
        Assert.Contains("POSTED", rb.Posting[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────
    // 11. MaxPostSelection = 3: 4th rejected
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task MaxPostSelection_4th_item_rejected()
    {
        var sut = CreateSut();
        var a = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        var b = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        var c = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        var d = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));

        var four = await sut.PostAsync([
            Keyed(a.DoNo!, a.Document!.RowVersion),
            Keyed(b.DoNo!, b.Document!.RowVersion),
            Keyed(c.DoNo!, c.Document!.RowVersion),
            Keyed(d.DoNo!, d.Document!.RowVersion)]);
        Assert.False(four.Succeeded);
        Assert.Contains($"at most {SaDoLimits.MaxPostSelection}", four.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaxPostSelection_3_posts_and_stops_on_first_failure()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var a = await sut.SaveNewAsync(Request(qty: 1m, price: 10m));
        var b = await sut.SaveNewAsync(Request(qty: 1m, price: 10m));
        var c = await sut.SaveNewAsync(Request(qty: 1m, price: 10m));
        // Only ship the first
        Assert.True((await ShipAsync(sut, a.DoNo!)).Succeeded);

        var three = await sut.PostAsync([
            Keyed(a.DoNo!, GetRowVersion(a.DoNo!)),
            Keyed(b.DoNo!, GetRowVersion(b.DoNo!)),
            Keyed(c.DoNo!, GetRowVersion(c.DoNo!))]);
        Assert.False(three.Succeeded);
        Assert.Equal(3, three.Posting.Count);
        Assert.Equal("Posted", three.Posting[0].Outcome);
        Assert.StartsWith("Failed:", three.Posting[1].Outcome, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Not attempted", three.Posting[2].Outcome);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(SaDoStatuses.Posted, (await db.SaDos.SingleAsync(x => x.DoNo == a.DoNo)).Status);
        Assert.Equal(SaDoStatuses.New, (await db.SaDos.SingleAsync(x => x.DoNo == b.DoNo)).Status);
        Assert.Equal(SaDoStatuses.New, (await db.SaDos.SingleAsync(x => x.DoNo == c.DoNo)).Status);
    }

    // ───────────────────────────────────────────────────────
    // 12. SoNo persists "" not null
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task SoNo_persists_empty_string_not_null()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.SaDoDetails.SingleAsync();
        // Standalone DO — SoNo must be "" (empty string), not null
        Assert.Equal(string.Empty, detail.SoNo);
    }

    // ───────────────────────────────────────────────────────
    // 13. Create stamps CompanyCode/BranchCode
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_stamps_CompanyCode_and_BranchCode()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var do_ = await db.SaDos.Include(x => x.Details).SingleAsync();
        Assert.Equal("DEMO", do_.CompanyCode);
        Assert.Equal("HQ", do_.BranchCode);
        Assert.All(do_.Details, d =>
        {
            Assert.Equal("DEMO", d.CompanyCode);
            Assert.Equal("HQ", d.BranchCode);
        });
    }

    // ───────────────────────────────────────────────────────
    // 14. Stale RowVersion on Update/ForceClose fails concurrency
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_RowVersion_on_Update_fails_concurrency()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var staleRv = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };
        var req = Request(qty: 2m, price: 10m, iCode: "SVC1", rowVersion: staleRv);
        var update = await sut.UpdateAsync(save.DoNo!, req);
        Assert.False(update.Succeeded);
        Assert.Equal(SaDoErrorKind.Concurrency, update.ErrorKind);
    }

    [Fact]
    public async Task Stale_RowVersion_on_ForceClose_fails_concurrency()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)])).Succeeded);

        var staleRv = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };
        var fc = await sut.ForceCloseAsync([Keyed(save.DoNo!, staleRv)]);
        Assert.False(fc.Succeeded);
        Assert.Equal(SaDoPostReasonCodes.Concurrency, fc.Posting[0].ReasonCode);
    }

    // ───────────────────────────────────────────────────────
    // 15. Idempotency guards
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_already_POSTED_fails_concurrency()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var first = await sut.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.True(first.Succeeded, first.ErrorMessage);

        // Second attempt with any RowVersion — DO is POSTED, not NEW
        var second = await sut.PostAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))]);
        Assert.False(second.Succeeded);
        Assert.Equal(SaDoPostReasonCodes.Concurrency, second.Posting[0].ReasonCode);
    }

    [Fact]
    public async Task ForceClose_already_CLOSED_fails()
    {
        var sut = CreateSut();
        var doNo = await CreatePostedClosedDo(sut);
        var rv = GetRowVersion(doNo);

        var second = await sut.ForceCloseAsync([Keyed(doNo, rv)]);
        Assert.False(second.Succeeded);
        Assert.Contains("POSTED", second.Posting[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rollback_on_NEW_fails()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var rb = await sut.RollbackAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.False(rb.Succeeded);
        Assert.Contains("POSTED", rb.Posting[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────
    // 16. List paging: Take clamped; SearchPaged header-only
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Search_paged_Take_clamped_to_MaxPageSize()
    {
        var sut = CreateSut();
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"))).Succeeded);
        }

        // Take = 999 should be clamped to SaDoRepository.MaxPageSize
        var result = await sut.SearchAsync(new SaDoListQuery { Take = 999 });
        Assert.True(result.Succeeded, result.ErrorMessage);
        // All 3 rows fit within the cap anyway, total count = 3
        Assert.Equal(3, result.ListPage!.TotalCount);
        Assert.Equal(3, result.ListPage.Rows.Count);

        // Take = 1 should return only 1 row
        var one = await sut.SearchAsync(new SaDoListQuery { Take = 1 });
        Assert.True(one.Succeeded);
        Assert.Equal(3, one.ListPage!.TotalCount);
        Assert.Single(one.ListPage.Rows);
    }

    [Fact]
    public async Task Search_paged_rows_do_not_contain_line_details()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveNewAsync(Request(qty: 2m, price: 10m))).Succeeded);

        var result = await sut.SearchAsync(new SaDoListQuery());
        Assert.True(result.Succeeded);
        var row = Assert.Single(result.ListPage!.Rows);
        // LineCount comes from a separate GROUP BY — verify it's populated
        Assert.Equal(1, row.LineCount);
    }

    // ───────────────────────────────────────────────────────
    // Additional: full end-to-end create → ship → post → rollback
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task End_to_end_create_ship_post_rollback()
    {
        var balId = await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 30m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var ship = await ShipAsync(sut, save.DoNo!);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        Assert.True(ship.Document!.Shipment.Count > 0);

        var post = await sut.PostAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(SaDoStatuses.Posted, (await db.SaDos.SingleAsync()).Status);
            Assert.Equal(1, await db.IvTrxHistories.CountAsync());
        }

        var rollback = await sut.RollbackAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(SaDoStatuses.New, (await db.SaDos.SingleAsync()).Status);
            Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        }
    }

    [Fact]
    public async Task AddShipment_fifo_order_repeatable()
    {
        var older = await SeedBalLocAsync(40m, transDate: new DateTime(2026, 8, 1), lot: "L1");
        var newer = await SeedBalLocAsync(40m, transDate: new DateTime(2026, 8, 15), lot: "L2");
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 50m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var ship = await ShipAsync(sut, save.DoNo!);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        Assert.Equal(2, ship.Document!.Shipment.Count);
        Assert.Equal(older, ship.Document.Shipment[0].FromBalLocId);
        Assert.Equal(40m, ship.Document.Shipment[0].FrStdQty);
        Assert.Equal(newer, ship.Document.Shipment[1].FromBalLocId);
        Assert.Equal(10m, ship.Document.Shipment[1].FrStdQty);

        // Repeatable — same result
        var ship2 = await ShipAsync(sut, save.DoNo!, overwriteExisting: true);
        Assert.True(ship2.Succeeded, ship2.ErrorMessage);
        Assert.Equal(
            ship.Document.Shipment.Select(x => x.FromBalLocId).ToArray(),
            ship2.Document!.Shipment.Select(x => x.FromBalLocId).ToArray());
    }

    [Fact]
    public async Task Missing_company_context_fails_closed()
    {
        var tenant = new Mock<IInventoryTenantContext>();
        tenant.Setup(x => x.TryBranchScope()).Returns((InventoryTenantScope?)null);
        tenant.Setup(x => x.TryWriteScope()).Returns((InventoryTenantScope?)null);

        var sut = CreateSut(tenant: tenant.Object);
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.False(save.Succeeded);
        Assert.Contains("company", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Customer_change_wipes_shipment()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await ShipAsync(sut, save.DoNo!)).Succeeded);

        var update = await UpdateDocAsync(sut, save.DoNo!, Request(qty: 5m, price: 10m, cust: "CUST02"));
        Assert.True(update.Succeeded, update.ErrorMessage);
        Assert.Equal("BETA", update.Document!.CustName);
        Assert.Empty(update.Document.Shipment);
    }

    [Fact]
    public async Task Post_requires_shipment_for_stock_items()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m)); // A100 = stock item
        Assert.True(save.Succeeded, save.ErrorMessage);
        // No shipment

        var post = await sut.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.False(post.Succeeded);
        Assert.Equal(SaDoPostReasonCodes.SpMissing, post.Posting[0].ReasonCode);
    }

    [Fact]
    public async Task Save_stock_NeedsShipment_true_until_shipment_added()
    {
        await SeedBalLocAsync(100m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m)); // A100 stock
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True(save.Document!.NeedsShipment);

        var ship = await ShipAsync(sut, save.DoNo!);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        Assert.False(ship.Document!.NeedsShipment);

        var get = await sut.GetAsync(save.DoNo!);
        Assert.True(get.Succeeded, get.ErrorMessage);
        Assert.False(get.Document!.NeedsShipment);
    }

    [Fact]
    public async Task Save_service_line_NeedsShipment_false()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.False(save.Document!.NeedsShipment);
    }

    [Fact]
    public async Task Save_blank_billing_still_succeeds()
    {
        var sut = CreateSut();
        var req = Request(qty: 1m, price: 10m, iCode: "SVC1");
        req.InvName = null;
        req.InvAddress1 = null;
        req.InvCountry = null;
        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);
    }

    [Fact]
    public async Task Save_stock_warehouse_missing_fails()
    {
        var sut = CreateSut();
        var req = new SaDoSaveRequest
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            Lines =
            [
                new SaDoLineRequest
                {
                    ICode = "A100",
                    Qty = 1m,
                    UnitPrice = 10m,
                    FrWarehouse = ""
                }
            ]
        };
        // Empty warehouse must not fall back to master/default list.
        var save = await sut.SaveNewAsync(req);
        Assert.False(save.Succeeded);
        Assert.Contains(save.ValidationErrors.Keys, k => k.Contains("FrWarehouse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Delete_new_DO_removes_record()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var del = await sut.DeleteAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.True(del.Succeeded, del.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.SaDos.CountAsync());
    }

    [Fact]
    public async Task Delete_posted_DO_fails()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)])).Succeeded);
        var rv = GetRowVersion(save.DoNo!);

        var del = await sut.DeleteAsync([Keyed(save.DoNo!, rv)]);
        Assert.False(del.Succeeded);
        Assert.Contains("not NEW", del.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────
    // Stale RowVersion on remaining mutations
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_RowVersion_on_Post_fails_concurrency()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = (byte[])save.Document!.RowVersion.Clone();

        // Bump RowVersion via a no-op update with current token
        var bump = await UpdateDocAsync(sut, save.DoNo!, Request(qty: 1m, price: 11m, iCode: "SVC1"));
        Assert.True(bump.Succeeded, bump.ErrorMessage);

        var post = await sut.PostAsync([Keyed(save.DoNo!, stale)]);
        Assert.False(post.Succeeded);
        Assert.Equal(SaDoPostReasonCodes.Concurrency, post.Posting[0].ReasonCode);
    }

    [Fact]
    public async Task Stale_RowVersion_on_AddShipment_fails_concurrency()
    {
        await SeedBalLocAsync(50m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = (byte[])save.Document!.RowVersion.Clone();

        var bump = await UpdateDocAsync(sut, save.DoNo!, Request(qty: 5m, price: 11m));
        Assert.True(bump.Succeeded, bump.ErrorMessage);

        var ship = await sut.AddShipmentAsync(save.DoNo!, overwriteExisting: false, stale);
        Assert.False(ship.Succeeded);
        Assert.Equal(SaDoErrorKind.Concurrency, ship.ErrorKind);
    }

    [Fact]
    public async Task Stale_RowVersion_on_Delete_fails_concurrency()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = (byte[])save.Document!.RowVersion.Clone();

        var bump = await UpdateDocAsync(sut, save.DoNo!, Request(qty: 1m, price: 12m, iCode: "SVC1"));
        Assert.True(bump.Succeeded, bump.ErrorMessage);

        var del = await sut.DeleteAsync([Keyed(save.DoNo!, stale)]);
        Assert.False(del.Succeeded);
        Assert.Equal(SaDoErrorKind.Concurrency, del.ErrorKind);
    }

    // ───────────────────────────────────────────────────────
    // Race simulations (sequential winner/loser on SQLite)
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Race_Post_then_ForceClose_with_stale_token_fails()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var prePostRv = GetRowVersion(save.DoNo!);

        var post = await sut.PostAsync([Keyed(save.DoNo!, prePostRv)]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        // Loser: ForceClose with pre-post token
        var fc = await sut.ForceCloseAsync([Keyed(save.DoNo!, prePostRv)]);
        Assert.False(fc.Succeeded);
        Assert.Equal(SaDoPostReasonCodes.Concurrency, fc.Posting[0].ReasonCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(SaDoStatuses.Posted, (await db.SaDos.SingleAsync(x => x.DoNo == save.DoNo)).Status);
    }

    [Fact]
    public async Task Race_ForceClose_then_Rollback_fails_status()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))])).Succeeded);

        var rv = GetRowVersion(save.DoNo!);
        Assert.True((await sut.ForceCloseAsync([Keyed(save.DoNo!, rv)])).Succeeded);

        var rb = await sut.RollbackAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))]);
        Assert.False(rb.Succeeded);
        Assert.Contains("POSTED", rb.Posting[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(SaDoStatuses.Closed, (await db.SaDos.SingleAsync(x => x.DoNo == save.DoNo)).Status);
        Assert.Equal(0, await db.IvTrxBatches.CountAsync(x => x.TrxType == IvTrxTypes.SalesOut));
    }

    [Fact]
    public async Task Race_Update_then_Post_with_stale_token_fails()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = GetRowVersion(save.DoNo!);

        Assert.True((await UpdateDocAsync(sut, save.DoNo!, Request(qty: 2m, price: 10m, iCode: "SVC1"))).Succeeded);

        var post = await sut.PostAsync([Keyed(save.DoNo!, stale)]);
        Assert.False(post.Succeeded);
        Assert.Equal(SaDoPostReasonCodes.Concurrency, post.Posting[0].ReasonCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(SaDoStatuses.New, (await db.SaDos.SingleAsync(x => x.DoNo == save.DoNo)).Status);
    }

    [Fact]
    public async Task Race_Shipment_then_Post_with_stale_token_fails()
    {
        await SeedBalLocAsync(50m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = GetRowVersion(save.DoNo!);

        Assert.True((await ShipAsync(sut, save.DoNo!)).Succeeded);

        var post = await sut.PostAsync([Keyed(save.DoNo!, stale)]);
        Assert.False(post.Succeeded);
        Assert.Equal(SaDoPostReasonCodes.Concurrency, post.Posting[0].ReasonCode);
    }

    [Fact]
    public async Task Race_Delete_then_Post_not_found()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var rv = GetRowVersion(save.DoNo!);

        Assert.True((await sut.DeleteAsync([Keyed(save.DoNo!, rv)])).Succeeded);

        var post = await sut.PostAsync([Keyed(save.DoNo!, rv)]);
        Assert.False(post.Succeeded);
        Assert.Contains("not found", post.Posting[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────
    // SP scope: Company+Branch+DO/ prefix only
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task ForceClose_does_not_delete_wrong_company_SP()
    {
        await SeedBalLocAsync(50m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await ShipAsync(sut, save.DoNo!)).Succeeded);
        Assert.True((await sut.PostAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))])).Succeeded);

        var doRef = SaDoSpRefs.ToRefNo(save.DoNo!);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                BatchNo = 9001,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.SalesOut,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = doRef,
                LocationCode = "SITE",
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "admin"
            });
            await db.SaveChangesAsync();
        }

        Assert.True((await sut.ForceCloseAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.True(await db.IvTrxBatches.AnyAsync(x =>
                x.CompanyCode == "OTHER" && x.RefNo == doRef && x.TrxType == IvTrxTypes.SalesOut));
            Assert.False(await db.IvTrxBatches.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.RefNo == doRef && x.TrxType == IvTrxTypes.SalesOut));
        }
    }

    [Fact]
    public async Task ForceClose_does_not_delete_wrong_branch_SP()
    {
        await SeedBalLocAsync(50m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await ShipAsync(sut, save.DoNo!)).Succeeded);
        Assert.True((await sut.PostAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))])).Succeeded);

        var doRef = SaDoSpRefs.ToRefNo(save.DoNo!);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                BatchNo = 9002,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.SalesOut,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = doRef,
                LocationCode = "SITE",
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "admin"
            });
            await db.SaveChangesAsync();
        }

        Assert.True((await sut.ForceCloseAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.True(await db.IvTrxBatches.AnyAsync(x =>
                x.BranchCode == "BR2" && x.RefNo == doRef && x.TrxType == IvTrxTypes.SalesOut));
        }
    }

    [Fact]
    public async Task ForceClose_does_not_match_bare_DoNo_or_invoice_RefNo()
    {
        await SeedBalLocAsync(50m);
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await ShipAsync(sut, save.DoNo!)).Succeeded);
        Assert.True((await sut.PostAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            // Accidental bare-DoNo SP + invoice-style bare InvNo-looking SP must survive ForceClose
            db.IvTrxBatches.AddRange(
                new IvTrxBatch
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    BatchNo = 9101,
                    TrxDtTime = FixedToday,
                    TrxType = IvTrxTypes.SalesOut,
                    BatchStatus = IvBatchStatuses.Posted,
                    RefNo = save.DoNo!, // bare — not DO/{doNo}
                    LocationCode = "SITE",
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = "admin"
                },
                new IvTrxBatch
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    BatchNo = 9102,
                    TrxDtTime = FixedToday,
                    TrxType = IvTrxTypes.SalesOut,
                    BatchStatus = IvBatchStatuses.Posted,
                    RefNo = "INV2609-0001",
                    LocationCode = "SITE",
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = "admin"
                });
            await db.SaveChangesAsync();
        }

        Assert.True((await sut.ForceCloseAsync([Keyed(save.DoNo!, GetRowVersion(save.DoNo!))])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.True(await db.IvTrxBatches.AnyAsync(x => x.BatchNo == 9101 && x.RefNo == save.DoNo));
            Assert.True(await db.IvTrxBatches.AnyAsync(x => x.BatchNo == 9102 && x.RefNo == "INV2609-0001"));
            Assert.False(await db.IvTrxBatches.AnyAsync(x =>
                x.RefNo == SaDoSpRefs.ToRefNo(save.DoNo!) && x.CompanyCode == "DEMO" && x.BranchCode == "HQ"));
        }
    }

    // ───────────────────────────────────────────────────────
    // Large list paging (TotalCount > page)
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task LargeDoList_IsPaged()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            for (var i = 1; i <= 105; i++)
            {
                db.SaDos.Add(new SaDo
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    DoNo = $"DOSEED-{i:D4}",
                    DoDate = FixedToday.AddDays(-(i % 30)),
                    Status = SaDoStatuses.New,
                    CustCode = "CUST01",
                    CustName = "Customer 01",
                    Currency = "MYR",
                    CurrRate = 1m,
                    TotAmnt = i,
                    LocationCode = "SITE",
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = "admin",
                    RowVersion = Guid.NewGuid().ToByteArray()
                });
            }

            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var page = await sut.SearchAsync(new SaDoListQuery { Take = 999 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(105, page.ListPage!.TotalCount);
        Assert.Equal(SaDoRepository.MaxPageSize, page.ListPage.Rows.Count);
        Assert.True(page.ListPage.TotalCount > page.ListPage.Rows.Count);
    }

    // ───────────────────────────────────────────────────────
    // Helper: create a POSTED→CLOSED DO
    // ───────────────────────────────────────────────────────

    private async Task<string> CreatePostedClosedDo(SaDoService sut)
    {
        var save = await sut.SaveNewAsync(Request(qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var posted = await sut.PostAsync([Keyed(save.DoNo!, save.Document!.RowVersion)]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        var rv = GetRowVersion(save.DoNo!);
        var fc = await sut.ForceCloseAsync([Keyed(save.DoNo!, rv)]);
        Assert.True(fc.Succeeded, fc.ErrorMessage);
        return save.DoNo!;
    }

    private byte[] GetRowVersion(string doNo)
    {
        using var db = _factory.CreateDbContext();
        var do_ = db.SaDos.AsNoTracking().Single(x => x.DoNo == doNo);
        return do_.RowVersion ?? [];
    }

    // ───────────────────────────────────────────────────────
    // Seed helpers
    // ───────────────────────────────────────────────────────

    private async Task<int> SeedBalLocAsync(
        decimal qty,
        DateTime? transDate = null,
        string lot = "",
        string? locationCode = "SITE")
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = lot,
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA",
            TransDate = transDate ?? FixedToday,
            LocationCode = locationCode
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static SaDoSaveRequest Request(
        decimal qty,
        decimal price,
        string cust = "CUST01",
        DateTime? date = null,
        string? currency = null,
        string? iCode = "A100",
        byte[]? rowVersion = null) =>
        new()
        {
            DoDate = date ?? FixedToday,
            CustCode = cust,
            Currency = currency ?? "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            RowVersion = rowVersion,
            Lines = [Line(iCode ?? "A100", qty, price)]
        };

    private static SaDoLineRequest Line(string iCode, decimal qty, decimal price) =>
        new()
        {
            ICode = iCode,
            Qty = qty,
            UnitPrice = price,
            FrWarehouse = "MAIN"
        };

    private static SaDoKeyedRequest Keyed(string doNo, byte[] rowVersion) =>
        new() { DoNo = doNo, RowVersion = rowVersion };

    private async Task<SaDoOperationResult> ShipAsync(
        SaDoService sut,
        string doNo,
        bool overwriteExisting = false,
        byte[]? rowVersion = null)
    {
        var token = rowVersion;
        if (token is null || token.Length == 0)
        {
            token = GetRowVersion(doNo);
        }

        return await sut.AddShipmentAsync(doNo, overwriteExisting, token);
    }

    private async Task<SaDoOperationResult> UpdateDocAsync(
        SaDoService sut,
        string doNo,
        SaDoSaveRequest request)
    {
        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            request.RowVersion = GetRowVersion(doNo);
        }

        return await sut.UpdateAsync(doNo, request);
    }

    // ───────────────────────────────────────────────────────
    // SUT factory
    // ───────────────────────────────────────────────────────

    private SaDoService CreateSut(
        string location = "SITE",
        IDocumentNumberingService? numbering = null,
        IInventoryTenantContext? tenant = null,
        Mock<IAccessRightService>? access = null)
    {
        tenant ??= InventoryTenantTestHelper.CreateTenantContext(location: location);
        var postingRepo = new IvStockPostingRepository();
        var accessObj = (access ?? Access()).Object;
        var salesOrders = new SaSoRepository();
        var docApplication = new SaDocApplicationService(salesOrders, new SaDoRepository());
        return new SaDoService(
            _factory,
            tenant,
            accessObj,
            numbering ?? new FakeDoDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new SaDoRepository(),
            new SaCustRepository(_factory),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreatePosting(),
            new IvSpShipmentService(postingRepo, new IvStockTransactionRepository(), new RunningNumberService()),
            salesOrders,
            docApplication,
            new SaCustLookupService(_factory, tenant),
            NullLogger<SaDoService>.Instance);
    }

    private IvInventoryPostingService CreatePosting() =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static Mock<IAccessRightService> DenyPermission(string permission)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.Is<string>(p => string.Equals(p, permission, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.Is<string>(p => !string.Equals(p, permission, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
