namespace ErpWeb.Core.Inventory;

public sealed class IvStockAdjustmentOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public int BatchNo { get; init; }
    public int BatchId { get; init; }
    public int PeekBatchNo { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public IvStockAdjustmentDocument? Document { get; init; }
    public IvStockAdjustmentListPage? ListPage { get; init; }
    public IvInventoryPostingResult? Posting { get; init; }

    public static IvStockAdjustmentOperationResult Ok() =>
        new() { Succeeded = true };

    public static IvStockAdjustmentOperationResult OkSaved(int batchId, int batchNo) =>
        new() { Succeeded = true, BatchId = batchId, BatchNo = batchNo };

    public static IvStockAdjustmentOperationResult OkPeek(int peekBatchNo) =>
        new() { Succeeded = true, PeekBatchNo = peekBatchNo };

    public static IvStockAdjustmentOperationResult OkDocument(IvStockAdjustmentDocument document) =>
        new() { Succeeded = true, Document = document, BatchId = document.Id, BatchNo = document.BatchNo };

    public static IvStockAdjustmentOperationResult OkList(IvStockAdjustmentListPage page) =>
        new() { Succeeded = true, ListPage = page };

    public static IvStockAdjustmentOperationResult OkPosting(IvInventoryPostingResult posting) =>
        new()
        {
            Succeeded = posting.Succeeded,
            ErrorMessage = posting.ErrorMessage,
            SucceededCount = posting.SucceededCount,
            FailedCount = posting.FailedCount,
            Posting = posting
        };

    public static IvStockAdjustmentOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvStockAdjustmentListQuery
{
    public string? SearchText { get; set; }
    public string? BatchStatus { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class IvStockAdjustmentListRow
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public DateTime TrxDate { get; init; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; init; }
    public string? Remarks { get; init; }
    public int LineCount { get; init; }
    public decimal TotalAmount { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed class IvStockAdjustmentListPage
{
    public IReadOnlyList<IvStockAdjustmentListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class IvStockAdjustmentDocument
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public DateTime TrxDate { get; set; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvStockAdjustmentLineDto> Lines { get; init; } = [];
}

public sealed class IvStockAdjustmentLineDto
{
    public short LineNo { get; init; }
    public int BalLocId { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string Warehouse { get; init; } = string.Empty;
    public string? Location { get; init; }
    public string? LotNo { get; init; }
    public decimal AdjustQty { get; init; }
    public decimal CurrentQty { get; init; }
    public decimal NewQty { get; init; }
    public string? Uom { get; init; }
    public string? IClassCode { get; init; }
    public string IStatus { get; init; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string? Reason { get; init; }
    public string? Remarks { get; init; }
    public bool LotControl { get; init; }
}

public sealed class IvStockAdjustmentSaveRequest
{
    public DateTime TrxDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvStockAdjustmentLineRequest>? Lines { get; set; }
}

public sealed class IvStockAdjustmentLineRequest
{
    public int BalLocId { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string Warehouse { get; set; } = string.Empty;
    public string? Location { get; set; }
    public string? LotNo { get; set; }
    public decimal AdjustQty { get; set; }
    public string? Uom { get; set; }
    public string? IClassCode { get; set; }
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
}

public interface IIvStockAdjustmentService
{
    Task<IvStockAdjustmentOperationResult> PeekNextBatchNoAsync(CancellationToken cancellationToken = default);

    Task<IvStockAdjustmentOperationResult> SearchAsync(
        IvStockAdjustmentListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvStockAdjustmentOperationResult> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<IvStockAdjustmentOperationResult> SaveNewAsync(
        IvStockAdjustmentSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvStockAdjustmentOperationResult> UpdateAsync(
        int batchNo,
        IvStockAdjustmentSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvStockAdjustmentOperationResult> DeleteAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvStockAdjustmentOperationResult> CancelAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvStockAdjustmentOperationResult> PostAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvStockAdjustmentOperationResult> RollbackAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);
}
