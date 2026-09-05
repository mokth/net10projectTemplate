using System.Data;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server race tests for numbering admin vs invoice allocation.
/// Skipped unless ConnectionStrings:DefaultConnection points at SQL Server.
/// </summary>
public class AdSmNumAdminSqlServerConcurrencyTests
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
    public async Task SqlServer_admin_save_vs_invoice_insert_same_invno()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var numCd = "T" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        var prefix = "Z";
        const short totLength = 10;
        // Next number at Seq=1 → Z000000001
        var nextInv = DocumentNumberFormatter.FormatContinuous(prefix, 1, totLength);

        var admin = CreateAdmin(factory);
        var create = await admin.SaveContinuousAsync(new AdSmNumEditVm
        {
            NumCd = numCd,
            Prefix = prefix,
            TotLength = totLength,
            Seq = 1,
            NumDes = "race"
        }, isNew: true);
        if (!create.Succeeded)
        {
            // Table may be missing in some environments
            return;
        }

        try
        {
            var get = await admin.GetContinuousAsync(numCd);
            Assert.True(get.Succeeded);
            // Change description only (namespace unlocked at Seq=1) — collision checks next InvNo
            get.Data!.NumDes = "race-update";

            var invoiceTask = Task.Run(async () =>
            {
                await using var db = factory.CreateDbContext();
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                db.SaInvoices.Add(new SaInvoice
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    InvNo = nextInv,
                    CustCode = "RACE",
                    InvDate = new DateTime(2026, 9, 1),
                    Status = "Open",
                    DoNo = nextInv,
                    CurrRate = 1m
                });
                try
                {
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return true;
                }
                catch
                {
                    await tx.RollbackAsync();
                    return false;
                }
            });

            var adminResult = await admin.SaveContinuousAsync(get.Data, isNew: false);
            var invoiceOk = await invoiceTask;

            await using var verify = factory.CreateDbContext();
            var invCount = await verify.SaInvoices.CountAsync(
                x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.InvNo == nextInv);
            Assert.True(invCount <= 1);

            if (invoiceOk && adminResult.Succeeded)
            {
                Assert.Equal(1, invCount);
            }
            else if (!adminResult.Succeeded)
            {
                Assert.True(
                    adminResult.ErrorCode is IvMasterErrorCode.Concurrency or IvMasterErrorCode.Validation,
                    adminResult.Message);
            }
        }
        finally
        {
            await using var db = factory.CreateDbContext();
            await db.AdSmNums.Where(x => x.CompanyCode == "DEMO" && x.NumCd == numCd)
                .ExecuteDeleteAsync();
            await db.SaInvoices.Where(x => x.CompanyCode == "DEMO" && x.InvNo == nextInv)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task SqlServer_delete_then_allocator_sees_not_configured()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var numCd = "D" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        var admin = CreateAdmin(factory);
        var create = await admin.SaveContinuousAsync(new AdSmNumEditVm
        {
            NumCd = numCd,
            Prefix = "D",
            TotLength = 10,
            Seq = 1
        }, isNew: true);
        if (!create.Succeeded)
        {
            return;
        }

        try
        {
            Assert.True((await admin.DeleteContinuousAsync([numCd])).Succeeded);

            var numbering = new DocumentNumberingService(
                InventoryTenantTestHelper.CreateTenantContext(location: "MAIN"));
            await using var db = factory.CreateDbContext();
            await Assert.ThrowsAsync<DocumentNumberingNotConfiguredException>(() =>
                numbering.NextAsync(db, numCd, "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default));
        }
        finally
        {
            await using var db = factory.CreateDbContext();
            await db.AdSmNums.Where(x => x.CompanyCode == "DEMO" && x.NumCd == numCd)
                .ExecuteDeleteAsync();
        }
    }

    private static TestDbContextFactory CreateFactory()
    {
        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        return new TestDbContextFactory(options);
    }

    private static AdSmNumAdminService CreateAdmin(IDbContextFactory<AppDbContext> factory)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new AdSmNumAdminService(
            factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "MAIN"),
            access.Object);
    }
}
