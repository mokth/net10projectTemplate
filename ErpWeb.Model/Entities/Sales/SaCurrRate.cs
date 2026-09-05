namespace ErpWeb.Model.Entities.Sales;

public class SaCurrRate
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string CurrCode { get; set; } = string.Empty;
    public double HomeCurPerUnit { get; set; }
    public bool Status { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}
