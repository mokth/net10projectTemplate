namespace ErpWeb.Core.Inventory;

public sealed class IvMiscReceiptOperationResult
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
    public IvMiscReceiptDocument? Document { get; init; }
    public IvMiscReceiptListPage? ListPage { get; init; }
    public IvInventoryPostingResult? Posting { get; init; }

    public static IvMiscReceiptOperationResult Ok() =>
        new() { Succeeded = true };

    public static IvMiscReceiptOperationResult OkSaved(int batchId, int batchNo) =>
        new() { Succeeded = true, BatchId = batchId, BatchNo = batchNo };

    public static IvMiscReceiptOperationResult OkPeek(int peekBatchNo) =>
        new() { Succeeded = true, PeekBatchNo = peekBatchNo };

    public static IvMiscReceiptOperationResult OkLookups(
        IReadOnlyList<IvStockLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses) =>
        new() { Succeeded = true, Items = items, Warehouses = warehouses };

    public static IvMiscReceiptOperationResult OkDocument(IvMiscReceiptDocument document) =>
        new() { Succeeded = true, Document = document, BatchId = document.Id, BatchNo = document.BatchNo };

    public static IvMiscReceiptOperationResult OkList(IvMiscReceiptListPage page) =>
        new() { Succeeded = true, ListPage = page };

    public static IvMiscReceiptOperationResult OkPosting(IvInventoryPostingResult posting) =>
        new()
        {
            Succeeded = posting.Succeeded,
            ErrorMessage = posting.ErrorMessage,
            SucceededCount = posting.SucceededCount,
            FailedCount = posting.FailedCount,
            Posting = posting
        };

    public static IvMiscReceiptOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvStockLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? IClassCode { get; init; }
    public string? StdUom { get; init; }
    public string? DefWarehouse { get; init; }
    public string? DefLocation { get; init; }
    public bool LotControl { get; init; }
    public decimal? PurchasePrice { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} — {IDesc}";
}

public sealed class IvWarehouseLookupRow
{
    public string WarehouseCode { get; init; } = string.Empty;
    public string? WarehouseDesc { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(WarehouseDesc)
        ? WarehouseCode
        : $"{WarehouseCode} — {WarehouseDesc}";
}

public sealed class IvMiscReceiptListQuery
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

public sealed class IvMiscReceiptListRow
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

public sealed class IvMiscReceiptListPage
{
    public IReadOnlyList<IvMiscReceiptListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class IvMiscReceiptDocument
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public DateTime TrxDate { get; set; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvMiscReceiptLineDto> Lines { get; init; } = [];
}

public sealed class IvMiscReceiptLineDto
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

public sealed class IvMiscReceiptSaveRequest
{
    public DateTime TrxDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvMiscReceiptLineRequest>? Lines { get; set; }
}

public sealed class IvMiscReceiptLineRequest
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

public interface IIvMiscReceiptService
{
    Task<IvMiscReceiptOperationResult> PeekNextBatchNoAsync(CancellationToken cancellationToken = default);

    Task<IvMiscReceiptOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<IvMiscReceiptOperationResult> SearchAsync(
        IvMiscReceiptListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMiscReceiptOperationResult> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<IvMiscReceiptOperationResult> SaveNewAsync(
        IvMiscReceiptSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMiscReceiptOperationResult> UpdateAsync(
        int batchNo,
        IvMiscReceiptSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMiscReceiptOperationResult> DeleteAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvMiscReceiptOperationResult> PostAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvMiscReceiptOperationResult> RollbackAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);
}
