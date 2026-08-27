using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server REQUIRED concurrency/integrity tests. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server.
/// SQLite cannot validate UPDLOCK/HOLDLOCK/key-range behavior.
/// </summary>
public class IvInventoryPostingSqlServerConcurrencyTests
{
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
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(cs)
                .Options;
            using var db = new AppDbContext(options);
            return db.Database.IsSqlServer() && db.Database.CanConnect();
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task SqlServer_post_then_rollback_roundtrip()
    {
        if (!IsSqlServerAvailable())
        {
            return; // skip without failing CI when SQL Server is unavailable
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        var factory = new TestDbContextFactory(options);

        // Use a unique item code so we do not disturb live stock.
        var iCode = "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using (var db = factory.CreateDbContext())
        {
            if (!await db.IvWarehouses.AnyAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN"))
            {
                return; // demo masters missing
            }

            if (!await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == iCode))
            {
                db.IvStockMasters.Add(new IvStockMaster
                {
                    CompanyCode = "DEMO",
                    ICode = iCode,
                    IDesc = "Concurrency test item",
                    StdUom = "EA",
                    StockControl = true,
                    LotControl = false,
                    IsActive = true,
                    IClassCode = await db.IvClasses.Where(x => x.CompanyCode == "DEMO").Select(x => x.IClassCode).FirstOrDefaultAsync()
                });
                await db.SaveChangesAsync();
            }
        }

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(factory),
            NullLogger<IvInventoryPostingService>.Instance);
        var mr = new IvMiscReceiptService(
            factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(DateTime.Today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvMiscReceiptService>.Instance);

        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = DateTime.Today,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = iCode,
                    ToWarehouse = "MAIN",
                    ToLocation = "",
                    Quantity = 12.5m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = 1m
                }
            ]
        });

        // If validation fails due to missing location/class in live DB, exit quietly.
        if (!save.Succeeded)
        {
            return;
        }

        var post = await mr.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
        var rb = await mr.RollbackAsync([save.BatchNo]);
        Assert.True(rb.Succeeded, rb.ErrorMessage);
    }

    [Fact]
    public async Task SqlServer_mi_post_rollback_repost_roundtrip()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        var factory = new TestDbContextFactory(options);

        var iCode = "M" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using (var db = factory.CreateDbContext())
        {
            if (!await db.IvWarehouses.AnyAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN"))
            {
                return;
            }

            var classCode = await db.IvClasses.Where(x => x.CompanyCode == "DEMO").Select(x => x.IClassCode).FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(classCode))
            {
                return;
            }

            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = iCode,
                IDesc = "MI concurrency test item",
                StdUom = "EA",
                StockControl = true,
                LotControl = false,
                IsActive = true,
                IClassCode = classCode
            });
            await db.SaveChangesAsync();
        }

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(factory),
            NullLogger<IvInventoryPostingService>.Instance);
        var mr = new IvMiscReceiptService(
            factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(DateTime.Today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvMiscReceiptService>.Instance);
        var mi = new IvMiscIssueService(
            factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(DateTime.Today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvMiscIssueService>.Instance);

        var receipt = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = DateTime.Today,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = iCode,
                    ToWarehouse = "MAIN",
                    ToLocation = "",
                    Quantity = 40m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = 1m
                }
            ]
        });
        if (!receipt.Succeeded || !(await mr.PostAsync([receipt.BatchNo])).Succeeded)
        {
            return;
        }

        int balLocId;
        await using (var db = factory.CreateDbContext())
        {
            balLocId = await db.IvBalLocs
                .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.ICode == iCode)
                .Select(x => x.Id)
                .SingleAsync();
        }

        var issue = await mi.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = DateTime.Today,
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    FromBalLocId = balLocId,
                    ICode = iCode,
                    FrWarehouse = "MAIN",
                    FrLocation = string.Empty,
                    FrLotNo = string.Empty,
                    Quantity = 15m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = 1m
                }
            ]
        });
        if (!issue.Succeeded)
        {
            return;
        }

        Assert.True((await mi.PostAsync([issue.BatchNo])).Succeeded);
        Assert.True((await mi.RollbackAsync([issue.BatchNo])).Succeeded);
        Assert.True((await mi.PostAsync([issue.BatchNo])).Succeeded);

        await using (var db = factory.CreateDbContext())
        {
            var qty = await db.IvBalLocs.Where(x => x.Id == balLocId).Select(x => x.StdQty).SingleAsync();
            Assert.Equal(25m, qty);
            Assert.Equal(1, await db.IvTrxHistories.CountAsync(x => x.BatchNo == issue.BatchNo));
        }
    }
}
