using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Core.Quality;

public sealed class DisabledQaReceiptAuthorization : IQaReceiptAuthorization
{
    public static DisabledQaReceiptAuthorization Instance { get; } = new();

    public Task<string?> ClaimGoodsReceiptAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string trxType,
        IvTrxBatch batch,
        IReadOnlyList<IvTrxBatchDetail> details,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> RestoreGoodsReceiptAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        IReadOnlyList<IvTrxBatchDetail> details,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
