using ErpWeb.Model.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Planning;

/// <summary>
/// Transaction-scoped SQL Server application lock for PlanningCalendar:{CompCode}:{Year}.
/// Release is automatic on commit/rollback (LockOwner = Transaction).
/// </summary>
public static class PlanningCalendarYearLock
{
    public const int DefaultTimeoutMs = 120_000;
    public const string BusyUserMessage =
        "The calendar is currently being updated by another operation. Please retry.";

    public static string ResourceName(string companyCode, int year)
    {
        var comp = PlanningCodeNormalizer.NormalizeCode(companyCode);
        return $"PlanningCalendar:{comp}:{year}";
    }

    /// <summary>Acquire exclusive locks for years in ascending order (deadlock-safe).</summary>
    public static async Task AcquireYearsAsync(
        AppDbContext db,
        string companyCode,
        IEnumerable<int> years,
        CancellationToken ct = default,
        int timeoutMs = DefaultTimeoutMs)
    {
        foreach (var year in years.Distinct().OrderBy(y => y))
            await AcquireAsync(db, companyCode, year, ct, timeoutMs);
    }

    public static async Task AcquireAsync(
        AppDbContext db,
        string companyCode,
        int year,
        CancellationToken ct = default,
        int timeoutMs = DefaultTimeoutMs)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        var tx = db.Database.CurrentTransaction?.GetDbTransaction();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
DECLARE @result int;
EXEC @result = sp_getapplock
    @Resource = @resource,
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = @timeoutMs;
SELECT @result;";
        cmd.Parameters.Add(new SqlParameter("@resource", ResourceName(companyCode, year)));
        cmd.Parameters.Add(new SqlParameter("@timeoutMs", timeoutMs));

        var raw = await cmd.ExecuteScalarAsync(ct);
        var result = raw is int i ? i : Convert.ToInt32(raw);
        if (result < 0)
            throw new PlanningCalendarLockException(BusyUserMessage, result);
    }
}

public sealed class PlanningCalendarLockException : Exception
{
    public int SqlReturnCode { get; }

    public PlanningCalendarLockException(string message, int sqlReturnCode)
        : base(message)
    {
        SqlReturnCode = sqlReturnCode;
    }
}
