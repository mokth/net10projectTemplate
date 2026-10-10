namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryTrip
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string TripNo { get; set; } = string.Empty;
    public DateTime TripDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? DriverId { get; set; }
    public string? DriverNameSnapshot { get; set; }
    public string? DriverMobileSnapshot { get; set; }
    public string? VehicleId { get; set; }
    public string? VehicleRegistrationSnapshot { get; set; }
    public string TransportType { get; set; } = string.Empty;
    public string? TransporterNameSnapshot { get; set; }
    public DateTime? PlannedDepartureAt { get; set; }
    public DateTime? ActualDepartureAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public string? Remarks { get; set; }
    public decimal? FuelCost { get; set; }
    public decimal? TollCost { get; set; }
    public decimal? ParkingCost { get; set; }
    public decimal? OtherCost { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public SaDeliveryDriver? Driver { get; set; }
    public SaDeliveryVehicle? Vehicle { get; set; }
    public ICollection<SaDeliveryTripStop> Stops { get; set; } = new List<SaDeliveryTripStop>();
}
