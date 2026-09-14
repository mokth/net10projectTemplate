namespace ErpWeb.Core.Inventory;

public sealed class IvGoodsReceiptOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public int BatchNo { get; init; }
    public int BatchId { get; init; }
    public int PeekBatchNo { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public IvGoodsReceiptDocument? Document { get; init; }
    public IvGoodsReceiptListPage? ListPage { get; init; }
    public IReadOnlyList<IvGoodsReceiptPoLineLookupRow> PoLines { get; init; } = [];
    public IvInventoryPostingResult? Posting { get; init; }
    public string? AllocatedLotNo { get; init; }

    public static IvGoodsReceiptOperationResult Ok() => new() { Succeeded = true };
    public static IvGoodsReceiptOperationResult OkSaved(int batchId, int batchNo) => new() { Succeeded = true, BatchId = batchId, BatchNo = batchNo };
    public static IvGoodsReceiptOperationResult OkPeek(int peekBatchNo) => new() { Succeeded = true, PeekBatchNo = peekBatchNo };
    public static IvGoodsReceiptOperationResult OkDocument(IvGoodsReceiptDocument document) => new() { Succeeded = true, Document = document, BatchId = document.Id, BatchNo = document.BatchNo };
    public static IvGoodsReceiptOperationResult OkList(IvGoodsReceiptListPage page) => new() { Succeeded = true, ListPage = page };
    public static IvGoodsReceiptOperationResult OkPoLines(IReadOnlyList<IvGoodsReceiptPoLineLookupRow> rows) => new() { Succeeded = true, PoLines = rows };
    public static IvGoodsReceiptOperationResult OkLot(string lotNo) => new() { Succeeded = true, AllocatedLotNo = lotNo };
    public static IvGoodsReceiptOperationResult OkPosting(IvInventoryPostingResult posting) => new()
    {
        Succeeded = posting.Succeeded,
        ErrorMessage = posting.ErrorMessage,
        SucceededCount = posting.SucceededCount,
        FailedCount = posting.FailedCount,
        Posting = posting
    };
    public static IvGoodsReceiptOperationResult Fail(string message) => new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvGoodsReceiptListQuery
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

public sealed class IvGoodsReceiptListRow
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public string TrxType { get; init; } = string.Empty;
    public DateTime TrxDate { get; init; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; init; }
    public string? Remarks { get; init; }
    public int LineCount { get; init; }
    public decimal TotalAmount { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed class IvGoodsReceiptListPage
{
    public IReadOnlyList<IvGoodsReceiptListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class IvGoodsReceiptDocument
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public string TrxType { get; init; } = IvTrxTypes.GoodsReceive;
    public DateTime TrxDate { get; set; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvGoodsReceiptLineDto> Lines { get; init; } = [];
}

public sealed class IvGoodsReceiptLineDto
{
    public short LineNo { get; init; }
    public string PoNo { get; init; } = string.Empty;
    public short PoRelNo { get; init; }
    public short PoLineNo { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string ToWarehouse { get; init; } = string.Empty;
    public string? ToLocation { get; init; }
    public string? ToLotNo { get; init; }
    public decimal FrPurQty { get; init; }
    public decimal ToRecvQty { get; init; }
    public decimal ToStdQty { get; init; }
    public string? PurchaseUom { get; init; }
    public string? StdUom { get; init; }
    public decimal PackSz { get; init; }
    public string? IClassCode { get; init; }
    public string IStatus { get; init; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string? Remarks { get; init; }
    public bool LotControl { get; init; }
}

public sealed class IvGoodsReceiptSaveRequest
{
    public string TrxType { get; set; } = IvTrxTypes.GoodsReceive;
    public DateTime TrxDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<IvGoodsReceiptLineRequest>? Lines { get; set; }
}

public sealed class IvGoodsReceiptLineRequest
{
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public short PoLineNo { get; set; }
    public string ToWarehouse { get; set; } = string.Empty;
    public string? ToLocation { get; set; }
    public string? ToLotNo { get; set; }
    public decimal ToRecvQty { get; set; }
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public DateTime? ExpiryDate { get; set; }
    public string? Remarks { get; set; }
}

public sealed class IvGoodsReceiptPoLineLookupRow
{
    public string PoNo { get; init; } = string.Empty;
    public short PoRelNo { get; init; }
    public short PoLineNo { get; init; }
    public string? VendCode { get; init; }
    public string? VendName { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public decimal OrderedQty { get; init; }
    public decimal ReceivedQty { get; init; }
    public decimal BalanceQty { get; init; }
    public decimal DraftQty { get; init; }
    public decimal AvailableQty { get; init; }
    public string? PurchaseUom { get; init; }
    public string? StdUom { get; init; }
    public decimal PackSz { get; init; }
    public string? ToWarehouse { get; init; }
    public string? DefWarehouse { get; init; }
    public string? DefLocation { get; init; }
    public bool LotControl { get; init; }
    public bool IsIndirect { get; init; }
    public string DisplayText =>
        $"{PoNo}/{PoRelNo} line {PoLineNo} - {ICode} ({AvailableQty:n4} avail / {BalanceQty:n4} bal {PurchaseUom})";
}

public interface IIvGoodsReceiptService
{
    Task<IvGoodsReceiptOperationResult> PeekNextBatchNoAsync(CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> SearchAsync(IvGoodsReceiptListQuery query, CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> SearchPoLinesAsync(string trxType, string? searchText, CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> AllocateLotAsync(
        string iCode,
        IReadOnlyCollection<string>? reservedInSave = null,
        int? excludeBatchNo = null,
        CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> GetAsync(int batchNo, CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> SaveNewAsync(IvGoodsReceiptSaveRequest request, CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> UpdateAsync(int batchNo, IvGoodsReceiptSaveRequest request, CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> DeleteAsync(IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> PostAsync(IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default);
    Task<IvGoodsReceiptOperationResult> RollbackAsync(IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default);
}
