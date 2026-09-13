namespace ErpWeb.Model.Entities.Purchase;

public class PoOrder
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public DateTime? PoDate { get; set; }
    public string? Buyer { get; set; }
    public string? PoType { get; set; }
    public bool? OneTime { get; set; }
    public string? VendCode { get; set; }
    public string? VendName { get; set; }
    public string? VendAddress1 { get; set; }
    public string? VendAddress2 { get; set; }
    public string? VendAddress3 { get; set; }
    public string? VendAddress4 { get; set; }
    public string? VendCity { get; set; }
    public string? VendState { get; set; }
    public string? VendPostal { get; set; }
    public string? VendCountryCode { get; set; }
    public string? VendTel { get; set; }
    public string? VendFax { get; set; }
    public string? CurCode { get; set; }
    public string? ShipCode { get; set; }
    public string? TermCode { get; set; }
    public string? ContactPerson { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipAddress4 { get; set; }
    public string? ShipCity { get; set; }
    public string? ShipState { get; set; }
    public string? ShipPostal { get; set; }
    public string? ShipCountryCode { get; set; }
    public string? ShipTel { get; set; }
    public string? ShipFax { get; set; }
    public string? TaxGrpCode { get; set; }
    public decimal TaxPercentage { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Discount { get; set; }
    public string? SiRemark { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? RegNo { get; set; }
    public string? DeptCode { get; set; }
    public bool? OneTimeItemYn { get; set; }
    public string? VCode { get; set; }
    public string? BuyingTerm { get; set; }
    public string? DoNo { get; set; }
    public string? InvNo { get; set; }
    public string? CostCode { get; set; }
    public string? LocationCode { get; set; }
    public bool? PoCosting { get; set; }
    public string? ProjId { get; set; }
    public string? Type { get; set; }
    public string? Prefix { get; set; }
    public string? CheckBy { get; set; }
    public DateTime? CheckOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? AuthorisedBy { get; set; }
    public bool FinClosed { get; set; }
    public DateTime? FinClosedOn { get; set; }
    public string? FinClosedBy { get; set; }
    public string? CloseReason { get; set; }
    public string? ClosedBy { get; set; }
    public DateTime? ClosedOn { get; set; }
    public string? QuatationNo { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public int? PrintCounter { get; set; }
    public string? HdrType { get; set; }
    public string? Ref1 { get; set; }
    public string? Ref2 { get; set; }
    public string? Ref3 { get; set; }
    public string? Ref4 { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoOrderDetail> Details { get; set; } = new List<PoOrderDetail>();
}
