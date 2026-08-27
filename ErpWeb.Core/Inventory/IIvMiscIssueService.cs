namespace ErpWeb.Core.Inventory;

public sealed class IvMiscIssueOperationResult
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
    public IvMiscIssueDocument? Document { get; init; }
    public IvMiscIssueListPage? ListPage { get; init; }
    public IvInventoryPostingResult? Posting { get; init; }

    public static IvMiscIssueOperationResult Ok() =>
        new() { Succeeded = true };

    public static IvMiscIssueOperationResult OkSaved(int batchId, int batchNo) =>
        new() { Succeeded = true, BatchId = batchId, BatchNo = batchNo };

    public static IvMiscIssueOperationResult OkPeek(int peekBatchNo) =>
        new() { Succeeded = true, PeekBatchNo = peekBatchNo };

    public static IvMiscIssueOperationResult OkLookups(
        IReadOnlyList<IvStockLookupRow> items,
        IReadOnlyList<IvWarehouseLookupRow> warehouses) =>
        new() { Succeeded = true, Items = items, Warehouses = warehouses };

    public static IvMiscIssueOperationResult OkDocument(IvMiscIssueDocument document) =>
        new() { Succeeded = true, Document = document, BatchId = document.Id, BatchNo = document.BatchNo };

    public static IvMiscIssueOperationResult OkList(IvMiscIssueListPage page) =>
        new() { Succeeded = true, ListPage = page };

    public static IvMiscIssueOperationResult OkPosting(IvInventoryPostingResult posting) =>
        new()
        {
            Succeeded = posting.Succeeded,
            ErrorMessage = posting.ErrorMessage,
            SucceededCount = posting.SucceededCount,
            FailedCount = posting.FailedCount,
            Posting = posting
        };

    public static IvMiscIssueOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvMiscIssueListQuery
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

public sealed class IvMiscIssueListRow
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

public sealed class IvMiscIssueListPage
{
    public IReadOnlyList<IvMiscIssueListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class IvMiscIssueDocument
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public DateTime TrxDate { get; set; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvMiscIssueLineDto> Lines { get; init; } = [];
}

public sealed class IvMiscIssueLineDto
{
    public short LineNo { get; init; }
    public int FromBalLocId { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string FrWarehouse { get; init; } = string.Empty;
    public string? FrLocation { get; init; }
    public string? FrLotNo { get; init; }
    public decimal Quantity { get; init; }
    public string? Uom { get; init; }
    public decimal AvailableQty { get; init; }
    public string? IClassCode { get; init; }
    public string IStatus { get; init; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string? Reason { get; init; }
    public string? Remarks { get; init; }
    public bool LotControl { get; init; }
}

public sealed class IvMiscIssueSaveRequest
{
    public DateTime TrxDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvMiscIssueLineRequest>? Lines { get; set; }
}

public sealed class IvMiscIssueLineRequest
{
    public int FromBalLocId { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string FrWarehouse { get; set; } = string.Empty;
    public string? FrLocation { get; set; }
    public string? FrLotNo { get; set; }
    public decimal Quantity { get; set; }
    public string? Uom { get; set; }
    public string? IClassCode { get; set; }
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
}

public interface IIvMiscIssueService
{
    Task<IvMiscIssueOperationResult> PeekNextBatchNoAsync(CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> SearchAsync(
        IvMiscIssueListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> SaveNewAsync(
        IvMiscIssueSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> UpdateAsync(
        int batchNo,
        IvMiscIssueSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> DeleteAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> CancelAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> PostAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);

    Task<IvMiscIssueOperationResult> RollbackAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default);
}
