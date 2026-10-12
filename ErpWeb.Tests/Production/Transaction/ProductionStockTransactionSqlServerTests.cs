using Microsoft.Data.SqlClient;

namespace ErpWeb.Tests.Production.Transaction;

/// <summary>Opt in with an explicitly designated disposable database; never uses the application connection.</summary>
[Trait(TestCategories.Name, TestCategories.Production)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class ProductionStockTransactionSqlServerTests
{
    public const string ConnectionVariable = "ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION";

    [ProductionStockSqlServerFact]
    public async Task Schema_is_rerunnable_and_actual_metadata_passes_release_preflight()
    {
        await using var connection = new SqlConnection(Environment.GetEnvironmentVariable(ConnectionVariable));
        await connection.OpenAsync();
        var migration = await File.ReadAllTextAsync(Script("create-production-stock-transactions.sql"));
        var preflight = await File.ReadAllTextAsync(Script("preflight-production-stock-transactions.sql"));
        await ExecuteAsync(connection, migration);
        await ExecuteAsync(connection, migration);
        await using var command = connection.CreateCommand();
        command.CommandText = preflight;
        Assert.Equal("PASS", await command.ExecuteScalarAsync());
    }

    [ProductionStockSqlServerFact]
    public async Task Missing_prerequisite_aborts_atomically_and_does_not_leave_a_disabled_trigger()
    {
        await using var connection = new SqlConnection(Environment.GetEnvironmentVariable(ConnectionVariable));
        await connection.OpenAsync();
        // Deliberately do not commit. The migration must reject the missing seal guard and
        // roll back the complete transaction, including this simulated deployment defect.
        try
        {
            var script = await File.ReadAllTextAsync(Script("create-production-stock-transactions.sql"));
            var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection,
                "BEGIN TRANSACTION; DISABLE TRIGGER dbo.TR_PrMaterialMovement_Seal ON dbo.PrMaterialMovement;\n" + script));
            Assert.Equal(51171, error.Number);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CONVERT(int,is_disabled) FROM sys.triggers WHERE name=N'TR_PrMaterialMovement_Seal';";
            Assert.Equal(0, await command.ExecuteScalarAsync());
        }
        finally
        {
            await ExecuteAsync(connection, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Script(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "scripts", name);
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Cannot locate {name}.");
    }
}

public sealed class ProductionStockSqlServerFactAttribute : FactAttribute
{
    public ProductionStockSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProductionStockTransactionSqlServerTests.ConnectionVariable)))
            Skip = $"Set {ProductionStockTransactionSqlServerTests.ConnectionVariable} to an explicitly designated disposable SQL Server database.";
    }
}
