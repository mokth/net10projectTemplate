using ErpWeb.Model.Data;

namespace ErpWeb.Core.Inventory;

public sealed class IvInventoryPostingResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<IvInventoryPostingBatchResult> Batches { get; init; } = [];

    public static IvInventoryPostingResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message, FailedCount = 1 };

    public static IvInventoryPostingResult FromBatches(IReadOnlyList<IvInventoryPostingBatchResult> batches)
    {
        var ok = batches.Count(x => x.Succeeded);
        var fail = batches.Count - ok;
        var summary = fail == 0
            ? null
            : string.Join(" ", batches.Where(x => !x.Succeeded).Select(x => $"Batch {x.BatchNo}: {x.ErrorMessage}"));
        return new IvInventoryPostingResult
        {
            Succeeded = fail == 0,
            SucceededCount = ok,
            FailedCount = fail,
            ErrorMessage = fail == 0 ? null : $"{(ok > 0 ? $"{(ok == 1 ? "Posted" : "Completed")} {ok} of {batches.Count}. " : string.Empty)}{summary}",
            Batches = batches
        };
    }
}

public sealed class IvInventoryPostingBatchResult
{
    public int BatchNo { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public Guid? OperationId { get; init; }

    public static IvInventoryPostingBatchResult Ok(int batchNo, Guid operationId) =>
        new() { BatchNo = batchNo, Succeeded = true, OperationId = operationId };

    public static IvInventoryPostingBatchResult Fail(int batchNo, string message) =>
        new() { BatchNo = batchNo, Succeeded = false, ErrorMessage = message };
}

public interface IIvInventoryPostingService
{
    Task<IvInventoryPostingResult> PostAsync(
        string trxType,
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvInventoryPostingResult> RollbackAsync(
        string trxType,
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stock-out post on the caller's context and transaction. Does not SaveChanges or Commit.
    /// Caller must already have begun a transaction on <paramref name="db"/>.
    /// </summary>
    Task<IvInventoryPostingBatchResult> PostStockOutInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stock-out rollback on the caller's context and transaction. Does not SaveChanges or Commit.
    /// Caller must already have begun a transaction on <paramref name="db"/>.
    /// </summary>
    Task<IvInventoryPostingBatchResult> RollBackStockOutInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default);
}
