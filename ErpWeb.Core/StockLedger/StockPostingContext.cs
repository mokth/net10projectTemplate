using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

public sealed class StockPostingContext
{
    internal StockPostingContext(
        AppDbContext db,
        StockLedgerEpoch epoch,
        StockPosting posting,
        string userId)
    {
        Db = db;
        Epoch = epoch;
        Posting = posting;
        UserId = userId;
    }

    public AppDbContext Db { get; }
    public StockLedgerEpoch Epoch { get; }
    public StockPosting Posting { get; }
    public string CompanyCode => Posting.CompanyCode;
    public string BranchCode => Posting.BranchCode;
    public string UserId { get; }
    public string CostMethod { get; internal set; } = StockCostMethods.MovingAverage;

    public void EnsureUnsealed()
    {
        if (Posting.SealedAtUtc is not null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch,
                "The posting is already sealed and cannot accept more facts."));
        if (Db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Stock writers require the coordinator transaction.");
    }
}

public sealed record StockPostingCommand
{
    public required Guid RequestId { get; init; }
    public required string CommandType { get; init; }
    public required string SourceModule { get; init; }
    public required string SourceDocumentType { get; init; }
    public required string SourceDocumentId { get; init; }
    public required string SourceDocumentNo { get; init; }
    public required int DocumentRevision { get; init; }
    public string PostingRole { get; init; } = "PRIMARY";
    public required DateTime EffectiveAt { get; init; }
    public required StockPostingEvidence Evidence { get; init; }
    public int SourceSnapshotSchemaVersion { get; init; } = 1;
    public long? ProductionPostingLinkId { get; init; }
    public long? ReversesPostingId { get; init; }
    public string? ReasonCode { get; init; }
    public string? ReasonText { get; init; }
    public IReadOnlyCollection<StockFreezeScope> FreezeScopes { get; init; } = [];
}

public sealed record StockPostingExecutionResult<T>(
    bool Succeeded,
    bool LedgerEnabled,
    bool WasReplay,
    long? StockPostingId,
    long? PostingSequence,
    T? Value,
    StockLedgerError? Error)
{
    public static StockPostingExecutionResult<T> Disabled() =>
        new(false, false, false, null, null, default,
            new(StockLedgerErrorCodes.LedgerDisabled, "Stock ledger V2 is not active for this branch."));
}
