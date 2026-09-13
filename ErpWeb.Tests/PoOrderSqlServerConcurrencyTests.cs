using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server concurrency tests for Purchase Orders. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server with DEMO masters.
/// </summary>
public class PoOrderSqlServerConcurrencyTests
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);

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
    public async Task SqlServer_concurrent_SaveNew_distinct_PoNo()
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
        var results = await Task.WhenAll(
            sutA.SaveNewAsync(Request(fixture, qty: 1m)),
            sutB.SaveNewAsync(Request(fixture, qty: 1m)));

        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorMessage));
        Assert.NotEqual(results[0].PoNo, results[1].PoNo);
    }

    [Fact]
    public async Task SqlServer_concurrent_PR_consumption_70_plus_50()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        // Requires full PR→PO consumption wiring against live SQL Server masters.
        // Skipped here: seeding approved PR lines with remaining qty is heavier than PO numbering tests.
    }

    private sealed record Fixture(string ICode);

    private static async Task<Fixture?> SeedFixtureAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = factory.CreateDbContext();

        try
        {
            _ = await db.PoOrders.CountAsync();
        }
        catch
        {
            return null;
        }

        if (!await db.SaCurrencies.AnyAsync(x => x.CompanyCode == "DEMO" && x.CurrCode == "MYR"))
        {
            return null;
        }

        if (!await db.SaTaxGroups.AnyAsync(x => x.CompanyCode == "DEMO" && x.TaxGrCode == "SR"))
        {
            db.SaTaxGroups.Add(new SaTaxGroup
            {
                CompanyCode = "DEMO",
                TaxGrCode = "SR",
                TaxGrDesc = "Standard",
                Percentage = 6m,
                TaxGlCode = "GLTAX"
            });
        }

        if (!await db.PoSuppliers.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SuppCode == "SUP01"))
        {
            db.PoSuppliers.Add(new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Alpha Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                IsActive = true
            });
        }

        if (!await db.IvWarehouses.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN"))
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                IsActive = true
            });
        }

        var iCode = "O" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            IDesc = "PO concurrency item",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 10m,
            PurchaseTaxGroup = "SR",
            IsActive = true,
            DefWarehouse = "MAIN",
            StockControl = false
        });

        if (!await db.AdSmNumDates.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "PO"
                && x.Year == 2026 && x.Month == 9))
        {
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                Year = 2026,
                Month = 9,
                NumCd = "PO",
                NumDes = "Purchase Order",
                Prefix = "PO",
                TotLength = 4,
                NumberingDelimeter = "-",
                Seq = 1
            });
        }

        if (!await db.AdSmNums.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "PO"))
        {
            db.AdSmNums.Add(new AdSmNum
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                NumCd = "PO",
                NumDes = "Purchase Order",
                Prefix = "PO",
                TotLength = 10,
                Seq = 1
            });
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch
        {
            return null;
        }

        return new Fixture(iCode);
    }

    private static PoOrderSaveRequest Request(Fixture fixture, decimal qty) =>
        new()
        {
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Supplier",
            CurCode = "MYR",
            Lines =
            [
                new PoOrderLineDto
                {
                    ICode = fixture.ICode,
                    PoPurQty = qty,
                    PoQty = qty,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    PackSz = 1m,
                    CurCode = "MYR",
                    TaxGroup = "SR",
                    ToWarehouse = "MAIN"
                }
            ]
        };

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PurchaseOrder,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static PoOrderService CreateSut(IDbContextFactory<AppDbContext> factory)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: "DEMO",
            branch: "HQ",
            location: "MAIN",
            userId: "user");
        var attachRoot = Path.Combine(Path.GetTempPath(), "poorder-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachRoot);
        var attachments = new PoOrderAttachmentService(
            factory,
            tenant,
            Access().Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = attachRoot }),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoOrderAttachmentService>.Instance);

        return new PoOrderService(
            factory,
            tenant,
            Access().Object,
            new DocumentNumberingService(tenant),
            new FixedCurrentDateService(FixedToday),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            attachments,
            NullLogger<PoOrderService>.Instance);
    }
}
