namespace ErpWeb.Model.Entities.Sales;

public class SaDeliveryAttemptDo
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long AttemptId { get; set; }
    public string DoNo { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string? Remark { get; set; }

    public SaDeliveryAttempt Attempt { get; set; } = null!;
    public SaDo Do { get; set; } = null!;
}
