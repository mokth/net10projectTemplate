using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server concurrency tests for Supplier Profile. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server with GlCode applied.
/// </summary>
public class PoSupplierSqlServerConcurrencyTests
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
            if (!db.Database.IsSqlServer() || !db.Database.CanConnect())
            {
                return false;
            }

            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                connection.Open();
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COL_LENGTH(N'dbo.POSupplier', N'GlCode')";
            var result = cmd.ExecuteScalar();
            return result is not null && result != DBNull.Value;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task SqlServer_B_saves_header_then_A_stale_fails_ZeroChildMutation()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        var factory = new TestDbContextFactory(options);
        var code = "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await SeedSupplierAsync(factory, code, withAddress: true);
        var sut = CreateSut(factory);

        var aLoad = (await sut.GetAsync(code)).Data!;
        var staleToken = (byte[])aLoad.RowVersion!.Clone();
        var originalAddressCount = aLoad.Addresses.Count;

        var bLoad = (await sut.GetAsync(code)).Data!;
        bLoad.SuppName = "Writer B";
        Assert.True((await sut.SaveAsync(bLoad, isNew: false)).Succeeded);

        aLoad.RowVersion = staleToken;
        aLoad.SuppName = "Writer A";
        aLoad.Addresses =
        [
            new PoSupplierAddressVm { SuppName = "ShouldNotPersist", Address1 = "X" }
        ];
        var aSave = await sut.SaveAsync(aLoad, isNew: false);
        Assert.False(aSave.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, aSave.ErrorCode);

        await using var db = factory.CreateDbContext();
        Assert.Equal("Writer B", await db.PoSuppliers
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SuppCode == code)
            .Select(x => x.SuppName)
            .SingleAsync());
        Assert.Equal(originalAddressCount, await db.PoSupplierAdds
            .CountAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SuppCode == code));

        await CleanupAsync(factory, code);
    }

    [Fact]
    public async Task SqlServer_B_address_only_then_A_stale_fails()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        var factory = new TestDbContextFactory(options);
        var code = "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await SeedSupplierAsync(factory, code, withAddress: true);
        var sut = CreateSut(factory);

        var aLoad = (await sut.GetAsync(code)).Data!;
        var staleToken = (byte[])aLoad.RowVersion!.Clone();

        var bLoad = (await sut.GetAsync(code)).Data!;
        bLoad.Addresses = [new PoSupplierAddressVm { SuppName = "B Addr", City = "B City" }];
        Assert.True((await sut.SaveAsync(bLoad, isNew: false)).Succeeded);

        aLoad.RowVersion = staleToken;
        aLoad.Tel = "A-phone";
        var aSave = await sut.SaveAsync(aLoad, isNew: false);
        Assert.False(aSave.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, aSave.ErrorCode);

        await CleanupAsync(factory, code);
    }

    private static async Task SeedSupplierAsync(
        IDbContextFactory<AppDbContext> factory,
        string code,
        bool withAddress = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SuppCode = code,
            SuppName = "Seed Supplier",
            Currency = "MYR",
            GlCode = "AP001",
            Country = "MY",
            IsActive = true
        });
        await db.SaveChangesAsync();

        if (withAddress)
        {
            db.PoSupplierAdds.Add(new PoSupplierAdd
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = code,
                Line = 1,
                SuppName = "Seed Addr",
                Address1 = "Line 1"
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task CleanupAsync(IDbContextFactory<AppDbContext> factory, string code)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.PoSuppliers
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SuppCode == code)
            .ToListAsync();
        db.PoSuppliers.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    private static PoSupplierService CreateSut(IDbContextFactory<AppDbContext> factory)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseSupplierProfile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        return new PoSupplierService(
            factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(new DateTime(2026, 9, 11)),
            new PoSupplierRepository(factory),
            new PoSupplierLookupService(factory, tenant));
    }
}
