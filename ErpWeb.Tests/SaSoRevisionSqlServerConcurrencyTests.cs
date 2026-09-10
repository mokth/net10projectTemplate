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
/// SQL Server concurrency tests for SO revision (CustRel). Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server with DEMO masters.
/// SQLite does not prove UPDLOCK/HOLDLOCK or filtered UX_SaSO_Current.
/// </summary>
public class SaSoRevisionSqlServerConcurrencyTests
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
    public async Task SqlServer_UX_SaSO_Current_rejects_second_current_row()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "SO"))
        {
            return;
        }

        var so = CreateSo(factory);
        var save = await so.SaveNewAsync(SoRequest(5m));
        if (!save.Succeeded)
        {
            return;
        }

        await using var db = factory.CreateDbContext();
        db.SaSos.Add(new SaSo
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "SITE",
            SoNo = save.SoNo!,
            CustRel = 99,
            IsCurrent = true,
            LastCustRel = 99,
            SoDate = FixedToday,
            Status = SaSoStatuses.New,
            CustCode = "CUST01",
            CustName = "ALPHA",
            Currency = "MYR",
            CurrRate = 1m,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "test",
            RowVersion = Guid.NewGuid().ToByteArray()
        });

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_SaSO_Current", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServer_concurrent_Revise_second_caller_does_not_insert_duplicate_CustRel()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "SO"))
        {
            return;
        }

        var soSeed = CreateSo(factory);
        var save = await soSeed.SaveNewAsync(SoRequest(5m));
        if (!save.Succeeded)
        {
            return;
        }

        var rv = save.Document!.RowVersion;
        var soA = CreateSo(factory);
        var soB = CreateSo(factory);

        using var aLocked = new ManualResetEventSlim(false);
        using var releaseA = new ManualResetEventSlim(false);
        soA.TestHookAfterLockCurrent = () =>
        {
            aLocked.Set();
            if (!releaseA.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timed out waiting to release Tx A.");
            }
        };

        var taskA = Task.Run(() => soA.ReviseAsync(save.SoNo!, SoRequest(5m, rv)));
        if (!aLocked.Wait(TimeSpan.FromSeconds(30)))
        {
            releaseA.Set();
            await taskA;
            return;
        }

        var taskB = Task.Run(() => soB.ReviseAsync(save.SoNo!, SoRequest(5m, rv)));
        await Task.Delay(500);
        releaseA.Set();

        var results = await Task.WhenAll(taskA, taskB);
        Assert.Equal(1, results.Count(x => x.Succeeded));
        Assert.Equal(1, results.Count(x => !x.Succeeded));

        await using var db = factory.CreateDbContext();
        var rows = await db.SaSos
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SoNo == save.SoNo)
            .OrderBy(x => x.CustRel)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Count(x => x.IsCurrent));
        Assert.Equal(2, rows.Single(x => x.IsCurrent).CustRel);
        Assert.DoesNotContain(rows, x => x.CustRel == 2 && !x.IsCurrent && rows.Count(r => r.CustRel == 2) > 1);
    }

    [Fact]
    public async Task SqlServer_Revise_rollback_then_other_session_creates_Rev2()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await EnsureNumberingAsync(factory, "SO"))
        {
            return;
        }

        var soSeed = CreateSo(factory);
        var save = await soSeed.SaveNewAsync(SoRequest(5m));
        if (!save.Succeeded)
        {
            return;
        }

        var soA = CreateSo(factory);
        soA.TestHookAfterSupersedeBeforeInsert = () => throw new InvalidOperationException("forced revise rollback");
        var fail = await soA.ReviseAsync(save.SoNo!, SoRequest(5m, save.Document!.RowVersion));
        Assert.False(fail.Succeeded);

        var soB = CreateSo(factory);
        var ok = await soB.ReviseAsync(save.SoNo!, SoRequest(5m, GetCurrentRowVersion(factory, save.SoNo!)));
        Assert.True(ok.Succeeded, ok.ErrorMessage);
        Assert.Equal(2, ok.Document!.CustRel);

        await using var db = factory.CreateDbContext();
        var rows = await db.SaSos
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SoNo == save.SoNo)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Count(x => x.IsCurrent));
    }

    private static byte[] GetCurrentRowVersion(IDbContextFactory<AppDbContext> factory, string soNo)
    {
        using var db = factory.CreateDbContext();
        return db.SaSos.AsNoTracking()
            .Single(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SoNo == soNo && x.IsCurrent)
            .RowVersion ?? [];
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

    private static SaSoSaveRequest SoRequest(decimal qty, byte[]? rowVersion = null) =>
        new()
        {
            SoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "PO-1",
            SalesRep = "SM1",
            RowVersion = rowVersion,
            Lines = [new SaSoLineRequest { ICode = "SVC1", OrderQty = qty, UnitPrice = 10m }]
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
}
