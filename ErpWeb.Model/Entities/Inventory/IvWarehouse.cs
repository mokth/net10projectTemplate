namespace ErpWeb.Model.Entities.Inventory;

public class IvWarehouse
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public string? WarehouseDesc { get; set; }
    public string? WarehouseType { get; set; }
    public string? WarehouseRemark { get; set; }
    public string? LocationCode { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<IvBalLoc> Balances { get; set; } = new List<IvBalLoc>();
    public ICollection<IvLocation> Locations { get; set; } = new List<IvLocation>();
}
