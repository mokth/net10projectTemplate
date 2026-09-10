namespace ErpWeb.Model.Entities.Sales;

public class SaSoDetail
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string SoNo { get; set; } = string.Empty;
    public short Line { get; set; }
    public short CustRel { get; set; } = 1;
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? CustICode { get; set; }
    public decimal OrderQty { get; set; }
    public decimal ShippedQty { get; set; }
    public decimal BalanceQty { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public decimal UnitPrice { get; set; }
    public string? SellingUom { get; set; }
    public string? StdUom { get; set; }
    public string? WtUom { get; set; }
    public decimal StdQty { get; set; }
    public decimal WtQty { get; set; }
    public decimal StdPsize { get; set; }
    public decimal TaxAmt { get; set; }
    public string? OrderType { get; set; }
    public string? Remarks { get; set; }
    public decimal Amount { get; set; }
    public string? ItemGlCode { get; set; }
    public decimal Discount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount2 { get; set; }
    public decimal ItemDiscount3 { get; set; }
    public decimal ItemDiscount4 { get; set; }
    public decimal ItemDiscount5 { get; set; }
    public decimal ItemDiscount6 { get; set; }
    public decimal ItemDiscAmount { get; set; }
    public decimal ItemDiscAmount1 { get; set; }
    public string? IDiscountType { get; set; }
    public string? Warehouse { get; set; }
    public string? TaxGroup { get; set; }
    public bool IsInclusive { get; set; }
    public decimal LocalAmount { get; set; }
    public bool StockControl { get; set; } = true;
    public string? Classification { get; set; }

    public SaSo So { get; set; } = null!;
}
