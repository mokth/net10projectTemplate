using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Shared fixture for the settings tests: an in-memory SQLite database plus a real
/// <see cref="AppSettingService"/> wired with the real provider registry and a real <see cref="IMemoryCache"/>.
///
/// <para>
/// Nothing here is mocked except the access rights and the current user, because the behaviours under
/// test — resolution, cache invalidation and concurrency — are exactly the ones a mock would hide.
/// </para>
/// </summary>
internal sealed class AppSettingTestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private AppSettingTestHost(
        SqliteConnection connection,
        IDbContextFactory<AppDbContext> factory,
        AppSettingService service,
        AppSettingCacheVersions versions,
        AppSettingProviderRegistry providers,
        ITenantScopeContext tenant)
    {
        _connection = connection;
        Factory = factory;
        Service = service;
        Versions = versions;
        Providers = providers;
        Tenant = tenant;
    }

    public IDbContextFactory<AppDbContext> Factory { get; }

    public AppSettingService Service { get; }

    public AppSettingCacheVersions Versions { get; }

    public AppSettingProviderRegistry Providers { get; }

    public ITenantScopeContext Tenant { get; }

    /// <summary>Company that owns the <c>Company</c> row seeded for the price-method projection.</summary>
    public const string Company = "DEMO";

    public static AppSettingTestHost Create(
        string company = Company,
        string branch = "HQ",
        bool canAccess = true,
        bool canEdit = true)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns(company);
        current.SetupGet(x => x.BranchCode).Returns(branch);
        current.SetupGet(x => x.LocationCode).Returns("SITE");

        var tenant = new TenantScopeContext(current.Object);

        var accessRights = new Mock<IAccessRightService>();
        accessRights
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string permission, CancellationToken _) =>
                permission.Equals(PermissionCodes.Edit, StringComparison.OrdinalIgnoreCase)
                    ? canEdit
                    : canAccess);

        var providers = new AppSettingProviderRegistry([new SaPriceMethodSettingProvider(factory)]);
        var versions = new AppSettingCacheVersions();
        var cache = new MemoryCache(new MemoryCacheOptions());

        var service = new AppSettingService(
            factory,
            tenant,
            accessRights.Object,
            cache,
            versions,
            providers,
            NullLogger<AppSettingService>.Instance);

        return new AppSettingTestHost(connection, factory, service, versions, providers, tenant);
    }

    /// <summary>
    /// Writes a stored row directly, bypassing the service. <paramref name="rowVersion"/> is needed for
    /// the update/clear paths: SQLite has no server-generated rowversion, so a token must be supplied.
    ///
    /// <para>
    /// HAZARD: because this bypasses <see cref="AppSettingService"/>, it does NOT invalidate the cache.
    /// Seed every row BEFORE the first read of that module, or call
    /// <see cref="AppSettingCacheVersions.BumpGlobal"/> afterwards.
    /// </para>
    /// </summary>
    public async Task SeedAsync(
        string module,
        string key,
        string scopeCode,
        string? companyCode = null,
        string? branchCode = null,
        string? text = null,
        decimal? number = null,
        bool? flag = null,
        byte[]? rowVersion = null)
    {
        await using var db = await Factory.CreateDbContextAsync();

        db.AdSmParams.Add(new AdSmParam
        {
            ModuleCode = module,
            ParamKey = key,
            ScopeCode = scopeCode,
            CompanyCode = companyCode,
            BranchCode = branchCode,
            ValueText = text,
            ValueNumber = number,
            ValueFlag = flag,
            IsActive = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "seed",
            RowVersion = rowVersion
        });

        await db.SaveChangesAsync();
    }

    public async Task SeedCompanyAsync(string companyCode, string? salesPriceMethod = null)
    {
        await using var db = await Factory.CreateDbContextAsync();

        db.Companies.Add(new Company
        {
            CompanyCode = companyCode,
            CompanyName = companyCode,
            SalesPriceMethod = salesPriceMethod,
            IsActive = true
        });

        await db.SaveChangesAsync();
    }

    public async Task<List<AdSmParam>> RowsAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.AdSmParams.AsNoTracking().ToListAsync();
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
