using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Core.Sales;

public static class SaCdnStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
}

public static class SaCdnTypes
{
    public const string CreditNote = "CN";
    public const string DebitNote = "DN";
}

public static class SaCdnLimits
{
    public const int MaxPostSelection = 3;
}

public static class SaCdnSpRefs
{
    public const string Prefix = "CN/";

    public static string ToRefNo(string docNo) => Prefix + (docNo ?? string.Empty).Trim();
}

public static class SaCdnReasonCodes
{
    public const string Concurrency = "CDN_CONCURRENCY";
    public const string StatusConflict = "CDN_STATUS";
    public const string RemainingExceeded = "CDN_REMAINING";
    public const string FingerprintMismatch = "CDN_FP_MISMATCH";
    public const string FingerprintMissing = "CDN_FP_MISSING";
    public const string CrOrphan = "CDN_CR_ORPHAN";
}

/// <summary>
/// Pure calculation / fingerprint helpers. No DbContext, no stock mutation.
/// </summary>
public static class SaCdnCalc
{
    private const char FieldSep = '\u001E';
    private const char RecordSep = '\u001F';

    /// <summary>
    /// IMPORTANT: This field list MUST remain synchronized with every field used by
    /// BuildMrPostLineAsync / inventory slice construction. Mandatory test:
    /// mutate each inventory-affecting field → POST reuse fails closed.
    /// </summary>
    public static string ComputeSourceFingerprint(IEnumerable<SaCdnDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rows = details
            .Where(x => x.StockControl)
            .OrderBy(x => x.Line)
            .ToList();

        var sb = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(RecordSep);
            }

            var d = rows[i];
            sb.Append(d.Line.ToString(CultureInfo.InvariantCulture));
            sb.Append(FieldSep).Append(Norm(d.ICode));
            sb.Append(FieldSep).Append(Norm(d.FrWarehouse));
            sb.Append(FieldSep).Append(Norm(d.LocCode));
            sb.Append(FieldSep).Append(Norm(d.IStatus));
            sb.Append(FieldSep).Append(Norm(d.LotNo));
            sb.Append(FieldSep).Append(FormatExpiry(d.ExpiryDate));
            sb.Append(FieldSep).Append(FormatQty(d.StdQty));
            sb.Append(FieldSep).Append(Norm(d.StdUom));
            sb.Append(FieldSep).Append(d.StockControl ? "1" : "0");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsValidFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return false;
        }

        var fp = fingerprint.Trim();
        if (fp.Length != 64)
        {
            return false;
        }

        foreach (var c in fp)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public static decimal MoneyNormalize(decimal amount, bool decPoint) =>
        SaInvoiceCalc.Money(amount, decPoint ? 0 : 2);

    public static decimal SumNormalized(IEnumerable<decimal> amounts, bool decPoint)
    {
        decimal sum = 0m;
        foreach (var a in amounts)
        {
            sum += MoneyNormalize(a, decPoint);
        }

        return MoneyNormalize(sum, decPoint);
    }

    public static (bool Ok, decimal Remaining, string? Error) EvaluateRemaining(
        decimal invoiceTotAmnt,
        IReadOnlyList<decimal> otherCnTotAmnts,
        decimal candidateTotAmnt,
        bool decPoint)
    {
        var invTotal = MoneyNormalize(invoiceTotAmnt, decPoint);
        var otherSum = SumNormalized(otherCnTotAmnts, decPoint);
        var remaining = MoneyNormalize(invTotal - otherSum, decPoint);
        var thisTotal = MoneyNormalize(candidateTotAmnt, decPoint);

        if (remaining < 0m)
        {
            return (false, remaining, "Invoice remaining is negative (legacy over-credit). Cannot save.");
        }

        if (thisTotal > remaining)
        {
            return (false, remaining, $"Credit note total {thisTotal:0.00} exceeds invoice remaining {remaining:0.00}.");
        }

        return (true, remaining, null);
    }

    public static string? ValidateQtyGates(SaCdnDetail detail)
    {
        if (detail.Qty <= 0m)
        {
            return $"Line {detail.Line}: quantity must be greater than zero.";
        }

        if (detail.StdQty <= 0m)
        {
            return $"Line {detail.Line}: standard quantity must be greater than zero.";
        }

        if (detail.StdCustPsize < 0m)
        {
            return $"Line {detail.Line}: pack size cannot be negative.";
        }

        return null;
    }

    public static DateTime? ToDateOnly(DateTime? value) =>
        value?.Date;

    public static string FormatExpiry(DateTime? expiry)
    {
        if (expiry is null)
        {
            return string.Empty;
        }

        return expiry.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatQty(decimal qty) =>
        IvQty.Round(qty).ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Norm(string? value) => (value ?? string.Empty).Trim();
}
