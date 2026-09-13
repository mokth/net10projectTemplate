namespace ErpWeb.Model.Entities.Purchase;

public class PoDesc
{
    public string CompanyCode { get; set; } = string.Empty;
    public string ItemDesc { get; set; } = string.Empty;
    public string? DeptCode { get; set; }
    public decimal? UnitPrice { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
