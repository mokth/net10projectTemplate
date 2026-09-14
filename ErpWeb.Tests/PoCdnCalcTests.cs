using ErpWeb.Core.Purchase;
using ErpWeb.Model.Entities.Purchase;

// The Model layer declares its own copies of these constants for repository use; the tests
// target the Core domain constants.
using PoCdnTypes = ErpWeb.Core.Purchase.PoCdnTypes;
using PoCdnStatuses = ErpWeb.Core.Purchase.PoCdnStatuses;

namespace ErpWeb.Tests;

/// <summary>
/// Pure-domain tests for the PoCdn controls (plan C6, C9, C17, C24, C26, C30, C34, C37, C43,
/// C44, C45, C46, C47). No database, no services.
/// </summary>
public class PoCdnCalcTests
{
    private static PoCdnDetail Line(
        short line = 1,
        decimal qty = 1m,
        decimal unitPrice = 10m,
        decimal stdQty = 1m,
        decimal netAmount = 10m,
        decimal taxAmt = 0m,
        decimal amount = 10m,
        bool isStockReturn = false,
        short? invLineNo = null,
        string? iCode = "ITEM1",
        string? poNo = null,
        short? poRelNo = null,
        short? poLineNo = null,
        int? fromBalLocId = null,
        string? lotNo = null,
        string? stdUom = "PCS",
        string? iStatus = "GOOD",
        string? frWarehouse = "WH1",
        string? locCode = "L1",
        string? taxGroup = "SST",
        bool isInclusive = false) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        DocNo = "PCN2609-0001",
        Line = line,
        Qty = qty,
        UnitPrice = unitPrice,
        StdQty = stdQty,
        NetAmount = netAmount,
        TaxAmt = taxAmt,
        Amount = amount,
        IsStockReturn = isStockReturn,
        InvLineNo = invLineNo,
        ICode = iCode,
        PoNo = poNo,
        PoRelNo = poRelNo,
        PoLineNo = poLineNo,
        FromBalLocId = fromBalLocId,
        LotNo = lotNo,
        StdUom = stdUom,
        IStatus = iStatus,
        FrWarehouse = frWarehouse,
        LocCode = locCode,
        TaxGroup = taxGroup,
        IsInclusive = isInclusive
    };

    // ─────────────────────────── C9/C17: reason metadata ───────────────────────────

    [Fact]
    public void Reason_codes_are_type_scoped()
    {
        Assert.True(PoCdnCalc.IsValidReasonCode(PoCdnTypes.CreditNote, "RETURN"));
        Assert.False(PoCdnCalc.IsValidReasonCode(PoCdnTypes.DebitNote, "RETURN"));

        Assert.True(PoCdnCalc.IsValidReasonCode(PoCdnTypes.DebitNote, "FREIGHT_ADJUSTMENT"));
        Assert.False(PoCdnCalc.IsValidReasonCode(PoCdnTypes.CreditNote, "FREIGHT_ADJUSTMENT"));

        // The price-adjustment reason exists on both sides but means different things.
        Assert.True(PoCdnCalc.IsValidReasonCode(PoCdnTypes.CreditNote, "PRICE_ADJUSTMENT"));
        Assert.True(PoCdnCalc.IsValidReasonCode(PoCdnTypes.DebitNote, "PRICE_ADJUSTMENT"));
        Assert.True(PoCdnCalc.GetReasonInfo(PoCdnTypes.CreditNote, "PRICE_ADJUSTMENT")!.RequiresInvoice);
        Assert.False(PoCdnCalc.GetReasonInfo(PoCdnTypes.DebitNote, "PRICE_ADJUSTMENT")!.RequiresInvoice);
    }

    [Fact]
    public void Internal_adjustment_is_the_only_code_without_a_supplier_document()
    {
        Assert.True(PoCdnCalc.IsInternalAdjustment(PoCdnTypes.CreditNote, "INTERNAL_ADJUSTMENT"));
        Assert.True(PoCdnCalc.IsInternalAdjustment(PoCdnTypes.DebitNote, "INTERNAL_ADJUSTMENT"));
        Assert.False(PoCdnCalc.IsInternalAdjustment(PoCdnTypes.CreditNote, "RETURN"));
    }

    [Fact]
    public void Ceilings_are_reason_based_with_explicit_exemptions()
    {
        // Return-like reasons are quantity-capped.
        Assert.True(PoCdnCalc.HasQuantityCeiling(PoCdnTypes.CreditNote, "DAMAGED"));
        Assert.False(PoCdnCalc.HasValueCeiling(PoCdnTypes.CreditNote, "DAMAGED"));

        // Overbill and price adjustment are value-capped.
        Assert.True(PoCdnCalc.HasValueCeiling(PoCdnTypes.CreditNote, "OVERBILL"));
        Assert.True(PoCdnCalc.HasValueCeiling(PoCdnTypes.CreditNote, "PRICE_ADJUSTMENT"));

        // Rebate, tax adjustment, internal adjustment and other are exempt from source-line ceilings.
        Assert.False(PoCdnCalc.HasQuantityCeiling(PoCdnTypes.CreditNote, "REBATE"));
        Assert.False(PoCdnCalc.HasValueCeiling(PoCdnTypes.CreditNote, "REBATE"));
        Assert.False(PoCdnCalc.HasValueCeiling(PoCdnTypes.CreditNote, "TAX_ADJUSTMENT"));
        Assert.False(PoCdnCalc.HasValueCeiling(PoCdnTypes.CreditNote, "INTERNAL_ADJUSTMENT"));
        Assert.False(PoCdnCalc.HasValueCeiling(PoCdnTypes.CreditNote, "OTHER"));
    }

    [Fact]
    public void Numbering_module_is_the_document_type()
    {
        Assert.Equal("PCN", PoCdnCalc.NumberingModuleFor("CN"));
        Assert.Equal("PDN", PoCdnCalc.NumberingModuleFor("DN"));
        Assert.Equal("PCN", PoCdnCalc.NumberingModuleFor(null));
    }

    // ─────────────────────────── C3: header reservation ───────────────────────────

    [Fact]
    public void Reservation_accepts_a_total_that_exactly_equals_the_remaining()
    {
        var (ok, remaining, error) = PoCdnCalc.EvaluateRemaining(1000m, [400m], 600m);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(600m, remaining);
    }

    [Fact]
    public void Reservation_rejects_one_cent_over_and_names_the_draft()
    {
        var (ok, remaining, error) = PoCdnCalc.EvaluateRemaining(
            1000m, [400m], 600.01m, ["PCN2609-0009"]);

        Assert.False(ok);
        Assert.Equal(600m, remaining);
        Assert.Contains("reserved by draft CN(s) PCN2609-0009", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Draft_credit_notes_consume_the_reservation_exactly_like_posted_ones()
    {
        // C39: a rolled-back (NEW) CN keeps consuming the invoice, because a NEW CN is counted.
        var (ok, _, _) = PoCdnCalc.EvaluateRemaining(1000m, [500m, 400m], 100m);
        Assert.True(ok);

        var (exceeds, _, _) = PoCdnCalc.EvaluateRemaining(1000m, [500m, 400m], 100.01m);
        Assert.False(exceeds);
    }

    [Fact]
    public void Reservation_fails_closed_on_a_legacy_negative_remaining()
    {
        var (ok, _, error) = PoCdnCalc.EvaluateRemaining(100m, [150m], 1m);

        Assert.False(ok);
        Assert.Contains("negative", error, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────── C34: source-line ceilings ───────────────────────────

    [Fact]
    public void Source_line_quantity_ceiling_uses_the_remaining_after_other_credit_notes()
    {
        var (ok, remaining, _) = PoCdnCalc.EvaluateSourceLineQuantity(1, 10m, 8m, 2m);
        Assert.True(ok);
        Assert.Equal(2m, remaining);

        var (exceeds, _, error) = PoCdnCalc.EvaluateSourceLineQuantity(1, 10m, 8m, 2.0001m);
        Assert.False(exceeds);
        Assert.Contains("exceeds the invoice line remaining", error);
    }

    [Fact]
    public void Source_line_quantity_ceiling_rejects_an_already_over_consumed_line()
    {
        var (ok, _, error) = PoCdnCalc.EvaluateSourceLineQuantity(3, 5m, 6m, 0m);

        Assert.False(ok);
        Assert.Contains("already over-consumed", error);
    }

    [Fact]
    public void Source_line_value_ceiling_is_money_rounded()
    {
        // Consumption is normalised to 2 dp before comparison, so 40.005 counts as 40.01.
        var (ok, remaining, _) = PoCdnCalc.EvaluateSourceLineValue(1, 100.00m, 40.005m, 59.99m);
        Assert.True(ok);
        Assert.Equal(59.99m, remaining);

        // Exactly at the remaining ceiling is accepted.
        var (exact, _, _) = PoCdnCalc.EvaluateSourceLineValue(1, 100.00m, 40.01m, 59.99m);
        Assert.True(exact);

        // One cent beyond the remaining ceiling is rejected.
        var (exceeds, _, _) = PoCdnCalc.EvaluateSourceLineValue(1, 100.00m, 40.01m, 60.00m);
        Assert.False(exceeds);
    }

    [Fact]
    public void Physical_ceiling_covers_the_combined_quantity_of_one_po_line()
    {
        // C47: received 10, already returned 8, two lines of 1 each → combined 2 is the limit.
        var (ok, remaining, _) = PoCdnCalc.EvaluatePhysicalCeiling("PO1", 1, 10m, 8m, 2m);
        Assert.True(ok);
        Assert.Equal(2m, remaining);

        var (exceeds, _, error) = PoCdnCalc.EvaluatePhysicalCeiling("PO1", 1, 10m, 8m, 3m);
        Assert.False(exceeds);
        Assert.Contains("exceeds the remaining returnable", error);
    }

    [Fact]
    public void Physical_ceiling_reports_nothing_left()
    {
        var (ok, _, error) = PoCdnCalc.EvaluatePhysicalCeiling("PO1", 1, 10m, 10m, 1m);

        Assert.False(ok);
        Assert.Contains("nothing left to return", error);
    }

    // ─────────────────────────── C46/C37: line value contract ───────────────────────────

    [Fact]
    public void Normal_line_requires_a_positive_price()
    {
        var error = PoCdnCalc.ValidateLineValueContract(
            Line(qty: 2m, unitPrice: 0m, stdQty: 2m, netAmount: 0m, amount: 0m));

        Assert.NotNull(error);
        Assert.Contains("unit price must be greater than zero", error);
    }

    [Fact]
    public void Normal_line_with_a_positive_price_is_valid()
    {
        Assert.Null(PoCdnCalc.ValidateLineValueContract(Line()));
    }

    [Fact]
    public void Tax_only_line_is_defined_by_zero_quantity_and_price()
    {
        var taxOnly = Line(qty: 0m, unitPrice: 0m, stdQty: 0m, netAmount: 0m, taxAmt: 5m, amount: 5m);

        Assert.True(PoCdnCalc.IsTaxOnlyLine(taxOnly));
        Assert.Null(PoCdnCalc.ValidateLineValueContract(taxOnly));
    }

    [Fact]
    public void Tax_only_line_rejects_a_non_zero_quantity()
    {
        var bad = Line(qty: 1m, unitPrice: 0m, stdQty: 1m, netAmount: 0m, taxAmt: 5m, amount: 5m);

        var error = PoCdnCalc.ValidateLineValueContract(bad);

        Assert.NotNull(error);
        Assert.Contains("tax-only line must have zero quantity", error);
    }

    [Fact]
    public void Tax_only_line_cannot_return_stock()
    {
        var bad = Line(qty: 0m, unitPrice: 0m, stdQty: 0m, netAmount: 0m, taxAmt: 5m, amount: 5m,
            isStockReturn: true);

        var error = PoCdnCalc.ValidateLineValueContract(bad);

        Assert.NotNull(error);
        Assert.Contains("cannot be a stock-return line", error);
    }

    [Fact]
    public void Tax_only_line_requires_a_tax_group()
    {
        var bad = Line(qty: 0m, unitPrice: 0m, stdQty: 0m, netAmount: 0m, taxAmt: 5m, amount: 5m,
            taxGroup: null);

        var error = PoCdnCalc.ValidateLineValueContract(bad);

        Assert.NotNull(error);
        Assert.Contains("requires a tax group", error);
    }

    [Fact]
    public void Mixed_inclusive_and_exclusive_tax_on_one_document_is_rejected()
    {
        var lines = new[] { Line(line: 1, isInclusive: true), Line(line: 2, isInclusive: false) };

        Assert.NotNull(PoCdnCalc.ValidateTaxModeConsistency(lines));
    }

    [Fact]
    public void Uniform_tax_mode_is_allowed()
    {
        var lines = new[] { Line(line: 1, isInclusive: true), Line(line: 2, isInclusive: true) };

        Assert.Null(PoCdnCalc.ValidateTaxModeConsistency(lines));
    }

    // ─────────────────────────── C6: totals ───────────────────────────

    [Fact]
    public void Header_total_is_exactly_gross_plus_taxes()
    {
        var lines = new[]
        {
            Line(line: 1, netAmount: 10.005m, taxAmt: 0.665m),
            Line(line: 2, netAmount: 20.005m, taxAmt: 1.33m)
        };

        var (gross, taxes, total) = PoCdnCalc.ComputeHeaderTotals(lines);

        Assert.Equal(30.01m, gross);
        Assert.Equal(2.0m, taxes);
        Assert.Equal(gross + taxes, total);
    }

    [Fact]
    public void Money_rounds_away_from_zero_at_two_decimals()
    {
        Assert.Equal(0.01m, PoCdnCalc.Money(0.005m));
        Assert.Equal(0.02m, PoCdnCalc.Money(0.015m));
        Assert.Equal(-0.01m, PoCdnCalc.Money(-0.005m));
    }

    // ─────────────────────────── C26/C42: snapshot conversion ───────────────────────────

    [Fact]
    public void StdQty_uses_the_supplied_conversion_snapshot()
    {
        // The invoice was posted with a pack size of 10 and stays 10 even if the item master
        // later says 12 — the caller passes the stored snapshot, never current master data.
        Assert.Equal(20m, PoCdnCalc.ComputeStdQty(2m, 10m));
        Assert.Equal(24m, PoCdnCalc.ComputeStdQty(2m, 12m));
    }

    [Fact]
    public void Pack_size_gate_rejects_a_missing_or_zero_factor()
    {
        Assert.False(PoCdnCalc.IsUsablePackSize(0m));
        Assert.False(PoCdnCalc.IsUsablePackSize(null));
        Assert.True(PoCdnCalc.IsUsablePackSize(1m));
        Assert.Equal(1m, PoCdnCalc.EffectivePackSize(0m));
    }

    // ─────────────────────────── C24: invoice-line traceability ───────────────────────────

    [Fact]
    public void Stock_return_line_requires_a_source_invoice_line()
    {
        var detail = Line(isStockReturn: true, invLineNo: null, poNo: "PO1", poRelNo: 1, poLineNo: 1,
            fromBalLocId: 5);

        var error = PoCdnCalc.ValidateInvLineReference(detail, "PINV1", null, null, null, null, null);

        Assert.NotNull(error);
        Assert.Contains("requires the source invoice line", error);
    }

    [Fact]
    public void Invoice_line_reference_requires_a_referenced_invoice()
    {
        var detail = Line(invLineNo: 1);

        var error = PoCdnCalc.ValidateInvLineReference(detail, null, 1, "ITEM1", null, null, null);

        Assert.NotNull(error);
        Assert.Contains("requires a referenced invoice", error);
    }

    [Fact]
    public void Invoice_line_reference_must_resolve()
    {
        var detail = Line(invLineNo: 3);

        var error = PoCdnCalc.ValidateInvLineReference(detail, "PINV1", null, null, null, null, null);

        Assert.NotNull(error);
        Assert.Contains("was not found on the referenced invoice", error);
    }

    [Fact]
    public void Invoice_line_item_must_match()
    {
        var detail = Line(invLineNo: 1, iCode: "ITEM1");

        var error = PoCdnCalc.ValidateInvLineReference(detail, "PINV1", 1, "ITEM9", null, null, null);

        Assert.NotNull(error);
        Assert.Contains("does not match invoice line", error);
    }

    [Fact]
    public void Invoice_line_po_link_must_agree_when_both_sides_carry_one()
    {
        var detail = Line(invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 2);

        var error = PoCdnCalc.ValidateInvLineReference(detail, "PINV1", 1, "ITEM1", "PO1", 1, 3);

        Assert.NotNull(error);
        Assert.Contains("PO link does not match", error);
    }

    [Fact]
    public void A_header_level_line_without_an_invoice_line_is_valid()
    {
        var detail = Line(invLineNo: null);

        Assert.Null(PoCdnCalc.ValidateInvLineReference(detail, "PINV1", null, null, null, null, null));
    }

    // ─────────────────────────── C43: stock-return shape ───────────────────────────

    [Fact]
    public void Line_cannot_return_stock_when_the_header_gate_is_off()
    {
        var header = new PoCdn { Type = PoCdnTypes.CreditNote, ReturnStock = false };
        var detail = Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1,
            fromBalLocId: 5);

        var error = PoCdnCalc.ValidateStockReturnShape(header, detail);

        Assert.NotNull(error);
        Assert.Contains("not enabled on this document", error);
    }

    [Fact]
    public void Debit_note_line_can_never_return_stock()
    {
        var header = new PoCdn { Type = PoCdnTypes.DebitNote, ReturnStock = true };
        var detail = Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1,
            fromBalLocId: 5);

        var error = PoCdnCalc.ValidateStockReturnShape(header, detail);

        Assert.NotNull(error);
        Assert.Contains("debit note line cannot be a stock-return line", error);
    }

    [Fact]
    public void Stock_return_line_requires_a_balance_location_and_a_po_link()
    {
        var header = new PoCdn { Type = PoCdnTypes.CreditNote, ReturnStock = true };

        var noBalance = PoCdnCalc.ValidateStockReturnShape(
            header, Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1));
        Assert.Contains("requires a balance location", noBalance);

        var noPo = PoCdnCalc.ValidateStockReturnShape(
            header, Line(isStockReturn: true, invLineNo: 1, fromBalLocId: 5));
        Assert.Contains("requires a PO line", noPo);

        var valid = PoCdnCalc.ValidateStockReturnShape(
            header, Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5));
        Assert.Null(valid);
    }

    // ─────────────────────────── C44: supplier-document normalisation ───────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_supplier_document_numbers_become_null(string? value)
    {
        Assert.Null(PoCdnCalc.NormalizeSupplierDocNo(value));
    }

    [Fact]
    public void Supplier_document_number_is_trimmed()
    {
        Assert.Equal("SUP-1", PoCdnCalc.NormalizeSupplierDocNo("  SUP-1  "));
    }

    // ─────────────────────────── C45: VR fingerprint ───────────────────────────

    [Fact]
    public void Fingerprint_is_stable_for_the_same_intent()
    {
        var a = new[] { Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5) };
        var b = new[] { Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5) };

        Assert.Equal(
            PoCdnCalc.ComputeSourceFingerprint(a),
            PoCdnCalc.ComputeSourceFingerprint(b));
    }

    [Fact]
    public void Fingerprint_changes_when_any_covered_field_changes()
    {
        var baseline = PoCdnCalc.ComputeSourceFingerprint(
            [Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5)]);

        // Quantity.
        Assert.NotEqual(baseline, PoCdnCalc.ComputeSourceFingerprint(
            [Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5, stdQty: 2m)]));

        // Lot (the whole point of C45 — a quantity+location fingerprint would have missed this).
        Assert.NotEqual(baseline, PoCdnCalc.ComputeSourceFingerprint(
            [Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5, lotNo: "LOT-A")]));

        // Item status.
        Assert.NotEqual(baseline, PoCdnCalc.ComputeSourceFingerprint(
            [Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5, iStatus: "HOLD")]));

        // Balance source.
        Assert.NotEqual(baseline, PoCdnCalc.ComputeSourceFingerprint(
            [Line(isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 6)]));
    }

    [Fact]
    public void Fingerprint_ignores_lines_that_are_not_stock_returns()
    {
        var withFinancial = new[]
        {
            Line(line: 1, isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5),
            Line(line: 2, isStockReturn: false, iCode: "SERVICE1")
        };
        var stockOnly = new[]
        {
            Line(line: 1, isStockReturn: true, invLineNo: 1, poNo: "PO1", poRelNo: 1, poLineNo: 1, fromBalLocId: 5)
        };

        Assert.Equal(
            PoCdnCalc.ComputeSourceFingerprint(stockOnly),
            PoCdnCalc.ComputeSourceFingerprint(withFinancial));
    }

    [Fact]
    public void Fingerprint_validator_rejects_anything_that_is_not_a_sha256_hex_string()
    {
        Assert.False(PoCdnCalc.IsValidFingerprint(null));
        Assert.False(PoCdnCalc.IsValidFingerprint(""));
        Assert.False(PoCdnCalc.IsValidFingerprint("abc"));
        Assert.False(PoCdnCalc.IsValidFingerprint(new string('z', 64)));
        Assert.True(PoCdnCalc.IsValidFingerprint(new string('a', 64)));
    }

    // ─────────────────────────── Supporting rules ───────────────────────────

    [Fact]
    public void Credit_reservation_amount_is_gross_plus_taxes()
    {
        var header = new PoCdn { GrossAmnt = 100.00m, Taxes = 6.00m };

        Assert.Equal(106.00m, PoCdnCalc.CreditReservationAmount(header));
    }

    [Fact]
    public void Vr_reference_is_system_owned_and_prefixed()
    {
        Assert.Equal("PCN/PCN2609-0001", PoCdnSpRefs.ToVrRefNo(" PCN2609-0001 "));
        Assert.StartsWith("PCN/", PoCdnSpRefs.ToVrRefNo("X"), StringComparison.Ordinal);
    }
}
