namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryAttempt
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long AttemptId { get; set; }
    public string TripNo { get; set; } = string.Empty;
    public long StopId { get; set; }
    public int AttemptNo { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? ArrivedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public string? Responsibility { get; set; }
    public string? ReceivedBy { get; set; }
    public string? ReceiverContact { get; set; }
    public string? Remark { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string RecordedBy { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public SaDeliveryTripStop Stop { get; set; } = null!;
    public SaDeliveryExceptionReason? ExceptionReason { get; set; }
    public ICollection<SaDeliveryAttemptDo> DeliveryOrders { get; set; } = new List<SaDeliveryAttemptDo>();
    public ICollection<SaDeliveryPodAttachment> Attachments { get; set; } = new List<SaDeliveryPodAttachment>();
}
