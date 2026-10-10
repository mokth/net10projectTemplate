namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryDriver
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string? MobileNo { get; set; }
    public string DriverType { get; set; } = string.Empty;
    public string? EmployeeCode { get; set; }
    public string? TransporterName { get; set; }
    public bool Active { get; set; } = true;
    public string? Remarks { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
