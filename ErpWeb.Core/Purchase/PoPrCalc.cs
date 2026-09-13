namespace ErpWeb.Core.Purchase;

public static class PoPrStatuses
{
    public const string New = "NEW";
    public const string Open = "OPEN";
    public const string Cancelled = "CANCELLED";
    public const string Approved = "APPROVED";
    public const string PartiallyOrdered = "PARTIALLY_ORDERED";
    public const string FullyOrdered = "FULLY_ORDERED";
}

public static class PoPrTypes
{
    public const string Purchasing = "PURCHASING";
    public const string Repair = "REPAIR";
}

public static class PoPrRepairTypes
{
    public const string Internal = "Internal";
    public const string External = "External";
}

public static class PoPrCalc
{
    public static decimal RoundQty(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    public static decimal RoundMoney(decimal value, int decimals = 2) =>
        decimal.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static decimal EffectivePackSize(decimal packSz) =>
        packSz == 0m ? 1m : packSz;

    public static decimal ComputeStdQty(decimal purchaseQty, decimal packSz) =>
        RoundQty(purchaseQty * EffectivePackSize(packSz));

    public static decimal ComputeAmount(decimal purchaseQty, decimal unitPrice) =>
        RoundMoney(purchaseQty * unitPrice);

    public static decimal ComputeWtQty(decimal stdQty, decimal defCatchWt) =>
        RoundQty(stdQty * defCatchWt);

    public static (decimal NetAmount, decimal TaxAmount) ComputeTax(
        decimal amount,
        decimal taxPercent,
        bool isInclusive,
        int taxDecimals)
    {
        if (taxPercent < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(taxPercent), "Tax rate cannot be negative.");
        }

        if (taxPercent == 0m)
        {
            return (RoundMoney(amount), 0m);
        }

        if (!isInclusive)
        {
            var tax = RoundMoney(amount * taxPercent / 100m, taxDecimals);
            return (RoundMoney(amount), tax);
        }

        var net = RoundMoney(amount / (100m + taxPercent) * 100m, taxDecimals);
        var inclusiveTax = RoundMoney(amount - net, taxDecimals);
        return (net, inclusiveTax);
    }

    public static (decimal Gross, decimal Taxes, decimal Total) SumTotals(
        IEnumerable<(decimal NetAmount, decimal TaxAmount)> lines)
    {
        decimal gross = 0m;
        decimal taxes = 0m;
        foreach (var line in lines)
        {
            gross += line.NetAmount;
            taxes += line.TaxAmount;
        }

        gross = RoundMoney(gross);
        taxes = RoundMoney(taxes);
        return (gross, taxes, RoundMoney(gross + taxes));
    }

    public static bool IsInactiveVendorItemStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.Trim().ToUpperInvariant() is "I" or "INACTIVE" or "0" or "N";
    }

    public static bool IsDerivedOrderedStatus(string? status)
    {
        var s = (status ?? string.Empty).Trim().ToUpperInvariant();
        return s is PoPrStatuses.PartiallyOrdered or PoPrStatuses.FullyOrdered;
    }

    /// <summary>
    /// Line status from live PO consumption. CANCELLED wins; no consumption preserves non-derived status.
    /// </summary>
    public static string ComputeLineDerivedStatus(decimal purchaseQty, decimal consumedQty, string? currentStatus)
    {
        if (string.Equals(currentStatus, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return PoPrStatuses.Cancelled;
        }

        var remaining = RoundQty(purchaseQty - consumedQty);
        var consumed = RoundQty(Math.Max(0m, consumedQty));
        if (consumed <= 0m)
        {
            return IsDerivedOrderedStatus(currentStatus) ? PoPrStatuses.New : (currentStatus ?? PoPrStatuses.New);
        }

        return remaining <= 0m ? PoPrStatuses.FullyOrdered : PoPrStatuses.PartiallyOrdered;
    }

    /// <summary>
    /// Header status from live PO consumption across lines. CANCELLED wins over derived states.
    /// Driven by consumption quantities, not PoNo stamps.
    /// </summary>
    public static string ComputeDerivedStatus(
        string? headerStatus,
        IReadOnlyList<(decimal PurchaseQty, decimal ConsumedQty)> lines)
    {
        if (string.Equals(headerStatus, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return PoPrStatuses.Cancelled;
        }

        if (lines is null || lines.Count == 0)
        {
            return headerStatus ?? PoPrStatuses.New;
        }

        var anyConsumed = false;
        var allFullyConsumed = true;
        foreach (var (purchaseQty, consumedQty) in lines)
        {
            var remaining = RoundQty(purchaseQty - consumedQty);
            if (RoundQty(Math.Max(0m, consumedQty)) > 0m)
            {
                anyConsumed = true;
            }

            if (remaining > 0m)
            {
                allFullyConsumed = false;
            }
        }

        if (anyConsumed && allFullyConsumed)
        {
            return PoPrStatuses.FullyOrdered;
        }

        if (anyConsumed)
        {
            return PoPrStatuses.PartiallyOrdered;
        }

        return IsDerivedOrderedStatus(headerStatus) ? PoPrStatuses.New : (headerStatus ?? PoPrStatuses.New);
    }
}
