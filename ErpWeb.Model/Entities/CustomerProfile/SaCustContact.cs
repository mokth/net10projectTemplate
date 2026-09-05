namespace ErpWeb.Model.Entities.CustomerProfile;

public class SaCustContact
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustCode { get; set; } = string.Empty;
    public int Line { get; set; }
    public string? ContactPerson { get; set; }
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactTelp { get; set; }
    public string? ContactFax { get; set; }

    public SaCust? Customer { get; set; }
}
