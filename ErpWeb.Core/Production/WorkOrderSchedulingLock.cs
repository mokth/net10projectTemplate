using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Production;

/// <summary>
/// Company-wide scheduling-source lock (plan §9.2). Recalculate and Release take Shared;
/// calendar writers take Exclusive. SQLite tests skip the lock because they have no
/// <c>sp_getapplock</c>.
/// </summary>
public static class WorkOrderSchedulingLock
{
    public const int DefaultTimeoutMs = 15_000;

    /// <summary>Overridable for SQL Server concurrency tests. Production code leaves the default.</summary>
    public static int TimeoutMs { get; set; } = DefaultTimeoutMs;

    public static string ResourceName(string companyCode) =>
        "PR_SCHEDULE|" + (companyCode ?? string.Empty).Trim().ToUpperInvariant();

    public static async Task AcquireAsync(
        AppDbContext db,
        string companyCode,
        bool exclusive,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            return;
        }

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = @"
DECLARE @result int;
EXEC @result = sp_getapplock
    @Resource = @resource,
    @LockMode = @mode,
    @LockOwner = 'Transaction',
    @LockTimeout = @timeoutMs;
SELECT @result;";
        cmd.Parameters.Add(new SqlParameter("@resource", ResourceName(companyCode)));
        cmd.Parameters.Add(new SqlParameter("@mode", exclusive ? "Exclusive" : "Shared"));
        cmd.Parameters.Add(new SqlParameter("@timeoutMs", TimeoutMs));

        var raw = await cmd.ExecuteScalarAsync(cancellationToken);
        var result = raw is int i ? i : Convert.ToInt32(raw);
        if (result < 0)
        {
            throw new WorkOrderSchedulingLockException();
        }
    }
}

public sealed class WorkOrderSchedulingLockException : Exception
{
    public WorkOrderSchedulingLockException()
        : base("A scheduling-source update is in progress. Retry the command.")
    {
    }
}
