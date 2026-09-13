using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

public static class PoOrderStatuses
{
    public const string New = "NEW";
    public const string Open = "OPEN";
    public const string Received = "RECEIVED";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";
    public const string Pending = "PENDING";
    public const string Checked = "CHECKED";
}

public static class PoOrderCalc
{
    public static decimal RoundQty(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    public static decimal RoundMoney(decimal value, int decimals = 2) =>
        decimal.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static decimal RoundPrice(decimal value, int decimals = 6) =>
        decimal.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static decimal EffectivePackSize(decimal packSz) =>
        packSz == 0m ? 1m : packSz;

    public static decimal ComputeStdQty(decimal purchaseQty, decimal packSz) =>
        RoundQty(purchaseQty * EffectivePackSize(packSz));

    /// <summary>Net received = effective GR/NG qty minus effective VR qty.</summary>
    public static decimal ComputeNetReceived(decimal recvQty, decimal returnQty) =>
        RoundQty(recvQty - returnQty);

    /// <summary>BalanceQty = Max(0, OrderedQty - NetReceivedQty).</summary>
    public static decimal ComputeBalance(decimal poPurQty, decimal recvQty, decimal returnQty) =>
        RoundQty(Math.Max(0m, poPurQty - ComputeNetReceived(recvQty, returnQty)));

    /// <summary>OverRecvQty = Max(0, NetReceivedQty - OrderedQty).</summary>
    public static decimal ComputeOverRecv(decimal poPurQty, decimal recvQty, decimal returnQty) =>
        RoundQty(Math.Max(0m, ComputeNetReceived(recvQty, returnQty) - poPurQty));

    /// <summary>InvoiceableQty = Max(0, NetReceivedQty - InvoicedQty). Derived only.</summary>
    public static decimal ComputeInvoiceable(decimal recvQty, decimal returnQty, decimal invoicedQty) =>
        RoundQty(Math.Max(0m, ComputeNetReceived(recvQty, returnQty) - invoicedQty));

    /// <summary>OverInvoicedQty = Max(0, InvoicedQty - NetReceivedQty). Derived only.</summary>
    public static decimal ComputeOverInvoiced(decimal recvQty, decimal returnQty, decimal invoicedQty) =>
        RoundQty(Math.Max(0m, invoicedQty - ComputeNetReceived(recvQty, returnQty)));

    public static decimal AllowedRecvQty(decimal poPurQty, decimal tolerancePercent) =>
        RoundQty(poPurQty * (1m + tolerancePercent / 100m));

    /// <summary>Invoice qty ceiling equals actual net received — no invoice qty tolerance.</summary>
    public static decimal AllowedInvoicedQty(decimal recvQty, decimal returnQty) =>
        ComputeNetReceived(recvQty, returnQty);

    public static decimal ComputeAmount(decimal purchaseQty, decimal unitPrice) =>
        RoundMoney(purchaseQty * unitPrice);

    public static decimal ApplyTwoLevelDiscount(
        decimal amount,
        decimal itemDiscount,
        string? discountType,
        decimal itemDiscount1,
        string? discountType1)
    {
        var result = amount;
        result = ApplyOneDiscount(result, itemDiscount, discountType);
        result = ApplyOneDiscount(result, itemDiscount1, discountType1);
        return RoundMoney(result);
    }

    private static decimal ApplyOneDiscount(decimal amount, decimal discount, string? discountType)
    {
        if (discount == 0m)
        {
            return amount;
        }

        var isPercent = string.IsNullOrWhiteSpace(discountType)
            || discountType.Trim().Equals("%", StringComparison.OrdinalIgnoreCase)
            || discountType.Trim().Equals("P", StringComparison.OrdinalIgnoreCase)
            || discountType.Trim().Equals("PERCENT", StringComparison.OrdinalIgnoreCase);

        if (isPercent)
        {
            return RoundMoney(amount * (1m - discount / 100m));
        }

        return RoundMoney(amount - discount);
    }

    public static (decimal NetAmount, decimal TaxAmount) ComputeTax(
        decimal amount,
        decimal taxPercent,
        bool isInclusive,
        int taxDecimals) =>
        PoPrCalc.ComputeTax(amount, taxPercent, isInclusive, taxDecimals);

    public static (decimal Gross, decimal Taxes, decimal Total) SumTotals(
        IEnumerable<(decimal NetAmount, decimal TaxAmount)> lines) =>
        PoPrCalc.SumTotals(lines);

    public static bool IsServiceIType(string? iType) =>
        string.Equals((iType ?? string.Empty).Trim(), "SERVICE", StringComparison.OrdinalIgnoreCase);

    public static bool HasRemainingBalance(IEnumerable<PoOrderDetail> details, Func<PoOrderDetail, bool> isService) =>
        details.Any(d => !isService(d) && d.BalanceQty > 0m);

    public static bool AnyReceived(IEnumerable<PoOrderDetail> details) =>
        details.Any(d => d.RecvQty > 0m);

    /// <summary>
    /// Structural check of the current persisted PO line.
    /// Accepts OrderedQty &lt; NetReceivedQty when a tolerated over-receipt is already posted.
    /// </summary>
    public static bool ValidateQtyInvariants(
        decimal poPurQty,
        decimal recvQty,
        decimal returnQty,
        decimal invoicedQty,
        decimal? persistedBalanceQty,
        decimal? persistedOverRecvQty,
        out string? error)
    {
        if (recvQty < 0m)
        {
            error = "Received quantity cannot be negative.";
            return false;
        }

        if (returnQty < 0m || returnQty > recvQty)
        {
            error = "Return quantity must be between 0 and received quantity.";
            return false;
        }

        if (invoicedQty < 0m)
        {
            error = "Invoiced quantity cannot be negative.";
            return false;
        }

        if (persistedBalanceQty is not null
            && persistedBalanceQty.Value != ComputeBalance(poPurQty, recvQty, returnQty))
        {
            error = "Persisted balance quantity does not match the computed balance.";
            return false;
        }

        if (persistedOverRecvQty is not null
            && persistedOverRecvQty.Value != ComputeOverRecv(poPurQty, recvQty, returnQty))
        {
            error = "Persisted over-receipt quantity does not match the computed over-receipt.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Backward-compatible overload without invoiced / persisted checks.</summary>
    public static bool ValidateQtyInvariants(decimal poPurQty, decimal recvQty, decimal returnQty, out string? error) =>
        ValidateQtyInvariants(poPurQty, recvQty, returnQty, invoicedQty: 0m, persistedBalanceQty: null, persistedOverRecvQty: null, out error);

    /// <summary>Transaction-specific proposed GR/NG result after lock.</summary>
    public static bool ValidateReceiptAgainstTolerance(
        decimal poPurQty,
        decimal existingRecvQty,
        decimal existingReturnQty,
        decimal incomingQty,
        decimal tolerancePercent,
        out string? error)
    {
        if (incomingQty <= 0m)
        {
            error = "Receive quantity must be greater than zero.";
            return false;
        }

        if (existingReturnQty < 0m || existingReturnQty > existingRecvQty)
        {
            error = "Return quantity must be between 0 and received quantity.";
            return false;
        }

        var newRecv = RoundQty(existingRecvQty + incomingQty);
        if (newRecv < existingReturnQty)
        {
            error = "Received quantity cannot fall below returned quantity.";
            return false;
        }

        var newNet = ComputeNetReceived(newRecv, existingReturnQty);
        var allowed = AllowedRecvQty(poPurQty, tolerancePercent);
        if (newNet > allowed)
        {
            error = $"Net received quantity exceeds allowed receive quantity ({allowed:n4}).";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>PO edit / revise floors for ordered quantity.</summary>
    public static bool ValidateOrderQtyChange(
        decimal newOrderedQty,
        decimal recvQty,
        decimal returnQty,
        decimal invoicedQty,
        out string? error)
    {
        if (newOrderedQty <= 0m)
        {
            error = "Quantity must be greater than zero.";
            return false;
        }

        var net = ComputeNetReceived(recvQty, returnQty);
        if (newOrderedQty < net)
        {
            error = "Purchase quantity cannot be less than net received quantity.";
            return false;
        }

        if (newOrderedQty < invoicedQty)
        {
            error = "Purchase quantity cannot be less than invoiced quantity.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Apply recomputed BalanceQty and OverRecvQty onto a PO line.</summary>
    public static void ApplyComputedQtyFields(PoOrderDetail line)
    {
        ArgumentNullException.ThrowIfNull(line);
        line.BalanceQty = ComputeBalance(line.PoPurQty, line.RecvQty, line.ReturnQty);
        line.OverRecvQty = ComputeOverRecv(line.PoPurQty, line.RecvQty, line.ReturnQty);
    }

    /// <summary>Master / override tolerance must be in 0..100 inclusive.</summary>
    public static bool ValidateTolerancePercent(decimal tolerancePercent, string fieldName, out string? error)
    {
        if (tolerancePercent < 0m || tolerancePercent > 100m)
        {
            error = $"{fieldName} must be between 0 and 100.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Unit-price match against effective price tolerance (percent).
    /// Zero PO price accepts only a zero invoice price; otherwise |inv-po|/po*100 &lt;= tol.
    /// </summary>
    public static bool ValidatePriceTolerance(
        decimal poUnitPrice,
        decimal invUnitPrice,
        decimal effectiveTolerancePercent,
        int priceDecimals,
        out string? error)
    {
        if (poUnitPrice < 0m || invUnitPrice < 0m)
        {
            error = "Unit price cannot be negative.";
            return false;
        }

        if (!ValidateTolerancePercent(effectiveTolerancePercent, "Price tolerance", out error))
        {
            return false;
        }

        var po = RoundPrice(poUnitPrice, priceDecimals);
        var inv = RoundPrice(invUnitPrice, priceDecimals);
        if (po == 0m)
        {
            if (inv != 0m)
            {
                error = "Invoice unit price must be zero when PO unit price is zero.";
                return false;
            }

            error = null;
            return true;
        }

        var variancePct = Math.Abs(inv - po) / po * 100m;
        if (variancePct > effectiveTolerancePercent)
        {
            error =
                $"Invoice unit price {inv:0.######} varies {variancePct:0.####}% from PO price {po:0.######} (tolerance {effectiveTolerancePercent:0.####}%).";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Recompute FinClosed from the complete PO line set under lock.
    /// Stamps FinClosedOn/By on false→true; clears on true→false.
    /// All lines (stock and service/NG) must have BalanceQty == 0 and InvoicedQty == NetReceivedQty.
    /// </summary>
    public static void RecalculateFinClosed(
        PoOrder header,
        IEnumerable<PoOrderDetail> allLines,
        string? userId,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(allLines);

        var lines = allLines as IList<PoOrderDetail> ?? allLines.ToList();
        var cancelled = string.Equals(header.Status, PoOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);
        var shouldClose = !cancelled
            && lines.Count > 0
            && lines.All(d =>
            {
                var net = ComputeNetReceived(d.RecvQty, d.ReturnQty);
                return d.BalanceQty == 0m && d.InvoicedQty == net;
            });

        if (shouldClose == header.FinClosed)
        {
            return;
        }

        if (shouldClose)
        {
            header.FinClosed = true;
            header.FinClosedOn = utcNow;
            header.FinClosedBy = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
            if (header.FinClosedBy is { Length: > 20 })
            {
                header.FinClosedBy = header.FinClosedBy[..20];
            }
        }
        else
        {
            header.FinClosed = false;
            header.FinClosedOn = null;
            header.FinClosedBy = null;
        }
    }
}
