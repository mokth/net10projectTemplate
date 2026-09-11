namespace ErpWeb.Core.Sales;

public static class SaInvoiceStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
}

public static class SaInvoiceLimits
{
    public const int MaxPostSelection = 3;
}

public static class SaInvoiceCalc
{
    public const string HomeCurrency = "MYR";
    public const string ExcludedDiscountOrderType = "EXCLD DIS";
    public const int TaxDecimalPlaces = 2;
    public const decimal TaxEpsilon = 0.01m;

    public static bool HasTax(decimal taxes) => Math.Abs(taxes) >= TaxEpsilon;

    public static decimal Money(decimal value, int decimals = 2) =>
        decimal.Round(value, decimals, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Per-unit discount from the active SalesDicountHelper.CalculateDiscount path.
    /// JOIN replaces the sequential stack; there is no SPLIT branch; CustDiscount is unused.
    /// </summary>
    public static decimal CalculateDiscountPerUnit(
        decimal unitPrice,
        decimal itemDiscount,
        decimal itemDiscount2,
        decimal itemDiscount3,
        decimal itemDiscount4,
        decimal itemDiscount5,
        decimal itemDiscount6,
        decimal itemDiscAmount,
        decimal itemDiscAmount1,
        string? discMethod)
    {
        var amounts = itemDiscAmount + itemDiscAmount1;
        if (string.Equals(discMethod, SaCustPaymentOptions.DiscountJoin, StringComparison.OrdinalIgnoreCase))
        {
            var sumPct = itemDiscount + itemDiscount2 + itemDiscount3 + itemDiscount4 + itemDiscount5 + itemDiscount6;
            return (sumPct / 100m * unitPrice) + amounts;
        }

        var remaining = unitPrice;
        remaining = ApplyPercent(remaining, itemDiscount);
        remaining = ApplyPercent(remaining, itemDiscount2);
        remaining = ApplyPercent(remaining, itemDiscount3);
        remaining = ApplyPercent(remaining, itemDiscount4);
        remaining = ApplyPercent(remaining, itemDiscount5);
        remaining = ApplyPercent(remaining, itemDiscount6);
        return (unitPrice - remaining) + amounts;
    }

    /// <summary>
    /// Calculates one line's ex-tax money columns.
    /// <para>
    /// <b>R2 / D2 — inclusive lines:</b> the entered <see cref="SaInvoiceLineCalcState.UnitPrice"/>
    /// <i>includes</i> tax (legacy semantics). It is un-taxed to derive the net and tax:
    /// <c>exclusiveUnitPrice = UnitPrice / (1 + t)</c>,
    /// <c>exclusiveDiscountPerUnit = discountPerUnit / (1 + t)</c>, then
    /// <c>Amount</c>/<c>NetAmount</c>/<c>TaxAmt</c> are all ex-tax.
    /// <c>TaxAmt</c> is derived as <c>inclusiveLineTotal − NetAmount</c> so that
    /// <c>NetAmount + TaxAmt</c> preserves the tax-inclusive line total exactly.
    /// </para>
    /// <para>
    /// <see cref="SaInvoiceLineCalcState.DiscountPerUnit"/> keeps the <i>raw</i> per-unit discount
    /// (tax-inclusive for inclusive lines) so the identity above is checkable.
    /// </para>
    /// </summary>
    public static void CalculateLine(
        SaInvoiceLineCalcState line,
        decimal taxPercent,
        bool decPoint,
        string? discMethod)
    {
        var qty = line.Qty;
        var unitPrice = line.UnitPrice;
        line.TaxPercent = taxPercent;
        var factor = 1m + (taxPercent / 100m);

        var discountPerUnit = CalculateDiscountPerUnit(
            unitPrice,
            line.ItemDiscount,
            line.ItemDiscount2,
            line.ItemDiscount3,
            line.ItemDiscount4,
            line.ItemDiscount5,
            line.ItemDiscount6,
            line.ItemDiscAmount,
            line.ItemDiscAmount1,
            discMethod);
        line.DiscountPerUnit = discountPerUnit;

        if (line.IsInclusive)
        {
            // Un-tax to legacy semantics: the entered unit price includes tax.
            var exclusiveUnitPrice = factor == 0m ? unitPrice : unitPrice / factor;
            var exclusiveDiscountPerUnit = factor == 0m ? discountPerUnit : discountPerUnit / factor;

            var amount = Money(qty * exclusiveUnitPrice);
            var net = amount - Money(qty * exclusiveDiscountPerUnit);
            if (!decPoint)
            {
                net = Money(net, 4);
            }

            line.Amount = amount;
            line.NetAmount = Money(net);

            // Preserve the tax-inclusive line total exactly (row 2 of §9.1: 90 + 9 = 99).
            var inclusiveLineTotal = Money(qty * (unitPrice - discountPerUnit));
            line.TaxAmt = inclusiveLineTotal - line.NetAmount;
            return;
        }

        var exAmount = Money(qty * unitPrice);
        var exNet = exAmount - Money(qty * discountPerUnit);
        if (!decPoint)
        {
            exNet = Money(exNet, 4);
        }

        line.Amount = exAmount;
        line.NetAmount = Money(exNet);
        line.TaxAmt = Money(line.NetAmount * taxPercent / 100m, TaxDecimalPlaces);
    }

    /// <summary>
    /// Redistributes rounding residue across the header tax so that per-line tax sums to the
    /// header figure. The tax rate is read from each line's <see cref="SaInvoiceLineCalcState.TaxPercent"/>
    /// (R10.1 removed the previously-unused <c>taxPercent</c> parameter).
    /// </summary>
    public static void ApplyTaxAdaptiveRounding(IReadOnlyList<SaInvoiceLineCalcState> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var inclusive = lines[0].IsInclusive;
        if (inclusive)
        {
            // R2: per-line tax is derived as (inclusive line total − ex-tax net) in CalculateLine,
            // so NetAmount + TaxAmt already preserves the tax-inclusive line total exactly. Re-assert
            // it here as an idempotent self-heal so adaptive rounding can never corrupt an inclusive
            // line. This is order-independent by construction; callers should still pass lines in
            // ascending Line order (§9.3) so any future document-level residual stays deterministic.
            foreach (var line in lines)
            {
                var inclusiveLineTotal = Money(line.Qty * (line.UnitPrice - line.DiscountPerUnit));
                var delta = inclusiveLineTotal - Money(line.NetAmount + line.TaxAmt);
                if (delta != 0m)
                {
                    line.TaxAmt += delta;
                }
            }

            return;
        }

        decimal runningUnrounded = 0m;
        decimal runningRounded = 0m;
        foreach (var line in lines)
        {
            var raw = line.Amount * line.TaxPercent / 100m;
            runningUnrounded += raw;
            var target = Money(runningUnrounded, TaxDecimalPlaces);
            line.TaxAmt = target - runningRounded;
            runningRounded = target;
        }
    }

    public static (decimal GrossAmnt, decimal Taxes, decimal TotAmnt) CalculateHeader(
        IReadOnlyList<SaInvoiceLineCalcState> lines,
        bool decPoint)
    {
        decimal gross = 0m;
        decimal excluded = 0m;
        decimal taxes = 0m;
        foreach (var line in lines)
        {
            taxes += line.TaxAmt;
            if (string.Equals(line.OrderType, ExcludedDiscountOrderType, StringComparison.OrdinalIgnoreCase))
            {
                excluded += line.NetAmount;
            }
            else
            {
                gross += line.NetAmount;
            }
        }

        var decimals = decPoint ? 0 : 2;
        taxes = Money(taxes, decimals);
        gross = Money(gross, decimals);
        excluded = Money(excluded, decimals);
        const decimal discountAmt = 0m;
        var tot = Money(gross - discountAmt + excluded + taxes, decimals);
        return (gross + excluded, taxes, tot);
    }

    private static decimal ApplyPercent(decimal price, decimal percent) =>
        percent == 0m ? price : price * (1m - (percent / 100m));
}

public sealed class SaInvoiceLineCalcState
{
    /// <summary>
    /// Persisted line ordinal. Authority for the inclusive residual redistribution tie-break
    /// (plan §9.3); callers should also pass lines ordered by <see cref="Line"/> ascending.
    /// </summary>
    public int Line { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount2 { get; set; }
    public decimal ItemDiscount3 { get; set; }
    public decimal ItemDiscount4 { get; set; }
    public decimal ItemDiscount5 { get; set; }
    public decimal ItemDiscount6 { get; set; }
    public decimal ItemDiscAmount { get; set; }
    public decimal ItemDiscAmount1 { get; set; }
    public bool IsInclusive { get; set; }
    public string? OrderType { get; set; }
    public decimal Amount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmt { get; set; }
    public decimal DiscountPerUnit { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal LocalAmount { get; set; }
}
