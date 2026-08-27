namespace ErpWeb.Model.Entities.Inventory;

public class IvType
{
    public int Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string TypeCode { get; set; } = string.Empty;
    public string? TypeName { get; set; }
    public string? TypeDesc { get; set; }
    public bool KeepStock { get; set; } = true;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
