using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server concurrency/atomicity tests for sales invoices. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server with DEMO masters.
/// </summary>
public class SaInvoiceSqlServerConcurrencyTests
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

    [Fact]
    public async Task SqlServer_stale_invoice_token_fails()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory);
        if (fixture is null)
        {
            return;
        }

        var sut = CreateSut(factory);
        var save = await sut.SaveNewAsync(Request(fixture, qty: 1m, price: 10m, iCode: "SVC1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = (byte[])save.Document!.RowVersion.Clone();

        var first = await UpdateDocAsync(sut, save.InvNo!, Request(fixture, qty: 2m, price: 10m, iCode: "SVC1"));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var second = await sut.UpdateAsync(save.InvNo!, Request(fixture, qty: 3m, price: 10m, iCode: "SVC1", rowVersion: stale));
        Assert.False(second.Succeeded);
        Assert.Equal(SaInvoiceErrorKind.Concurrency, second.ErrorKind);
    }

    [Fact]
    public async Task SqlServer_AddShipment_bumps_RowVersion()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null)
        {
            return;
        }

        var sut = CreateSut(factory);
        var save = await sut.SaveNewAsync(Request(fixture, qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var before = save.Document!.RowVersion.ToArray();

        var ship = await ShipAsync(sut, save.InvNo!);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        Assert.False(before.SequenceEqual(ship.Document!.RowVersion));
    }

    [Fact]
    public async Task SqlServer_stale_shipment_confirm_token_fails_after_rebuild()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null)
        {
            return;
        }

        var sut = CreateSut(factory);
        var save = await sut.SaveNewAsync(Request(fixture, qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var first = await ShipAsync(sut, save.InvNo!);
        Assert.True(first.Succeeded, first.ErrorMessage);
        var stale = first.Document!.RowVersion.ToArray();

        var rebuild = await ShipAsync(sut, save.InvNo!, overwriteExisting: true);
        Assert.True(rebuild.Succeeded, rebuild.ErrorMessage);

        var staleConfirm = await sut.AddShipmentAsync(save.InvNo!, overwriteExisting: true, stale);
        Assert.False(staleConfirm.Succeeded);
        Assert.Equal(SaInvoiceErrorKind.Concurrency, staleConfirm.ErrorKind);
    }

    [Fact]
    public async Task SqlServer_concurrent_sp_create_one_batch()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null)
        {
            return;
        }

        var sut = CreateSut(factory);
        var save = await sut.SaveNewAsync(Request(fixture, qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var token = save.Document!.RowVersion;

        var t1 = sut.AddShipmentAsync(save.InvNo!, overwriteExisting: false, token);
        var t2 = sut.AddShipmentAsync(save.InvNo!, overwriteExisting: false, token);
        var results = await Task.WhenAll(t1, t2);

        var ok = results.Count(x => x.Succeeded);
        var confirm = results.Count(x => x.RequiresConfirmation);
        var concurrency = results.Count(x => x.ErrorKind == SaInvoiceErrorKind.Concurrency);
        Assert.Equal(1, ok);
        Assert.True(confirm + concurrency >= 1);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.IvTrxBatches.CountAsync(x =>
            x.CompanyCode == "DEMO"
            && x.BranchCode == "HQ"
            && x.TrxType == IvTrxTypes.SalesOut
            && x.RefNo == save.InvNo));
    }

    [Fact]
    public async Task SqlServer_forced_exception_mid_rebuild_keeps_old_sp()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null)
        {
            return;
        }

        var sut = CreateSut(factory);
        var save = await sut.SaveNewAsync(Request(fixture, qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var first = await ShipAsync(sut, save.InvNo!);
        Assert.True(first.Succeeded, first.ErrorMessage);
        var version = first.Document!.RowVersion.ToArray();
        var detailCount = first.Document.Shipment.Count;

        sut.TestHookAfterSpDelete = () => throw new InvalidOperationException("forced mid rebuild");
        var failed = await ShipAsync(sut, save.InvNo!, overwriteExisting: true, rowVersion: version);
        Assert.False(failed.Succeeded);

        var after = await sut.GetAsync(save.InvNo!);
        Assert.True(version.SequenceEqual(after.Document!.RowVersion));
        Assert.Equal(detailCount, after.Document.Shipment.Count);
    }

    [Fact]
    public async Task SqlServer_concurrent_new_invoices_no_duplicate_InvNo()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory);
        if (fixture is null)
        {
            return;
        }

        var sutA = CreateSut(factory);
        var sutB = CreateSut(factory);
        var reqA = Request(fixture, qty: 1m, price: 10m, iCode: "SVC1");
        var reqB = Request(fixture, qty: 1m, price: 11m, iCode: "SVC1");

        var results = await Task.WhenAll(sutA.SaveNewAsync(reqA), sutB.SaveNewAsync(reqB));
        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorMessage));
        Assert.NotEqual(results[0].InvNo, results[1].InvNo);
    }

    [Fact]
    public async Task SqlServer_update_vs_shipment_race_one_winner()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null)
        {
            return;
        }

        var sut = CreateSut(factory);
        var save = await sut.SaveNewAsync(Request(fixture, qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var token = save.Document!.RowVersion;

        var updateTask = sut.UpdateAsync(save.InvNo!, Request(fixture, qty: 6m, price: 10m, rowVersion: token));
        var shipTask = sut.AddShipmentAsync(save.InvNo!, overwriteExisting: false, token);
        var results = await Task.WhenAll(updateTask, shipTask);

        var winners = results.Count(x => x.Succeeded);
        var losers = results.Count(x => !x.Succeeded);
        Assert.Equal(1, winners);
        Assert.Equal(1, losers);
        Assert.Contains(results, x => x.ErrorKind == SaInvoiceErrorKind.Concurrency);
    }

    [Fact]
    public async Task SqlServer_opposite_discovery_order_no_deadlock_on_shared_lots()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: false);
        if (fixture is null)
        {
            return;
        }

        await using (var db = factory.CreateDbContext())
        {
            var loc = await db.IvLocations
                .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN" && x.IsActive)
                .Select(x => x.LocCode)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(loc))
            {
                return;
            }

            db.IvBalLocs.AddRange(
                new IvBalLoc
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    ICode = fixture.ICode,
                    WhCode = "MAIN",
                    LocCode = loc,
                    LotNo = "A-LOT",
                    IStatus = IvItemStatuses.Active,
                    StdQty = 6m,
                    StdUom = "EA",
                    TransDate = FixedToday.AddDays(-20),
                    LocationCode = "SITE"
                },
                new IvBalLoc
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    ICode = fixture.ICode,
                    WhCode = "MAIN",
                    LocCode = loc,
                    LotNo = "Z-LOT",
                    IStatus = IvItemStatuses.Active,
                    StdQty = 6m,
                    StdUom = "EA",
                    TransDate = FixedToday.AddDays(-1),
                    LocationCode = "SITE"
                });
            await db.SaveChangesAsync();
        }

        var sutA = CreateSut(factory);
        var sutB = CreateSut(factory);
        var saveA = await sutA.SaveNewAsync(Request(fixture, qty: 6m, price: 10m));
        var saveB = await sutB.SaveNewAsync(Request(fixture, qty: 6m, price: 10m));
        Assert.True(saveA.Succeeded, saveA.ErrorMessage);
        Assert.True(saveB.Succeeded, saveB.ErrorMessage);

        var results = await Task.WhenAll(
            ShipAsync(sutA, saveA.InvNo!),
            ShipAsync(sutB, saveB.InvNo!));

        Assert.All(results, r => Assert.True(
            r.Succeeded || r.ErrorKind is SaInvoiceErrorKind.Concurrency or SaInvoiceErrorKind.Unexpected,
            r.ErrorMessage));
        Assert.DoesNotContain(results, r =>
            (r.ErrorMessage ?? string.Empty).Contains("deadlock", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, r => r.Succeeded);
    }

    [Fact]
    public async Task SqlServer_stale_Post_fails_before_physical_deduction()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null)
        {
            return;
        }

        int balId;
        await using (var db = factory.CreateDbContext())
        {
            var bal = await db.IvBalLocs.SingleAsync(x => x.ICode == fixture.ICode);
            bal.StdQty = 10m;
            await db.SaveChangesAsync();
            balId = bal.Id;
        }

        var sut = CreateSut(factory);
        var save = await sut.SaveNewAsync(Request(fixture, qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await ShipAsync(sut, save.InvNo!)).Succeeded);

        await using (var db = factory.CreateDbContext())
        {
            var aBatch = await db.IvTrxBatches.SingleAsync(x => x.RefNo == save.InvNo);
            var spoiler = new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = aBatch.BatchNo + 9000,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.SalesOut,
                BatchStatus = IvBatchStatuses.New,
                RefNo = "SPOIL-" + Guid.NewGuid().ToString("N")[..8],
                LocationCode = "SITE",
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "admin"
            };
            spoiler.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = spoiler.BatchNo,
                TrxLineNo = 1,
                TrxType = IvTrxTypes.SalesOut,
                ICode = fixture.ICode,
                FrWarehouse = "MAIN",
                FrLocation = (await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.LocCode).SingleAsync()),
                FrLotNo = string.Empty,
                FrStdQty = 6m,
                FrStdUom = "EA",
                IStatus = IvItemStatuses.Active,
                FromBalLocId = balId,
                InvNo = spoiler.RefNo,
                SoLineNo = 1,
                LocationCode = "SITE"
            });
            db.IvTrxBatches.Add(spoiler);
            await db.SaveChangesAsync();
        }

        var post = await sut.PostAsync([save.InvNo!]);
        Assert.False(post.Succeeded);
        Assert.Contains(post.Posting, x => !x.Succeeded);

        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(SaInvoiceStatuses.New, (await db.SaInvoices.SingleAsync(x => x.InvNo == save.InvNo)).Status);
            Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(x => x.RefNo == save.InvNo)).BatchStatus);
            Assert.Equal(10m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        }
    }

    [Fact]
    public async Task SqlServer_alter_script_columns_present_and_legacy_row_loads()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        if (!await db.SaInvoices.AnyAsync())
        {
            // Still prove columns exist via projection.
        }

        try
        {
            _ = await db.SaInvoices
                .Select(x => new { x.InvPrefix, x.PayCode, x.InvAddress4, x.ShipName, x.Remark })
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            Assert.Fail("Run scripts/alter-sainvoice-header.sql before deploying invoice entry. " + ex.Message);
        }

        var invNo = "LEG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.SaInvoices.Add(new SaInvoice
        {
            CompanyCode = "DEMO",
            InvNo = invNo,
            BranchCode = "HQ",
            LocationCode = "SITE",
            CustCode = "LEGACY",
            InvDate = FixedToday,
            Status = SaInvoiceStatuses.New,
            DoNo = invNo,
            Currency = "MYR",
            CurrRate = 1m,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "test"
        });
        await db.SaveChangesAsync();

        var sut = CreateSut(factory);
        var get = await sut.GetAsync(invNo);
        Assert.True(get.Succeeded, get.ErrorMessage);
        Assert.Null(get.Document!.PayCode);
        Assert.Null(get.Document.InvAddress4);
    }

    private static TestDbContextFactory CreateFactory()
    {
        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        return new TestDbContextFactory(options);
    }

    private sealed record Fixture(string CustCode, string PayCode, string ICode, string ServiceCode);

    private static async Task<Fixture?> SeedFixtureAsync(
        IDbContextFactory<AppDbContext> factory,
        bool withStock = false)
    {
        await using var db = factory.CreateDbContext();
        if (!await db.IvWarehouses.AnyAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN"))
        {
            return null;
        }

        if (!await db.SaCurrencies.AnyAsync(x => x.CompanyCode == "DEMO" && x.CurrCode == "MYR" && x.IsActive == true))
        {
            return null;
        }

        var classCode = await db.IvClasses.Where(x => x.CompanyCode == "DEMO").Select(x => x.IClassCode).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(classCode))
        {
            return null;
        }

        var payCode = "P" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        if (!await db.IvMsCodes.AnyAsync(x => x.CodeType == IvMsCodeTypes.PayCode && x.Code == payCode))
        {
            db.IvMsCodes.Add(new IvMsCode { Code = payCode, Name = "Test pay", CodeType = IvMsCodeTypes.PayCode });
        }

        var custCode = "C" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = custCode,
            CustName = "Invoice concurrency",
            Currency = "MYR",
            PayCode = payCode,
            IsActive = true
        });

        var iCode = "I" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var serviceCode = "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            IDesc = "Invoice stock item",
            IClassCode = classCode,
            StdUom = "EA",
            StockControl = true,
            IsActive = true,
            SellingPrice = 10m,
            DefWarehouse = "MAIN"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = serviceCode,
            IDesc = "Invoice service",
            IClassCode = classCode,
            StdUom = "EA",
            StockControl = false,
            IsActive = true,
            SellingPrice = 10m
        });
        await db.SaveChangesAsync();

        if (withStock)
        {
            var loc = await db.IvLocations
                .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN" && x.IsActive)
                .Select(x => x.LocCode)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(loc))
            {
                return null;
            }

            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                ICode = iCode,
                WhCode = "MAIN",
                LocCode = loc,
                LotNo = string.Empty,
                IStatus = IvItemStatuses.Active,
                StdQty = 100m,
                StdUom = "EA",
                TransDate = FixedToday.AddDays(-10),
                LocationCode = "SITE"
            });
            await db.SaveChangesAsync();
        }

        if (!await db.AdSmNumDates.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "INV"
                && x.Year == 2026 && x.Month == 9))
        {
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                Year = 2026,
                Month = 9,
                NumCd = "INV",
                NumDes = "Sales Invoice",
                Prefix = "INV",
                TotLength = 4,
                NumberingDelimeter = "-",
                Seq = 1
            });
            await db.SaveChangesAsync();
        }

        return new Fixture(custCode, payCode, iCode, serviceCode);
    }

    private static SaInvoiceSaveRequest Request(
        Fixture fixture,
        decimal qty,
        decimal price,
        string? iCode = null,
        byte[]? rowVersion = null) =>
        new()
        {
            InvDate = FixedToday,
            CustCode = fixture.CustCode,
            Currency = "MYR",
            PayCode = fixture.PayCode,
            RowVersion = rowVersion,
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = iCode ?? fixture.ICode,
                    Qty = qty,
                    UnitPrice = price,
                    FrWarehouse = "MAIN"
                }
            ]
        };

    private static async Task<SaInvoiceOperationResult> ShipAsync(
        SaInvoiceService sut,
        string invNo,
        bool overwriteExisting = false,
        byte[]? rowVersion = null)
    {
        var token = rowVersion;
        if (token is null || token.Length == 0)
        {
            var current = await sut.GetAsync(invNo);
            Assert.True(current.Succeeded, current.ErrorMessage);
            token = current.Document!.RowVersion;
        }

        return await sut.AddShipmentAsync(invNo, overwriteExisting, token);
    }

    private static async Task<SaInvoiceOperationResult> UpdateDocAsync(
        SaInvoiceService sut,
        string invNo,
        SaInvoiceSaveRequest request)
    {
        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            var current = await sut.GetAsync(invNo);
            Assert.True(current.Succeeded, current.ErrorMessage);
            request.RowVersion = current.Document!.RowVersion;
        }

        return await sut.UpdateAsync(invNo, request);
    }

    private static SaInvoiceService CreateSut(IDbContextFactory<AppDbContext> factory)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
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
            new SaCustLookupService(factory, tenant),
            NullLogger<SaInvoiceService>.Instance);
    }
}
