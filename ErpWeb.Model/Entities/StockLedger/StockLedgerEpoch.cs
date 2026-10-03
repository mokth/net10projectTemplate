namespace ErpWeb.Model.Entities.StockLedger;

public sealed class StockLedgerEpoch
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public long CutoverPostingSequence { get; set; }
    public int Version { get; set; } = 2;
    public string Status { get; set; } = StockLedgerEpochStatuses.Prepared;
    public Guid MigrationBatchId { get; set; }
    public string ReconciliationManifestHash { get; set; } = string.Empty;
    public DateTime? ActivatedAtUtc { get; set; }
    public string? ActivatedBy { get; set; }

    public ICollection<StockPosting> Postings { get; set; } = new List<StockPosting>();
}

public static class StockLedgerEpochStatuses
{
    public const string Prepared = "PREPARED";
    public const string Active = "ACTIVE";
    public const string Retired = "RETIRED";
}
