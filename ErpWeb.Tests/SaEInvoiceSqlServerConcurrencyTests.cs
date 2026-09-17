using ErpWeb.Core.EInvoice;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.Tests;

/// <summary>
/// The e-Invoice locking that SQLite cannot prove: two operators racing to submit the same document, and
/// an outcome write that loses the <c>RowVersion</c> check because another session touched the row while
/// the submission was at MyInvois.
///
/// <para>
/// Skipped unless <c>ConnectionStrings:SqlServerTestConnection</c> points at a scratch database whose name
/// contains "test" — the same two safety rails as <c>SaItemFamilySqlServerConcurrencyTests</c>, because
/// the other suites' <c>DefaultConnection</c> convention points at the LIVE ERPWeb database.
/// <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> turns the silent skip into a failure, so a green
/// <c>dotnet test</c> cannot mean "nothing ran".
/// </para>
/// </summary>
public class SaEInvoiceSqlServerConcurrencyTests : IAsyncLifetime
{
    private const string Company = "EINV01";
    private const string Branch = "HQ";

    internal const string TestConnectionKey = "SqlServerTestConnection";

    private IDbContextFactory<AppDbContext>? _factory;

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    private bool SkipWithoutSqlServer()
    {
        if (_factory is not null)
        {
            return false;
        }

        if (RequireSqlServer)
        {
            Assert.Fail(
                "The e-Invoice concurrency suite requires a scratch SQL Server: set "
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

    private static bool IsScratchDatabase(string connectionString)
    {
        var name = DatabaseNameOf(connectionString);
        return name is not null && name.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync()
    {
        var connectionString = GetSqlServerConnectionString();
        if (connectionString is null || !IsScratchDatabase(connectionString))
        {
            return;   // skip: no scratch SQL Server configured
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

        try
        {
            await using var db = await factory.CreateDbContextAsync();
            if (!db.Database.IsSqlServer())
            {
                return;
            }

            // The e-Invoice columns only exist once the model has been applied to the scratch database.
            await db.Database.EnsureCreatedAsync();
            if (!await HasEInvoiceSchemaAsync(db))
            {
                if (RequireSqlServer)
                {
                    Assert.Fail(
                        "The scratch database is missing the e-Invoice columns (SaInvoice.IRBM*, "
                        + "SaEInvoiceLog). Recreate it from the current model or apply the scripts/ "
                        + "migrations first.");
                }

                return;
            }

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

    private static async Task<bool> HasEInvoiceSchemaAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sys.columns WHERE Name = N'IRBMStatus' AND Object_ID = OBJECT_ID(N'dbo.SaInvoice')";
        var columns = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        if (columns == 0)
        {
            return false;
        }

        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = N'SaEInvoiceLog'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task CleanupAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.SaEInvoiceLog WHERE CompanyCode = N'{Company}'");
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.SaInvoiceDetail WHERE CompanyCode = N'{Company}'");
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.SaInvoice WHERE CompanyCode = N'{Company}'");
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.Company WHERE CompanyCode = N'{Company}'");
    }

    // ─────────────────────────────── The races ───────────────────────────────

    [Fact]
    public async Task Two_operators_submitting_the_same_invoice_produce_exactly_one_submission()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        await using var seeder = EInvoiceTestHost.CreateWithFactory(_factory!, Company, Branch);
        await seeder.SeedCompanyAsync();
        await seeder.SeedInvoiceAsync();

        // Two independent scoped façades - the same thing Blazor Server does for two concurrent users.
        await using var first = EInvoiceTestHost.CreateWithFactory(_factory!, Company, Branch);
        await using var second = EInvoiceTestHost.CreateWithFactory(_factory!, Company, Branch);
        var serviceA = first.CreateService();
        var serviceB = second.CreateService();

        var start = new ManualResetEventSlim(false);
        var key = EInvoiceTestHost.InvoiceKey();

        var runA = Task.Run(() => { start.Wait(); return serviceA.SubmitAsync(key); });
        var runB = Task.Run(() => { start.Wait(); return serviceB.SubmitAsync(key); });
        start.Set();

        var results = await Task.WhenAll(runA, runB);

        // Exactly one submission must reach MyInvois, whichever way the other request is refused.
        var submissions = first.Helper.SubmitCallCount + second.Helper.SubmitCallCount;
        Assert.Equal(1, submissions);
        Assert.Single(results.Where(x => x.Succeeded));

        var refused = results.Single(x => !x.Succeeded);
        Assert.Contains(refused.ErrorKind, new[] { SaEInvoiceErrorKind.StateRule, SaEInvoiceErrorKind.Concurrency });

        var saved = await seeder.GetInvoiceAsync(Company);
        Assert.NotNull(saved);
        Assert.Equal(EInvoiceStatuses.Submitted, saved!.IrbmStatus);
        Assert.False(string.IsNullOrWhiteSpace(saved.IrbmUuid));
    }

    [Fact]
    public async Task A_row_touched_during_the_submission_loses_the_rowversion_check_and_recovers()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        await using var host = EInvoiceTestHost.CreateWithFactory(_factory!, Company, Branch);
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        // MyInvois accepts the document; another session updates the row before the ERP writes the
        // outcome back. SQL Server bumps RowVersion on that update, so the write-back must lose.
        host.Helper.OnSubmitCalled = () => host.BumpInvoiceRowVersionAsync().GetAwaiter().GetResult();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Concurrency, result.ErrorKind);

        // The accepted UUID is not written back: the document still looks in-flight, which is the safe
        // direction. Recover then reconciles it with MyInvois instead of submitting a second time.
        var inFlight = await host.GetInvoiceAsync(Company);
        Assert.Equal(EInvoiceStatuses.Submitting, inFlight!.IrbmStatus);
        Assert.Null(inFlight.IrbmUuid);

        host.Helper.Calls.Clear();
        host.Helper.SubmissionHandler = _ =>
            FakeSubmitDocumentHelper.SubmissionWith(EInvoiceTestHost.InvNo, "Valid", "UUID-RACE-1");

        var recovered = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(recovered.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, recovered.Status);
        Assert.Equal(0, host.Helper.SubmitCallCount);

        var saved = await host.GetInvoiceAsync(Company);
        Assert.Equal(EInvoiceStatuses.Valid, saved!.IrbmStatus);
        Assert.Equal("UUID-RACE-1", saved.IrbmUuid);
    }
}
