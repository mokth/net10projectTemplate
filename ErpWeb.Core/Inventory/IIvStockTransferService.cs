namespace ErpWeb.Core.Inventory;

public sealed class IvStockTransferOperationResult
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
    public IvStockTransferDocument? Document { get; init; }
    public IvStockTransferListPage? ListPage { get; init; }
    public IvInventoryPostingResult? Posting { get; init; }

    public static IvStockTransferOperationResult Ok() =>
        new() { Succeeded = true };

    public static IvStockTransferOperationResult OkSaved(int batchId, int batchNo) =>
        new() { Succeeded = true, BatchId = batchId, BatchNo = batchNo };

    public static IvStockTransferOperationResult OkPeek(int peekBatchNo) =>
        new() { Succeeded = true, PeekBatchNo = peekBatchNo };

    public static IvStockTransferOperationResult OkLookups(
        IReadOnlyList<IvStockLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses) =>
        new() { Succeeded = true, Items = items, Warehouses = warehouses };

    public static IvStockTransferOperationResult OkDocument(IvStockTransferDocument document) =>
        new() { Succeeded = true, Document = document, BatchId = document.Id, BatchNo = document.BatchNo };

    public static IvStockTransferOperationResult OkList(IvStockTransferListPage page) =>
        new() { Succeeded = true, ListPage = page };

    public static IvStockTransferOperationResult OkPosting(IvInventoryPostingResult posting) =>
        new()
        {
            Succeeded = posting.Succeeded,
            ErrorMessage = posting.ErrorMessage,
            SucceededCount = posting.SucceededCount,
            FailedCount = posting.FailedCount,
            Posting = posting
        };

    public static IvStockTransferOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvStockTransferListQuery
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

public sealed class IvStockTransferListRow
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

public sealed class IvStockTransferListPage
{
    public IReadOnlyList<IvStockTransferListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class IvStockTransferDocument
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public DateTime TrxDate { get; set; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvStockTransferLineDto> Lines { get; init; } = [];
}

public sealed class IvStockTransferLineDto
{
    public short LineNo { get; init; }
    public int FromBalLocId { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string FrWarehouse { get; init; } = string.Empty;
    public string? FrLocation { get; init; }
    public string? FrLotNo { get; init; }
    public string ToWarehouse { get; init; } = string.Empty;
    public string? ToLocation { get; init; }
    public string? ToLotNo { get; init; }
    public bool LotControl { get; init; }
    public decimal Quantity { get; init; }
    public string? Uom { get; init; }
    public decimal AvailableQty { get; init; }
    public string? IClassCode { get; init; }
    public string IStatus { get; init; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; init; }
    public string? Remarks { get; init; }
}

public sealed class IvStockTransferSaveRequest
{
    public DateTime TrxDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvStockTransferLineRequest>? Lines { get; set; }
}

public sealed class IvStockTransferLineRequest
{
    public int FromBalLocId { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string FrWarehouse { get; set; } = string.Empty;
    public string? FrLocation { get; set; }
    public string? FrLotNo { get; set; }
    public string ToWarehouse { get; set; } = string.Empty;
    public string? ToLocation { get; set; }
    public string? ToLotNo { get; set; }
    public short LineNo { get; set; }
    public string? OriginalToLotNo { get; set; }
    public decimal Quantity { get; set; }
    public string? Uom { get; set; }
    public string? IClassCode { get; set; }
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public string? Remarks { get; set; }
}

public interface IIvStockTransferService
{
    Task<IvStockTransferOperationResult> PeekNextBatchNoAsync(CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> SearchAsync(
        IvStockTransferListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> SaveNewAsync(
        IvStockTransferSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> UpdateAsync(
        int batchNo,
        IvStockTransferSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> DeleteAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> CancelAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> PostAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvStockTransferOperationResult> RollbackAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);
}
