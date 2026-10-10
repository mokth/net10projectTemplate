namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryTripStopDo
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string TripNo { get; set; } = string.Empty;
    public long StopId { get; set; }
    public string DoNo { get; set; } = string.Empty;
    public DateTime? PromisedDeliveryDateSnapshot { get; set; }
    public TimeSpan? PromisedFromTimeSnapshot { get; set; }
    public TimeSpan? PromisedToTimeSnapshot { get; set; }
    public DateTime DoDateSnapshot { get; set; }
    public bool IsActiveAssignment { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public SaDeliveryTripStop Stop { get; set; } = null!;
    public SaDo Do { get; set; } = null!;
}
