namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Sales Quotation line. Mirrors the commercial half of <see cref="SaSoDetail"/> and deliberately
/// omits every fulfilment column (<c>ShippedQty</c>, <c>BalanceQty</c>, <c>DeliveredQty</c>,
/// <c>InvoicedQty</c>, <c>WrittenOffQty</c>) — a quotation is never shipped against.
/// </summary>
public class SaQtDetail
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string QtNo { get; set; } = string.Empty;
    public short CustRel { get; set; } = 1;
    public short Line { get; set; }

    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? CustICode { get; set; }
    public decimal OrderQty { get; set; }

    /// <summary>
    /// Quantity already turned into a Sales Order. Monotonic, never negative.
    /// MVP moves this 0 → <see cref="OrderQty"/> in one atomic full conversion, so
    /// <c>rem = OrderQty - ConvertedQty</c> is never partially consumed by any MVP operation.
    /// </summary>
    public decimal ConvertedQty { get; set; }

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

    /// <summary>Customer promised / requested delivery date. Planning only; not an actual.</summary>
    public DateTime? DeliveryDate { get; set; }
    /// <summary>Estimated arrival at customer / port. Planning only; not an actual.</summary>
    public DateTime? Eta { get; set; }
    /// <summary>Estimated departure from warehouse / factory. Planning only; not an actual.</summary>
    public DateTime? Etd { get; set; }

    /// <summary>
    /// WHICH price source produced <see cref="UnitPrice"/> — the persisted <c>SaPriceSourceTokens</c>
    /// value. Explanatory only: the price is FROZEN at entry and is never re-derived from this.
    /// </summary>
    public string? PricingSource { get; set; }

    /// <summary>Readable reference behind <see cref="PricingSource"/> (<c>PL1</c>, <c>MOQ=100</c>, <c>QTY 10-99</c>).</summary>
    public string? PricingRef { get; set; }

    /// <summary>
    /// Phase 4: the price the ENGINE resolved, retained only when an operator changed it. A quotation is
    /// where a price is first OFFERED, so it is exactly where an override needs recording. NULL means
    /// the line was never overridden.
    /// </summary>
    public decimal? OriginalUnitPrice { get; set; }

    /// <summary>Phase 4: why the resolved price was changed. The service requires it on any override.</summary>
    public string? OverrideReason { get; set; }

    public SaQt Qt { get; set; } = null!;
}
