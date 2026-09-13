namespace ErpWeb.Model.Entities.Purchase;

public class PoOrderDetail
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public short Line { get; set; }
    public string? PrNo { get; set; }
    public short? PrLineNo { get; set; }
    public bool? OneTime { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal PoUnitPrice { get; set; }
    public decimal PoQty { get; set; }
    public decimal PoPurQty { get; set; }
    public decimal WtQty { get; set; }
    public decimal Amount { get; set; }
    public decimal RecvQty { get; set; }
    public decimal ReturnQty { get; set; }
    public decimal ReturnQtyCn { get; set; }
    public decimal BalanceQty { get; set; }
    public decimal OverRecvQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public decimal PackSz { get; set; }
    public string? StdUom { get; set; }
    public string? WtUom { get; set; }
    public string? PurchaseUom { get; set; }
    public DateTime? EtaDate { get; set; }
    public string? CurCode { get; set; }
    public string? Remarks { get; set; }
    public string? PoDesc { get; set; }
    public DateTime? RecvDate { get; set; }
    public string? RepairType { get; set; }
    public decimal Discount { get; set; }
    public decimal ItemDiscount { get; set; }
    public string? DiscountType { get; set; }
    public decimal NetAmount { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public string? DiscountType1 { get; set; }
    public string? CjNo { get; set; }
    public int? CjRelNo { get; set; }
    public string? ProjId { get; set; }
    public string? VendorPartNo { get; set; }
    public string? TaxGroup { get; set; }
    public decimal TaxAmount { get; set; }
    public bool IsInclusive { get; set; }
    public decimal M2UnitPrice { get; set; }
    public bool? Trim { get; set; }
    public string? ToWarehouse { get; set; }
    public string? Requester { get; set; }

    public PoOrder Order { get; set; } = null!;
}
