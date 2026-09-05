namespace ErpWeb.Model.Entities.CustomerProfile;

public class SaCust
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustCode { get; set; } = string.Empty;
    public string CustName { get; set; } = string.Empty;
    public string? CustShortName { get; set; }
    public string? CustType { get; set; }
    public string? InvoicePrefix { get; set; }
    public string? CustGroupCode { get; set; }
    public bool? LmwAts { get; set; }
    public string? SalesmanCode { get; set; }
    public string? AreaCode { get; set; }
    public string? SubGroupCode { get; set; }
    public string? IndustryCode { get; set; }
    public string? ChannelCode { get; set; }

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
    public string? CjLmw { get; set; }
    public string? CustBrn { get; set; }
    public string? RegType { get; set; }
    public string? Remark { get; set; }

    public string? GstregNo { get; set; }
    public string? PayCode { get; set; }
    public string? Currency { get; set; }
    public bool? Taxable { get; set; }
    public string? TaxGrCode { get; set; }
    public string? GroupDiscount { get; set; }
    public string? DiscountMethod { get; set; }
    public string? PriceMethod { get; set; }
    public string? AgingType { get; set; }
    public decimal? PaidUpCapital { get; set; }
    public string? GlCode { get; set; }
    public decimal? OpeningAmount { get; set; }
    public string? CreditTerm { get; set; }
    public decimal? CreditLimit { get; set; }
    public string? CustPriceCode { get; set; }

    public string? ContactPerson { get; set; }
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactTelp { get; set; }
    public string? ContactFax { get; set; }

    public bool? AppShip { get; set; }
    public bool? AppInvoice { get; set; }
    public bool IsActive { get; set; } = true;
    public bool? DecPoint { get; set; }

    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipCity { get; set; }
    public string? ShipState { get; set; }
    public string? ShipPostalCode { get; set; }
    public string? ShipCountry { get; set; }
    public string? ShipTel { get; set; }
    public string? ShipFax { get; set; }
    public string? ShipTelex { get; set; }
    public string? ShipEmail { get; set; }
    public string? ShipWebsite { get; set; }

    public string? InvName { get; set; }
    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvCity { get; set; }
    public string? InvState { get; set; }
    public string? InvPostalCode { get; set; }
    public string? InvCountry { get; set; }
    public string? InvTel { get; set; }
    public string? InvFax { get; set; }
    public string? InvTelex { get; set; }
    public string? InvEmail { get; set; }
    public string? InvWebsite { get; set; }

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SaCustAdd> Addresses { get; set; } = new List<SaCustAdd>();
    public ICollection<SaCustContact> Contacts { get; set; } = new List<SaCustContact>();
}
