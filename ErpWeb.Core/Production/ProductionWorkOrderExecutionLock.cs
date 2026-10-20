using ErpWeb.Model.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Production;

/// <summary>
/// Transaction-owned serialization boundary shared by planner placement and Daily Production
/// operations for one Work Order. Call inside the transaction before taking Work Order or execution
/// row locks. This is deliberately narrower than a company-wide production lock.
/// </summary>
public static class ProductionWorkOrderExecutionLock
{
    public const int DefaultTimeoutMs = 15_000;
    public static int TimeoutMs { get; set; } = DefaultTimeoutMs;

    public static string ResourceName(string companyCode, string branchCode, long workOrderId) =>
        $"PR_WO_EXECUTION|{Normalize(companyCode)}|{Normalize(branchCode)}|{workOrderId:D20}";

    public static async Task AcquireAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderId,
        CancellationToken cancellationToken = default)
    {
        if (workOrderId <= 0)
            throw new ArgumentOutOfRangeException(nameof(workOrderId));
        if (!db.Database.IsSqlServer())
            return;

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = @"
DECLARE @result int;
EXEC @result = sp_getapplock
    @Resource = @resource,
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = @timeoutMs;
SELECT @result;";
        command.Parameters.Add(new SqlParameter("@resource", ResourceName(companyCode, branchCode, workOrderId)));
        command.Parameters.Add(new SqlParameter("@timeoutMs", TimeoutMs));

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        var result = raw is int value ? value : Convert.ToInt32(raw);
        if (result < 0)
            throw new ProductionWorkOrderExecutionLockException();
    }

    /// <summary>
    /// Acquires several Work Order execution locks in ascending UID order. Advanced scheduling
    /// touches more than one Work Order, and a deterministic order is what prevents two planners
    /// from deadlocking against each other.
    /// </summary>
    public static async Task AcquireManyAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<long> workOrderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        foreach (var workOrderId in workOrderIds
                     .Where(x => x > 0)
                     .Distinct()
                     .Order())
        {
            await AcquireAsync(db, companyCode, branchCode, workOrderId, cancellationToken);
        }
    }

    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

public sealed class ProductionWorkOrderExecutionLockException : Exception
{
    public ProductionWorkOrderExecutionLockException()
        : base("Production execution or schedule placement is being updated for this Work Order. Reload and retry.")
    {
    }
}
