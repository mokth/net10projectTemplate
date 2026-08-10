using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Inventory;

public sealed class CreateDocumentDto
{
    public DocumentType DocType { get; set; }
    public DateTime DocDate { get; set; }
    public long? WarehouseId { get; set; }
    public long? SourceWarehouseId { get; set; }
    public long? DestinationWarehouseId { get; set; }
    public long? SourceLocationId { get; set; }
    public long? DestinationLocationId { get; set; }
    public string? ReferenceNo { get; set; }
    public string? Remarks { get; set; }
    public bool AllowZeroCost { get; set; }
    public long? StockTakeId { get; set; }
    public long? ReversalOfDocumentId { get; set; }
    public IList<CreateDocumentLineDto> Lines { get; set; } = [];
}

public sealed class CreateDocumentLineDto
{
    public long ItemVariantId { get; set; }
    public long UOMId { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitCost { get; set; }
    public long LocationId { get; set; }
    public string? LotNo { get; set; }
    public long? LotId { get; set; }
    public IList<LotAllocationInput>? LotAllocations { get; set; }
    public AdjustmentDirection? Direction { get; set; }
    public long? ReasonCodeId { get; set; }
    public string? Remarks { get; set; }
}

public sealed class LotAllocationInput
{
    public long LotId { get; set; }
    public string? LotNo { get; set; }
    public decimal Qty { get; set; }
}

public sealed class DocumentDto
{
    public long Id { get; set; }
    public string DocNo { get; set; } = null!;
    public DocumentType DocType { get; set; }
    public DateTime DocDate { get; set; }
    public DocumentStatus Status { get; set; }
    public long? WarehouseId { get; set; }
    public long? SourceWarehouseId { get; set; }
    public long? DestinationWarehouseId { get; set; }
    public long? SourceLocationId { get; set; }
    public long? DestinationLocationId { get; set; }
    public string? ReferenceNo { get; set; }
    public string? Remarks { get; set; }
    public bool AllowZeroCost { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public long? ReversalOfDocumentId { get; set; }
    public long? StockTakeId { get; set; }
    public IList<DocumentLineDto> Lines { get; set; } = [];
}

public sealed class DocumentLineDto
{
    public long Id { get; set; }
    public int LineNo { get; set; }
    public long ItemVariantId { get; set; }
    public long UOMId { get; set; }
    public decimal Qty { get; set; }
    public decimal ConversionRateUsed { get; set; }
    public decimal QtyInBase { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal? TotalCost { get; set; }
    public long LocationId { get; set; }
    public string? LotNo { get; set; }
    public long? LotId { get; set; }
    public AdjustmentDirection? Direction { get; set; }
    public long? ReasonCodeId { get; set; }
    public string? Remarks { get; set; }
}

public sealed class PostingResultDto
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DocumentDto? Document { get; init; }
    public IReadOnlyList<long> LedgerIds { get; init; } = [];

    public static PostingResultDto Ok(DocumentDto document, IReadOnlyList<long> ledgerIds) =>
        new() { Succeeded = true, Document = document, LedgerIds = ledgerIds };

    public static PostingResultDto Fail(string code, string? message = null) =>
        new() { Succeeded = false, ErrorCode = code, ErrorMessage = message ?? code };
}

public interface IPostingEngine
{
    Task<PostingResultDto> CreateDocumentAsync(CreateDocumentDto dto, CancellationToken ct = default);
    Task<PostingResultDto> SubmitForApprovalAsync(long documentId, CancellationToken ct = default);
    Task<PostingResultDto> ApproveAsync(long documentId, string approvedBy, CancellationToken ct = default);
    Task<PostingResultDto> PostAsync(long documentId, string postedBy, CancellationToken ct = default);
    Task<PostingResultDto> CancelAsync(long documentId, CancellationToken ct = default);
    Task<PostingResultDto> ReverseAsync(long documentId, string reversedBy, CancellationToken ct = default);
    Task<DocumentDto?> GetDocumentAsync(long documentId, CancellationToken ct = default);
}

public interface IStockTakeService
{
    Task<PostingResultDto> CreateAsync(DateTime countDate, long warehouseId, IList<StockTakeLineInput> lines, CancellationToken ct = default);
    Task<PostingResultDto> StartCountingAsync(long stockTakeId, CancellationToken ct = default);
    Task<PostingResultDto> CompleteCountingAsync(long stockTakeId, IList<StockTakeLineInput> countedLines, CancellationToken ct = default);
    Task<PostingResultDto> SubmitForApprovalAsync(long stockTakeId, CancellationToken ct = default);
    Task<PostingResultDto> ApproveAsync(long stockTakeId, string approvedBy, CancellationToken ct = default);
    Task<PostingResultDto> GenerateAdjustmentAsync(long stockTakeId, CancellationToken ct = default);
    Task<PostingResultDto> PostGeneratedAdjustmentAsync(long stockTakeId, string postedBy, CancellationToken ct = default);
    Task<StockTake?> GetAsync(long stockTakeId, CancellationToken ct = default);
}

public sealed class StockTakeLineInput
{
    public long ItemVariantId { get; set; }
    public long LocationId { get; set; }
    public long? LotId { get; set; }
    public decimal SystemQty { get; set; }
    public decimal CountedQty { get; set; }
    public long? ReasonCodeId { get; set; }
}
