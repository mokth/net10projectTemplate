namespace ErpWeb.Model.Entities.Purchase;

public class PoCjDetail
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string CjNo { get; set; } = string.Empty;
    public int RelNo { get; set; }
    public int Line { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal CjdOrderQty { get; set; }
    public decimal CjdBalQty { get; set; }

    public PoCj Cj { get; set; } = null!;
}
