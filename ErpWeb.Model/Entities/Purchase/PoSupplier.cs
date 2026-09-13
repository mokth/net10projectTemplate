namespace ErpWeb.Model.Entities.Purchase;

public class PoSupplier
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string SuppCode { get; set; } = string.Empty;
    public string SuppName { get; set; } = string.Empty;
    public string? SuppShortName { get; set; }
    public string? SuppType { get; set; }

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
    public string? Telex { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    public string? GstregNo { get; set; }
    public string? PayCode { get; set; }
    public string? Currency { get; set; }
    public string? GlCode { get; set; }
    public string? ContactPerson { get; set; }
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactTelp { get; set; }
    public string? ContactFax { get; set; }
    public bool? Taxable { get; set; }
    public string? TaxGrCode { get; set; }
    public bool IsActive { get; set; } = true;
    public bool? Suspend { get; set; }
    public string? CategoryCode { get; set; }

    public string? ContactPerson2 { get; set; }
    public string? Title2 { get; set; }
    public string? Department2 { get; set; }
    public string? ContactEmail2 { get; set; }
    public string? ContactTelp2 { get; set; }
    public string? ContactFax2 { get; set; }
    public string? ContactPerson3 { get; set; }
    public string? Title3 { get; set; }
    public string? Department3 { get; set; }
    public string? ContactEmail3 { get; set; }
    public string? ContactTelp3 { get; set; }
    public string? ContactFax3 { get; set; }
    public string? ContactPerson4 { get; set; }
    public string? Title4 { get; set; }
    public string? Department4 { get; set; }
    public string? ContactEmail4 { get; set; }
    public string? ContactTelp4 { get; set; }
    public string? ContactFax4 { get; set; }

    public string? PoPrefix { get; set; }
    public string? TaxGroup { get; set; }
    public string? BuyingTerm { get; set; }
    public string? SupplierBrn { get; set; }
    public string? BankName { get; set; }
    public string? AccountNo { get; set; }
    public string? Remark { get; set; }
    public bool? Lmw { get; set; }
    public string? CreditorGroup { get; set; }
    public string? CreditorSubGroup { get; set; }
    public decimal? CreditLimit { get; set; }
    public string? AreaCode { get; set; }
    public string? AgingType { get; set; }
    public string? StatementType { get; set; }
    public string? TinNo { get; set; }
    public string? RegType { get; set; }
    public string? StateCode { get; set; }
    public string? CountryCode { get; set; }
    public string? MiscCode { get; set; }
    public string? BizDesc { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoSupplierAdd> Addresses { get; set; } = new List<PoSupplierAdd>();
}
