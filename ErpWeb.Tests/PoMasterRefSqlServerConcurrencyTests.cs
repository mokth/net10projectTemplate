using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server RowVersion concurrency for Purchase Category. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server with POCategory present.
/// </summary>
public class PoMasterRefSqlServerConcurrencyTests
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
            cmd.CommandText = "SELECT OBJECT_ID(N'dbo.POCategory', N'U')";
            var result = cmd.ExecuteScalar();
            return result is not null && result != DBNull.Value;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task SqlServer_Category_CaseInsensitiveDuplicate_Rejected()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
        var code = "C" + Guid.NewGuid().ToString("N")[..7];

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoCategories.Add(new PoCategory
            {
                CompanyCode = "DEMO",
                Category = code.ToUpperInvariant(),
                Description = "Upper",
                IsActive = true,
                BranchCode = "HQ",
                LocationCode = "SITE"
            });
            await db.SaveChangesAsync();
        }

        try
        {
            var sut = CreateSut(factory);
            var result = await sut.SaveCategoryAsync(
                new PoCategoryEditVm { Code = code.ToLowerInvariant(), Description = "Lower", IsActive = true },
                isNew: true);
            Assert.False(result.Succeeded);
            Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
        }
        finally
        {
            await using var cleanup = await factory.CreateDbContextAsync();
            var doomed = await cleanup.PoCategories
                .Where(x => x.CompanyCode == "DEMO" && x.Category == code.ToUpperInvariant())
                .ToListAsync();
            cleanup.PoCategories.RemoveRange(doomed);
            await cleanup.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task SqlServer_Category_StaleRowVersion_Rejected()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
        var code = "T" + Guid.NewGuid().ToString("N")[..8];

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoCategories.Add(new PoCategory
            {
                CompanyCode = "DEMO",
                Category = code,
                Description = "Original",
                IsActive = true,
                BranchCode = "HQ",
                LocationCode = "SITE"
            });
            await db.SaveChangesAsync();
        }

        try
        {
            var sut = CreateSut(factory);
            var aLoad = (await sut.GetCategoryAsync(code)).Data!;
            var stale = (byte[])aLoad.RowVersion!.Clone();

            var bLoad = (await sut.GetCategoryAsync(code)).Data!;
            bLoad.Description = "Writer B";
            Assert.True((await sut.SaveCategoryAsync(bLoad, isNew: false)).Succeeded);

            aLoad.RowVersion = stale;
            aLoad.Description = "Writer A stale";
            var staleResult = await sut.SaveCategoryAsync(aLoad, isNew: false);
            Assert.False(staleResult.Succeeded);
            Assert.Equal(IvMasterErrorCode.Concurrency, staleResult.ErrorCode);

            await using var verify = await factory.CreateDbContextAsync();
            var row = await verify.PoCategories.AsNoTracking()
                .SingleAsync(x => x.CompanyCode == "DEMO" && x.Category == code);
            Assert.Equal("Writer B", row.Description);
        }
        finally
        {
            await using var cleanup = await factory.CreateDbContextAsync();
            var doomed = await cleanup.PoCategories
                .Where(x => x.CompanyCode == "DEMO" && x.Category == code)
                .ToListAsync();
            cleanup.PoCategories.RemoveRange(doomed);
            await cleanup.SaveChangesAsync();
        }
    }

    private static PoMasterRefService CreateSut(IDbContextFactory<AppDbContext> factory)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new PoMasterRefService(
            factory,
            InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "SITE"),
            access.Object,
            new FixedCurrentDateService(new DateTime(2026, 9, 11)));
    }
}
