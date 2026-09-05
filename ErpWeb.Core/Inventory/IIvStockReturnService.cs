namespace ErpWeb.Core.Inventory;

public sealed class IvStockReturnOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public int BatchNo { get; init; }
    public int BatchId { get; init; }
    public int PeekBatchNo { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<IvStockLookupRow> Items { get; init; } = [];
    public IReadOnlyList<IvWarehouseLookupRow> Warehouses { get; init; } = [];
    public IvStockReturnDocument? Document { get; init; }
    public IvStockReturnListPage? ListPage { get; init; }
    public IvInventoryPostingResult? Posting { get; init; }

    public static IvStockReturnOperationResult Ok() =>
        new() { Succeeded = true };

    public static IvStockReturnOperationResult OkSaved(int batchId, int batchNo) =>
        new() { Succeeded = true, BatchId = batchId, BatchNo = batchNo };

    public static IvStockReturnOperationResult OkPeek(int peekBatchNo) =>
        new() { Succeeded = true, PeekBatchNo = peekBatchNo };

    public static IvStockReturnOperationResult OkLookups(
        IReadOnlyList<IvStockLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses) =>
        new() { Succeeded = true, Items = items, Warehouses = warehouses };

    public static IvStockReturnOperationResult OkDocument(IvStockReturnDocument document) =>
        new() { Succeeded = true, Document = document, BatchId = document.Id, BatchNo = document.BatchNo };

    public static IvStockReturnOperationResult OkList(IvStockReturnListPage page) =>
        new() { Succeeded = true, ListPage = page };

    public static IvStockReturnOperationResult OkPosting(IvInventoryPostingResult posting) =>
        new()
        {
            Succeeded = posting.Succeeded,
            ErrorMessage = posting.ErrorMessage,
            SucceededCount = posting.SucceededCount,
            FailedCount = posting.FailedCount,
            Posting = posting
        };

    public static IvStockReturnOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvStockReturnListQuery
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

public sealed class IvStockReturnListRow
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

public sealed class IvStockReturnListPage
{
    public IReadOnlyList<IvStockReturnListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class IvStockReturnDocument
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public DateTime TrxDate { get; set; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvStockReturnLineDto> Lines { get; init; } = [];
}

public sealed class IvStockReturnLineDto
{
    public short LineNo { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string ToWarehouse { get; init; } = string.Empty;
    public string? ToLocation { get; init; }
    public string? ToLotNo { get; init; }
    public decimal Quantity { get; init; }
    public string? Uom { get; init; }
    public string? IClassCode { get; init; }
    public string IStatus { get; init; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string? Reason { get; init; }
    public string? Remarks { get; init; }
    public bool LotControl { get; init; }
}

public sealed class IvStockReturnSaveRequest
{
    public DateTime TrxDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvStockReturnLineRequest>? Lines { get; set; }
}

public sealed class IvStockReturnLineRequest
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string ToWarehouse { get; set; } = string.Empty;
    public string? ToLocation { get; set; }
    public string? ToLotNo { get; set; }
    public decimal Quantity { get; set; }
    public string? Uom { get; set; }
    public string? IClassCode { get; set; }
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
}

public interface IIvStockReturnService
{
    Task<IvStockReturnOperationResult> PeekNextBatchNoAsync(CancellationToken cancellationToken = default);

    Task<IvStockReturnOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<IvStockReturnOperationResult> SearchAsync(
        IvStockReturnListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvStockReturnOperationResult> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<IvStockReturnOperationResult> SaveNewAsync(
        IvStockReturnSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvStockReturnOperationResult> UpdateAsync(
        int batchNo,
        IvStockReturnSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvStockReturnOperationResult> DeleteAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvStockReturnOperationResult> PostAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvStockReturnOperationResult> RollbackAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);
}
