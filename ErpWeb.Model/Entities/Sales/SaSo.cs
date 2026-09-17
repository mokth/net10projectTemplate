namespace ErpWeb.Model.Entities.Sales;

public class SaSo
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string SoNo { get; set; } = string.Empty;
    /// <summary>Revision number (UI: Revision). Never reused within a SoNo chain.</summary>
    public short CustRel { get; set; } = 1;
    /// <summary>True only for the live revision of this SoNo.</summary>
    public bool IsCurrent { get; set; } = true;
    /// <summary>Chain-level high-water revision. Only the current row is authoritative.</summary>
    public short LastCustRel { get; set; } = 1;
    /// <summary>Why this revision was created (not why previous was superseded). Rev 1 is null.</summary>
    public string? RevisionReason { get; set; }
    public DateTime SoDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string FulfillmentStatus { get; set; } = "NONE";
    public string BillingStatus { get; set; } = "NONE";
    public string? ClosedReason { get; set; }
    public DateTime? ClosedDate { get; set; }
    public string? ClosedBy { get; set; }
    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; set; }
    public string? CustPo { get; set; }
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
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; set; }
    public string? Remarks { get; set; }
    public decimal GrossAmnt { get; set; }
    public decimal Taxes { get; set; }
    public decimal TotAmnt { get; set; }
    public string? Prefix { get; set; }
    public string? LocationCode { get; set; }
    public decimal CustDiscount { get; set; }
    public string? ContactPerson { get; set; }
    public string? ProjId { get; set; }
    public string? SalesRep { get; set; }
    public string? Ref1 { get; set; }
    public string? Ref2 { get; set; }
    public string? Ref3 { get; set; }
    public string? Ref4 { get; set; }

    /// <summary>
    /// Source quotation this SO was converted from, when it came from one. Together with
    /// <see cref="QtCustRel"/> this identifies the exact quotation revision, and a filtered unique
    /// index (<c>UX_SaSO_QtSource</c>) allows only one SO per quotation revision.
    /// </summary>
    public string? QtNo { get; set; }

    /// <summary>Revision of <see cref="QtNo"/> this SO was converted from. Null for non-quotation SOs.</summary>
    public short? QtCustRel { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SaSoDetail> Details { get; set; } = new List<SaSoDetail>();
}
