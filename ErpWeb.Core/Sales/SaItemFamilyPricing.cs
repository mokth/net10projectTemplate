namespace ErpWeb.Core.Sales;

// ============================================================================================
// Sales item family — price and discount resolution.
// Plan: plans/sales-item-family-v2-plan.md §7 (price), §7.1 (basis + currency), §7.2 (worked
// examples), §8/§8.1/§8.1.1 (discount pipeline, JOIN vs SPLIT), §8.3/§8.3.1 (rule matching and the
// deterministic tie-break), §8.4 (rounding), §8.9 (the deferred consumer contract).
//
// These rules are PURE — no EF, no tenant context, no screen. They are the single place where a price
// or a discount is decided, so no consumer (and no UI) can re-derive either, and the behaviour is
// pinned by tests before the SO/DO/INV/CN consumer exists (§9.1 "artefact + test").
//
// Rounding lives in the document engine (SaInvoiceCalc.Money), never here: the per-unit discount this
// file returns is deliberately unrounded (§8.4).
// ============================================================================================

/// <summary>Traceability labels — which source produced a resolved price (§7 steps 1-3).</summary>
public static class SaItemFamilyPriceSources
{
    public const string CustomerItem = "SaItemCust.UnitPrice";
    public const string PriceList = "IvCustPrice.SellingPrice";
    public const string ItemSellingPrice = "IvStockMaster.SellingPrice";
}

/// <summary>
/// Price request — the §7 input key, plus the customer attributes the caller reads once from
/// <c>SaCust</c>. <see cref="CustPriceCode"/> is deliberately not validated here: an unknown code
/// falls through to the next source (D-6 clause 3 — legacy free text must never block a sale).
/// </summary>
public sealed class SaItemFamilyPriceRequest
{
    public string CustCode { get; init; } = string.Empty;
    public string ICode { get; init; } = string.Empty;
    public string UOM { get; init; } = string.Empty;
    public decimal Qty { get; init; }

    /// <summary>Part of the §7 key; price tables are undated, so nothing selects on it yet (§8.7).</summary>
    public DateTime DocDate { get; init; }

    /// <summary>Part of the §7 key; the customer-level group discount is excluded (§8.2).</summary>
    public string? PayCode { get; init; }

    /// <summary><c>SaCust.PriceMethod</c>, verbatim legacy string. A value containing DEALER fails closed.</summary>
    public string? PriceMethod { get; init; }

    /// <summary><c>SaCust.CustPriceCode</c> — the price-list assignment (§11.2).</summary>
    public string? CustPriceCode { get; init; }

    /// <summary>Document currency; a candidate price list is base-currency only (D8).</summary>
    public string? DocumentCurrency { get; init; }
}

/// <summary>One <c>SaItemCust</c> row — the customer/item/UOM/MOQ override (§7 step 1).</summary>
public sealed class SaItemCustPriceCandidate
{
    public string ICode { get; init; } = string.Empty;
    public string UOM { get; init; } = string.Empty;
    public int MOQ { get; init; }

    /// <summary>Tax-exclusive stored price (the live column is <c>float</c>; scale at the boundary).</summary>
    public decimal? UnitPrice { get; init; }

    public string? Currency { get; init; }
}

/// <summary>One <c>IvCustPrice</c> row — an item price inside a price list (§7 step 2).</summary>
public sealed class IvCustPriceCandidate
{
    public string CustPriceCode { get; init; } = string.Empty;
    public string ICode { get; init; } = string.Empty;
    public string UOM { get; init; } = string.Empty;
    public decimal? SellingPrice { get; init; }
}

/// <summary>One <c>IvStockMaster</c> row — the item's own default selling price (§7 step 3).</summary>
public sealed class IvStockMasterPriceCandidate
{
    public string ICode { get; init; } = string.Empty;

    /// <summary>An inactive item is not a price candidate (§7.2 E8).</summary>
    public bool IsActive { get; init; }

    public string? SellingUom { get; init; }
    public string? StdUom { get; init; }
    public decimal? SellingPrice { get; init; }
}

/// <summary>The candidate set for one resolution call, already company-scoped by the caller.</summary>
public sealed class SaItemFamilyPriceCandidates
{
    public IReadOnlyList<SaItemCustPriceCandidate> CustomerItems { get; init; } = [];
    public IReadOnlyList<IvCustPriceCandidate> PriceListLines { get; init; } = [];
    public IReadOnlyList<IvStockMasterPriceCandidate> Items { get; init; } = [];
}

/// <summary>Outcome of §7: either one usable tax-exclusive price with its source, or a blocking message.</summary>
public sealed class SaItemFamilyPriceResolution
{
    public bool Found { get; init; }

    /// <summary>Present only when <see cref="Found"/> is false — always actionable, never a zero.</summary>
    public string? Message { get; init; }

    public string? Source { get; init; }
    public string? ICode { get; init; }
    public string? UOM { get; init; }

    /// <summary>Always tax-EXCLUSIVE (§7.1 D7). Gross up at the document boundary if the line is inclusive.</summary>
    public decimal? UnitPrice { get; init; }

    /// <summary>The winning MOQ band when the source is <c>SaItemCust</c>; otherwise null.</summary>
    public int? MatchedMoq { get; init; }

    public static SaItemFamilyPriceResolution Blocked(string message) =>
        new() { Found = false, Message = message };
}

/// <summary>
/// §7 — the ordered price resolution algorithm. First usable candidate wins; the end of the chain is a
/// hard block, never <c>0</c> (defaulting to zero is the legacy defect this feature removes).
/// </summary>
public static class SaItemFamilyPriceResolver
{
    /// <summary>A dealer-mode customer has no price source in v2 (D6) — fail closed, never map to selling.</summary>
    public static bool IsDealerPriceMethod(string? priceMethod) =>
        !string.IsNullOrWhiteSpace(priceMethod) &&
        priceMethod.Contains("DEALER", StringComparison.OrdinalIgnoreCase);

    public static SaItemFamilyPriceResolution Resolve(
        SaItemFamilyPriceRequest request,
        SaItemFamilyPriceCandidates candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var iCode = Normalize(request.ICode);
        var uom = Normalize(request.UOM);

        // Step 0 — the customer's PriceMethod decides the baseline source set.
        if (IsDealerPriceMethod(request.PriceMethod))
        {
            return SaItemFamilyPriceResolution.Blocked(
                "Dealer pricing is not supported for this customer. Point the customer at a price list, "
                + "or store a customer-item price, before using this item.");
        }

        // Step 1 — customer-specific override: highest MOQ <= qty wins; MOQ 0/blank is the base row.
        var customerRow = candidates.CustomerItems
            .Where(x => string.Equals(Normalize(x.ICode), iCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Normalize(x.UOM), uom, StringComparison.OrdinalIgnoreCase)
                        && x.MOQ <= request.Qty)
            .Where(x => x.UnitPrice is > 0m)
            .OrderByDescending(x => x.MOQ)
            .FirstOrDefault();

        if (customerRow is not null)
        {
            // §7.1 D8 — a currency mismatch fails closed rather than silently using another source.
            if (!CurrencyMatches(customerRow.Currency, request.DocumentCurrency))
            {
                return SaItemFamilyPriceResolution.Blocked(
                    $"The customer-item price for item {iCode} is stored in {customerRow.Currency} but this "
                    + "document is in a different currency. Correct the customer-item price or the document "
                    + "currency; conversion is not implicit.");
            }

            return new SaItemFamilyPriceResolution
            {
                Found = true,
                Source = SaItemFamilyPriceSources.CustomerItem,
                ICode = iCode,
                UOM = uom,
                UnitPrice = customerRow.UnitPrice,
                MatchedMoq = customerRow.MOQ
            };
        }

        // Step 2 — the assigned price list. An unknown CustPriceCode yields nothing (D-6 clause 3).
        var priceListCode = Normalize(request.CustPriceCode);
        if (priceListCode.Length > 0)
        {
            var line = candidates.PriceListLines
                .Where(x => string.Equals(Normalize(x.CustPriceCode), priceListCode, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Normalize(x.ICode), iCode, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Normalize(x.UOM), uom, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.SellingPrice is > 0m)
                .FirstOrDefault();

            if (line is not null)
            {
                return new SaItemFamilyPriceResolution
                {
                    Found = true,
                    Source = SaItemFamilyPriceSources.PriceList,
                    ICode = iCode,
                    UOM = uom,
                    UnitPrice = line.SellingPrice
                };
            }
        }

        // Step 3 — the item default, only when the document UOM is the item's selling UOM. No conversion.
        var item = candidates.Items
            .FirstOrDefault(x => string.Equals(Normalize(x.ICode), iCode, StringComparison.OrdinalIgnoreCase));

        if (item is not null && item.IsActive && item.SellingPrice is > 0m)
        {
            var sellingUom = Normalize(string.IsNullOrWhiteSpace(item.SellingUom) ? item.StdUom : item.SellingUom);
            if (sellingUom.Length > 0 && string.Equals(sellingUom, uom, StringComparison.OrdinalIgnoreCase))
            {
                return new SaItemFamilyPriceResolution
                {
                    Found = true,
                    Source = SaItemFamilyPriceSources.ItemSellingPrice,
                    ICode = iCode,
                    UOM = uom,
                    UnitPrice = item.SellingPrice
                };
            }
        }

        // Step 4 — no candidate: block with a message the operator can act on (§8.9 rule 5).
        return SaItemFamilyPriceResolution.Blocked(
            $"No price found for item {iCode} / UOM {uom}. Store a customer-item price, a price-list line, "
            + "or the item's selling price for this UOM.");
    }

    /// <summary>Blank on either side means "not declared", which is not a mismatch (legacy data is sparse).</summary>
    private static bool CurrencyMatches(string? candidateCurrency, string? documentCurrency) =>
        string.IsNullOrWhiteSpace(candidateCurrency) ||
        string.IsNullOrWhiteSpace(documentCurrency) ||
        string.Equals(Normalize(candidateCurrency), Normalize(documentCurrency), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}

/// <summary>Discount request — the §8 input (item identity, quantity, document date, method, base price).</summary>
public sealed class SaItemFamilyDiscountRequest
{
    public string ICode { get; init; } = string.Empty;
    public string? IClass { get; init; }
    public decimal Qty { get; init; }
    public DateTime DocDate { get; init; }

    /// <summary><c>SaCust.DiscountMethod</c> — <c>JOIN</c> (sum against the original price) or anything else (sequential stack).</summary>
    public string? DiscountMethod { get; init; }

    /// <summary>The §7 result; percentages are applied against it (§8.2).</summary>
    public decimal UnitPrice { get; init; }
}

/// <summary>One <c>SaDisGroupItem</c> rule (item + quantity band + date window + two discount slots).</summary>
public sealed class SaItemFamilyDiscountRule
{
    public int Id { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IClass { get; init; }
    public decimal QtyFr { get; init; }
    public decimal QtyTo { get; init; }
    public DateTime DateFr { get; init; }
    public DateTime? DateTo { get; init; }

    /// <summary>Slot A value; <c>PERCENTAGE</c> or <c>AMOUNT</c> per <see cref="DiscountType"/>.</summary>
    public decimal? Discount { get; init; }

    public string? DiscountType { get; init; }

    /// <summary>Slot B value (mapped onto the engine's second percent/amount slot — §8.2).</summary>
    public decimal? Discount1 { get; init; }

    public string? DiscountType1 { get; init; }
}

/// <summary>The single matching rule and the engine-ready slots it contributes (§8 P2/P3).</summary>
public sealed class SaItemFamilyDiscountSelection
{
    public bool Found { get; init; }
    public int? RuleId { get; init; }
    public decimal PercentSlot1 { get; init; }
    public decimal PercentSlot2 { get; init; }
    public decimal AmountSlot1 { get; init; }
    public decimal AmountSlot2 { get; init; }

    /// <summary>Per-unit discount, UNROUNDED — rounding belongs to the document engine (§8.4).</summary>
    public decimal DiscountPerUnit { get; init; }

    /// <summary>
    /// Ids of the other matching rules that lost the §8.3.1 tie-break. Legacy data may still contain
    /// overlaps, so the winner must be explainable: log these when the collection is non-empty.
    /// </summary>
    public IReadOnlyList<int> CompetingRuleIds { get; init; } = [];

    public static SaItemFamilyDiscountSelection None { get; } = new();
}

/// <summary>§8.3 band/date/class matching — stated once and reused by the validator and the resolver.</summary>
public static class SaItemFamilyRuleMatch
{
    /// <summary>Inclusive band match: <c>QtyFr &lt;= qty &lt;= QtyTo</c> (§8.3).</summary>
    public static bool BandContains(decimal qtyFr, decimal qtyTo, decimal qty) => qtyFr <= qty && qtyTo >= qty;

    /// <summary>Two bands touch or cross. Used by the save-time overlap rule.</summary>
    public static bool BandsOverlap(decimal aFrom, decimal aTo, decimal bFrom, decimal bTo) =>
        aFrom <= bTo && aTo >= bFrom;

    /// <summary>Inclusive window match on the date part; a NULL end date is open-ended (§8.3/A.4).</summary>
    public static bool WindowsOverlap(DateTime aFrom, DateTime? aTo, DateTime bFrom, DateTime? bTo) =>
        (bTo is null || aFrom.Date <= bTo.Value.Date) && (aTo is null || bFrom.Date <= aTo.Value.Date);

    /// <summary>A blank class applies to all classes, so it overlaps any class-specific rule (§8.3).</summary>
    public static bool ClassesOverlap(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ||
        string.IsNullOrWhiteSpace(right) ||
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does this rule apply to the request? The item must match, the band and window must contain the
    /// quantity/date, and a class-specific rule must match the item's class (blank = all classes).
    /// </summary>
    public static bool Applies(SaItemFamilyDiscountRule rule, SaItemFamilyDiscountRequest request) =>
        rule is not null &&
        request is not null &&
        string.Equals(rule.ICode?.Trim(), request.ICode?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        BandContains(rule.QtyFr, rule.QtyTo, request.Qty) &&
        WindowsOverlap(rule.DateFr, rule.DateTo, request.DocDate, request.DocDate) &&
        ClassesOverlap(rule.IClass, request.IClass);
}

/// <summary>
/// §8 — discount resolution. At most one rule may match (enforced at save time, §8.3); if legacy data
/// still holds overlaps, the pick is deterministic and the losers are reported (§8.3.1) — never an
/// arbitrary row.
/// </summary>
public static class SaItemFamilyDiscountResolver
{
    /// <summary>Precedence: class-specific → higher QtyFr → earlier DateFr → lower Id (§8.3.1).</summary>
    public static IReadOnlyList<SaItemFamilyDiscountRule> Order(IEnumerable<SaItemFamilyDiscountRule> matches) =>
        matches
            .OrderByDescending(x => string.IsNullOrWhiteSpace(x.IClass) ? 0 : 1)
            .ThenByDescending(x => x.QtyFr)
            .ThenBy(x => x.DateFr)
            .ThenBy(x => x.Id)
            .ToList();

    public static SaItemFamilyDiscountSelection Resolve(
        SaItemFamilyDiscountRequest request,
        IEnumerable<SaItemFamilyDiscountRule> rules)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rules);

        var ordered = Order(rules.Where(x => SaItemFamilyRuleMatch.Applies(x, request)));
        if (ordered.Count == 0)
        {
            return SaItemFamilyDiscountSelection.None;
        }

        var winner = ordered[0];
        var percent1 = Slot(winner.Discount, winner.DiscountType, asPercent: true);
        var percent2 = Slot(winner.Discount1, winner.DiscountType1, asPercent: true);
        var amount1 = Slot(winner.Discount, winner.DiscountType, asPercent: false);
        var amount2 = Slot(winner.Discount1, winner.DiscountType1, asPercent: false);

        return new SaItemFamilyDiscountSelection
        {
            Found = true,
            RuleId = winner.Id,
            PercentSlot1 = percent1,
            PercentSlot2 = percent2,
            AmountSlot1 = amount1,
            AmountSlot2 = amount2,
            // Slots 3-6 stay 0 by design: the legacy rule has two slots, and spreading values across
            // unused slots would change the arithmetic (§8.2).
            DiscountPerUnit = SaInvoiceCalc.CalculateDiscountPerUnit(
                request.UnitPrice,
                percent1,
                percent2,
                0m,
                0m,
                0m,
                0m,
                amount1,
                amount2,
                request.DiscountMethod),
            CompetingRuleIds = ordered.Skip(1).Select(x => x.Id).ToList()
        };
    }

    /// <summary>
    /// A slot value is a percentage unless its type says AMOUNT. A blank type on a non-zero value is
    /// treated as PERCENTAGE (the save-time validator requires a type, so this only guards legacy rows).
    /// </summary>
    private static decimal Slot(decimal? value, string? type, bool asPercent)
    {
        if (value is null or 0m)
        {
            return 0m;
        }

        var isAmount = !string.IsNullOrWhiteSpace(type) &&
                       type.Trim().Equals(SaDiscountSlotTypes.Amount, StringComparison.OrdinalIgnoreCase);

        // A slot feeds the percent argument only when it is a percentage, and the amount argument only
        // when it is an amount — never both.
        return isAmount == asPercent ? 0m : value.Value;
    }
}

/// <summary>
/// §7.1 D7 — every stored master price is tax-EXCLUSIVE. The line engine un-taxes an inclusive
/// <c>UnitPrice</c>, so a consumer assigning a master price to an inclusive line must gross it up
/// first. Masters never perform tax maths; this is the only conversion allowed at the boundary.
/// </summary>
public static class SaItemFamilyPriceBasis
{
    /// <summary>Exclusive master price → the tax-inclusive value an inclusive line expects.</summary>
    public static decimal ToInclusive(decimal taxExclusiveUnitPrice, decimal taxRate) =>
        taxRate <= 0m ? taxExclusiveUnitPrice : taxExclusiveUnitPrice * (1m + taxRate);

    /// <summary>Inclusive line value → the tax-exclusive value (the engine's own <c>/(1+t)</c> step).</summary>
    public static decimal ToExclusive(decimal taxInclusiveUnitPrice, decimal taxRate) =>
        taxRate <= -1m ? taxInclusiveUnitPrice : taxInclusiveUnitPrice / (1m + taxRate);
}
