namespace ErpWeb.Model.Entities.StockLedger;

public sealed class StockPostingBranchSequence
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long LastSequence { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
