using ErpWeb.Core.Services;

namespace ErpWeb.Tests;

/// <summary>
/// Contract tests for the shared validation-message formatter. These pin the three rules that the
/// entry pages depend on: deterministic ordering, a headline that always names a cause, and the
/// guarantee that no row-scoped message is ever collapsed or attributed to the wrong row.
/// </summary>
public class ValidationMessageFormatTests
{
    private static Dictionary<string, string> Errors(params (string Key, string Message)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.Message, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Order_puts_document_level_keys_before_line_keys()
    {
        var ordered = ValidationMessageFormat.Order(Errors(
            ("Lines[0].Qty", "Quantity must be greater than zero."),
            ("TaxGrCode", "Tax group is required."),
            ("Lines[1].ICode", "Item code is required."),
            ("ArGlCode", "Customer AR GL code is missing.")));

        Assert.Equal(
            new[] { "ArGlCode", "TaxGrCode", "Lines[0].Qty", "Lines[1].ICode" },
            ordered.Select(x => x.Key).ToArray());
    }

    [Fact]
    public void Order_sorts_document_keys_alphabetically()
    {
        var ordered = ValidationMessageFormat.Order(Errors(
            ("TaxGlCode", "t"), ("ArGlCode", "a"), ("Currency", "c")));

        Assert.Equal(new[] { "ArGlCode", "Currency", "TaxGlCode" }, ordered.Select(x => x.Key).ToArray());
    }

    [Fact]
    public void Order_sorts_line_keys_by_index_ascending_not_lexically()
    {
        var ordered = ValidationMessageFormat.Order(Errors(
            ("Lines[10].Qty", "q"), ("Lines[2].Qty", "q"), ("Lines[1].Qty", "q")));

        Assert.Equal(
            new[] { "Lines[1].Qty", "Lines[2].Qty", "Lines[10].Qty" },
            ordered.Select(x => x.Key).ToArray());
    }

    [Fact]
    public void Order_ignores_blank_keys_but_keeps_every_other_entry()
    {
        var ordered = ValidationMessageFormat.Order(Errors(
            ("   ", "ignored"), ("CustCode", "Customer is required."), ("PayCode", "Payment term is required.")));

        Assert.Equal(new[] { "CustCode", "PayCode" }, ordered.Select(x => x.Key).ToArray());
    }

    [Fact]
    public void Order_of_null_is_empty() => Assert.Empty(ValidationMessageFormat.Order(null));

    [Theory]
    [InlineData("SellingGlCode", "Sales GL")]
    [InlineData("ArGlCode", "Customer AR GL")]
    [InlineData("Qty", "Quantity")]
    [InlineData("FrWarehouse", "Warehouse")]
    [InlineData("VendorCode", "Supplier")]
    public void Label_maps_known_keys_to_friendly_names(string key, string expected) =>
        Assert.Equal(expected, ValidationMessageFormat.Label(key));

    [Fact]
    public void Label_falls_back_to_the_raw_key_for_an_unmapped_field() =>
        Assert.Equal("SomeNewField", ValidationMessageFormat.Label("SomeNewField"));

    [Theory]
    [InlineData("Lines[0].SellingGlCode", "Line 1 — Sales GL")]
    [InlineData("Lines[9].Qty", "Line 10 — Quantity")]
    [InlineData("Lines[3].UnknownField", "Line 4 — UnknownField")]
    public void Label_rewrites_row_scoped_keys_to_a_1_based_row(string key, string expected) =>
        Assert.Equal(expected, ValidationMessageFormat.Label(key));

    [Theory]
    [InlineData("Lines[0]", "Line 1")]
    [InlineData("Lines", "Lines")]
    [InlineData("", "")]
    public void Label_handles_non_field_keys(string key, string expected) =>
        Assert.Equal(expected, ValidationMessageFormat.Label(key));

    [Theory]
    [InlineData("Lines[0].Qty", true, 0)]
    [InlineData("Lines[12].Qty", true, 12)]
    [InlineData("Lines", false, 0)]
    [InlineData("Currency", false, 0)]
    [InlineData("Lines[].Qty", false, 0)]
    public void TryGetLineIndex_only_accepts_the_row_scoped_shape(string key, bool expected, int index)
    {
        Assert.Equal(expected, ValidationMessageFormat.TryGetLineIndex(key, out var actual));
        if (expected)
        {
            Assert.Equal(index, actual);
        }
    }

    [Fact]
    public void BuildHeadline_never_returns_the_generic_literal_when_the_dictionary_has_content()
    {
        var headline = ValidationMessageFormat.BuildHeadline(
            Errors(("ArGlCode", "Customer AR GL code is missing.")),
            "Validation failed.");

        Assert.Equal("Customer AR GL: Customer AR GL code is missing.", headline);
        Assert.DoesNotContain(ValidationMessageFormat.GenericFallback, headline, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHeadline_reports_every_cause_in_ordered_form()
    {
        var headline = ValidationMessageFormat.BuildHeadline(
            Errors(
                ("Lines[1].Qty", "Quantity must be greater than zero."),
                ("ArGlCode", "Customer AR GL code is missing.")),
            "Validation failed.");

        Assert.Equal(
            "Customer AR GL: Customer AR GL code is missing. Line 2 — Quantity: Quantity must be greater than zero.",
            headline);
    }

    [Fact]
    public void BuildHeadline_falls_back_to_the_server_message_then_the_generic_literal()
    {
        Assert.Equal("Something specific.", ValidationMessageFormat.BuildHeadline(Errors(), "Something specific."));
        Assert.Equal("Validation failed.", ValidationMessageFormat.BuildHeadline(Errors(), null));
        Assert.Equal("Validation failed.", ValidationMessageFormat.BuildHeadline(null, "   "));
        Assert.Equal("Validation failed.", ValidationMessageFormat.BuildHeadline(Errors(("A", "  ")), null));
    }

    [Fact]
    public void BuildHeadline_keeps_two_rows_that_carry_identical_text()
    {
        // The tax-group message carries no row number, so both rows produce exactly the same
        // sentence. Both must still be reported, and the row label is what distinguishes them.
        const string message = "Tax group 'ZZZ' was not found.";
        var headline = ValidationMessageFormat.BuildHeadline(
            Errors(("Lines[0].TaxGrCode", message), ("Lines[1].TaxGrCode", message)),
            "Validation failed.");

        Assert.Equal(
            $"Line 1 — Tax group: {message} Line 2 — Tax group: {message}",
            headline);
    }

    [Fact]
    public void LineMessages_returns_every_message_for_the_requested_row_only()
    {
        var errors = Errors(
            ("Lines[0].Qty", "Quantity must be greater than zero."),
            ("Lines[1].Qty", "Quantity must be greater than zero."),
            ("Lines[1].ICode", "Item code is required."));

        Assert.Equal(
            new[] { "Quantity must be greater than zero." },
            ValidationMessageFormat.LineMessages(errors, 1).ToArray());

        Assert.Equal(
            new[] { "Item code is required.", "Quantity must be greater than zero." },
            ValidationMessageFormat.LineMessages(errors, 2).ToArray());

        Assert.Empty(ValidationMessageFormat.LineMessages(errors, 3));
    }

    [Fact]
    public void LineMessages_does_not_confuse_row_2_with_row_10()
    {
        var errors = Errors(("Lines[1].Qty", "one"), ("Lines[10].Qty", "ten"));

        // Row 1 has nothing; row 2 is Lines[1] and must NOT pick up Lines[10].
        Assert.Empty(ValidationMessageFormat.LineMessages(errors, 1));
        Assert.Equal(new[] { "one" }, ValidationMessageFormat.LineMessages(errors, 2).ToArray());
        Assert.Equal(new[] { "ten" }, ValidationMessageFormat.LineMessages(errors, 11).ToArray());
    }

    [Fact]
    public void LineMessages_ignores_a_zero_or_negative_row() =>
        Assert.Empty(ValidationMessageFormat.LineMessages(
            Errors(("Lines[0].Qty", "Quantity must be greater than zero.")), 0));

    [Fact]
    public void JoinMessages_keeps_duplicate_text_and_falls_back_when_there_is_nothing()
    {
        const string message = "Tax group 'ZZZ' was not found.";

        Assert.Equal(
            $"{message} {message}",
            ValidationMessageFormat.JoinMessages(Errors(
                ("Lines[0].TaxGrCode", message), ("Lines[1].TaxGrCode", message))));

        Assert.Equal("Validation failed.", ValidationMessageFormat.JoinMessages(Errors()));
        Assert.Equal("specific", ValidationMessageFormat.JoinMessages(Errors(), "specific"));
        Assert.Equal("Validation failed.", ValidationMessageFormat.JoinMessages(null));
    }

    [Fact]
    public void ResolveServiceMessage_keeps_a_supplied_reason_code_but_replaces_the_generic_literal()
    {
        var errors = Errors(("Lines", "An invoice cannot mix direct Sales Order lines with Delivery Order lines."));

        // A documented reason code is part of the contract and must survive untouched.
        Assert.Equal("ST000032", ValidationMessageFormat.ResolveServiceMessage(errors, "ST000032"));

        // The generic literal is the bug being fixed, so it is replaced by the concrete causes.
        Assert.Equal(
            "An invoice cannot mix direct Sales Order lines with Delivery Order lines.",
            ValidationMessageFormat.ResolveServiceMessage(errors, "Validation failed."));

        // No message at all still yields the concrete causes.
        Assert.Equal(
            "An invoice cannot mix direct Sales Order lines with Delivery Order lines.",
            ValidationMessageFormat.ResolveServiceMessage(errors, null));
    }
}
