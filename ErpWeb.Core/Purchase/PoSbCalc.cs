using ErpWeb.Core.EInvoice;
using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

/// <summary>ERP document status of a self-billed purchase document.</summary>
public static class PoSbStatuses
{
    public const string New = "NEW";

    /// <summary>
    /// LEGACY value: finalised under the retired ERP lifecycle. Nothing writes it any more — the
    /// e-Invoice state is the only lifecycle for these documents — but a row created before that
    /// change may still hold it, so code that READS the status column must tolerate it. Kept as the
    /// single definition of the value (the list status filter still offers it).
    /// </summary>
    public const string Posted = "POSTED";
}

/// <summary>Self-billed credit (12) / debit (13) note types.</summary>
public static class PoSbTypes
{
    public const string CreditNote = "CN";
    public const string DebitNote = "DN";
}

public static class PoSbLimits
{
    /// <summary>
    /// Cap on a batch action. Now bounds ONLY delete (post/rollback were retired with the ERP
    /// NEW/POSTED dimension); the name is kept for parity with the other document families.
    /// </summary>
    public const int MaxPostSelection = 3;
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>Numbering module for the self-billed invoice (AdSmNum/AdSmNumDate <c>NumCd</c>).</summary>
    public const string InvoiceNumberingModule = "PO_SBI";

    public const string CreditNoteNumberingModule = "PO_SBC";
    public const string DebitNoteNumberingModule = "PO_SBD";

    /// <summary>
    /// Decimal places for tax rounding. Matches the shipped purchase documents (the purchase entry
    /// screens use 2), so a self-billed line rounds exactly like a PO invoice line.
    /// </summary>
    public const int TaxDecimals = 2;
}

/// <summary>
/// Pure rules and money maths for the self-billed documents. The maths is deliberately delegated to
/// <see cref="PoOrderCalc"/> / <see cref="PoPrCalc"/> so a self-billed line rounds at the same point as
/// every other purchase document — the header totals are then derived, never hand-assigned.
/// </summary>
public static class PoSbCalc
{
    public static bool IsValidType(string? type) =>
        NormalizeType(type) is PoSbTypes.CreditNote or PoSbTypes.DebitNote;

    public static string NormalizeType(string? type) =>
        (type ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Numbering module for a note type, or the invoice module when no type is given.</summary>
    public static string NumberingModuleFor(string? type) => NormalizeType(type) switch
    {
        PoSbTypes.CreditNote => PoSbLimits.CreditNoteNumberingModule,
        PoSbTypes.DebitNote => PoSbLimits.DebitNoteNumberingModule,
        _ => PoSbLimits.InvoiceNumberingModule
    };

    /// <summary>
    /// True while the e-Invoice status forbids structural edits (SUBMITTING / SUBMITTED / VALID).
    /// One definition, shared by both self-billed services, mirroring the shipped sales guard.
    /// </summary>
    public static bool IsEInvoiceLocked(string? irbmStatus) => EInvoiceStatuses.IsLocked(irbmStatus);

    /// <summary>
    /// Line extension: amount → two-level discount → tax. Returns (net excluding tax, tax, gross before
    /// discount) with money rounded at each step exactly as <see cref="PoOrderCalc"/> does.
    /// </summary>
    public static (decimal NetAmount, decimal TaxAmount, decimal Amount) ComputeLineAmounts(
        decimal qty,
        decimal unitPrice,
        decimal itemDiscount,
        string? discountType,
        decimal itemDiscount1,
        string? discountType1,
        decimal taxPercent,
        bool isInclusive)
    {
        var amount = PoOrderCalc.ComputeAmount(qty, unitPrice);
        var discounted = PoOrderCalc.ApplyTwoLevelDiscount(
            amount, itemDiscount, discountType, itemDiscount1, discountType1);
        var (net, tax) = PoOrderCalc.ComputeTax(
            discounted, taxPercent, isInclusive, PoSbLimits.TaxDecimals);
        return (net, tax, amount);
    }

    /// <summary>
    /// Derives the header totals from the lines: <c>GrossAmnt = Σ NetAmount</c>, <c>Taxes = Σ TaxAmt</c>
    /// and <c>TotAmnt = GrossAmnt + Taxes</c>. The e-Invoice validator cross-checks the header against
    /// the sum of the lines, so assigning them anywhere else is a defect.
    /// </summary>
    public static void ApplyHeaderTotals(PoSbInvoice header, IEnumerable<PoSbInvoiceDetail> details)
    {
        ArgumentNullException.ThrowIfNull(header);
        var (gross, taxes, total) = PoOrderCalc.SumTotals(details.Select(d => (d.NetAmount, d.TaxAmt)));
        header.GrossAmnt = gross;
        header.Taxes = taxes;
        header.TotAmnt = total;
    }

    /// <inheritdoc cref="ApplyHeaderTotals(PoSbInvoice, IEnumerable{PoSbInvoiceDetail})" />
    public static void ApplyHeaderTotals(PoSbCdn header, IEnumerable<PoSbCdnDetail> details)
    {
        ArgumentNullException.ThrowIfNull(header);
        var (gross, taxes, total) = PoOrderCalc.SumTotals(details.Select(d => (d.NetAmount, d.TaxAmt)));
        header.GrossAmnt = gross;
        header.Taxes = taxes;
        header.TotAmnt = total;
    }

    /// <summary>Trim-to-null, the house idiom for optional text columns.</summary>
    public static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Truncates to a column width so a long caller value cannot fail the INSERT.</summary>
    public static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    // ── Line rules ─────────────────────────────────────────────────────────────
    // The e-Invoice validator is the authority at submit time (Qty > 0, a UNECE UOM and a non-blank
    // classification code). These checks apply the same rules at SAVE time so an operator is told on
    // the line they typed rather than at submission. Error keys mirror EInvoiceValidator's
    // (Line{n}.Field) so the same screen mapping works for both.

    private const int MaxLineCount = 500;

    public static decimal ResolveTaxPercent(
        string? taxGroupCode, IReadOnlyDictionary<string, decimal> taxPercents)
    {
        if (string.IsNullOrWhiteSpace(taxGroupCode))
        {
            return 0m;
        }

        return taxPercents.TryGetValue(taxGroupCode.Trim(), out var percent) ? percent : 0m;
    }

    /// <summary>Validates the request lines and writes field-keyed errors. Returns true when valid.</summary>
    public static bool ValidateLines(
        IReadOnlyList<PoSbLineRequest> lines,
        IReadOnlyDictionary<string, decimal> taxPercents,
        IDictionary<string, string> errors)
    {
        if (lines.Count == 0)
        {
            errors["Lines"] = "At least one line is required.";
            return false;
        }

        if (lines.Count > MaxLineCount)
        {
            errors["Lines"] = $"A document cannot have more than {MaxLineCount} lines.";
            return false;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var key = $"Line{i + 1}";
            var label = string.IsNullOrWhiteSpace(line.ICode) ? $"line {i + 1}" : line.ICode!.Trim();

            if (string.IsNullOrWhiteSpace(line.ICode))
            {
                errors[key + ".ICode"] = $"Item code is required on {label}.";
            }

            if (string.IsNullOrWhiteSpace(line.IDesc))
            {
                errors[key + ".IDesc"] = $"Item description is required on {label}.";
            }

            // The e-Invoice validator refuses Qty <= 0, so a zero-qty line can never be submitted.
            if (line.Qty <= 0m)
            {
                errors[key + ".Qty"] = $"Quantity must be greater than zero on {label}.";
            }

            if (line.UnitPrice < 0m)
            {
                errors[key + ".UnitPrice"] = $"Unit price cannot be negative on {label}.";
            }

            if (string.IsNullOrWhiteSpace(line.StdUom))
            {
                errors[key + ".Uom"] = $"Unit of measure is required on {label}.";
            }

            if (string.IsNullOrWhiteSpace(line.Classification))
            {
                errors[key + ".Classification"] = $"Item classification code is required on {label}.";
            }

            if (!string.IsNullOrWhiteSpace(line.TaxGroup)
                && !taxPercents.ContainsKey(line.TaxGroup.Trim()))
            {
                errors[key + ".TaxGroup"] =
                    $"Tax group '{line.TaxGroup.Trim()}' does not exist on {label}.";
            }
        }

        return errors.Count == 0;
    }

    /// <summary>Builds a stored invoice line from a request line. Amounts are computed, never trusted.</summary>
    public static PoSbInvoiceDetail BuildInvoiceDetail(
        short lineNo, PoSbLineRequest request, decimal taxPercent)
    {
        var (net, tax, amount) = ComputeLineAmounts(
            request.Qty,
            request.UnitPrice,
            request.ItemDiscount,
            request.IDiscountType,
            request.ItemDiscount1,
            request.IDiscountType1,
            taxPercent,
            request.IsInclusive);

        return new PoSbInvoiceDetail
        {
            Line = lineNo,
            ICode = Truncate(request.ICode, 30),
            IDesc = Truncate(request.IDesc, 200),
            Qty = PoOrderCalc.RoundQty(request.Qty),
            UnitPrice = request.UnitPrice,
            SellingUom = Truncate(request.SellingUom, 10),
            StdUom = Truncate(request.StdUom, 10),
            Amount = amount,
            TaxAmt = tax,
            NetAmount = net,
            TaxGroup = Truncate(request.TaxGroup, 20),
            IsInclusive = request.IsInclusive,
            Discount = request.ItemDiscount,
            ItemDiscount = request.ItemDiscount,
            ItemDiscount1 = request.ItemDiscount1,
            IDiscountType = Truncate(request.IDiscountType, 20),
            IDiscountType1 = Truncate(request.IDiscountType1, 20),
            Classification = Truncate(request.Classification, 50),
            Remarks = Truncate(request.Remarks, 250)
        };
    }

    /// <inheritdoc cref="BuildInvoiceDetail(short, PoSbLineRequest, decimal)" />
    public static PoSbCdnDetail BuildCdnDetail(
        short lineNo, PoSbLineRequest request, decimal taxPercent)
    {
        var (net, tax, amount) = ComputeLineAmounts(
            request.Qty,
            request.UnitPrice,
            request.ItemDiscount,
            request.IDiscountType,
            request.ItemDiscount1,
            request.IDiscountType1,
            taxPercent,
            request.IsInclusive);

        return new PoSbCdnDetail
        {
            Line = lineNo,
            ICode = Truncate(request.ICode, 30),
            IDesc = Truncate(request.IDesc, 200),
            Qty = PoOrderCalc.RoundQty(request.Qty),
            UnitPrice = request.UnitPrice,
            SellingUom = Truncate(request.SellingUom, 10),
            StdUom = Truncate(request.StdUom, 10),
            Amount = amount,
            TaxAmt = tax,
            NetAmount = net,
            TaxGroup = Truncate(request.TaxGroup, 20),
            IsInclusive = request.IsInclusive,
            Discount = request.ItemDiscount,
            ItemDiscount = request.ItemDiscount,
            ItemDiscount1 = request.ItemDiscount1,
            IDiscountType = Truncate(request.IDiscountType, 20),
            IDiscountType1 = Truncate(request.IDiscountType1, 20),
            Classification = Truncate(request.Classification, 50),
            Remarks = Truncate(request.Remarks, 250)
        };
    }
}
