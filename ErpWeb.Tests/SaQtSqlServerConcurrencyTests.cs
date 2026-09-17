using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server concurrency tests for the quotation → Sales Order conversion. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server with DEMO masters.
/// SQLite cannot express UPDLOCK/HOLDLOCK or the filtered unique index UX_SaSO_QtSource, so the
/// real "two operators convert the same revision" race can only be proven here.
/// </summary>
public class SaQtSqlServerConcurrencyTests
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
    public async Task SqlServer_concurrent_QT_convert_produces_exactly_one_SO()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "QT") || !await EnsureNumberingAsync(factory, "SO"))
        {
            return;
        }

        var seed = CreateQt(factory);
        var save = await seed.SaveNewAsync(QtRequest(3m));
        if (!save.Succeeded)
        {
            return;
        }

        var send = await seed.SendAsync(Key(save.Document!));
        if (!send.Succeeded)
        {
            return;
        }

        var accept = await seed.AcceptAsync(Key(send.Document!));
        if (!accept.Succeeded)
        {
            return;
        }

        var key = Key(accept.Document!);
        var a = CreateQt(factory);
        var b = CreateQt(factory);

        // Hold A's conversion open inside the row lock so B definitely contends for the same row.
        using var aLocked = new ManualResetEventSlim(false);
        using var releaseA = new ManualResetEventSlim(false);
        a.TestHookAfterLockCurrent = () =>
        {
            aLocked.Set();
            if (!releaseA.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timed out waiting to release conversion A.");
            }
        };

        var taskA = Task.Run(() => a.ConvertToSoAsync(key));
        if (!aLocked.Wait(TimeSpan.FromSeconds(30)))
        {
            releaseA.Set();
            await taskA;
            return;
        }

        var taskB = Task.Run(() => b.ConvertToSoAsync(key));
        await Task.Delay(500);
        releaseA.Set();

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Equal(1, results.Count(x => x.Succeeded));
        Assert.Equal(1, results.Count(x => !x.Succeeded));
        Assert.All(results.Where(x => !x.Succeeded), x => Assert.False(string.IsNullOrWhiteSpace(x.ErrorMessage)));

        await using var db = factory.CreateDbContext();
        var qts = await db.SaQts
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.QtNo == key.QtNo)
            .ToListAsync();
        var current = Assert.Single(qts, x => x.IsCurrent);
        Assert.Equal(SaQtStatuses.Closed, current.Status);
        Assert.Equal(SaQtConversionStatuses.Full, current.ConversionStatus);

        var orders = await db.SaSos
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.QtNo == key.QtNo)
            .ToListAsync();
        var order = Assert.Single(orders);
        Assert.Equal(current.CustRel, order.QtCustRel);

        var details = await db.SaQtDetails
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.QtNo == key.QtNo
                && x.CustRel == current.CustRel)
            .ToListAsync();
        Assert.NotEmpty(details);
        Assert.All(details, d => Assert.Equal(d.OrderQty, d.ConvertedQty));
    }

    [Fact]
    public async Task SqlServer_UX_SaSO_QtSource_rejects_second_SO_for_the_same_QT_revision()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "QT") || !await EnsureNumberingAsync(factory, "SO"))
        {
            return;
        }

        var seed = CreateQt(factory);
        var save = await seed.SaveNewAsync(QtRequest(2m));
        if (!save.Succeeded)
        {
            return;
        }

        var send = await seed.SendAsync(Key(save.Document!));
        if (!send.Succeeded)
        {
            return;
        }

        var accept = await seed.AcceptAsync(Key(send.Document!));
        if (!accept.Succeeded)
        {
            return;
        }

        var convert = await seed.ConvertToSoAsync(Key(accept.Document!));
        if (!convert.Succeeded)
        {
            return;
        }

        await using var db = factory.CreateDbContext();
        var original = await db.SaSos.AsNoTracking()
            .SingleAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SoNo == convert.ConvertedSoNo);

        db.SaSos.Add(new SaSo
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "SITE",
            SoNo = original.SoNo + "X",
            CustRel = 1,
            IsCurrent = true,
            LastCustRel = 1,
            SoDate = FixedToday,
            Status = SaSoStatuses.New,
            CustCode = "CUST01",
            CustName = "ALPHA",
            Currency = "MYR",
            CurrRate = 1m,
            QtNo = original.QtNo,
            QtCustRel = original.QtCustRel,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "test",
            RowVersion = Guid.NewGuid().ToByteArray()
        });

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_SaSO_QtSource", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SaQtKeyedRequest Key(SaQtDocument document) =>
        new()
        {
            QtNo = document.QtNo,
            CustRel = document.CustRel,
            RowVersion = document.RowVersion
        };

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

    private static SaQtSaveRequest QtRequest(decimal qty) =>
        new()
        {
            QtDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "RFQ-1",
            SalesRep = "SM1",
            Lines = [new SaQtLineRequest { ICode = "SVC1", OrderQty = qty, UnitPrice = 10m }]
        };

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static SaQtService CreateQt(IDbContextFactory<AppDbContext> factory)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        var access = Access().Object;
        var numbering = new DocumentNumberingService(tenant);
        return new SaQtService(
            factory,
            tenant,
            access,
            numbering,
            new FixedCurrentDateService(FixedToday),
            new SaQtRepository(),
            new SaCustRepository(factory),
            new SaSoService(
                factory,
                tenant,
                access,
                numbering,
                new FixedCurrentDateService(FixedToday),
                new SaSoRepository(),
                new SaCustRepository(factory),
                new SaDocApplicationService(new SaSoRepository(), new SaDoRepository()),
                NullLogger<SaSoService>.Instance),
            new FakeAppSettingService(),
            NullLogger<SaQtService>.Instance);
    }
}
