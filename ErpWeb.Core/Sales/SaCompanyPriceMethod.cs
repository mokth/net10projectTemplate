namespace ErpWeb.Core.Sales;

/// <summary>
/// Per-company pricing method — plan section "Company pricing mode".
///
/// The method selects which price SOURCES are ELIGIBLE. It never reorders them: the walk order is
/// fixed by specificity, so a company cannot configure a group price to beat a negotiated customer
/// price. <see cref="SaPriceSource.ItemDefault"/> is the last step in every method and is never
/// removable — an item with no usable selling price is still a BLOCK, but the search always reaches
/// the item default first.
///
/// NULL / blank / unknown = <see cref="CustomerItemAndList"/>, which reproduces the shipped chain, so
/// an unmigrated company behaves exactly as it does today.
/// </summary>
public static class SaCompanyPriceMethod
{
    /// <summary>Every source, fixed specificity order. The default method, and the pre-migration behaviour.</summary>
    public const string CustomerItemAndList = "CUSTOMER_ITEM_AND_LIST";

    /// <summary>Negotiated / contract customers: the customer-item price, then the item default.</summary>
    public const string CustomerItemOnly = "CUSTOMER_ITEM_ONLY";

    /// <summary>Distributors on price lists: customer list, then group list, then the item default.</summary>
    public const string PriceListOnly = "PRICE_LIST_ONLY";

    /// <summary>Simple retail / single-price shop: the item default only.</summary>
    public const string ItemDefaultOnly = "ITEM_DEFAULT_ONLY";

    /// <summary>Column width for <c>Company.SalesPriceMethod</c>.</summary>
    public const int MaxLength = 32;

    public static readonly IReadOnlyList<string> All =
    [
        CustomerItemAndList, CustomerItemOnly, PriceListOnly, ItemDefaultOnly
    ];

    // The eligible chains, pre-built so ResolveSources cannot allocate or be mutated per line.
    private static readonly IReadOnlyList<SaPriceSource> FullChain =
    [
        SaPriceSource.CustomerItem,
        SaPriceSource.CustomerPriceList,
        SaPriceSource.CustomerGroupPriceList,
        SaPriceSource.ItemDefault
    ];

    private static readonly IReadOnlyList<SaPriceSource> CustomerItemChain =
    [
        SaPriceSource.CustomerItem,
        SaPriceSource.ItemDefault
    ];

    private static readonly IReadOnlyList<SaPriceSource> PriceListChain =
    [
        SaPriceSource.CustomerPriceList,
        SaPriceSource.CustomerGroupPriceList,
        SaPriceSource.ItemDefault
    ];

    private static readonly IReadOnlyList<SaPriceSource> ItemDefaultChain =
    [
        SaPriceSource.ItemDefault
    ];

    /// <summary>Canonical token for a stored value; blank or unknown input yields the default method.</summary>
    public static string Normalize(string? method)
    {
        var value = (method ?? string.Empty).Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, value, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return CustomerItemAndList;
    }

    /// <summary>
    /// The ordered, eligible source list for a method. The item default is always present and always
    /// last, so no configuration can produce a chain that ends without a fallback source.
    /// </summary>
    public static IReadOnlyList<SaPriceSource> ResolveSources(string? method) =>
        Normalize(method) switch
        {
            CustomerItemOnly => CustomerItemChain,
            PriceListOnly => PriceListChain,
            ItemDefaultOnly => ItemDefaultChain,
            _ => FullChain
        };

    /// <summary>True when the method makes <paramref name="source"/> eligible. Used by the explanation ladder.</summary>
    public static bool IsEligible(string? method, SaPriceSource source) =>
        ResolveSources(method).Contains(source);

    /// <summary>Human label for the admin combo and the price-inquiry ladder.</summary>
    public static string Describe(string? method) => Normalize(method) switch
    {
        CustomerItemOnly => "Customer item price, then item default",
        PriceListOnly => "Customer / group price list, then item default",
        ItemDefaultOnly => "Item default selling price only",
        _ => "Customer item price, then price list, then item default"
    };
}
