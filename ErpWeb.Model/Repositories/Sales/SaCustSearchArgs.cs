namespace ErpWeb.Model.Repositories.Sales;

public sealed class SaCustSearchArgs
{
    public string? SearchText { get; set; }
    public bool? IsActive { get; set; }
    public string? CustType { get; set; }
    public string? CustGroupCode { get; set; }
    public string? SalesmanCode { get; set; }
    public string? AreaCode { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}
