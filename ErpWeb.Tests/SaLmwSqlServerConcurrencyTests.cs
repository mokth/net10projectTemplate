using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// TC29 — the only control that can catch the D-9 mistake. Two REAL concurrent writers insert
/// overlapping system windows for the same customer; exactly one may commit.
///
/// SQLite cannot prove this: it serialises writers anyway, so the SQLite suite proves the *logic*
/// and this test proves the *locking*. Critically, it must actually RUN — a green
/// <c>dotnet test</c> that skipped this class proves nothing about the overlap rule.
///
/// Skipped unless <c>ConnectionStrings:SqlServerTestConnection</c> points at a scratch database
/// (the name must contain "test"). Same two safety rails as
/// <c>PoCdnSqlServerConcurrencyTests</c>: an explicit key, and a scratch-looking database name.
/// </summary>
public class SaLmwSqlServerConcurrencyTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    // 5 characters: InventoryTenantContext rejects a company claim longer than MaxCompanyLength.
    private const string Company = "LMWSX";

    internal const string TestConnectionKey = "SqlServerTestConnection";

    private string? _connectionString;
    private IDbContextFactory<AppDbContext>? _factory;

    /// <summary>
    /// Set <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> to turn the silent skip into a failure. The plan's
    /// checklist demands that TC29 actually ran, and a green <c>dotnet test</c> on a machine without
    /// a scratch SQL Server would otherwise look identical to a passing concurrency proof.
    /// </summary>
    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    /// <summary>Returns true when the test must bail out (no scratch SQL Server).</summary>
    private bool SkipWithoutSqlServer()
    {
        if (_factory is not null)
        {
            return false;
        }

        if (RequireSqlServer)
        {
            Assert.Fail(
                "TC29 requires a scratch SQL Server: set ConnectionStrings:SqlServerTestConnection to a "
                + "database whose name contains 'test' (see the class summary).");
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

    public static bool IsSqlServerAvailable()
    {
        var cs = GetSqlServerConnectionString();
        if (cs is null || !IsScratchDatabase(cs))
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

    public async Task InitializeAsync()
    {
        _connectionString = GetSqlServerConnectionString();
        if (_connectionString is null
            || !IsScratchDatabase(_connectionString)
            || !IsSqlServerAvailable())
        {
            return;   // skip: no usable scratch SQL Server
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        _factory = new TestDbContextFactory(options);

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        // EnsureCreated is a no-op when the scratch database already exists (another test class may
        // have built it first), so the one table this test needs is created defensively — the same
        // DDL as scripts/init-sales-master-refs.sql, minus the CHECK constraints (the service is the
        // primary guard and is what this test exercises).
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.SaLMW', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SaLMW (
                    CompanyCode      nvarchar(10)  NOT NULL,
                    LicenseNo        nvarchar(40)  NOT NULL,
                    CustCode         nvarchar(30)  NOT NULL,
                    LicenseID        nvarchar(40)  NULL,
                    LicenseType      nvarchar(20)  NULL,
                    LicenseStartDate date          NOT NULL,
                    LicenseEndDate   date          NOT NULL,
                    SystemStartDate  date          NOT NULL,
                    SystemEndDate    date          NOT NULL,
                    Name             nvarchar(100) NULL,
                    IC               nvarchar(30)  NULL,
                    Position         nvarchar(50)  NULL,
                    CustName         nvarchar(200) NULL,
                    BranchCode       nvarchar(10)  NULL,
                    LocationCode     nvarchar(20)  NULL,
                    Created          datetime2     NULL,
                    Updated          datetime2     NULL,
                    UserID           nvarchar(20)  NULL,
                    UpdatedUID       nvarchar(20)  NULL,
                    RowVersion       rowversion    NOT NULL,
                    CONSTRAINT PK_SaLMW PRIMARY KEY (CompanyCode, LicenseNo, CustCode)
                );
            END
            """);

        await CleanupAsync(db);
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

    private static Task CleanupAsync(AppDbContext db) =>
        db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.SaLMW WHERE CompanyCode = {0}", Company);

    /// <summary>
    /// Two writers race to insert overlapping system windows for one customer. The loser must be
    /// rejected — either by the serializable range lock (deadlock/serialization → Concurrency) or by
    /// seeing the winner's committed row (→ the overlap message). It must never both commit.
    /// </summary>
    [Fact]
    public async Task TC29_TwoConcurrentOverlappingInserts_OnlyOneCommits()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        const string custCode = "RACECUST";
        var start = new ManualResetEventSlim(false);

        async Task<IvMasterOperationResult<SaLMWEditVm>> InsertAsync(string licenseNo, int startDay, int endDay)
        {
            var sut = CreateSut();
            start.Wait();
            return await sut.SaveLmwAsync(new SaLMWEditVm
            {
                LicenseNo = licenseNo,
                CustCode = custCode,
                LicenseStartDate = new DateTime(2026, 1, 1),
                LicenseEndDate = new DateTime(2026, 12, 31),
                // Deliberately overlapping: 2026-06-01..2026-06-30 vs 2026-06-15..2026-07-15.
                SystemStartDate = new DateTime(2026, 6, startDay),
                SystemEndDate = new DateTime(2026, 7, endDay),
                CustName = "Race customer"
            }, isNew: true);
        }

        var taskA = Task.Run(() => InsertAsync("RACEA", 1, 15));
        var taskB = Task.Run(() => InsertAsync("RACEB", 15, 15));

        // Release both writers as close together as possible.
        start.Set();
        var results = await Task.WhenAll(taskA, taskB);

        var winners = results.Where(r => r.Succeeded).ToList();
        var losers = results.Where(r => !r.Succeeded).ToList();

        Assert.Single(winners);
        Assert.Single(losers);

        // The loser must say something actionable — never a silent retry, never a partial write.
        var loser = losers[0];
        Assert.Contains(loser.ErrorCode,
            new[] { IvMasterErrorCode.Validation, IvMasterErrorCode.Concurrency, IvMasterErrorCode.DuplicateKey });
        Assert.False(string.IsNullOrWhiteSpace(loser.Message));
        Assert.True(
            loser.Message!.Contains("overlap", StringComparison.OrdinalIgnoreCase)
            || loser.Message.Contains("Reload", StringComparison.OrdinalIgnoreCase),
            $"Unexpected loser message: {loser.Message}");

        // No overlapping pair may exist afterwards.
        var factory = _factory!;
        await using var db = await factory.CreateDbContextAsync();
        var windows = await db.SaLmws
            .AsNoTracking()
            .Where(x => x.CompanyCode == Company && x.CustCode == custCode)
            .Select(x => new { x.LicenseNo, x.SystemStartDate, x.SystemEndDate })
            .ToListAsync();

        Assert.Single(windows);
        Assert.All(windows, w => Assert.True(
            w.SystemStartDate <= new DateTime(2026, 7, 15) && w.SystemEndDate >= new DateTime(2026, 6, 1)));
    }

    /// <summary>
    /// Safety companion: the same race with NON-overlapping windows may legitimately serialise (and a
    /// same-customer Serializable race can deadlock, which the service reports as "reload and try
    /// again"). Liveness is therefore not asserted — what must hold is that no overlapping pair can
    /// survive and that any loser is told to reload rather than silently retried.
    /// </summary>
    [Fact]
    public async Task TC29_TwoConcurrentNonOverlappingInserts_LeaveAConsistentState()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        const string custCode = "ADJCUST";
        var start = new ManualResetEventSlim(false);

        async Task<IvMasterOperationResult<SaLMWEditVm>> InsertAsync(string licenseNo, DateTime sysStart, DateTime sysEnd)
        {
            var sut = CreateSut();
            start.Wait();
            return await sut.SaveLmwAsync(new SaLMWEditVm
            {
                LicenseNo = licenseNo,
                CustCode = custCode,
                LicenseStartDate = new DateTime(2026, 1, 1),
                LicenseEndDate = new DateTime(2026, 12, 31),
                SystemStartDate = sysStart,
                SystemEndDate = sysEnd,
                CustName = "Adjacent customer"
            }, isNew: true);
        }

        var taskA = Task.Run(() => InsertAsync("ADJA", new DateTime(2026, 6, 1), new DateTime(2026, 6, 30)));
        var taskB = Task.Run(() => InsertAsync("ADJB", new DateTime(2026, 7, 1), new DateTime(2026, 7, 31)));

        start.Set();
        var results = await Task.WhenAll(taskA, taskB);

        // At least one committed, and every failure is actionable.
        Assert.Contains(results, r => r.Succeeded);
        foreach (var failed in results.Where(r => !r.Succeeded))
        {
            Assert.Contains(failed.ErrorCode,
                new[] { IvMasterErrorCode.Concurrency, IvMasterErrorCode.Validation });
            Assert.False(string.IsNullOrWhiteSpace(failed.Message));
        }

        // Whatever the outcome, the committed rows must not overlap each other.
        var factory = _factory!;
        await using var db = await factory.CreateDbContextAsync();
        var windows = await db.SaLmws
            .AsNoTracking()
            .Where(x => x.CompanyCode == Company && x.CustCode == custCode)
            .Select(x => new { x.SystemStartDate, x.SystemEndDate })
            .ToListAsync();

        for (var i = 0; i < windows.Count; i++)
        {
            for (var j = i + 1; j < windows.Count; j++)
            {
                Assert.False(
                    SaLmwRules.SystemWindowsOverlap(
                        windows[i].SystemStartDate, windows[i].SystemEndDate,
                        windows[j].SystemStartDate, windows[j].SystemEndDate),
                    "Two committed system windows overlap.");
            }
        }
    }

    /// <summary>
    /// Sequential near-miss: adjacent and same-customer windows must both persist. This is the
    /// liveness control that shows the race test above measures the overlap rule and not a blanket
    /// refusal to write.
    /// </summary>
    [Fact]
    public async Task TC29_SequentialAdjacentWindows_BothCommit()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var sut = CreateSut();

        var first = await sut.SaveLmwAsync(new SaLMWEditVm
        {
            LicenseNo = "SEQA",
            CustCode = "SEQCUST",
            LicenseStartDate = new DateTime(2026, 1, 1),
            LicenseEndDate = new DateTime(2026, 12, 31),
            SystemStartDate = new DateTime(2026, 6, 1),
            SystemEndDate = new DateTime(2026, 6, 30)
        }, isNew: true);
        Assert.True(first.Succeeded, first.Message);

        var second = await sut.SaveLmwAsync(new SaLMWEditVm
        {
            LicenseNo = "SEQB",
            CustCode = "SEQCUST",
            LicenseStartDate = new DateTime(2026, 1, 1),
            LicenseEndDate = new DateTime(2026, 12, 31),
            // Adjacent: starts the day after the first window ends — not an overlap (§9.3).
            SystemStartDate = new DateTime(2026, 7, 1),
            SystemEndDate = new DateTime(2026, 7, 31)
        }, isNew: true);
        Assert.True(second.Succeeded, second.Message);
    }

    private SaSalesRefService CreateSut()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new SaSalesRefService(
            _factory!,
            InventoryTenantTestHelper.CreateTenantContext(Company, "HQ", "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday));
    }
}
