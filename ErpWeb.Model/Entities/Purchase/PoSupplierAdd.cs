namespace ErpWeb.Model.Entities.Purchase;

public class PoSupplierAdd
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string SuppCode { get; set; } = string.Empty;
    public int Line { get; set; }
    public string? SuppName { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? Address4 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Fax { get; set; }

    public PoSupplier Supplier { get; set; } = null!;
}
