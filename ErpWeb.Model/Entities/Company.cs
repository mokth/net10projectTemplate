namespace ErpWeb.Model.Entities;

public class Company
{
    public int CompanyId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string? RegistrationNo { get; set; }
    public string? TaxNo { get; set; }

    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostCode { get; set; }
    public string? Country { get; set; }

    public string? LogoUrl { get; set; }
    public string? CurrencyCode { get; set; }
    public string? TimeZoneId { get; set; }
    public byte? FiscalYearStartMonth { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}
