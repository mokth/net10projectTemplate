namespace ErpWeb.Model.Entities.CustomerProfile;

public class SaDisGroup
{
    public string CompanyCode { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string PayCode { get; set; } = string.Empty;
    public short? GroupLevel { get; set; }
    public double? Discount { get; set; }
    public double? Discount2 { get; set; }
    public double? Discount3 { get; set; }
    public string? DiscountType { get; set; }
    public string? GroupStatus { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public ICollection<SaDisCust> Members { get; set; } = new List<SaDisCust>();
}
