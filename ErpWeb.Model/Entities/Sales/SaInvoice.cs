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
    /// <summary>
    /// Legacy column reuse (R10.7): <c>SaveNewAsync</c> stores the invoice number here
    /// (<c>DoNo = invNo</c>). This is <b>not</b> a delivery-order reference — use
    /// <see cref="SaInvoiceDetail.LinkDo"/> / <see cref="SaInvoiceDetail.DoNo"/> for DO linkage.
    /// </summary>
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

    public DateTime? DueDate { get; set; }
    public string? ArGlCode { get; set; }
    public string? InvEmail { get; set; }
    public string? BuyerTin { get; set; }
    public string? BuyerBrn { get; set; }
    public string? BuyerRegType { get; set; }
    public string? GstregNo { get; set; }
    public string? CustType { get; set; }
    public string? CustGroupCode { get; set; }
    public string? AreaCode { get; set; }
    public string? IndustryCode { get; set; }
    public string? ChannelCode { get; set; }

    /// <summary>Department code (MsDept). Optional, nvarchar(20).</summary>
    public string? Dept { get; set; }

    /// <summary>Project code (MsProject). Optional, nvarchar(20). Column is <c>ProjID</c>.</summary>
    public string? ProjId { get; set; }

    /// <summary>When the LHDN e-Invoice cancellation succeeded. Column is <c>IRNMCancelOn</c> (existing spelling).</summary>
    public DateTime? IrnmCancelOn { get; set; }
    /// <summary>MyInvois submission UID (batch). Set when the submission is accepted by the API.</summary>
    public string? IrbmSubmitId { get; set; }
    /// <summary>MyInvois document UUID.</summary>
    public string? IrbmUuid { get; set; }
    /// <summary>Original document UUID. Null on an invoice; set on CN/DN to the source invoice UUID.</summary>
    public string? IrbmOriUuid { get; set; }
    /// <summary>When MyInvois accepted the submission.</summary>
    public DateTime? IrbmSentOn { get; set; }
    /// <summary>When the mapped e-Invoice status became <c>VALID</c>.</summary>
    public DateTime? IrbmValidOn { get; set; }
    /// <summary>Short latest user-facing error. Full detail lives in <see cref="SaEInvoiceLog"/>.</summary>
    public string? IrbmError { get; set; }
    /// <summary>ERP e-Invoice lifecycle status (NEW/SUBMITTING/SUBMITTED/VALID/INVALID/FAILED/REJECTED/CANCELLED).</summary>
    public string? IrbmStatus { get; set; }
    /// <summary>When <see cref="IrbmStatus"/> is FAILED: <c>ConfirmedFailure</c> or <c>Unknown</c>.</summary>
    public string? IrbmOutcome { get; set; }

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
