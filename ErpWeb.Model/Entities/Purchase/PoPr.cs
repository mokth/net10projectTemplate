namespace ErpWeb.Model.Entities.Purchase;

public class PoPr
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string PrNo { get; set; } = string.Empty;
    public DateTime CreateDt { get; set; }
    public string? Requester { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? DeptCode { get; set; }
    public string? CheckedBy { get; set; }
    public string? AuthorisedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public string? Remarks { get; set; }
    public string? AuthorisedBy2nd { get; set; }
    public string? PrType { get; set; }
    public string? LocationCode { get; set; }
    public string? PoNo { get; set; }
    public string? ApprReason { get; set; }
    public string? ProjId { get; set; }
    public string? ApprovedBy2 { get; set; }
    public string? ApprovedDate2 { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoPrDetail> Details { get; set; } = new List<PoPrDetail>();
}
