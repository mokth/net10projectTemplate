using System.Text.RegularExpressions;
using ErpWeb.Model.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Procurement.Transaction;

/// <summary>
/// Isolated SQL Server catalog for PR/PO approval Gate 3.
/// Connection must come from <see cref="ConnectionEnv"/> and the catalog name must start with
/// <see cref="CatalogPrefix"/>. Shared DEMO/ERPWeb catalogs are rejected.
/// </summary>
[CollectionDefinition("PoApprovalSqlGate3")]
public sealed class PoApprovalSqlGate3Collection : ICollectionFixture<PoApprovalSqlGate3Fixture>
{
}

public sealed class PoApprovalSqlFactAttribute : FactAttribute
{
    public PoApprovalSqlFactAttribute()
    {
        if (!PoApprovalSqlGate3Fixture.IsRequired
            && string.IsNullOrWhiteSpace(PoApprovalSqlGate3Fixture.ConfiguredConnectionString()))
        {
            Skip = PoApprovalSqlGate3Fixture.MissingConnectionMessage;
        }
    }
}

public sealed class PoApprovalSqlGate3Fixture : IAsyncLifetime
{
    public const string ConnectionEnv = "PR_PO_APPROVAL_TEST_SQL_CONNECTION";
    public const string RequiredEnv = "PR_PO_APPROVAL_SQL_REQUIRED";
    public const string CatalogPrefix = "erpweb_poapr_test_";

    public const string MissingConnectionMessage =
        "Set PR_PO_APPROVAL_TEST_SQL_CONNECTION to an isolated catalog named erpweb_poapr_test_*. "
        + "Never use DefaultConnection or the shared ERPWeb DEMO database. "
        + "Set PR_PO_APPROVAL_SQL_REQUIRED=1 on the Gate 3 job so a missing connection fails instead of skipping.";

    public IDbContextFactory<AppDbContext>? Factory { get; private set; }

    public static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable(RequiredEnv), "1", StringComparison.OrdinalIgnoreCase);

    public static string? ConfiguredConnectionString()
    {
        var cs = Environment.GetEnvironmentVariable(ConnectionEnv);
        return string.IsNullOrWhiteSpace(cs) ? null : cs.Trim();
    }

    public IDbContextFactory<AppDbContext> RequireFactory() =>
        Factory ?? throw new InvalidOperationException(MissingConnectionMessage);

    public async Task InitializeAsync()
    {
        var cs = ConfiguredConnectionString();
        if (cs is null)
        {
            if (IsRequired)
            {
                throw new InvalidOperationException(MissingConnectionMessage);
            }

            return;
        }

        var catalog = ExtractDatabaseName(cs);
        if (catalog is null
            || catalog.Equals("ERPWeb", StringComparison.OrdinalIgnoreCase)
            || catalog.Equals("DEMO", StringComparison.OrdinalIgnoreCase)
            || catalog.Equals("master", StringComparison.OrdinalIgnoreCase)
            || !catalog.StartsWith(CatalogPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConnectionEnv} catalog '{catalog ?? "(missing)"}' is not an isolated Gate 3 database. "
                + $"Use a catalog named {CatalogPrefix}* and never the shared ERPWeb DEMO database.");
        }

        await EnsureCatalogExistsAsync(cs, catalog);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        Factory = new TestDbContextFactory(options);
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await ApplyApprovalScriptAsync(db);
        await AssertFilteredUniqueIndexAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static string? ExtractDatabaseName(string cs)
    {
        foreach (var part in cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var key in new[] { "Database", "Initial Catalog" })
            {
                if (part.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return part[(key.Length + 1)..].Trim();
                }
            }
        }

        return null;
    }

    private static async Task EnsureCatalogExistsAsync(string connectionString, string catalog)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF DB_ID(@name) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
            END
            """;
        cmd.Parameters.Add(new SqlParameter("@name", catalog));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ApplyApprovalScriptAsync(AppDbContext db)
    {
        var scriptPath = FindApprovalScript();
        var sql = await File.ReadAllTextAsync(scriptPath);
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var trimmed = batch.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            await db.Database.ExecuteSqlRawAsync(trimmed);
        }
    }

    private static string FindApprovalScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "create-po-approval.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("scripts/create-po-approval.sql was not found from the test directory.");
    }

    public static async Task AssertFilteredUniqueIndexAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(i.filter_definition AS nvarchar(400))
            FROM sys.indexes i
            WHERE i.name = N'UX_PoApprovalInstance_Active'
              AND i.object_id = OBJECT_ID(N'dbo.PoApprovalInstance')
            """;
        var filter = await command.ExecuteScalarAsync() as string;
        if (string.IsNullOrWhiteSpace(filter)
            || filter.IndexOf("PENDING", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Isolated catalog is missing filtered unique index UX_PoApprovalInstance_Active. "
                + "Apply scripts/create-po-approval.sql before Gate 3.");
        }
    }
}
