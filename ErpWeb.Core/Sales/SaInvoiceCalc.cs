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

    public static void CalculateLine(
        SaInvoiceLineCalcState line,
        decimal taxPercent,
        bool decPoint,
        string? discMethod)
    {
        var qty = line.Qty;
        var unitPrice = line.UnitPrice;
        line.TaxPercent = taxPercent;
        line.Amount = Money(qty * unitPrice);

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

        decimal net;
        if (discountPerUnit != 0m)
        {
            if (line.IsInclusive)
            {
                var factor = 1m + (taxPercent / 100m);
                var totalDiscount = factor == 0m
                    ? 0m
                    : Money(qty * (discountPerUnit / factor));
                net = Money(qty * unitPrice) - totalDiscount;
            }
            else
            {
                var totalDiscount = Money(qty * discountPerUnit);
                net = Money(qty * unitPrice) - totalDiscount;
            }
        }
        else
        {
            net = Money(qty * unitPrice);
        }

        if (!decPoint)
        {
            net = Money(net, 4);
        }

        line.NetAmount = Money(net);
        line.DiscountPerUnit = discountPerUnit;

        if (line.IsInclusive)
        {
            line.TaxAmt = Money((unitPrice - discountPerUnit) * qty, TaxDecimalPlaces) - line.NetAmount;
        }
        else
        {
            line.TaxAmt = Money(line.NetAmount * taxPercent / 100m, TaxDecimalPlaces);
        }
    }

    public static void ApplyTaxAdaptiveRounding(IReadOnlyList<SaInvoiceLineCalcState> lines, decimal taxPercent)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var inclusive = lines[0].IsInclusive;
        if (inclusive)
        {
            foreach (var line in lines)
            {
                var left = Money(line.Amount + line.TaxAmt);
                var right = Money(line.NetAmount + line.TaxAmt);
                if (left != right)
                {
                    line.Amount += left - right;
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
