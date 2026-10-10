namespace ErpWeb.Core.Sales;

public sealed record SaPriceListOverlapLine(
    int Id,
    string ICode,
    string Uom,
    decimal MinQty,
    decimal? MaxQty,
    DateTime ValidFrom,
    DateTime? ValidTo,
    string? CurrencyCode);

public static class SaPriceListOverlapValidator
{
    /// <summary>
    /// Returns an actionable conflict for the first pair of rows that can both match the same item,
    /// UOM, currency, quantity band, and validity date. Currency remains part of the identity.
    /// </summary>
    public static string? FindConflict(IReadOnlyList<SaPriceListOverlapLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = i + 1; j < lines.Count; j++)
            {
                var a = lines[i];
                var b = lines[j];

                if (!string.Equals(a.ICode?.Trim(), b.ICode?.Trim(), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(a.Uom?.Trim(), b.Uom?.Trim(), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        a.CurrencyCode?.Trim() ?? string.Empty,
                        b.CurrencyCode?.Trim() ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!SaItemFamilyRuleMatch.BandsOverlap(
                        a.MinQty,
                        a.MaxQty ?? decimal.MaxValue,
                        b.MinQty,
                        b.MaxQty ?? decimal.MaxValue)
                    || !SaItemFamilyRuleMatch.WindowsOverlap(
                        a.ValidFrom,
                        a.ValidTo,
                        b.ValidFrom,
                        b.ValidTo))
                {
                    continue;
                }

                var band = a.MaxQty is null
                    ? $"quantity {a.MinQty:0.####} and above"
                    : $"quantity {a.MinQty:0.####} to {a.MaxQty:0.####}";

                return $"Item {a.ICode} / UOM {a.Uom} already has a price for {band} effective "
                     + $"{a.ValidFrom:yyyy-MM-dd} that also applies here. Adjust the quantity band or the "
                     + "validity window so only one line can match.";
            }
        }

        return null;
    }
}
