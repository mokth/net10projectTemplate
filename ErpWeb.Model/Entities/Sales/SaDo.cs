namespace ErpWeb.Model.Entities.Sales;

public class SaDo
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DoNo { get; set; } = string.Empty;
    public DateTime DoDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string BillingStatus { get; set; } = "NONE";
    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; set; }
    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipAddress4 { get; set; }
    public string? ShipCity { get; set; }
    public string? ShipState { get; set; }
    public string? ShipPostalCode { get; set; }
    public string? ShipCountry { get; set; }
    public string? ShipTel { get; set; }
    public string? ShipFax { get; set; }
    public string? InvName { get; set; }
    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvAddress4 { get; set; }
    public string? InvCity { get; set; }
    public string? InvState { get; set; }
    public string? InvPostalCode { get; set; }
    public string? InvCountry { get; set; }
    public string? InvTel { get; set; }
    public string? InvFax { get; set; }
    public string? TaxGrCode { get; set; }
    public string? ShipVia { get; set; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; set; }
    public string? Remarks { get; set; }
    public string? Departure { get; set; }
    public string? Destination { get; set; }
    public string? Vessel { get; set; }
    public decimal GrossAmnt { get; set; }
    public decimal Taxes { get; set; }
    public decimal TotAmnt { get; set; }
    public string? Prefix { get; set; }
    public string? ShipWarehouse { get; set; }
    public string? LocationCode { get; set; }
    public decimal CustDiscount { get; set; }
    public string? ContactPerson { get; set; }
    public string? ProjId { get; set; }
    public string? SalesRep { get; set; }
    public string? Ref1 { get; set; }
    public string? Ref2 { get; set; }
    public string? Ref3 { get; set; }
    public string? Ref4 { get; set; }
    public int? PrintCounter { get; set; }
    public DateTime? ShipOutDate { get; set; }
    public string? DriverName { get; set; }
    public string? DriverPlate { get; set; }
    public DateTime? RecordTime { get; set; }
    public string? ShipOutRemark { get; set; }
    public bool? IsPack { get; set; }

    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? RollbackDate { get; set; }
    public string? RollbackBy { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SaDoDetail> Details { get; set; } = new List<SaDoDetail>();
}
