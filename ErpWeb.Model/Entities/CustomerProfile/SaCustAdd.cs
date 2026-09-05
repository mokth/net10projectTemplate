namespace ErpWeb.Model.Entities.CustomerProfile;

public class SaCustAdd
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustCode { get; set; } = string.Empty;
    public int Line { get; set; }
    public string? AddName { get; set; }
    public string? DeliverTo { get; set; }
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

    public SaCust? Customer { get; set; }
}
