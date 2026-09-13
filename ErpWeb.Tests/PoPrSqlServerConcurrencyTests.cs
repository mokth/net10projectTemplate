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
/// SQL Server concurrency/atomicity tests for Purchase Requisitions. Skipped unless
/// ConnectionStrings:DefaultConnection points at SQL Server with DEMO masters.
/// </summary>
public class PoPrSqlServerConcurrencyTests
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
    public async Task SqlServer_concurrent_SaveNew_distinct_PrNo()
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
        Assert.NotEqual(results[0].PrNo, results[1].PrNo);
    }

    [Fact]
    public async Task SqlServer_edit_vs_edit_concurrency()
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
        var save = await sut.SaveNewAsync(Request(fixture, qty: 1m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = (byte[])save.Document!.RowVersion.Clone();
        var line = save.Document.Lines[0].Line;

        var first = await sut.UpdateAsync(
            save.PrNo!,
            Request(fixture, qty: 2m, line: line, rowVersion: save.Document.RowVersion));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var second = await sut.UpdateAsync(
            save.PrNo!,
            Request(fixture, qty: 3m, line: line, rowVersion: stale));
        Assert.False(second.Succeeded);
        Assert.Equal(PoPrErrorKind.Concurrency, second.ErrorKind);
    }

    [Fact]
    public async Task SqlServer_cancel_vs_edit()
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
        var save = await sut.SaveNewAsync(Request(fixture, qty: 1m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var token = save.Document!.RowVersion;
        var line = save.Document.Lines[0].Line;

        var cancel = sut.CancelAsync(new PoPrCancelRequest
        {
            PrNo = save.PrNo!,
            RowVersion = token,
            ApprReason = "race cancel"
        });
        var edit = sut.UpdateAsync(
            save.PrNo!,
            Request(fixture, qty: 4m, line: line, rowVersion: token));
        var results = await Task.WhenAll(cancel, edit);

        Assert.Equal(1, results.Count(x => x.Succeeded));
        Assert.Equal(1, results.Count(x => x.ErrorKind == PoPrErrorKind.Concurrency));

        var current = await sut.GetAsync(save.PrNo!);
        Assert.True(current.Succeeded, current.ErrorMessage);
        Assert.True(
            current.Document!.Status is PoPrStatuses.Cancelled or PoPrStatuses.New,
            current.Document.Status);
    }

    [Fact]
    public async Task SqlServer_delete_vs_edit()
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
        var save = await sut.SaveNewAsync(Request(fixture, qty: 1m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var token = save.Document!.RowVersion;
        var line = save.Document.Lines[0].Line;

        var delete = sut.DeleteAsync(new PoPrKeyedRequest
        {
            PrNo = save.PrNo!,
            RowVersion = token
        });
        var edit = sut.UpdateAsync(
            save.PrNo!,
            Request(fixture, qty: 5m, line: line, rowVersion: token));
        var results = await Task.WhenAll(delete, edit);

        Assert.Equal(1, results.Count(x => x.Succeeded));
        Assert.Equal(1, results.Count(x => x.ErrorKind == PoPrErrorKind.Concurrency));
    }

    private sealed record Fixture(string ICode);

    private static async Task<Fixture?> SeedFixtureAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = factory.CreateDbContext();

        try
        {
            _ = await db.PoPrs.CountAsync();
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

        var iCode = "P" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            IDesc = "PR concurrency item",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 10m,
            PurchaseTaxGroup = "SR",
            IsActive = true,
            StockControl = false
        });

        if (!await db.AdSmNumDates.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "PR"
                && x.Year == 2026 && x.Month == 9))
        {
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                Year = 2026,
                Month = 9,
                NumCd = "PR",
                NumDes = "Purchase Requisition",
                Prefix = "PR",
                TotLength = 4,
                NumberingDelimeter = "-",
                Seq = 1
            });
        }

        if (!await db.AdSmNums.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "PR"))
        {
            // Prefer monthly AdSmNumDate; continuous is a fallback if date rows are absent elsewhere.
            db.AdSmNums.Add(new AdSmNum
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                NumCd = "PR",
                NumDes = "Purchase Requisition",
                Prefix = "PR",
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

    private static PoPrSaveRequest Request(
        Fixture fixture,
        decimal qty,
        short line = 0,
        byte[]? rowVersion = null) =>
        new()
        {
            CreateDt = FixedToday,
            Requester = "user",
            PrType = PoPrTypes.Purchasing,
            Remarks = "sql concurrency",
            RowVersion = rowVersion,
            Lines =
            [
                new PoPrLineDto
                {
                    Line = line,
                    ICode = fixture.ICode,
                    PurchaseQty = qty,
                    Qty = qty,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    PackSz = 1m,
                    Currency = "MYR",
                    UnitPrice = 999999m,
                    TaxGroup = "SR",
                    ToWarehouse = "MAIN"
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

    private static PoPrService CreateSut(IDbContextFactory<AppDbContext> factory)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: "DEMO",
            branch: "HQ",
            location: "MAIN",
            userId: "user");
        var attachRoot = Path.Combine(Path.GetTempPath(), "popr-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachRoot);
        var attachments = new PoPrAttachmentService(
            factory,
            tenant,
            Access().Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = attachRoot }),
            Options.Create(new PoPrOptions()),
            NullLogger<PoPrAttachmentService>.Instance);

        return new PoPrService(
            factory,
            tenant,
            Access().Object,
            new DocumentNumberingService(tenant),
            new FixedCurrentDateService(FixedToday),
            new PoPrRepository(),
            Options.Create(new PoPrOptions()),
            attachments,
            NullLogger<PoPrService>.Instance);
    }
}
