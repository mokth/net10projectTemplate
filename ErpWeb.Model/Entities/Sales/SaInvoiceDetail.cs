namespace ErpWeb.Model.Entities.Sales;

public class SaInvoiceDetail
{
    public int Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string InvNo { get; set; } = string.Empty;
    public int Line { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal Qty { get; set; }
    public decimal StdQty { get; set; }
    public string? StdUom { get; set; }
    public string? FrWarehouse { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount2 { get; set; }
    public decimal ItemDiscount3 { get; set; }
    public decimal ItemDiscount4 { get; set; }
    public decimal ItemDiscount5 { get; set; }
    public decimal ItemDiscount6 { get; set; }
    public decimal ItemDiscAmount { get; set; }
    public decimal ItemDiscAmount1 { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGrCode { get; set; }
    public decimal TaxAmt { get; set; }
    public decimal NetAmount { get; set; }
    public decimal LocalAmount { get; set; }
    public string? OrderType { get; set; }
    public bool StockControl { get; set; } = true;
    public string? SellingGlCode { get; set; }
    public string? Remarks { get; set; }

    public SaInvoice Invoice { get; set; } = null!;
}
