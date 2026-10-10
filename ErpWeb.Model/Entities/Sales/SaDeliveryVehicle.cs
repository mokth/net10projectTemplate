namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryVehicle
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string VehicleId { get; set; } = string.Empty;
    public string RegistrationNo { get; set; } = string.Empty;
    public string VehicleType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal? CapacityWeight { get; set; }
    public decimal? CapacityVolume { get; set; }
    public string TransportType { get; set; } = string.Empty;
    public string? TransporterName { get; set; }
    public bool Active { get; set; } = true;
    public string? Remarks { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
