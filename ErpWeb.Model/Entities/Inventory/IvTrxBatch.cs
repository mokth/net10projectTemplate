namespace ErpWeb.Model.Entities.Inventory;

public class IvTrxBatch
{
    public int Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public int BatchNo { get; set; }
    public DateTime TrxDtTime { get; set; }
    public string TrxType { get; set; } = string.Empty;
    public string BatchStatus { get; set; } = string.Empty;
    public string? RefNo { get; set; }
    public string? Remarks { get; set; }
    public string? LocationCode { get; set; }
    /// <summary>SHA-256 hex of CN stock-return snapshot for CN-generated CR batches.</summary>
    public string? SourceFingerprint { get; set; }

    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? RollbackDate { get; set; }
    public string? RollbackBy { get; set; }
    public int PostedCount { get; set; }
    public int RollbackCount { get; set; }
    public Guid? PostingOperationId { get; set; }
    public Guid? RollbackOperationId { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public ICollection<IvTrxBatchDetail> Details { get; set; } = new List<IvTrxBatchDetail>();
}
