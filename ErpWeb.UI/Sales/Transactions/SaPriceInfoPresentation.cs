using ErpWeb.Core.Sales;

namespace ErpWeb.UI.Sales.Transactions;

public enum SaPriceInfoStatus
{
    SystemResolved,
    ManualOverride,
    ManualPriceWithoutSystemSource,
    Inherited,
    NotRecorded
}

/// <summary>Pure display rules for recorded Sales line price provenance.</summary>
public static class SaPriceInfoPresentation
{
    public static string SourceLabel(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "Not recorded";

        return token.Trim().ToUpperInvariant() switch
        {
            SaPriceSourceTokens.CustomerItem => "Customer Special",
            SaPriceSourceTokens.CustomerPriceList => "Customer Price List",
            SaPriceSourceTokens.CustomerGroupPriceList => "Customer Group Price List",
            SaPriceSourceTokens.ItemDefault => "Item Default",
            _ => $"Recorded source ({token.Trim()})"
        };
    }

    public static string SourceLabel(SaPriceSource? source) => source switch
    {
        SaPriceSource.CustomerItem => "Customer Special",
        SaPriceSource.CustomerPriceList => "Customer Price List",
        SaPriceSource.CustomerGroupPriceList => "Customer Group Price List",
        SaPriceSource.ItemDefault => "Item Default",
        _ => "No usable system price"
    };

    public static SaPriceInfoStatus Classify(SaPriceInfoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hasSource = !string.IsNullOrWhiteSpace(context.PricingSource);
        if (hasSource && context.OriginalUnitPrice is { } original && original != context.UnitPrice)
            return SaPriceInfoStatus.ManualOverride;

        if (!hasSource && context.OriginalUnitPrice == 0m && context.UnitPrice > 0m &&
            !string.IsNullOrWhiteSpace(context.OverrideReason))
            return SaPriceInfoStatus.ManualPriceWithoutSystemSource;

        if (!string.IsNullOrWhiteSpace(context.SourceDocumentNo))
            return SaPriceInfoStatus.Inherited;

        if (hasSource && (context.OriginalUnitPrice is null || context.OriginalUnitPrice == context.UnitPrice) &&
            string.IsNullOrWhiteSpace(context.OverrideReason))
            return SaPriceInfoStatus.SystemResolved;

        return SaPriceInfoStatus.NotRecorded;
    }

    public static string StatusLabel(SaPriceInfoStatus status) => status switch
    {
        SaPriceInfoStatus.SystemResolved => "System resolved",
        SaPriceInfoStatus.ManualOverride => "Manual override",
        SaPriceInfoStatus.ManualPriceWithoutSystemSource => "Manual price",
        SaPriceInfoStatus.Inherited => "Inherited from source document",
        _ => "Price source not recorded"
    };

    public static decimal? Difference(SaPriceInfoContext context) =>
        context.OriginalUnitPrice is { } original ? context.UnitPrice - original : null;

    public static decimal? DifferencePercent(SaPriceInfoContext context)
    {
        if (context.OriginalUnitPrice is not { } original || original == 0m)
            return null;

        return (context.UnitPrice - original) / original * 100m;
    }

    public static string ReferenceLabel(SaPriceInfoContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.PricingRef))
            return context.PricingRef.Trim();

        return string.Equals(context.PricingSource, SaPriceSourceTokens.ItemDefault, StringComparison.OrdinalIgnoreCase)
            ? "Standard item selling price"
            : "Not recorded";
    }

    public static string Tooltip(SaPriceInfoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var status = Classify(context);
        if (status == SaPriceInfoStatus.ManualPriceWithoutSystemSource)
            return "Manual price";

        var source = SourceLabel(context.PricingSource);
        if (status == SaPriceInfoStatus.ManualOverride)
            return $"Manual override · {source}";

        if (status == SaPriceInfoStatus.Inherited)
        {
            var type = string.IsNullOrWhiteSpace(context.SourceDocumentType)
                ? "source document"
                : context.SourceDocumentType.Trim();
            var no = string.IsNullOrWhiteSpace(context.SourceDocumentNo)
                ? null
                : context.SourceDocumentNo.Trim();
            var inherited = string.IsNullOrWhiteSpace(no)
                ? $"Inherited from {type}"
                : $"Inherited from {type} {no}";
            return string.IsNullOrWhiteSpace(context.PricingSource)
                ? inherited
                : $"{inherited} · {source}";
        }

        if (string.IsNullOrWhiteSpace(context.PricingSource))
            return "Price information";

        var reference = string.IsNullOrWhiteSpace(context.PricingRef) ? string.Empty : $" · {context.PricingRef.Trim()}";
        return $"{source}{reference}";
    }

    public static string UomLabel(SaPriceInfoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var commercial = string.IsNullOrWhiteSpace(context.Uom) ? "—" : context.Uom.Trim();
        if (!string.IsNullOrWhiteSpace(context.StockUom)
            && !string.Equals(context.StockUom.Trim(), commercial, StringComparison.OrdinalIgnoreCase))
        {
            return $"{commercial} (stock {context.StockUom.Trim()})";
        }

        return commercial;
    }

    public static string DiscountLabel(SaPriceInfoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var parts = new List<string>();
        foreach (var pct in new[]
                 {
                     context.ItemDiscount, context.ItemDiscount2, context.ItemDiscount3,
                     context.ItemDiscount4, context.ItemDiscount5, context.ItemDiscount6
                 })
        {
            if (pct != 0m)
            {
                parts.Add($"{pct:0.####}%");
            }
        }

        if (context.ItemDiscAmount != 0m)
        {
            parts.Add(context.ItemDiscAmount.ToString("0.####"));
        }

        if (context.ItemDiscAmount1 != 0m)
        {
            parts.Add(context.ItemDiscAmount1.ToString("0.####"));
        }

        return parts.Count == 0 ? "None" : string.Join(" + ", parts);
    }

    /// <summary>
    /// One-line recorded commercial facts: agreed price, commercial UOM, source, discount, inherited/agreed state.
    /// </summary>
    public static string Glance(SaPriceInfoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{FormatPrice(context.UnitPrice, context.Currency)} / {UomLabel(context)}"
            + $" · {SourceLabel(context.PricingSource)}"
            + $" · Discount {DiscountLabel(context)}"
            + $" · {StatusLabel(Classify(context))}";
    }

    public static string FormatPrice(decimal price, string? currency)
    {
        var currencyLabel = string.IsNullOrWhiteSpace(currency)
            ? string.Empty
            : string.Equals(currency.Trim(), "MYR", StringComparison.OrdinalIgnoreCase)
                ? "RM "
                : $"{currency.Trim()} ";
        var sign = price < 0m ? "-" : string.Empty;
        return $"{sign}{currencyLabel}{Math.Abs(price):n4}";
    }

    public static string Basis(SaPriceExplanationLevel level)
    {
        var parts = new List<string>();
        if (level.MinQty is not null || level.MaxQty is not null)
        {
            var min = level.MinQty ?? 0m;
            var max = level.MaxQty is null ? "and above" : $"to {level.MaxQty:0.####}";
            parts.Add($"qty {min:0.####} {max}");
        }

        if (level.ValidFrom is not null || level.ValidTo is not null)
        {
            var from = level.ValidFrom?.ToString("dd/MM/yyyy") ?? "always";
            var to = level.ValidTo?.ToString("dd/MM/yyyy") ?? "open";
            parts.Add($"{from} – {to}");
        }

        if (!string.IsNullOrWhiteSpace(level.Ref))
            parts.Add(level.Ref.Trim());
        if (!string.IsNullOrWhiteSpace(level.Currency))
            parts.Add(level.Currency.Trim());

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }
}
