namespace ErpWeb.Core.Sales;

/// <summary>
/// Input to <see cref="ISaSalesRefService.ResolveLinePricingAsync"/> — everything the four pipeline
/// stages need for ONE document line, gathered by the caller from the document header.
/// <para>
/// The caller supplies the TAX PERCENT and the line's <see cref="IsInclusive"/> flag because the
/// master price is tax-exclusive and only the document knows the line's basis. That conversion is
/// stage 2 and happens inside the orchestrator, never in the page.
/// </para>
/// </summary>
public sealed class SaLinePricingRequest
{
    public string CustCode { get; init; } = string.Empty;
    public string ICode { get; init; } = string.Empty;
    public string UOM { get; init; } = string.Empty;
    public decimal Qty { get; init; }

    /// <summary>The TRANSACTION date, never the server date — the engine is validity-aware.</summary>
    public DateTime DocDate { get; init; }

    /// <summary>Document currency; a price stored in another currency fails closed.</summary>
    public string? DocumentCurrency { get; init; }

    /// <summary>Item class, used only by discount-rule matching (a blank class matches any).</summary>
    public string? IClass { get; init; }

    /// <summary>Tax percent of the line's tax group, e.g. 6 for 6%.</summary>
    public decimal TaxPercent { get; init; }

    /// <summary>When true the stored line price must be tax-INCLUSIVE, so stage 2 grosses it up.</summary>
    public bool IsInclusive { get; init; }

    /// <summary><c>SaCust.DiscountMethod</c> — JOIN sums the percentages, anything else stacks them.</summary>
    public string? DiscountMethod { get; init; }
}

/// <summary>
/// Output of the four-stage pipeline, ready to assign onto a document line.
/// <para>
/// <see cref="UnitPrice"/> is ALREADY basis-corrected: it is tax-inclusive when the request said the
/// line is inclusive, because that is the value the line must store. Every other money value is the
/// engine-ready discount slot, unrounded.
/// </para>
/// </summary>
public sealed class SaLinePricingResult
{
    /// <summary>The value to store on the line: tax-exclusive, or grossed up when the line is inclusive.</summary>
    public decimal UnitPrice { get; init; }

    /// <summary>The tax-exclusive price the engine actually resolved, before stage 2. Audit only.</summary>
    public decimal BaseUnitPrice { get; init; }

    // ---------- traceability (persisted on the line from Phase 2) ----------

    public SaPriceSource PricingSource { get; init; }

    public string PricingSourceToken => SaPriceSourceTokens.For(PricingSource);

    /// <summary>Operator-facing label, e.g. "Customer item price".</summary>
    public string PricingSourceLabel => SaPriceSourceTokens.Describe(PricingSource);

    /// <summary>Readable reference: list code, <c>MOQ=100</c>, <c>QTY 10-99</c>.</summary>
    public string? PricingRef { get; init; }

    // ---------- the winning band / window (popup hint and inquiry ladder) ----------

    public int? MatchedMoq { get; init; }
    public decimal? MatchedMinQty { get; init; }
    public decimal? MatchedMaxQty { get; init; }
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public string? Currency { get; init; }

    // ---------- stage 3: engine-ready discount slots ----------

    public decimal ItemDiscount { get; init; }
    public decimal ItemDiscount2 { get; init; }
    public decimal ItemDiscAmount { get; init; }
    public decimal ItemDiscAmount1 { get; init; }

    /// <summary>Per-unit discount, UNROUNDED — rounding belongs to the document engine (§8.4).</summary>
    public decimal DiscountPerUnit { get; init; }

    public int? DiscountRuleId { get; init; }

    /// <summary>Rules that lost the deterministic tie-break; log when non-empty (legacy overlap).</summary>
    public IReadOnlyList<int> CompetingDiscountRuleIds { get; init; } = [];

    /// <summary>One-line explanation for the popup hint. Never a price, only the provenance.</summary>
    public string Describe()
    {
        var reference = string.IsNullOrWhiteSpace(PricingRef) ? string.Empty : $" ({PricingRef})";
        return $"{PricingSourceLabel}{reference}";
    }
}
