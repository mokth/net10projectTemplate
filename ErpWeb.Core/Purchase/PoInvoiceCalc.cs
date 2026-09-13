using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

public static class PoInvoiceStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
}

public static class PoInvoiceTypes
{
    public const string Invoice = "INV";
    public const string CreditNote = "CN";
}

public static class PoInvoiceLimits
{
    public const int MaxPostSelection = 3;
    public const string NumberingModule = "POCDN";
}

public static class PoInvoiceCalc
{
    public static bool IsValidType(string? type)
    {
        var t = (type ?? string.Empty).Trim().ToUpperInvariant();
        return t is PoInvoiceTypes.Invoice or PoInvoiceTypes.CreditNote;
    }

    public static string NormalizeType(string? type) =>
        (type ?? string.Empty).Trim().ToUpperInvariant();

    public static decimal RemainingOnInvLine(decimal postedInvQty, decimal postedCnQtyAgainstInv) =>
        PoOrderCalc.RoundQty(postedInvQty - postedCnQtyAgainstInv);

    public static (decimal NetAmount, decimal TaxAmount, decimal Amount) ComputeLineAmounts(
        decimal qty,
        decimal unitPrice,
        decimal itemDiscount,
        string? discountType,
        decimal itemDiscount1,
        string? discountType1,
        decimal taxPercent,
        bool isInclusive,
        int taxDecimals)
    {
        var amount = PoOrderCalc.ComputeAmount(qty, unitPrice);
        var discounted = PoOrderCalc.ApplyTwoLevelDiscount(
            amount, itemDiscount, discountType, itemDiscount1, discountType1);
        var (net, tax) = PoOrderCalc.ComputeTax(discounted, taxPercent, isInclusive, taxDecimals);
        return (net, tax, amount);
    }

    public static void ApplyHeaderTotals(PoInvoice header, IEnumerable<PoInvoiceDetail> details)
    {
        ArgumentNullException.ThrowIfNull(header);
        var (gross, taxes, total) = PoOrderCalc.SumTotals(
            details.Select(d => (d.NetAmount, d.TaxAmt)));
        header.GrossAmnt = gross;
        header.Taxes = taxes;
        header.TotAmnt = total;
    }
}
