namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryPodAttachment
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long AttachmentId { get; set; }
    public long AttemptId { get; set; }
    public string? DoNo { get; set; }
    public string AttachmentType { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public SaDeliveryAttempt Attempt { get; set; } = null!;
    public SaDo? Do { get; set; }
}
