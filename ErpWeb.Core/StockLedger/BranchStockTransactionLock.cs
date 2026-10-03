using ErpWeb.Model.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.StockLedger;

public interface IBranchStockTransactionLock
{
    Task AcquireAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default);
}

public sealed class BranchStockTransactionLock : IBranchStockTransactionLock
{
    public const int DefaultTimeoutMs = 15_000;
    public static int TimeoutMs { get; set; } = DefaultTimeoutMs;

    public static string ResourceName(string companyCode, string branchCode) =>
        $"STOCK_WRITE|{companyCode.Trim().ToUpperInvariant()}|{branchCode.Trim().ToUpperInvariant()}";

    public async Task AcquireAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlServer())
            return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The branch stock lock requires an active transaction.");

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = @timeoutMs;
            SELECT @result;
            """;
        command.Parameters.Add(new SqlParameter("@resource", ResourceName(companyCode, branchCode)));
        command.Parameters.Add(new SqlParameter("@timeoutMs", TimeoutMs));
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture) < 0)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.StockBusy,
                "Another stock transaction is in progress for this branch. Retry the command.",
                Retryable: true));
    }
}
