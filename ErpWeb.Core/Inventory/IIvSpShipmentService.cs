using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

public enum IvSpLineStatus
{
    Allocated = 0,
    Incomplete = 1,
    NoStock = 2,
    Insufficient = 3
}

public enum IvSpLotFailReason
{
    None = 0,
    StaleQuantity = 1,
    LotNoLongerEligible = 2,
    DuplicateLot = 3,
    QtyExceedsAvailable = 4,
    QtyExceedsLineStdQty = 5,
    QtyDoesNotMatchLineStdQty = 6
}

public enum IvSpShipmentErrorKind
{
    None = 0,
    Validation = 1,
    Concurrency = 2,
    BusinessRule = 3,
    Unexpected = 4
}

public sealed class IvSpLineResult
{
    public int SoLineNo { get; init; }
    public IvSpLineStatus Status { get; init; }
    public string? ErrorCode { get; init; }
    public decimal RequestedStdQty { get; init; }
    public decimal AllocatedStdQty { get; init; }
    public decimal? CurrentAvailableQty { get; init; }
}

public sealed class IvSpLotResult
{
    public int SoLineNo { get; init; }
    public int? FromBalLocId { get; init; }
    public string? ICode { get; init; }
    public string? FrWarehouse { get; init; }
    public string? FrLocation { get; init; }
    public string? FrLotNo { get; init; }
    public string? IStatus { get; init; }
    public decimal FrStdQty { get; init; }
    public decimal? CurrentAvailableQty { get; init; }
    public IvSpLotFailReason FailReason { get; init; }
}

public sealed class IvSpShipmentResult
{
    public bool Succeeded { get; init; }
    public bool HasIncompleteLines { get; init; }
    public string? ErrorMessage { get; init; }
    public IvSpShipmentErrorKind ErrorKind { get; init; }
    public int BatchId { get; init; }
    public int BatchNo { get; init; }
    public IReadOnlyList<IvSpLineResult> Lines { get; init; } = [];
    public IReadOnlyList<IvSpLotResult> Lots { get; init; } = [];

    public static IvSpShipmentResult Fail(
        string message,
        IvSpShipmentErrorKind kind = IvSpShipmentErrorKind.BusinessRule) =>
        new()
        {
            Succeeded = false,
            ErrorMessage = message,
            ErrorKind = kind
        };

    public static IvSpShipmentResult Ok(
        int batchId,
        int batchNo,
        IReadOnlyList<IvSpLineResult> lines,
        IReadOnlyList<IvSpLotResult> lots) =>
        new()
        {
            Succeeded = true,
            ErrorKind = IvSpShipmentErrorKind.None,
            BatchId = batchId,
            BatchNo = batchNo,
            Lines = lines,
            Lots = lots,
            HasIncompleteLines = lines.Any(x =>
                x.Status is IvSpLineStatus.Incomplete or IvSpLineStatus.NoStock or IvSpLineStatus.Insufficient
                || IvQty.Round(x.AllocatedStdQty) != IvQty.Round(x.RequestedStdQty))
        };
}

public sealed class IvSpValidatePostResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }

    public static IvSpValidatePostResult Ok() => new() { Succeeded = true };

    public static IvSpValidatePostResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvSpRequiredLine
{
    public int Line { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public decimal StdQty { get; init; }
    public string? StdUom { get; init; }
    public string FrWarehouse { get; init; } = string.Empty;
    public decimal UnitPrice { get; init; }
    public bool StockControl { get; init; }
}

public sealed class IvSpCreateOrReplaceCommand
{
    public required string CompanyCode { get; init; }
    public required string BranchCode { get; init; }
    public required string LocationCode { get; init; }
    public required string UserId { get; init; }
    public required string InvNo { get; init; }
    public required DateTime InvDate { get; init; }
    public required IReadOnlyList<IvSpRequiredLine> RequiredLines { get; init; }
    public bool OverwriteExisting { get; init; }

    /// <summary>Test-only: after old SP details removed, before new details staged.</summary>
    public Action? AfterSpDelete { get; init; }
}

public sealed class IvSpShipmentEditQuery
{
    public required string CompanyCode { get; init; }
    public required string BranchCode { get; init; }
    public required string LocationCode { get; init; }
    public required string InvNo { get; init; }
    public int SoLineNo { get; init; }
    public required DateTime InvDate { get; init; }
    public required string ICode { get; init; }
    public required string FrWarehouse { get; init; }
    public decimal RequestedStdQty { get; init; }
}

public sealed class IvSpSubmittedLot
{
    public int FromBalLocId { get; init; }
    public decimal IssueQty { get; init; }
}

public sealed class IvSpReplaceLineCommand
{
    public required string CompanyCode { get; init; }
    public required string BranchCode { get; init; }
    public required string LocationCode { get; init; }
    public required string UserId { get; init; }
    public required string InvNo { get; init; }
    public required DateTime InvDate { get; init; }
    public int SoLineNo { get; init; }
    public required string ICode { get; init; }
    public string? IDesc { get; init; }
    public decimal PersistedStdQty { get; init; }
    public string? StdUom { get; init; }
    public required string FrWarehouse { get; init; }
    public decimal UnitPrice { get; init; }
    public required IReadOnlyList<IvSpSubmittedLot> Lots { get; init; }
}

public sealed class IvSpValidatePostQuery
{
    public required string CompanyCode { get; init; }
    public required string BranchCode { get; init; }
    public required string LocationCode { get; init; }
    public required string InvNo { get; init; }
    public required DateTime InvDate { get; init; }
    public required IvTrxBatch Batch { get; init; }
    public required IReadOnlyList<IvTrxBatchDetail> Details { get; init; }
    public required IReadOnlyList<SaInvoiceDetail> InvoiceLines { get; init; }
    public required IReadOnlyDictionary<int, IvBalLocLockResult> LockedBalances { get; init; }
}

public interface IIvSpShipmentService
{
    Task<IvSpShipmentResult> CreateOrReplaceShipmentAsync(
        AppDbContext db,
        IvSpCreateOrReplaceCommand command,
        CancellationToken cancellationToken = default);

    Task<IvSpShipmentResult> GetShipmentEditAsync(
        AppDbContext db,
        IvSpShipmentEditQuery query,
        CancellationToken cancellationToken = default);

    Task<IvSpShipmentResult> ReplaceShipmentLineAsync(
        AppDbContext db,
        IvSpReplaceLineCommand command,
        CancellationToken cancellationToken = default);

    Task<IvSpValidatePostResult> ValidateShipmentForPostAsync(
        AppDbContext db,
        IvSpValidatePostQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lock released FromBalLocIds (slice-sorted) then remove SP details and optionally the batch.
    /// Caller owns SaveChanges.
    /// </summary>
    Task ReleaseShipmentReservationAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string locationCode,
        string invNo,
        bool removeBatch,
        CancellationToken cancellationToken = default);
}
