using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server concurrency tests for Customer Profile. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server.
/// </summary>
public class SaCustSqlServerConcurrencyTests
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
    public async Task SqlServer_B_saves_header_then_A_stale_fails()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        var factory = new TestDbContextFactory(options);
        var code = "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await SeedCustomerAsync(factory, code);
        var sut = CreateSut(factory);

        var aLoad = (await sut.GetAsync(code)).Data!;
        var staleToken = (byte[])aLoad.RowVersion!.Clone();

        var bLoad = (await sut.GetAsync(code)).Data!;
        bLoad.CustName = "Writer B";
        var bSave = await sut.SaveAsync(bLoad, isNew: false);
        Assert.True(bSave.Succeeded, bSave.Message);

        aLoad.RowVersion = staleToken;
        aLoad.CustName = "Writer A";
        var aSave = await sut.SaveAsync(aLoad, isNew: false);
        Assert.False(aSave.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, aSave.ErrorCode);

        await using var db = factory.CreateDbContext();
        Assert.Equal("Writer B", await db.SaCusts
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == code)
            .Select(x => x.CustName)
            .SingleAsync());
    }

    [Fact]
    public async Task SqlServer_B_address_only_then_A_stale_phone_fails()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        var factory = new TestDbContextFactory(options);
        var code = "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await SeedCustomerAsync(factory, code, withAddress: true);
        var sut = CreateSut(factory);

        var aLoad = (await sut.GetAsync(code)).Data!;
        var staleToken = (byte[])aLoad.RowVersion!.Clone();

        var bLoad = (await sut.GetAsync(code)).Data!;
        bLoad.Addresses = [new SaCustAddressVm { Line = 1, AddName = "B Addr", City = "B City" }];
        Assert.True((await sut.SaveAsync(bLoad, isNew: false)).Succeeded);

        aLoad.RowVersion = staleToken;
        aLoad.Tel = "A-phone";
        var aSave = await sut.SaveAsync(aLoad, isNew: false);
        Assert.False(aSave.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, aSave.ErrorCode);
    }

    [Fact]
    public async Task SqlServer_B_contact_only_then_A_stale_fails()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        var factory = new TestDbContextFactory(options);
        var code = "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await SeedCustomerAsync(factory, code);
        var sut = CreateSut(factory);

        var aLoad = (await sut.GetAsync(code)).Data!;
        var staleToken = (byte[])aLoad.RowVersion!.Clone();

        var bLoad = (await sut.GetAsync(code)).Data!;
        bLoad.Contacts =
        [
            new SaCustContactVm { Line = 1, ContactPerson = "Line1" },
            new SaCustContactVm { Line = 2, ContactPerson = "Line2" }
        ];
        Assert.True((await sut.SaveAsync(bLoad, isNew: false)).Succeeded);

        aLoad.RowVersion = staleToken;
        aLoad.CustName = "Stale A";
        var aSave = await sut.SaveAsync(aLoad, isNew: false);
        Assert.False(aSave.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, aSave.ErrorCode);
    }

    [Fact]
    public async Task SqlServer_DuplicateKey_ReturnsDuplicateKey()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        var factory = new TestDbContextFactory(options);
        var code = "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await SeedCustomerAsync(factory, code);
        var sut = CreateSut(factory);

        var dup = new SaCustEditVm
        {
            CustCode = code,
            CustName = "Duplicate attempt",
            CustType = "RETAIL",
            PayCode = "NET30",
            Currency = "MYR",
            GlCode = "AR001",
            Contacts = [new SaCustContactVm { Line = 1 }]
        };

        var result = await sut.SaveAsync(dup, isNew: true);
        Assert.False(result.Succeeded);
        Assert.True(
            result.ErrorCode is IvMasterErrorCode.DuplicateKey or IvMasterErrorCode.Validation,
            $"Unexpected error: {result.ErrorCode}");
    }

    private static async Task SeedCustomerAsync(
        IDbContextFactory<AppDbContext> factory,
        string code,
        bool withAddress = false)
    {
        await using var db = factory.CreateDbContext();
        if (await db.SaCusts.AnyAsync(x => x.CompanyCode == "DEMO" && x.CustCode == code))
        {
            return;
        }

        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = code,
            CustName = "SQL Server test",
            CustType = "RETAIL",
            PayCode = "NET30",
            Currency = "MYR",
            GlCode = "AR001",
            IsActive = true
        });
        await db.SaveChangesAsync();

        if (withAddress)
        {
            db.SaCustAdds.Add(new SaCustAdd
            {
                CompanyCode = "DEMO",
                CustCode = code,
                Line = 1,
                AddName = "Seed",
                City = "Seed City"
            });
            await db.SaveChangesAsync();
        }
    }

    private static SaCustService CreateSut(IDbContextFactory<AppDbContext> factory)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        return new SaCustService(
            factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(DateTime.Today),
            new SaCustRepository(factory),
            new SaCustLookupService(factory, tenant));
    }
}
