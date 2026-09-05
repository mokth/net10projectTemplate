namespace ErpWeb.Model.Entities.Sales;

public class SaInvoice
{
    public string CompanyCode { get; set; } = string.Empty;
    public string InvNo { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string? LocationCode { get; set; }
    public string CustCode { get; set; } = string.Empty;
    public DateTime InvDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DoNo { get; set; } = string.Empty;
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public decimal GrossAmnt { get; set; }
    public decimal Taxes { get; set; }
    public decimal TotAmnt { get; set; }

    public string? InvPrefix { get; set; }
    public string? PayCode { get; set; }
    public string? TaxGrCode { get; set; }
    public string? SalesmanCode { get; set; }
    public string? PoNo { get; set; }
    public string? Remark { get; set; }
    public string? CustName { get; set; }

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

    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? RollbackDate { get; set; }
    public string? RollbackBy { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SaInvoiceDetail> Details { get; set; } = new List<SaInvoiceDetail>();
}
