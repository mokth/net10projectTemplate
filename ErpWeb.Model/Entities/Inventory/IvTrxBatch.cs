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
    /// <summary>Supplier code snapshot for Misc Receipt (MR). Null for other trx types / legacy rows.</summary>
    public string? VendCode { get; set; }
    /// <summary>Supplier name snapshot at MR save. Null for other trx types / legacy rows.</summary>
    public string? VendName { get; set; }
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

    /// <summary>
    /// Force-close stamp (R4). Non-null marks a DO-shipment batch retained after its delivery order
    /// was force-closed; <see cref="BatchStatus"/> deliberately stays <c>POSTED</c> and this stamp is
    /// the tombstone. Every SP-batch mutation path must reject a batch where this is set (plan §5.4).
    /// </summary>
    public DateTime? ForceCloseDate { get; set; }
    public string? ForceCloseBy { get; set; }
    public string? ForceCloseReason { get; set; }

    /// <summary>True when this batch has been stamped by a DO force-close and is now immutable.</summary>
    public bool IsForceClosed => ForceCloseDate is not null;

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public ICollection<IvTrxBatchDetail> Details { get; set; } = new List<IvTrxBatchDetail>();
}
