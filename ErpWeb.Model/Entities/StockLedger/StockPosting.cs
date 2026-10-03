namespace ErpWeb.Model.Entities.StockLedger;

public sealed class StockPosting
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long LedgerEpochId { get; set; }
    public StockLedgerEpoch? LedgerEpoch { get; set; }
    public long PostingSequence { get; set; }

    public Guid RequestId { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string SourceModule { get; set; } = string.Empty;
    public string SourceDocumentType { get; set; } = string.Empty;
    public string SourceDocumentId { get; set; } = string.Empty;
    public string SourceDocumentNo { get; set; } = string.Empty;
    public int DocumentRevision { get; set; }
    public string PostingRole { get; set; } = "PRIMARY";

    public string SourceSnapshotJson { get; set; } = string.Empty;
    public string SourceSnapshotHash { get; set; } = string.Empty;
    public int SourceSnapshotSchemaVersion { get; set; } = 1;
    public DateTime EffectiveAt { get; set; }
    public DateTime BusinessDate { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public DateTime PostedAtUtc { get; set; }
    public string PostedBy { get; set; } = string.Empty;

    public long? ProductionPostingLinkId { get; set; }
    public long? ReversesPostingId { get; set; }
    public StockPosting? ReversesPosting { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonText { get; set; }
    public DateTime? SealedAtUtc { get; set; }
}
