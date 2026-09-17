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
    /// <summary>
    /// Quantity declared non-billable by a DO force-close (R3). Monotonic and additive — a second
    /// force-closed DO on the same SO line accrues further write-off. Written <b>only</b> by
    /// <c>SaDoService.ForceCloseOneAsync</c> under the DO → SO lock order. Lineage comes from the
    /// CLOSED DO plus the retained SP-batch stamp — there is no separate write-off ledger in Phase 2.
    /// </summary>
    public decimal WrittenOffQty { get; set; }
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
    /// WHICH price source produced <see cref="UnitPrice"/> (plan Phase 2) — the persisted
    /// <c>SaPriceSourceTokens</c> value, e.g. <c>CUSTOMER_ITEM</c>. Explanatory only: the price is
    /// FROZEN at entry and is never re-derived from this.
    /// </summary>
    public string? PricingSource { get; set; }

    /// <summary>
    /// The readable reference behind <see cref="PricingSource"/> (<c>PL1</c>, <c>MOQ=100</c>,
    /// <c>QTY 10-99</c>), so support can explain a price without a join.
    /// </summary>
    public string? PricingRef { get; set; }

    /// <summary>
    /// Phase 4: the price the ENGINE resolved, retained only when an operator overrode it. NULL means
    /// the line was never overridden, which is the normal case, so this column costs nothing until it
    /// is used.
    /// </summary>
    public decimal? OriginalUnitPrice { get; set; }

    /// <summary>
    /// Phase 4: why the operator changed the resolved price. The service requires it whenever
    /// <see cref="OriginalUnitPrice"/> is set, so an unexplained price change cannot reach the ledger.
    /// </summary>
    public string? OverrideReason { get; set; }

    /// <summary>
    /// Source quotation revision this line was converted from. Always stamped together with
    /// <see cref="QtLine"/> and <see cref="QtConsumedQty"/>; null for manually entered SO lines.
    /// </summary>
    public string? QtNo { get; set; }

    /// <summary>Line number on the source quotation revision (see <see cref="QtNo"/>).</summary>
    public short? QtLine { get; set; }

    /// <summary>Revision of <see cref="QtNo"/> this line was converted from.</summary>
    public short? QtCustRel { get; set; }

    /// <summary>
    /// Quantity on the source quotation line that this SO line consumed. Written once by
    /// <c>SaQtService.ConvertToSoAsync</c> under the QT → SO lock order and never mutated.
    /// Named to parallel the existing <c>SoConsumedQty</c> convention on DO / Invoice details.
    /// </summary>
    public decimal QtConsumedQty { get; set; }

    public SaSo So { get; set; } = null!;
}
