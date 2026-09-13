namespace ErpWeb.Model.Entities.Purchase;

public class PoPrDetail
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string PrNo { get; set; } = string.Empty;
    public short Line { get; set; }
    public DateTime? EtaDt { get; set; }
    public bool? OneTimeItemYn { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? Category { get; set; }
    public decimal Qty { get; set; }
    public decimal PackSz { get; set; }
    public string? StdUom { get; set; }
    public decimal PurchaseQty { get; set; }
    public string? PurchaseUom { get; set; }
    public string? Currency { get; set; }
    public decimal UnitPrice { get; set; }
    public bool? OneTimeVendor { get; set; }
    public string? VendorCd { get; set; }
    public string? VendNm { get; set; }
    public string? Purpose { get; set; }
    public string? Status { get; set; }
    public decimal StdQty { get; set; }
    public decimal WtQty { get; set; }
    public string? WtUom { get; set; }
    public string? PaymentTerm { get; set; }
    public string? BuyingTerm { get; set; }
    public string? RepairType { get; set; }
    public decimal Amount { get; set; }
    public string? PoNo { get; set; }
    public string? TaxGroup { get; set; }
    public decimal TaxAmount { get; set; }
    public bool IsInclusive { get; set; }
    public string? ToWarehouse { get; set; }
    public string? SoNo { get; set; }
    public int? SoLine { get; set; }

    public PoPr Pr { get; set; } = null!;
}
