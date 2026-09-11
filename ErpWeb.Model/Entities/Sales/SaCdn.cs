namespace ErpWeb.Model.Entities.Sales;

public class SaCdn
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? InvNo { get; set; }
    /// <summary>
    /// Genuine-looking but never validated (R10.4): the write path persists this field without
    /// checking that the DO exists or belongs to the same customer. Do not rely on it.
    /// </summary>
    public string? DoNo { get; set; }
    public string Type { get; set; } = string.Empty;
    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; set; }
    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvAddress4 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Fax { get; set; }
    public string? PayCode { get; set; }
    public string? Currency { get; set; }
    public string? TaxGrCode { get; set; }
    public string? Remarks { get; set; }
    public decimal GrossAmnt { get; set; }
    public decimal Taxes { get; set; }
    public decimal TotAmnt { get; set; }
    public string? Prefix { get; set; }
    public string? LocationCode { get; set; }
    public string? ProjId { get; set; }
    public string? SalesRep { get; set; }
    public string? Dept { get; set; }
    public bool? ExportStatus { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? RefNo { get; set; }
    public string? ExternalDocNo { get; set; }
    public bool ReturnStock { get; set; }

    public DateTime? IrnmCancelOn { get; set; }
    public string? IrbmSubmitId { get; set; }
    public string? IrbmUuid { get; set; }
    public string? IrbmOriUuid { get; set; }
    public DateTime? IrbmSentOn { get; set; }
    public DateTime? IrbmValidOn { get; set; }
    public string? IrbmError { get; set; }
    public string? IrbmStatus { get; set; }

    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? RollbackDate { get; set; }
    public string? RollbackBy { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SaCdnDetail> Details { get; set; } = new List<SaCdnDetail>();
}
