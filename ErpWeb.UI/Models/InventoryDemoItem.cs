namespace ErpWeb.UI.Models;

public class InventoryDemoItem
{
    public int Id { get; set; }
    public string StockCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Uom { get; set; } = string.Empty;
    public decimal OnHand { get; set; }
    public decimal UnitCost { get; set; }
    public bool Active { get; set; }
    public DateTime UpdatedOn { get; set; }
}
