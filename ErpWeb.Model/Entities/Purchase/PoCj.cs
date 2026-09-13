namespace ErpWeb.Model.Entities.Purchase;

public class PoCj
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string CjNo { get; set; } = string.Empty;
    public int RelNo { get; set; }
    public DateTime? CjDtFrom { get; set; }
    public DateTime? CjDtTo { get; set; }
    public decimal CjOrderQty { get; set; }
    public decimal CjBalQty { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Remark { get; set; }
    public string? LocationCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoCjDetail> Details { get; set; } = new List<PoCjDetail>();
}
