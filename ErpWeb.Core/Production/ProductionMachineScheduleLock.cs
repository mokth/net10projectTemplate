using ErpWeb.Model.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Production;

/// <summary>
/// Serializes schedule-capacity changes for one physical machine. Acquire all affected machine
/// locks in normalized code order and inside the caller's SQL transaction.
/// </summary>
public static class ProductionMachineScheduleLock
{
    public const int DefaultTimeoutMs = 15_000;
    public static int TimeoutMs { get; set; } = DefaultTimeoutMs;

    public static string ResourceName(string companyCode, string machineCode) =>
        $"PR_MACHINE_SCHEDULE|{Normalize(companyCode)}|{Normalize(machineCode)}";

    public static async Task AcquireManyAsync(
        AppDbContext db,
        string companyCode,
        IEnumerable<string> machineCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!db.Database.IsSqlServer())
            return;

        foreach (var machineCode in machineCodes
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(Normalize)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            await AcquireAsync(db, companyCode, machineCode, cancellationToken);
        }
    }

    private static async Task AcquireAsync(
        AppDbContext db,
        string companyCode,
        string machineCode,
        CancellationToken cancellationToken)
    {
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
        command.Parameters.Add(new SqlParameter("@resource", ResourceName(companyCode, machineCode)));
        command.Parameters.Add(new SqlParameter("@timeoutMs", TimeoutMs));

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        var result = raw is int value ? value : Convert.ToInt32(raw);
        if (result < 0)
            throw new ProductionMachineScheduleLockException(machineCode);
    }

    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

public sealed class ProductionMachineScheduleLockException : Exception
{
    public ProductionMachineScheduleLockException(string machineCode)
        : base($"Machine {machineCode} is being rescheduled. Retry this operation.")
    {
    }
}
