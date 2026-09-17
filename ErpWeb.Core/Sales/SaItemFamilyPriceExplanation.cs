namespace ErpWeb.Core.Sales;

/// <summary>
/// One level of the explanation ladder: what this source COULD have produced and why it did or did not.
/// </summary>
public sealed class SaPriceExplanationLevel
{
    public SaPriceSource Source { get; init; }

    public string SourceLabel => SaPriceSourceTokens.Describe(Source);

    /// <summary>
    /// False when the company's pricing method excludes this level. The level is still REPORTED, because
    /// "this company does not use customer prices" is the single most common answer to "why didn't the
    /// customer's special price apply?", and hiding the level would hide that answer.
    /// </summary>
    public bool Eligible { get; init; }

    public bool Applied { get; init; }

    /// <summary>The tax-EXCLUSIVE price this level produced, when it applied.</summary>
    public decimal? UnitPrice { get; init; }

    /// <summary>The readable reference behind the price (<c>PL1</c>, <c>MOQ=100</c>, <c>QTY 10-99</c>).</summary>
    public string? Ref { get; init; }

    public decimal? MinQty { get; init; }
    public decimal? MaxQty { get; init; }
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public string? Currency { get; init; }

    /// <summary>Why this level did not produce the price. Null when it applied.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// The full answer to "where did this price come from, and why not from somewhere else?".
///
/// Produced by <see cref="SaItemFamilyPriceExplainer.Explain"/>, which goes through exactly the same
/// <c>Select*</c> helpers the resolver uses, so an inquiry can never disagree with the price a document
/// receives.
/// </summary>
public sealed class SaPriceExplanation
{
    public bool Found { get; init; }

    /// <summary>Tax-EXCLUSIVE, exactly as the resolver produced it — the basis conversion is NOT applied here.</summary>
    public decimal? UnitPrice { get; init; }

    public SaPriceSource? WinningSource { get; init; }

    public string? PricingRef { get; init; }

    /// <summary>Set when a candidate existed but was unusable (a currency mismatch); this STOPS the walk.</summary>
    public string? BlockedMessage { get; init; }

    /// <summary>The company pricing method in force, so the caller can explain the eligibility column.</summary>
    public string CompanyPriceMethod { get; init; } = string.Empty;

    /// <summary>Every level in the FIXED specificity order, whether or not it was eligible or reached.</summary>
    public IReadOnlyList<SaPriceExplanationLevel> Levels { get; init; } = [];

    /// <summary>
    /// A support-facing summary of the winning level, e.g. "Customer price list (PL1) — RM 13.75".
    /// </summary>
    public string Describe()
    {
        if (!Found)
        {
            return BlockedMessage ?? "No price found at any level.";
        }

        var label = WinningSource is { } source ? SaPriceSourceTokens.Describe(source) : "Unknown";
        var reference = string.IsNullOrWhiteSpace(PricingRef) ? string.Empty : $" ({PricingRef})";
        return $"{label}{reference} - {UnitPrice:0.####}";
    }
}

/// <summary>
/// Phase 6 — the explanation ladder. Deliberately a SEPARATE type from the resolver: the tested
/// <c>Resolve</c> contract is left untouched, and this only WALKS the same sources through the same
/// public <c>ResolveSource</c> entry point.
/// </summary>
public static class SaItemFamilyPriceExplainer
{
    /// <summary>
    /// The fixed specificity order, spelled out here rather than taken from the company method, because
    /// the ladder must SHOW the excluded levels instead of silently omitting them.
    /// </summary>
    private static readonly IReadOnlyList<SaPriceSource> AllLevels =
    [
        SaPriceSource.CustomerItem,
        SaPriceSource.CustomerPriceList,
        SaPriceSource.CustomerGroupPriceList,
        SaPriceSource.ItemDefault
    ];

    public static SaPriceExplanation Explain(
        SaItemFamilyPriceRequest request,
        SaItemFamilyPriceCandidates candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var mode = SaCompanyPriceMethod.Normalize(request.CompanyPriceMethod);
        var eligible = SaCompanyPriceMethod.ResolveSources(mode);
        var dealer = SaItemFamilyPriceResolver.IsDealerPriceMethod(request.PriceMethod);

        var levels = new List<SaPriceExplanationLevel>(AllLevels.Count);
        SaPriceSource? winner = null;
        decimal? winnerPrice = null;
        string? winnerRef = null;
        string? blocked = null;
        var decided = false;

        foreach (var source in AllLevels)
        {
            if (dealer)
            {
                levels.Add(NotApplicable(source, eligible: false, "Not consulted: this customer is set to dealer pricing, which has no price source."));
                continue;
            }

            if (!eligible.Contains(source))
            {
                levels.Add(NotApplicable(source, eligible: false, $"Not consulted: excluded by this company's pricing method ({mode})."));
                continue;
            }

            if (decided)
            {
                levels.Add(NotApplicable(source, eligible: true, "Not consulted: a more specific level already produced the price."));
                continue;
            }

            // The SAME entry point the walk uses, so the two can never disagree.
            var result = SaItemFamilyPriceResolver.ResolveSource(source, request, candidates);

            if (result.IsBlocking)
            {
                blocked = result.BlockingReason;
                decided = true;
                levels.Add(NotApplicable(source, eligible: true, result.BlockingReason!));
                continue;
            }

            if (result.Found)
            {
                winner = source;
                winnerPrice = result.UnitPrice;
                winnerRef = result.Ref;
                decided = true;

                levels.Add(new SaPriceExplanationLevel
                {
                    Source = source,
                    Eligible = true,
                    Applied = true,
                    UnitPrice = result.UnitPrice,
                    Ref = result.Ref,
                    MinQty = result.MinQty,
                    MaxQty = result.MaxQty,
                    ValidFrom = result.ValidFrom,
                    ValidTo = result.ValidTo,
                    Currency = result.Currency
                });
                continue;
            }

            levels.Add(NotApplicable(
                source,
                eligible: true,
                result.RejectReason ?? "No matching row for this item, UOM, quantity, date and currency."));
        }

        return new SaPriceExplanation
        {
            Found = winner is not null,
            UnitPrice = winnerPrice,
            WinningSource = winner,
            PricingRef = winnerRef,
            BlockedMessage = blocked,
            CompanyPriceMethod = mode,
            Levels = levels
        };
    }

    private static SaPriceExplanationLevel NotApplicable(SaPriceSource source, bool eligible, string reason) =>
        new() { Source = source, Eligible = eligible, Reason = reason };
}
