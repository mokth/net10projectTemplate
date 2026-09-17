using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Sales item family — SQL Server concurrency (plan §24 "Concurrency" row, §10).
///
/// SQLite cannot prove any of this: it serialises writers anyway, so the SQLite suite proves the
/// *logic* and this class proves the *locking* — header update/update, line update/update, delete with a
/// stale row version, a concurrent duplicate insert, and two operators creating overlapping discount
/// rules at the same instant.
///
/// Skipped unless <c>ConnectionStrings:SqlServerTestConnection</c> points at a scratch database whose
/// name contains "test" — the same two safety rails as <c>PoCdnSqlServerConcurrencyTests</c>, because
/// the other suites' <c>DefaultConnection</c> convention points at the LIVE ERPWeb database.
/// <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> turns the silent skip into a failure, so a green
/// <c>dotnet test</c> cannot mean "nothing ran".
/// </summary>
public class SaItemFamilySqlServerConcurrencyTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 15);

    // 5 characters: InventoryTenantContext rejects a company claim longer than MaxCompanyLength.
    private const string Company = "IFM01";

    internal const string TestConnectionKey = "SqlServerTestConnection";

    private static readonly string[] RequiredTables =
    [
        "IvStockMaster", "MsUOM", "IvClass", "SaCust",
        "IvCustPriceGroup", "IvCustPrice", "SaItemCust", "SaDisGroupItem"
    ];

    private string? _connectionString;
    private IDbContextFactory<AppDbContext>? _factory;

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    /// <summary>Returns true when the test must bail out (no usable scratch SQL Server).</summary>
    private bool SkipWithoutSqlServer()
    {
        if (_factory is not null)
        {
            return false;
        }

        if (RequireSqlServer)
        {
            Assert.Fail(
                "The item-family concurrency suite requires a scratch SQL Server: set "
                + "ConnectionStrings:SqlServerTestConnection to a database whose name contains 'test' "
                + "(see the class summary).");
        }

        return true;
    }

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString(TestConnectionKey);
        return string.IsNullOrWhiteSpace(cs) ? null : cs;
    }

    private static string? DatabaseNameOf(string connectionString)
    {
        foreach (var raw in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Split('=', 2);
            if (part.Length != 2)
            {
                continue;
            }

            var key = part[0].Trim();
            if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
            {
                return part[1].Trim();
            }
        }

        return null;
    }

    internal static bool IsScratchDatabase(string connectionString)
    {
        var name = DatabaseNameOf(connectionString);
        return name is not null && name.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync()
    {
        _connectionString = GetSqlServerConnectionString();
        if (_connectionString is null || !IsScratchDatabase(_connectionString))
        {
            return;   // skip: no scratch SQL Server configured
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

        try
        {
            await using var db = await factory.CreateDbContextAsync();
            if (!db.Database.IsSqlServer())
            {
                return;
            }

            // Creates the scratch database and the whole model when absent; a no-op when it already exists.
            await db.Database.EnsureCreatedAsync();

            if (!await AllTablesExistAsync(db))
            {
                // A database created from an older model (or only from scripts/init-sales-item-family.sql,
                // which is an additive migration over a legacy schema) cannot host this suite.
                if (RequireSqlServer)
                {
                    Assert.Fail(
                        "The scratch database is missing one or more item-family tables. Point "
                        + "ConnectionStrings:SqlServerTestConnection at a freshly created (or EF-bootstrapped) "
                        + "scratch database. Missing: " + await MissingTablesAsync(db) + ".");
                }

                return;
            }

            await SeedMastersAsync(db);
            await CleanupAsync(db);
        }
        catch when (!RequireSqlServer)
        {
            return;   // an unusable scratch server is a skip, not a failure (unless the rail is on)
        }

        _factory = factory;
    }

    public async Task DisposeAsync()
    {
        if (_factory is null)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync();
        await CleanupAsync(db);
    }

    private static async Task<bool> AllTablesExistAsync(AppDbContext db) =>
        string.IsNullOrEmpty(await MissingTablesAsync(db));

    private static Task<string> MissingTablesAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        var quoted = string.Join(", ", RequiredTables.Select(t => $"N'{t}'"));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sys.tables WHERE name IN ({quoted})";

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                found.Add(reader.GetString(0));
            }
        }

        return Task.FromResult(string.Join(", ", RequiredTables.Where(t => !found.Contains(t))));
    }

    private static async Task SeedMastersAsync(AppDbContext db)
    {
        if (!await db.IvStockMasters.AnyAsync(x => x.CompanyCode == Company && x.ICode == "I1"))
        {
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = Company,
                ICode = "I1",
                IDesc = "Race item one",
                IClassCode = "C1",
                StdUom = "PCS",
                SellingUom = "PCS",
                SellingPrice = 14m,
                IsActive = true
            });
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = Company,
                ICode = "I2",
                IDesc = "Race item two",
                IClassCode = "C1",
                StdUom = "PCS",
                SellingUom = "PCS",
                SellingPrice = 24m,
                IsActive = true
            });
        }

        if (!await db.MsUoms.AnyAsync(x => x.CompanyCode == Company && x.UomCode == "PCS"))
        {
            db.MsUoms.Add(new MsUom { CompanyCode = Company, UomCode = "PCS", UomDesc = "Pieces", IsActive = true });
        }

        if (!await db.IvClasses.AnyAsync(x => x.CompanyCode == Company && x.IClassCode == "C1"))
        {
            db.IvClasses.Add(new IvClass { CompanyCode = Company, IClassCode = "C1", IDesc = "Class one" });
        }

        if (!await db.SaCusts.AnyAsync(x => x.CompanyCode == Company && x.CustCode == "CUST1"))
        {
            db.SaCusts.Add(new SaCust
            {
                CompanyCode = Company,
                CustCode = "CUST1",
                CustName = "Race customer",
                Currency = "MYR"
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.SaDisGroupItem WHERE CompanyCode = N'{Company}'");
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.SaItemCust WHERE CompanyCode = N'{Company}'");
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.IvCustPrice WHERE CompanyCode = N'{Company}'");
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.IvCustPriceGroup WHERE CompanyCode = N'{Company}'");
    }

    // ═════════════════════════════ §10 — the aggregate is versioned as a unit ═════════════════════════════

    [Fact]
    public async Task Header_ConcurrentEdits_ExactlyOneCommits()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var code = "PL" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var created = await CreateSut().SaveCustPriceGroupAsync(
            new IvCustPriceGroupEditVm
            {
                CustPriceCode = code,
                CustPriceDesc = "RACE",
                IsActive = true,
                Lines = [new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = 10m }]
            },
            isNew: true);
        Assert.True(created.Succeeded, created.Message);

        var version = created.Data!.RowVersion;
        var start = new ManualResetEventSlim(false);

        Task<IvMasterOperationResult<IvCustPriceGroupEditVm>> WriteAsync(decimal price) =>
            Task.Run(() =>
            {
                var sut = CreateSut();
                start.Wait();
                return sut.SaveCustPriceGroupAsync(
                    new IvCustPriceGroupEditVm
                    {
                        CustPriceCode = code,
                        CustPriceDesc = "RACE",
                        IsActive = true,
                        RowVersion = version,
                        Lines = [new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = price }]
                    },
                    isNew: false);
            });

        var taskA = WriteAsync(11m);
        var taskB = WriteAsync(12m);
        start.Set();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Succeeded);
        var loser = Assert.Single(results, r => !r.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, loser.ErrorCode);
        Assert.Contains("another user", loser.Message!, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory!.CreateDbContextAsync();
        var line = await db.IvCustPrices.AsNoTracking()
            .SingleAsync(x => x.CompanyCode == Company && x.CustPriceCode == code);
        var winner = results.Single(r => r.Succeeded).Data!.Lines[0].SellingPrice;
        Assert.Equal(winner, line.SellingPrice);
    }

    [Fact]
    public async Task Lines_ConcurrentEditsOnDifferentLines_DoNotMerge()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var code = "PL" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var created = await CreateSut().SaveCustPriceGroupAsync(
            new IvCustPriceGroupEditVm
            {
                CustPriceCode = code,
                CustPriceDesc = "RACE LINES",
                IsActive = true,
                Lines =
                [
                    new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = 10m },
                    new IvCustPriceLineVm { ICode = "I2", UOM = "PCS", SellingPrice = 20m }
                ]
            },
            isNew: true);
        Assert.True(created.Succeeded, created.Message);

        var version = created.Data!.RowVersion;
        var start = new ManualResetEventSlim(false);

        // Writer A changes I1 only; writer B changes I2 only. The header version is the gate, so the
        // outcome must be A's payload or B's payload — never a merge of both.
        Task<IvMasterOperationResult<IvCustPriceGroupEditVm>> WriteAsync(bool changeFirst) =>
            Task.Run(() =>
            {
                var sut = CreateSut();
                start.Wait();
                return sut.SaveCustPriceGroupAsync(
                    new IvCustPriceGroupEditVm
                    {
                        CustPriceCode = code,
                        CustPriceDesc = "RACE LINES",
                        IsActive = true,
                        RowVersion = version,
                        Lines =
                        [
                            new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = changeFirst ? 111m : 10m },
                            new IvCustPriceLineVm { ICode = "I2", UOM = "PCS", SellingPrice = changeFirst ? 20m : 222m }
                        ]
                    },
                    isNew: false);
            });

        var taskA = WriteAsync(changeFirst: true);
        var taskB = WriteAsync(changeFirst: false);
        start.Set();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, Assert.Single(results, r => !r.Succeeded).ErrorCode);

        await using var db = await _factory!.CreateDbContextAsync();
        var lines = await db.IvCustPrices.AsNoTracking()
            .Where(x => x.CompanyCode == Company && x.CustPriceCode == code)
            .ToListAsync();

        var i1 = Assert.Single(lines, x => x.ICode == "I1").SellingPrice;
        var i2 = Assert.Single(lines, x => x.ICode == "I2").SellingPrice;

        var aWon = i1 == 111m && i2 == 20m;
        var bWon = i1 == 10m && i2 == 222m;
        Assert.True(aWon || bWon, $"The line set was merged instead of replaced: I1={i1}, I2={i2}.");
    }

    [Fact]
    public async Task HeaderDelete_WithStaleRowVersion_IsRejected()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var code = "PL" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var created = await CreateSut().SaveCustPriceGroupAsync(
            new IvCustPriceGroupEditVm
            {
                CustPriceCode = code,
                CustPriceDesc = "DELETE RACE",
                IsActive = true,
                Lines = [new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = 10m }]
            },
            isNew: true);
        Assert.True(created.Succeeded, created.Message);
        var staleVersion = created.Data!.RowVersion;

        // Another operator saves first: the header version moves on.
        var second = await CreateSut().SaveCustPriceGroupAsync(
            new IvCustPriceGroupEditVm
            {
                CustPriceCode = code,
                CustPriceDesc = "DELETE RACE EDITED",
                IsActive = true,
                RowVersion = staleVersion,
                Lines = [new IvCustPriceLineVm { ICode = "I1", UOM = "PCS", SellingPrice = 11m }]
            },
            isNew: false);
        Assert.True(second.Succeeded, second.Message);

        var delete = await CreateSut().DeleteCustPriceGroupsAsync([
            new SaItemFamilyKeyToken { Key = code, RowVersion = staleVersion }
        ]);

        Assert.False(delete.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, delete.ErrorCode);

        await using var db = await _factory!.CreateDbContextAsync();
        Assert.True(await db.IvCustPriceGroups.AnyAsync(x => x.CompanyCode == Company && x.CustPriceCode == code));
    }

    [Fact]
    public async Task CustomerItem_ConcurrentDuplicateInsert_ExactlyOneCommits()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var start = new ManualResetEventSlim(false);

        Task<IvMasterOperationResult<SaItemCustEditVm>> InsertAsync() =>
            Task.Run(() =>
            {
                var sut = CreateSut();
                start.Wait();
                return sut.SaveItemCustAsync(
                    new SaItemCustEditVm
                    {
                        CustCode = "CUST1",
                        ICode = "I1",
                        CustICode = "RACE-PART",
                        SellingUOM = "PCS",
                        MOQ = 0,
                        UnitPrice = 12m
                    },
                    isNew: true);
            });

        var taskA = InsertAsync();
        var taskB = InsertAsync();
        start.Set();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Succeeded);
        var loser = Assert.Single(results, r => !r.Succeeded);
        Assert.Contains(loser.ErrorCode, new[] { IvMasterErrorCode.DuplicateKey, IvMasterErrorCode.Concurrency });
        Assert.False(string.IsNullOrWhiteSpace(loser.Message));

        await using var db = await _factory!.CreateDbContextAsync();
        Assert.Single(await db.SaItemCusts.AsNoTracking()
            .Where(x => x.CompanyCode == Company && x.CustCode == "CUST1" && x.ICode == "I1")
            .ToListAsync());
    }

    [Fact]
    public async Task DiscountRule_ConcurrentOverlappingCreates_ExactlyOneCommits()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var start = new ManualResetEventSlim(false);

        Task<IvMasterOperationResult<SaDisGroupItemEditVm>> InsertAsync(decimal qtyFr, decimal qtyTo) =>
            Task.Run(() =>
            {
                var sut = CreateSut();
                start.Wait();
                return sut.SaveDisGroupItemAsync(
                    new SaDisGroupItemEditVm
                    {
                        ICode = "I1",
                        QtyFr = qtyFr,
                        QtyTo = qtyTo,
                        DateFr = FixedToday,
                        Discount = 10m,
                        DiscountType = SaDiscountSlotTypes.Percentage,
                        EffectPrice = SaEffectPriceOptions.Selling
                    },
                    isNew: true);
            });

        // Deliberately overlapping bands: 1-10 vs 5-20.
        var taskA = InsertAsync(1m, 10m);
        var taskB = InsertAsync(5m, 20m);
        start.Set();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Succeeded);
        var loser = Assert.Single(results, r => !r.Succeeded);
        Assert.Contains(loser.ErrorCode,
            new[] { IvMasterErrorCode.Validation, IvMasterErrorCode.Concurrency, IvMasterErrorCode.DuplicateKey });
        Assert.True(
            loser.Message!.Contains("overlap", StringComparison.OrdinalIgnoreCase)
            || loser.Message.Contains("Reload", StringComparison.OrdinalIgnoreCase),
            $"Unexpected loser message: {loser.Message}");

        await using var db = await _factory!.CreateDbContextAsync();
        var rules = await db.SaDisGroupItems.AsNoTracking()
            .Where(x => x.CompanyCode == Company && x.ICode == "I1")
            .Select(x => new { x.QtyFr, x.QtyTo })
            .ToListAsync();

        Assert.Single(rules);
        for (var i = 0; i < rules.Count; i++)
        {
            for (var j = i + 1; j < rules.Count; j++)
            {
                Assert.False(
                    SaItemFamilyRuleMatch.BandsOverlap(rules[i].QtyFr, rules[i].QtyTo, rules[j].QtyFr, rules[j].QtyTo),
                    "Two committed discount bands overlap.");
            }
        }
    }

    private SaSalesRefService CreateSut()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = InventoryTenantTestHelper.CreateTenantContext(Company, "HQ", "SITE");
        return new SaSalesRefService(
            _factory!,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new SaCustLookupService(_factory!, tenant));
    }
}
