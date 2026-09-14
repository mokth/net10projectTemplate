namespace ErpWeb.Model.Entities.Purchase;

/// <summary>A line of a Purchase Credit / Debit Note.</summary>
/// <remarks>
/// A line is either a <b>normal financial line</b> (<c>Qty &gt; 0</c>, <c>UnitPrice &gt; 0</c>,
/// <c>Amount &gt; 0</c>) or a <b>tax-only line</b> (<c>Qty = 0</c>, <c>UnitPrice = 0</c>,
/// <c>NetAmount = 0</c>, <c>TaxAmt &gt; 0</c>) — the two shapes are mutually exclusive.
/// </remarks>
public class PoCdnDetail
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public short Line { get; set; }

    /// <summary>
    /// Source <c>PoInvoiceDetail.Line</c>. Presence marks a <i>line-based</i> adjustment;
    /// absence marks a <i>header-level</i> adjustment. Required on every stock-return line
    /// and forbidden when the document has no referenced invoice.
    /// </summary>
    public short? InvLineNo { get; set; }

    /// <summary>
    /// Declared physical-return intent. Never inferred from <see cref="FromBalLocId"/> or a PO
    /// link. Must be false on every line when the header <c>ReturnStock</c> is false, and is
    /// always false on a debit note.
    /// </summary>
    public bool IsStockReturn { get; set; }

    /// <summary>Item code. Blank on a tax-only line.</summary>
    public string? ICode { get; set; }
    public string? IDesc { get; set; }

    /// <summary>Document / purchase UOM quantity. Never passed to the VR subsystem directly.</summary>
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public string? SellingUom { get; set; }
    public string? StdUom { get; set; }
    public string? WtUom { get; set; }

    /// <summary>Inventory standard quantity — the value the VR subsystem receives.</summary>
    public decimal StdQty { get; set; }
    public decimal WtQty { get; set; }

    /// <summary>Conversion snapshot actually used for this line (invoice pack size, PO pack size or item pack size).</summary>
    public decimal StdCustPsize { get; set; }

    public decimal TaxAmt { get; set; }
    public decimal Amount { get; set; }
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

    /// <summary>
    /// Informational snapshot only. The VR/inventory posting service is the sole authority for
    /// stock-out valuation; this value must never be used as the inventory cost.
    /// </summary>
    public decimal CostPrice { get; set; }

    public string? Classification { get; set; }

    // PO link. Optional overall; required on a stock-return line.
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }

    // Inventory source. FromBalLocId is authoritative — warehouse/location/status must match it.
    public string? FrWarehouse { get; set; }
    public string? LocCode { get; set; }
    public string? IStatus { get; set; }
    public string? LotNo { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public bool StockControl { get; set; }
    public int? FromBalLocId { get; set; }

    public PoCdn Cdn { get; set; } = null!;
}
