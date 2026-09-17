namespace ErpWeb.Model.Entities.Sales;

public class SaCdnDetail
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public short Line { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? CustICode { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public string? SellingUom { get; set; }
    public string? StdUom { get; set; }
    public string? WtUom { get; set; }
    public decimal StdQty { get; set; }
    public decimal WtQty { get; set; }
    public decimal StdCustPsize { get; set; }
    public decimal TaxAmt { get; set; }
    public decimal Amount { get; set; }
    public string? CustPo { get; set; }
    public string? Remarks { get; set; }
    public string? ItemGlCode { get; set; }
    public string? TaxGroup { get; set; }
    public bool IsInclusive { get; set; }
    public decimal Discount { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public decimal ItemDiscount2 { get; set; }
    public decimal ItemDiscount3 { get; set; }
    public decimal ItemDiscount4 { get; set; }
    public decimal ItemDiscount5 { get; set; }
    public decimal ItemDiscount6 { get; set; }
    public string? IDiscountType { get; set; }
    public string? IDiscountType1 { get; set; }
    public decimal NetAmount { get; set; }
    public decimal CostPrice { get; set; }
    public string? Classification { get; set; }

    public string? FrWarehouse { get; set; }
    public string? LocCode { get; set; }
    public string? IStatus { get; set; }
    public string? LotNo { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public bool StockControl { get; set; }

    /// <summary>WHICH price source produced <see cref="UnitPrice"/>; the persisted token, e.g. <c>CUSTOMER_ITEM</c>.</summary>
    public string? PricingSource { get; set; }

    /// <summary>Readable reference behind <see cref="PricingSource"/> (<c>PL1</c>, <c>MOQ=100</c>, <c>QTY 10-99</c>).</summary>
    public string? PricingRef { get; set; }

    /// <summary>Phase 4: the price the ENGINE resolved; NULL unless an operator overrode it.</summary>
    public decimal? OriginalUnitPrice { get; set; }

    /// <summary>Phase 4: why the resolved price was changed. Required by the service when an override exists.</summary>
    public string? OverrideReason { get; set; }

    public SaCdn Cdn { get; set; } = null!;
}
