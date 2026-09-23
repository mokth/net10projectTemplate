namespace ErpWeb.Model.Entities.Purchase;

/// <summary>
/// One line of a self-billed purchase invoice.
/// </summary>
/// <remarks>
/// Shaped by the e-Invoice validator, which requires <c>Qty &gt; 0</c>, a UN/ECE unit of measure and a
/// non-blank classification code on every line. <see cref="Amount"/> is quantity × unit price before
/// discount; <see cref="NetAmount"/> is after discount and excluding tax. The document header totals are
/// derived from these two plus <see cref="TaxAmt"/>, which is what keeps the payload self-consistent
/// (the validator cross-checks the header against the sum of the lines within 0.05).
/// </remarks>
public class PoSbInvoiceDetail
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public short Line { get; set; }

    public string? ICode { get; set; }
    public string? IDesc { get; set; }

    /// <summary>Document / purchase UOM quantity. Must be greater than zero to be submissible.</summary>
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>Purchase UOM code — translated to the LHDN UNECE code from <c>MsUom.UneceUom</c>.</summary>
    public string? StdUom { get; set; }

    /// <summary>Selling UOM code (display only).</summary>
    public string? SellingUom { get; set; }

    /// <summary>Line extension: quantity × unit price, before discount.</summary>
    public decimal Amount { get; set; }

    public decimal TaxAmt { get; set; }

    /// <summary>Net of discount, excluding tax.</summary>
    public decimal NetAmount { get; set; }

    public string? TaxGroup { get; set; }
    public bool IsInclusive { get; set; }

    public decimal Discount { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public string? IDiscountType { get; set; }
    public string? IDiscountType1 { get; set; }

    /// <summary>LHDN item classification code. Required by the validator on every line.</summary>
    public string? Classification { get; set; }

    public string? Remarks { get; set; }

    public PoSbInvoice Invoice { get; set; } = null!;
}
