using ErpWeb.Model.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Purchase;

internal static class ProcurementApprovalSqlLocks
{
    public const int TimeoutMs = 15_000;

    public static string PolicyResource(string companyCode, string docType) =>
        $"PO_APPR_POLICY|{companyCode.Trim().ToUpperInvariant()}|{docType.Trim().ToUpperInvariant()}";

    public static string PoRevisionResource(string companyCode, string branchCode, string poNo) =>
        $"PO_REV|{companyCode.Trim().ToUpperInvariant()}|{branchCode.Trim().ToUpperInvariant()}|{poNo.Trim().ToUpperInvariant()}";

    public static async Task<string?> TryAcquireAsync(
        AppDbContext db,
        string resource,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
        {
            return null;
        }

        if (db.Database.CurrentTransaction is null)
        {
            return timeoutMessage;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

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
        command.Parameters.Add(new SqlParameter("@resource", resource));
        command.Parameters.Add(new SqlParameter("@timeoutMs", TimeoutMs));
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture) < 0)
        {
            return timeoutMessage;
        }

        return null;
    }
}

public static class PurchaseOrderRevisionSetLock
{
    public const string RevisedMessage =
        "Purchase Order was revised. Reload and select the latest approved revision.";

    public static string MessageFor(string poNo) =>
        $"Purchase Order {poNo.Trim()} was revised. Reload and select the latest approved revision.";

    public static Task<string?> AcquireAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        CancellationToken cancellationToken = default) =>
        ProcurementApprovalSqlLocks.TryAcquireAsync(
            db,
            ProcurementApprovalSqlLocks.PoRevisionResource(companyCode, branchCode, poNo),
            "Another user is revising this purchase order. Retry the command.",
            cancellationToken);
}
