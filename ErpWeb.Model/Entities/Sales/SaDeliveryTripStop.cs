namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryTripStop
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string TripNo { get; set; } = string.Empty;
    public long StopId { get; set; }
    public int StopSequence { get; set; }
    public string CustCode { get; set; } = string.Empty;
    public string? CustNameSnapshot { get; set; }
    public string? ShipNameSnapshot { get; set; }
    public string? ShipAddress1Snapshot { get; set; }
    public string? ShipAddress2Snapshot { get; set; }
    public string? ShipAddress3Snapshot { get; set; }
    public string? ShipAddress4Snapshot { get; set; }
    public string? ShipCitySnapshot { get; set; }
    public string? ShipStateSnapshot { get; set; }
    public string? ShipPostalCodeSnapshot { get; set; }
    public string? ShipCountrySnapshot { get; set; }
    public string? ContactNameSnapshot { get; set; }
    public string? ContactPhoneSnapshot { get; set; }
    public DateTime? PlannedArrivalAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? LastAttemptNo { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Remarks { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public SaDeliveryTrip Trip { get; set; } = null!;
    public ICollection<SaDeliveryTripStopDo> DeliveryOrders { get; set; } = new List<SaDeliveryTripStopDo>();
    public ICollection<SaDeliveryAttempt> Attempts { get; set; } = new List<SaDeliveryAttempt>();
}
