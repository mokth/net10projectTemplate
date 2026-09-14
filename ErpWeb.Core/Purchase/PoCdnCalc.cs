using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

public static class PoCdnStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
}

public static class PoCdnTypes
{
    public const string CreditNote = "CN";
    public const string DebitNote = "DN";
}

public static class PoCdnLimits
{
    public const int MaxPostSelection = 3;

    /// <summary>Numbering module == document type, mirroring SaCdn ("module == doc type").</summary>
    public const string CreditNoteModule = "PCN";
    public const string DebitNoteModule = "PDN";
}

public static class PoCdnSpRefs
{
    /// <summary>
    /// System-owned linkage prefix on the PoCdn's VR batch. Never a user-editable reference,
    /// and never overloaded to mean "many" — Phase 2 uses a link table.
    /// </summary>
    public const string VrPrefix = "PCN/";

    public static string ToVrRefNo(string docNo) => VrPrefix + (docNo ?? string.Empty).Trim();
}

/// <summary>Machine-readable failure codes surfaced to the UI and to the tests.</summary>
public static class PoCdnReasonCodes
{
    public const string Concurrency = "PCN_CONCURRENCY";
    public const string StatusConflict = "PCN_STATUS";
    public const string RemainingExceeded = "PCN_REMAINING";
    public const string SourceLineQtyExceeded = "PCN_SRC_LINE_QTY";
    public const string SourceLineValueExceeded = "PCN_SRC_LINE_VALUE";
    public const string PhysicalCeilingExceeded = "PCN_PHYSICAL_QTY";
    public const string FingerprintMismatch = "PCN_FP_MISMATCH";
    public const string FingerprintMissing = "PCN_FP_MISSING";
    public const string VrOrphan = "PCN_VR_ORPHAN";
    public const string VrStateInconsistent = "PCN_VR_STATE";
    public const string SupplierDocDuplicate = "PCN_SUPPLIER_DOC";
    public const string InternalAdjustmentDenied = "PCN_INTERNAL_ADJ";
}

/// <summary>
/// Reason-code metadata. Evaluated together with the document <c>Type</c>: the CN rule
/// "posted INV, same vendor" is enforced before any of this and can never be relaxed here.
/// </summary>
/// <param name="InventoryCapable">The reason <i>can</i> be settled by returning stock. The actual
/// inventory effect is decided by <c>ReturnStock</c>/<c>IsStockReturn</c> alone.</param>
public sealed record PoCdnReasonInfo(
    string Code,
    bool InventoryCapable,
    bool RequiresPo,
    bool RequiresInvoice,
    bool InternalAdjustment,
    bool QuantityCeiling,
    bool ValueCeiling);

public static class PoCdnCalc
{
    private const char FieldSep = '\u001E';
    private const char RecordSep = '\u001F';

    private static readonly IReadOnlyDictionary<string, PoCdnReasonInfo> CnReasons =
        new Dictionary<string, PoCdnReasonInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["RETURN"] = new("RETURN", true, true, true, false, true, false),
            ["DAMAGED"] = new("DAMAGED", true, true, true, false, true, false),
            ["SHORT_SUPPLY"] = new("SHORT_SUPPLY", true, true, true, false, true, false),
            ["PRICE_ADJUSTMENT"] = new("PRICE_ADJUSTMENT", false, false, true, false, false, true),
            ["REBATE"] = new("REBATE", false, false, true, false, false, false),
            ["OVERBILL"] = new("OVERBILL", false, false, true, false, false, true),
            ["TAX_ADJUSTMENT"] = new("TAX_ADJUSTMENT", false, false, true, false, false, false),
            ["INTERNAL_ADJUSTMENT"] = new("INTERNAL_ADJUSTMENT", false, false, false, true, false, false),
            ["OTHER"] = new("OTHER", false, false, true, false, false, false)
        };

    private static readonly IReadOnlyDictionary<string, PoCdnReasonInfo> DnReasons =
        new Dictionary<string, PoCdnReasonInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["PRICE_ADJUSTMENT"] = new("PRICE_ADJUSTMENT", false, false, false, false, false, false),
            ["QUANTITY_ADJUSTMENT"] = new("QUANTITY_ADJUSTMENT", false, false, false, false, false, false),
            ["FREIGHT_ADJUSTMENT"] = new("FREIGHT_ADJUSTMENT", false, false, false, false, false, false),
            ["TAX_ADJUSTMENT"] = new("TAX_ADJUSTMENT", false, false, false, false, false, false),
            ["REBATE_REVERSAL"] = new("REBATE_REVERSAL", false, false, false, false, false, false),
            ["SUPPLIER_CLAIM"] = new("SUPPLIER_CLAIM", false, false, false, false, false, false),
            ["INTERNAL_ADJUSTMENT"] = new("INTERNAL_ADJUSTMENT", false, false, false, true, false, false),
            ["OTHER"] = new("OTHER", false, false, false, false, false, false)
        };

    public static bool IsValidType(string? type)
    {
        var t = NormalizeType(type);
        return t is PoCdnTypes.CreditNote or PoCdnTypes.DebitNote;
    }

    public static string NormalizeType(string? type) =>
        (type ?? string.Empty).Trim().ToUpperInvariant();

    public static string NumberingModuleFor(string? type) =>
        NormalizeType(type) == PoCdnTypes.DebitNote
            ? PoCdnLimits.DebitNoteModule
            : PoCdnLimits.CreditNoteModule;

    public static IReadOnlyList<PoCdnReasonInfo> ReasonsFor(string? type) =>
        (NormalizeType(type) == PoCdnTypes.DebitNote
            ? DnReasons.Values
            : CnReasons.Values)
        .ToList();

    public static PoCdnReasonInfo? GetReasonInfo(string? type, string? reasonCode)
    {
        var code = (reasonCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            return null;
        }

        var table = NormalizeType(type) == PoCdnTypes.DebitNote ? DnReasons : CnReasons;
        return table.TryGetValue(code, out var info) ? info : null;
    }

    public static bool IsValidReasonCode(string? type, string? reasonCode) =>
        GetReasonInfo(type, reasonCode) is not null;

    public static bool IsInternalAdjustment(string? type, string? reasonCode) =>
        GetReasonInfo(type, reasonCode)?.InternalAdjustment == true;

    public static bool HasQuantityCeiling(string? type, string? reasonCode) =>
        GetReasonInfo(type, reasonCode)?.QuantityCeiling == true;

    public static bool HasValueCeiling(string? type, string? reasonCode) =>
        GetReasonInfo(type, reasonCode)?.ValueCeiling == true;

    // ─────────────────────────── Money ───────────────────────────

    /// <summary>
    /// Purchase money is always 2 dp <see cref="MidpointRounding.AwayFromZero"/>: the purchase
    /// master has no per-vendor 0-decimal flag.
    /// </summary>
    public static decimal Money(decimal value) => PoOrderCalc.RoundMoney(value);

    public static decimal Qty(decimal value) => PoOrderCalc.RoundQty(value);

    public static decimal SumMoney(IEnumerable<decimal> amounts)
    {
        decimal sum = 0m;
        foreach (var a in amounts)
        {
            sum += Money(a);
        }

        return Money(sum);
    }

    // ─────────────────────────── C42: conversion snapshot ───────────────────────────

    /// <summary>
    /// C42: a historical document is never reinterpreted with current master data. The caller
    /// resolves the pack size from the stored snapshot appropriate to the line kind and passes it
    /// here; this method only performs the conversion.
    /// </summary>
    public static decimal ComputeStdQty(decimal documentQty, decimal packSz) =>
        PoOrderCalc.ComputeStdQty(documentQty, packSz);

    /// <summary>Effective conversion factor: zero means 1 (mirrors PoOrderCalc.EffectivePackSize).</summary>
    public static decimal EffectivePackSize(decimal packSz) => PoOrderCalc.EffectivePackSize(packSz);

    /// <summary>A conversion factor must be usable; C26 rejects a missing or non-positive factor.</summary>
    public static bool IsUsablePackSize(decimal? packSz) => packSz is > 0m;

    // ─────────────────────────── C6: line and header amounts ───────────────────────────

    public static (decimal NetAmount, decimal TaxAmount, decimal Amount) ComputeLineAmounts(
        decimal qty,
        decimal unitPrice,
        decimal itemDiscount,
        string? discountType,
        decimal itemDiscount1,
        string? discountType1,
        decimal taxPercent,
        bool isInclusive,
        int taxDecimals) =>
        PoInvoiceCalc.ComputeLineAmounts(
            qty, unitPrice, itemDiscount, discountType, itemDiscount1, discountType1,
            taxPercent, isInclusive, taxDecimals);

    /// <summary>
    /// Header totals: <c>GrossAmnt = Σ NetAmount</c>, <c>Taxes = Σ TaxAmt</c>,
    /// <c>TotAmnt = GrossAmnt + Taxes</c> exactly (adaptive header rounding, C6).
    /// </summary>
    public static (decimal GrossAmnt, decimal Taxes, decimal TotAmnt) ComputeHeaderTotals(
        IEnumerable<PoCdnDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var (gross, taxes, total) = PoOrderCalc.SumTotals(details.Select(d => (d.NetAmount, d.TaxAmt)));
        return (Money(gross), Money(taxes), Money(total));
    }

    /// <summary>A tax-only line is the only zero-value line shape (C37/C46).</summary>
    public static bool IsTaxOnlyLine(PoCdnDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return detail.NetAmount == 0m && detail.TaxAmt > 0m;
    }

    /// <summary>
    /// C46: the two line shapes are mutually exclusive. Returns null when the line is valid.
    /// </summary>
    public static string? ValidateLineValueContract(PoCdnDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        if (IsTaxOnlyLine(detail))
        {
            if (detail.Qty != 0m)
            {
                return $"Line {detail.Line}: a tax-only line must have zero quantity.";
            }

            if (detail.UnitPrice != 0m)
            {
                return $"Line {detail.Line}: a tax-only line must have zero unit price.";
            }

            if (string.IsNullOrWhiteSpace(detail.TaxGroup))
            {
                return $"Line {detail.Line}: a tax-only line requires a tax group.";
            }

            if (detail.IsStockReturn)
            {
                return $"Line {detail.Line}: a tax-only line cannot be a stock-return line.";
            }

            return null;
        }

        if (detail.Qty <= 0m)
        {
            return $"Line {detail.Line}: quantity must be greater than zero.";
        }

        if (detail.StdQty <= 0m)
        {
            return $"Line {detail.Line}: standard quantity must be greater than zero.";
        }

        if (detail.UnitPrice <= 0m)
        {
            return $"Line {detail.Line}: unit price must be greater than zero.";
        }

        if (detail.Amount <= 0m)
        {
            return $"Line {detail.Line}: line amount must be greater than zero.";
        }

        return null;
    }

    /// <summary>C6: mixed inclusive / exclusive tax across the lines of one document is rejected.</summary>
    public static string? ValidateTaxModeConsistency(IEnumerable<PoCdnDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var inclusive = new List<bool>();
        foreach (var d in details)
        {
            inclusive.Add(d.IsInclusive);
        }

        if (inclusive.Count > 1 && inclusive.Any(x => x) && inclusive.Any(x => !x))
        {
            return "Mixed inclusive and exclusive tax lines are not allowed on one document.";
        }

        return null;
    }

    // ─────────────────────────── C3: header reservation ───────────────────────────

    public static decimal CreditReservationAmount(PoCdn header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return Money(Money(header.GrossAmnt) + Money(header.Taxes));
    }

    public static (bool Ok, decimal Remaining, string? Error) EvaluateRemaining(
        decimal invoiceTotAmnt,
        IReadOnlyList<decimal> otherCnTotAmnts,
        decimal candidateTotAmnt) =>
        EvaluateRemaining(invoiceTotAmnt, otherCnTotAmnts, candidateTotAmnt, draftCnNos: null);

    /// <summary>
    /// Names the NEW credit notes holding part of the invoice's remaining balance, so the operator
    /// is told <i>which</i> drafts reserved it rather than merely that the total was exceeded.
    /// </summary>
    public static (bool Ok, decimal Remaining, string? Error) EvaluateRemaining(
        decimal invoiceTotAmnt,
        IReadOnlyList<decimal> otherCnTotAmnts,
        decimal candidateTotAmnt,
        IReadOnlyList<string>? draftCnNos)
    {
        var invTotal = Money(invoiceTotAmnt);
        var otherSum = SumMoney(otherCnTotAmnts ?? []);
        var remaining = Money(invTotal - otherSum);
        var thisTotal = Money(candidateTotAmnt);

        if (remaining < 0m)
        {
            return (false, remaining, "Invoice remaining is negative (legacy over-credit). Cannot save.");
        }

        if (thisTotal > remaining)
        {
            var reserved = FormatDraftReservation(draftCnNos);
            return (false, remaining,
                $"Credit note total {thisTotal:0.00} exceeds invoice remaining {remaining:0.00}.{reserved}");
        }

        return (true, remaining, null);
    }

    /// <summary>Kept public so the UI indicator and the error message cannot drift apart.</summary>
    public static string FormatDraftReservation(IReadOnlyList<string>? draftCnNos)
    {
        if (draftCnNos is null || draftCnNos.Count == 0)
        {
            return string.Empty;
        }

        var named = draftCnNos
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return named.Count == 0
            ? string.Empty
            : $" Reserved by draft CN(s) {string.Join(", ", named)}.";
    }

    // ─────────────────────────── C34: source-line ceilings ───────────────────────────

    /// <summary>
    /// Quantity ceiling for a line carrying <c>InvLineNo</c>:
    /// <c>candidate &lt;= invoice line StdQty - StdQty already consumed on that line</c>.
    /// </summary>
    public static (bool Ok, decimal Remaining, string? Error) EvaluateSourceLineQuantity(
        short lineNo,
        decimal invoiceLineStdQty,
        decimal consumedStdQty,
        decimal candidateStdQty)
    {
        var remaining = Qty(Qty(invoiceLineStdQty) - Qty(consumedStdQty));
        var candidate = Qty(candidateStdQty);

        if (remaining < 0m)
        {
            return (false, remaining,
                $"Line {lineNo}: the referenced invoice line is already over-consumed. Cannot save.");
        }

        if (candidate > remaining)
        {
            return (false, remaining,
                $"Line {lineNo}: source-line quantity {candidate:n4} exceeds the invoice line remaining {remaining:n4}.");
        }

        return (true, remaining, null);
    }

    /// <summary>
    /// Value ceiling for <c>OVERBILL</c> / <c>PRICE_ADJUSTMENT</c>: the line's creditable amount
    /// must not exceed the source line's remaining creditable value. <c>REBATE</c>,
    /// <c>TAX_ADJUSTMENT</c>, <c>INTERNAL_ADJUSTMENT</c> and <c>OTHER</c> are exempt by design.
    /// </summary>
    public static (bool Ok, decimal Remaining, string? Error) EvaluateSourceLineValue(
        short lineNo,
        decimal invoiceLineAmount,
        decimal consumedAmount,
        decimal candidateAmount)
    {
        var remaining = Money(Money(invoiceLineAmount) - Money(consumedAmount));
        var candidate = Money(candidateAmount);

        if (remaining < 0m)
        {
            return (false, remaining,
                $"Line {lineNo}: the referenced invoice line is already over-credited. Cannot save.");
        }

        if (candidate > remaining)
        {
            return (false, remaining,
                $"Line {lineNo}: source-line value {candidate:0.00} exceeds the invoice line remaining {remaining:0.00}.");
        }

        return (true, remaining, null);
    }

    /// <summary>
    /// C34/C47: the physical ceiling for the <b>combined</b> standard quantity of every line sharing
    /// one PO line. Splitting a return across inventory sources must not exceed
    /// <c>RecvQty - ReturnQty</c>.
    /// </summary>
    public static (bool Ok, decimal Remaining, string? Error) EvaluatePhysicalCeiling(
        string? poNo,
        short? poLineNo,
        decimal recvQty,
        decimal returnQty,
        decimal combinedCandidateStdQty)
    {
        var remaining = Qty(Qty(recvQty) - Qty(returnQty));
        var candidate = Qty(combinedCandidateStdQty);
        var label = string.IsNullOrWhiteSpace(poNo)
            ? "PO line"
            : $"PO {poNo} line {poLineNo}";

        if (remaining <= 0m)
        {
            return (false, remaining, $"{label}: nothing left to return.");
        }

        if (candidate > remaining)
        {
            return (false, remaining,
                $"{label}: return quantity {candidate:n4} exceeds the remaining returnable {remaining:n4}.");
        }

        return (true, remaining, null);
    }

    // ─────────────────────────── C24: invoice-line traceability ───────────────────────────

    /// <summary>
    /// C24: an <c>InvLineNo</c> is only legal against a referenced invoice; a stock-return line
    /// always requires one.
    /// </summary>
    public static string? ValidateInvLineReference(
        PoCdnDetail detail,
        string? headerInvNo,
        short? matchedInvoiceLine,
        string? matchedInvoiceICode,
        string? matchedInvoicePoNo,
        short? matchedInvoicePoRelNo,
        short? matchedInvoicePoLineNo)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var hasHeaderInv = !string.IsNullOrWhiteSpace(headerInvNo);

        if (detail.InvLineNo is not null && !hasHeaderInv)
        {
            return $"Line {detail.Line}: an invoice line reference requires a referenced invoice.";
        }

        if (detail.IsStockReturn && detail.InvLineNo is null)
        {
            return $"Line {detail.Line}: a stock-return line requires the source invoice line.";
        }

        if (detail.InvLineNo is null)
        {
            return null;
        }

        if (matchedInvoiceLine is null)
        {
            return $"Line {detail.Line}: invoice line {detail.InvLineNo} was not found on the referenced invoice.";
        }

        if (!string.IsNullOrWhiteSpace(detail.ICode)
            && !string.Equals(detail.ICode.Trim(), (matchedInvoiceICode ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return $"Line {detail.Line}: item '{detail.ICode}' does not match invoice line {detail.InvLineNo}.";
        }

        // When both sides carry a PO link they must agree, otherwise the traceability is a fiction.
        if (!string.IsNullOrWhiteSpace(detail.PoNo))
        {
            var samePo = string.Equals(detail.PoNo.Trim(), (matchedInvoicePoNo ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
            var sameRel = detail.PoRelNo == matchedInvoicePoRelNo;
            var sameLine = detail.PoLineNo == matchedInvoicePoLineNo;
            if (!samePo || !sameRel || !sameLine)
            {
                return $"Line {detail.Line}: the PO link does not match invoice line {detail.InvLineNo}.";
            }
        }

        return null;
    }

    // ─────────────────────────── C43: stock-return shape ───────────────────────────

    /// <summary>C43: header gate and line declaration must agree.</summary>
    public static string? ValidateStockReturnShape(PoCdn header, PoCdnDetail detail)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(detail);

        if (NormalizeType(header.Type) == PoCdnTypes.DebitNote && detail.IsStockReturn)
        {
            return $"Line {detail.Line}: a debit note line cannot be a stock-return line.";
        }

        if (!header.ReturnStock && detail.IsStockReturn)
        {
            return $"Line {detail.Line}: 'return stock' is not enabled on this document.";
        }

        if (detail.IsStockReturn)
        {
            if (detail.FromBalLocId is null || detail.FromBalLocId <= 0)
            {
                return $"Line {detail.Line}: a stock-return line requires a balance location.";
            }

            if (string.IsNullOrWhiteSpace(detail.PoNo) || detail.PoRelNo is null || detail.PoLineNo is null)
            {
                return $"Line {detail.Line}: a stock-return line requires a PO line.";
            }
        }

        return null;
    }

    // ─────────────────────────── C45: VR fingerprint ───────────────────────────

    /// <summary>
    /// C45: identifies the exact intended return. Any change to any covered field invalidates
    /// batch reuse and forces an explicit rollback rather than a silent re-post.
    /// </summary>
    public static string ComputeSourceFingerprint(IEnumerable<PoCdnDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var rows = details
            .Where(x => x.IsStockReturn)
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
            sb.Append(Norm(d.CompanyCode));
            sb.Append(FieldSep).Append(Norm(d.BranchCode));
            sb.Append(FieldSep).Append(Norm(d.DocNo));
            sb.Append(FieldSep).Append(d.Line.ToString(CultureInfo.InvariantCulture));
            sb.Append(FieldSep).Append(Norm(d.ICode));
            sb.Append(FieldSep).Append(Norm(d.PoNo));
            sb.Append(FieldSep).Append(Norm(d.PoRelNo));
            sb.Append(FieldSep).Append(Norm(d.PoLineNo));
            sb.Append(FieldSep).Append(Norm(d.FromBalLocId));
            sb.Append(FieldSep).Append(Norm(d.FrWarehouse));
            sb.Append(FieldSep).Append(Norm(d.LocCode));
            sb.Append(FieldSep).Append(Norm(d.LotNo));
            sb.Append(FieldSep).Append(Norm(d.IStatus));
            sb.Append(FieldSep).Append(Norm(d.StdQty));
            sb.Append(FieldSep).Append(Norm(d.StdUom));
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

    // ─────────────────────────── C44: supplier-document normalisation ───────────────────────────

    /// <summary>
    /// C44: NULL, '' and whitespace-only all become <c>null</c> — never an empty string — so the
    /// filtered unique index only ever sees real supplier numbers.
    /// </summary>
    public static string? NormalizeSupplierDocNo(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string Norm(string? value) => (value ?? string.Empty).Trim();

    private static string Norm(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Norm(decimal? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Norm(short? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
