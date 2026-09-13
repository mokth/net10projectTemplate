namespace ErpWeb.Model.Repositories.Purchase;

public sealed class PoSupplierSearchArgs
{
    public string? SearchText { get; set; }
    public bool? IsActive { get; set; }
    public string? SuppType { get; set; }
    public string? CategoryCode { get; set; }
    public string? AreaCode { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}
