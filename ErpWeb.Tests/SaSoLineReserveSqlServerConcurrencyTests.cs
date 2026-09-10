using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server concurrency tests for SO soft-reserve. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server with DEMO masters.
/// SQLite does not prove UPDLOCK/HOLDLOCK.
/// </summary>
public class SaSoLineReserveSqlServerConcurrencyTests
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return null;
        }

        if (!cs.Contains("Database=", StringComparison.OrdinalIgnoreCase)
            && !cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return cs;
    }

    public static bool IsSqlServerAvailable()
    {
        var cs = GetSqlServerConnectionString();
        if (cs is null)
        {
            return false;
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
            using var db = new AppDbContext(options);
            return db.Database.IsSqlServer() && db.Database.CanConnect();
        }
        catch
        {
            return false;
        }
    }

    private static IDbContextFactory<AppDbContext> CreateFactory()
    {
        var cs = GetSqlServerConnectionString()
            ?? throw new InvalidOperationException("SQL Server connection string is required.");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        return new TestDbContextFactory(options);
    }

    [Fact]
    public async Task SqlServer_parallel_NEW_DO_saves_only_one_takes_full_qty()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "SO") || !await EnsureNumberingAsync(factory, "DO"))
        {
            return;
        }

        var so = CreateSo(factory);
        var soSave = await so.SaveNewAsync(SoRequest(100m));
        if (!soSave.Succeeded)
        {
            return;
        }

        var dosA = CreateDo(factory);
        var dosB = CreateDo(factory);
        var results = await Task.WhenAll(
            dosA.SaveNewAsync(DoRequest(100m, soSave.SoNo!)),
            dosB.SaveNewAsync(DoRequest(100m, soSave.SoNo!)));

        Assert.Equal(1, results.Count(x => x.Succeeded));
        Assert.Equal(1, results.Count(x => !x.Succeeded));

        await using var db = factory.CreateDbContext();
        var live = await db.SaDoDetails
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SoNo == soSave.SoNo)
            .SumAsync(x => x.Qty);
        Assert.Equal(100m, live);
    }

    [Fact]
    public async Task SqlServer_parallel_DO_vs_direct_invoice_never_both_full()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "SO")
            || !await EnsureNumberingAsync(factory, "DO")
            || !await EnsureNumberingAsync(factory, "INV"))
        {
            return;
        }

        var so = CreateSo(factory);
        var soSave = await so.SaveNewAsync(SoRequest(100m));
        if (!soSave.Succeeded)
        {
            return;
        }

        var dos = CreateDo(factory);
        var inv = CreateInvoice(factory);
        var doTask = dos.SaveNewAsync(DoRequest(100m, soSave.SoNo!));
        var invTask = inv.SaveNewAsync(InvoiceRequest(100m, soSave.SoNo!));
        await Task.WhenAll(doTask, invTask);
        var doResult = await doTask;
        var invResult = await invTask;
        var winners = (doResult.Succeeded ? 1 : 0) + (invResult.Succeeded ? 1 : 0);
        Assert.Equal(1, winners);
    }

    [Fact]
    public async Task SqlServer_parallel_direct_invoice_saves_only_one_full()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "SO") || !await EnsureNumberingAsync(factory, "INV"))
        {
            return;
        }

        var so = CreateSo(factory);
        var soSave = await so.SaveNewAsync(SoRequest(100m));
        if (!soSave.Succeeded)
        {
            return;
        }

        var invA = CreateInvoice(factory);
        var invB = CreateInvoice(factory);
        var results = await Task.WhenAll(
            invA.SaveNewAsync(InvoiceRequest(100m, soSave.SoNo!)),
            invB.SaveNewAsync(InvoiceRequest(100m, soSave.SoNo!)));

        Assert.Equal(1, results.Count(x => x.Succeeded));
        Assert.Equal(1, results.Count(x => !x.Succeeded));
    }

    [Fact]
    public async Task SqlServer_POST_DO_vs_SAVE_invoice_and_second_DO()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "SO")
            || !await EnsureNumberingAsync(factory, "DO")
            || !await EnsureNumberingAsync(factory, "INV"))
        {
            return;
        }

        var so = CreateSo(factory);
        var dos = CreateDo(factory);
        var inv = CreateInvoice(factory);

        var soSave = await so.SaveNewAsync(SoRequest(100m));
        if (!soSave.Succeeded)
        {
            return;
        }

        var do60 = await dos.SaveNewAsync(DoRequest(60m, soSave.SoNo!));
        Assert.True(do60.Succeeded, do60.ErrorMessage);

        var postTask = dos.PostAsync([new SaDoKeyedRequest { DoNo = do60.DoNo!, RowVersion = do60.Document!.RowVersion }]);
        var inv50Task = inv.SaveNewAsync(InvoiceRequest(50m, soSave.SoNo!));
        await Task.WhenAll(postTask, inv50Task);
        var postResult = await postTask;
        var inv50Result = await inv50Task;
        Assert.True(postResult.Succeeded || postResult.Posting.Any(x => x.Succeeded));
        Assert.False(inv50Result.Succeeded);

        var so2 = await so.SaveNewAsync(SoRequest(100m));
        if (!so2.Succeeded)
        {
            return;
        }

        var doA = await dos.SaveNewAsync(DoRequest(60m, so2.SoNo!));
        Assert.True(doA.Succeeded, doA.ErrorMessage);
        var postDoTask = dos.PostAsync([new SaDoKeyedRequest { DoNo = doA.DoNo!, RowVersion = doA.Document!.RowVersion }]);
        var secondDoTask = dos.SaveNewAsync(DoRequest(50m, so2.SoNo!));
        await Task.WhenAll(postDoTask, secondDoTask);
        var postDoResult = await postDoTask;
        var secondDoResult = await secondDoTask;
        Assert.True(postDoResult.Succeeded || postDoResult.Posting.Any(x => x.Succeeded));
        Assert.False(secondDoResult.Succeeded);

        await using var db = factory.CreateDbContext();
        var delivered = await db.SaSoDetails.Where(x => x.SoNo == so2.SoNo).Select(x => x.DeliveredQty).SingleAsync();
        var newDoQty = await db.SaDoDetails
            .Where(x => x.SoNo == so2.SoNo && db.SaDos.Any(h =>
                h.CompanyCode == x.CompanyCode && h.BranchCode == x.BranchCode
                && h.DoNo == x.DoNo && h.Status == SaDoStatuses.New))
            .SumAsync(x => (decimal?)x.Qty) ?? 0m;
        Assert.True(delivered + newDoQty <= 100m);
    }

    [Fact]
    public async Task SqlServer_parallel_standalone_DO_INV_posts_cannot_over_allocate()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "DO") || !await EnsureNumberingAsync(factory, "INV"))
        {
            return;
        }

        var dos = CreateDo(factory);
        var doSave = await dos.SaveNewAsync(StandaloneDoRequest(10m));
        if (!doSave.Succeeded)
        {
            return;
        }

        Assert.True((await dos.PostAsync([
            new SaDoKeyedRequest { DoNo = doSave.DoNo!, RowVersion = doSave.Document!.RowVersion }
        ])).Succeeded);

        var invA = CreateInvoice(factory);
        var invB = CreateInvoice(factory);
        var saveA = await invA.SaveNewAsync(InvoiceFromDoStandalone(10m, doSave.DoNo!));
        var saveB = await invB.SaveNewAsync(InvoiceFromDoStandalone(10m, doSave.DoNo!));
        Assert.True(saveA.Succeeded, saveA.ErrorMessage);
        Assert.True(saveB.Succeeded, saveB.ErrorMessage);

        var posts = await Task.WhenAll(
            invA.PostAsync([saveA.InvNo!]),
            invB.PostAsync([saveB.InvNo!]));

        Assert.Equal(1, posts.Count(x => x.Succeeded));
        Assert.Equal(1, posts.Count(x => !x.Succeeded));
        Assert.Contains(
            posts.Where(x => !x.Succeeded),
            x => x.Posting.Any(p => p.ReasonCode == SaDocAllocationReasonCodes.OverAllocate));

        await using var db = factory.CreateDbContext();
        var billed = await db.SaDocApplications
            .Where(x =>
                x.CompanyCode == "DEMO"
                && x.BranchCode == "HQ"
                && x.SourceDocType == SaDocTypes.Do
                && x.SourceDocId == doSave.DoNo
                && x.TargetDocType == SaDocTypes.Inv)
            .SumAsync(x => x.AppliedQty);
        Assert.True(billed <= 10m);
    }

    private static async Task<bool> EnsureNumberingAsync(IDbContextFactory<AppDbContext> factory, string numCd)
    {
        await using var db = factory.CreateDbContext();
        if (!await db.AdSmNumDates.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == numCd
                && x.Year == 2026 && x.Month == 9))
        {
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                Year = 2026,
                Month = 9,
                NumCd = numCd,
                NumDes = numCd,
                Prefix = numCd,
                TotLength = 4,
                NumberingDelimeter = "-",
                Seq = 1
            });
            try
            {
                await db.SaveChangesAsync();
            }
            catch
            {
                return false;
            }
        }

        return await db.SaCusts.AnyAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST01")
            || await db.SaCusts.AnyAsync(x => x.CompanyCode == "DEMO");
    }

    private static SaSoSaveRequest SoRequest(decimal qty) =>
        new()
        {
            SoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "PO-1",
            SalesRep = "SM1",
            Lines = [new SaSoLineRequest { ICode = "SVC1", OrderQty = qty, UnitPrice = 10m }]
        };

    private static SaDoSaveRequest DoRequest(decimal qty, string soNo) =>
        new()
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Lines =
            [
                new SaDoLineRequest
                {
                    ICode = "SVC1",
                    Qty = qty,
                    UnitPrice = 10m,
                    SoNo = soNo,
                    SoLine = 1
                }
            ]
        };

    private static SaDoSaveRequest StandaloneDoRequest(decimal qty) =>
        new()
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Lines =
            [
                new SaDoLineRequest
                {
                    ICode = "SVC1",
                    Qty = qty,
                    UnitPrice = 10m,
                    SoNo = "",
                    SoLine = null
                }
            ]
        };

    private static SaInvoiceSaveRequest InvoiceRequest(decimal qty, string soNo) =>
        new()
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            InvName = "Alpha",
            InvAddress1 = "INV ADDR 1",
            InvCity = "INV CITY",
            InvPostalCode = "50000",
            InvCountry = "MY",
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = qty,
                    UnitPrice = 10m,
                    SoNo = soNo,
                    SoLine = 1
                }
            ]
        };

    private static SaInvoiceSaveRequest InvoiceFromDoStandalone(decimal qty, string doNo) =>
        new()
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            InvName = "Alpha",
            InvAddress1 = "INV ADDR 1",
            InvCity = "INV CITY",
            InvPostalCode = "50000",
            InvCountry = "MY",
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = qty,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doNo,
                    DoLine = 1
                }
            ]
        };

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static SaSoService CreateSo(IDbContextFactory<AppDbContext> factory)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        return new SaSoService(
            factory,
            tenant,
            Access().Object,
            new DocumentNumberingService(tenant),
            new FixedCurrentDateService(FixedToday),
            new SaSoRepository(),
            new SaCustRepository(factory),
            new SaDocApplicationService(new SaSoRepository(), new SaDoRepository()),
            NullLogger<SaSoService>.Instance);
    }

    private static SaDoService CreateDo(IDbContextFactory<AppDbContext> factory)
    {
        var postingRepo = new IvStockPostingRepository();
        var salesOrders = new SaSoRepository();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        var access = Access();
        var posting = new IvInventoryPostingService(
            factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(factory),
            NullLogger<IvInventoryPostingService>.Instance);
        return new SaDoService(
            factory,
            tenant,
            access.Object,
            new DocumentNumberingService(tenant),
            new FixedCurrentDateService(FixedToday),
            new SaDoRepository(),
            new SaCustRepository(factory),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            new IvSpShipmentService(postingRepo, new IvStockTransactionRepository(), new RunningNumberService()),
            salesOrders,
            new SaDocApplicationService(salesOrders, new SaDoRepository()),
            new SaCustLookupService(factory, tenant),
            NullLogger<SaDoService>.Instance);
    }

    private static SaInvoiceService CreateInvoice(IDbContextFactory<AppDbContext> factory)
    {
        var postingRepo = new IvStockPostingRepository();
        var salesOrders = new SaSoRepository();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        var access = Access();
        var posting = new IvInventoryPostingService(
            factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(factory),
            NullLogger<IvInventoryPostingService>.Instance);
        return new SaInvoiceService(
            factory,
            tenant,
            access.Object,
            new RunningNumberService(),
            new DocumentNumberingService(tenant),
            new FixedCurrentDateService(FixedToday),
            new SaInvoiceRepository(),
            new SaCustRepository(factory),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            new IvSpShipmentService(postingRepo, new IvStockTransactionRepository(), new RunningNumberService()),
            salesOrders,
            new SaDocApplicationService(salesOrders, new SaDoRepository()),
            new SaCustLookupService(factory, tenant),
            NullLogger<SaInvoiceService>.Instance);
    }
}
