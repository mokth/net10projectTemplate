namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryExceptionReason
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Responsibility { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
