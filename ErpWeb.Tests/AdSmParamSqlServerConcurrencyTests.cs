using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Race behaviour that only a real SQL Server can exercise: a server-generated <c>rowversion</c>, and a
/// genuine duplicate-key collision between two concurrent inserts.
///
/// <para>
/// Skipped unless <c>ConnectionStrings:SqlServerTestConnection</c> points at a scratch database whose name
/// contains "test". <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> turns the silent skip into a failure, so a
/// green suite cannot hide that these never ran.
/// </para>
/// </summary>
public class AdSmParamSqlServerConcurrencyTests : IAsyncLifetime
{
    internal const string TestConnectionKey = "SqlServerTestConnection";

    /// <summary>5 characters: TenantScopeContext rejects a company claim longer than 5.</summary>
    private const string Company = "SET01";

    private const string OtherCompany = "SET02";

    private static readonly string[] RequiredTables = ["AdSmParam", "Company"];

    private string? _connectionString;
    private IDbContextFactory<AppDbContext>? _factory;

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

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

            // Creates the scratch database and the whole model when absent; a no-op when it exists.
            await db.Database.EnsureCreatedAsync();

            var missing = await MissingTablesAsync(db);
            if (missing.Length > 0)
            {
                // A database created before this feature existed will not have AdSmParam: EnsureCreatedAsync
                // never adds a table to an existing database. Apply scripts/create-adsmparam.sql to it.
                if (RequireSqlServer)
                {
                    Assert.Fail(
                        "The scratch database is missing one or more settings tables. Apply "
                        + "scripts/create-adsmparam.sql to it (or recreate it). Missing: " + missing + ".");
                }

                return;
            }

            await CleanupAsync(db);
            await SeedCompaniesAsync(db);
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

    // ── the cases ───────────────────────────────────────────────────────────

    /// <summary>
    /// Two admins setting the same global key at the same moment. The pre-check cannot see the other
    /// insert, so the unique key is the only thing standing between the pair and two rows — which would
    /// make resolution ambiguous forever.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_inserts_of_the_same_key_leave_one_row_and_one_concurrency()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        const string module = AppSettingModules.Admin;
        const string key = AppSettingCatalogue.AdminKeys.SessionTimeoutMinutes;

        var first = CreateService();
        var second = CreateService();

        var results = await Task.WhenAll(
            first.SaveAsync(new AppSettingEditVm
            {
                Module = module,
                Key = key,
                Scope = AppSettingScope.Global,
                Value = "10"
            }),
            second.SaveAsync(new AppSettingEditVm
            {
                Module = module,
                Key = key,
                Scope = AppSettingScope.Global,
                Value = "20"
            }));

        Assert.Single(results, r => r.Succeeded);

        var loser = Assert.Single(results, r => !r.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, loser.ErrorCode);

        await using var db = await _factory!.CreateDbContextAsync();
        Assert.Equal(1, await db.AdSmParams.CountAsync(x => x.ModuleCode == module && x.ParamKey == key));
    }

    /// <summary>A real server-generated rowversion, so the stale-token path is genuinely exercised.</summary>
    [Fact]
    public async Task A_stale_row_version_update_returns_concurrency_and_leaves_the_row_alone()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        const string module = AppSettingModules.Sales;
        const string key = AppSettingCatalogue.SalesKeys.QuoteValidDays;

        var service = CreateService();

        var created = await service.SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = Company,
            Value = "30"
        });
        Assert.True(created.Succeeded);
        Assert.NotNull(created.Data!.RowVersion);

        // Someone else changes it first, so our token is now stale.
        var other = CreateService();
        var second = await other.SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = Company,
            Value = "45",
            RowVersion = created.Data.RowVersion
        });
        Assert.True(second.Succeeded);

        var stale = await CreateService().SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = Company,
            Value = "99",
            RowVersion = created.Data.RowVersion
        });

        Assert.False(stale.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, stale.ErrorCode);

        await using var db = await _factory!.CreateDbContextAsync();
        var row = await db.AdSmParams.SingleAsync(
            x => x.ModuleCode == module && x.ParamKey == key && x.ScopeCode == "COMPANY" && x.CompanyCode == Company);
        Assert.Equal(45m, row.ValueNumber);
    }

    /// <summary>
    /// Clearing DELETEs, so the key becomes free again. This is why the unique index needs no
    /// IsActive filter: no inactive row can ever block a replacement.
    /// </summary>
    [Fact]
    public async Task Clearing_a_row_allows_the_same_key_to_be_created_again()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        const string module = AppSettingModules.Sales;
        const string key = AppSettingCatalogue.SalesKeys.QuoteValidDays;

        var service = CreateService();

        var created = await service.SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = Company,
            Value = "30"
        });
        Assert.True(created.Succeeded);

        var cleared = await service.ClearAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = Company,
            RowVersion = created.Data!.RowVersion
        });
        Assert.True(cleared.Succeeded);

        var recreated = await CreateService().SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = Company,
            Value = "60"
        });

        Assert.True(recreated.Succeeded);
    }

    /// <summary>
    /// The NULL-tolerance of the unique index: two COMPANY rows for the same key must be distinguished by
    /// CompanyCode even though their BranchCode is NULL in both.
    /// </summary>
    [Fact]
    public async Task The_same_key_can_be_held_by_two_companies_at_once()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        const string module = AppSettingModules.Sales;
        const string key = AppSettingCatalogue.SalesKeys.QuoteValidDays;

        var first = await CreateService().SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = Company,
            Value = "30"
        });
        var second = await CreateService().SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = OtherCompany,
            Value = "45"
        });

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
    }

    [Fact]
    public async Task A_company_row_and_a_branch_row_for_the_same_key_coexist_and_the_branch_wins()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        const string module = AppSettingModules.Sales;
        const string key = AppSettingCatalogue.SalesKeys.AllowBelowCost;

        var service = CreateService();

        Assert.True((await service.SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Company,
            CompanyCode = Company,
            Value = "false"
        })).Succeeded);

        Assert.True((await CreateService().SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Branch,
            CompanyCode = Company,
            BranchCode = "HQ",
            Value = "true"
        })).Succeeded);

        var atBranch = await CreateService().GetValueAsync(module, key, AppSettingScope.Branch, Company, "HQ");
        var atCompany = await CreateService().GetValueAsync(module, key, AppSettingScope.Company, Company, null);

        Assert.Equal("true", atBranch.Data);
        Assert.Equal("false", atCompany.Data);
    }

    /// <summary>
    /// The projection reads the company column, and the registry is never consulted for it — so a
    /// hand-inserted AdSmParam row cannot hijack a column-backed setting.
    /// </summary>
    [Fact]
    public async Task A_registry_row_cannot_hijack_a_column_backed_setting()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        const string module = AppSettingModules.Sales;
        const string key = AppSettingCatalogue.SalesKeys.PriceMethod;

        await using (var db = await _factory!.CreateDbContextAsync())
        {
            db.AdSmParams.Add(new AdSmParam
            {
                ModuleCode = module,
                ParamKey = key,
                ScopeCode = "COMPANY",
                CompanyCode = Company,
                ValueText = SaCompanyPriceMethod.PriceListOnly,
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "test"
            });

            await db.SaveChangesAsync();
        }

        var result = await CreateService().GetValueAsync(module, key, AppSettingScope.Company, Company, null);

        // The Company.SalesPriceMethod column is NULL for this company, so the DEFAULT applies — the
        // registry row was ignored entirely.
        Assert.Equal(SaCompanyPriceMethod.CustomerItemAndList, result.Data);
    }

    // ── harness ─────────────────────────────────────────────────────────────

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
                "The settings concurrency suite requires a scratch SQL Server: set "
                + "ConnectionStrings:SqlServerTestConnection to a database whose name contains 'test' "
                + "and apply scripts/create-adsmparam.sql to it.");
        }

        return true;
    }

    private AppSettingService CreateService()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString!)
            .Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns(Company);
        current.SetupGet(x => x.BranchCode).Returns("HQ");

        var accessRights = new Mock<IAccessRightService>();
        accessRights
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new AppSettingService(
            factory,
            new TenantScopeContext(current.Object),
            accessRights.Object,
            new MemoryCache(new MemoryCacheOptions()),
            new AppSettingCacheVersions(),
            new AppSettingProviderRegistry([new SaPriceMethodSettingProvider(factory)]),
            NullLogger<AppSettingService>.Instance);
    }

    private static async Task CleanupAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.AdSmParam;");
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM dbo.Company WHERE CompanyCode IN ('{Company}', '{OtherCompany}');");
    }

    private static async Task SeedCompaniesAsync(AppDbContext db)
    {
        foreach (var code in new[] { Company, OtherCompany })
        {
            if (!await db.Companies.AnyAsync(x => x.CompanyCode == code))
            {
                db.Companies.Add(new Company
                {
                    CompanyCode = code,
                    CompanyName = $"Settings test {code}",
                    IsActive = true
                });
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task<string> MissingTablesAsync(AppDbContext db)
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

        return string.Join(", ", RequiredTables.Where(t => !found.Contains(t)));
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
}
