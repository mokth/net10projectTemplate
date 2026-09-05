using ErpWeb.Core.Numbering;

namespace ErpWeb.Tests;

public class DocumentNumberFormatterTests
{
    [Fact]
    public void Continuous_pads_to_totlength_minus_prefix()
    {
        Assert.Equal("INV0000001", DocumentNumberFormatter.FormatContinuous("INV", 1, 10));
    }

    [Fact]
    public void Monthly_mode_uses_delimiter()
    {
        var d = new DateTime(2026, 9, 2);
        Assert.Equal(
            "INV2609-0001",
            DocumentNumberFormatter.FormatDateMode("INV", 1, 4, "-", d, DocumentNumberFormatter.DateMode.Monthly));
    }

    [Fact]
    public void Yearly_mode()
    {
        var d = new DateTime(2026, 9, 2);
        Assert.Equal(
            "INV26-0001",
            DocumentNumberFormatter.FormatDateMode("INV", 1, 4, "-", d, DocumentNumberFormatter.DateMode.Yearly));
    }

    [Fact]
    public void Template_requires_seq_token()
    {
        Assert.Throws<DocumentNumberingConfigurationException>(() =>
            DocumentNumberFormatter.FormatTemplate("{0}YYMM", "INV", 1, 4, new DateTime(2026, 9, 2)));
    }

    [Fact]
    public void Template_replaces_tokens()
    {
        var d = new DateTime(2026, 9, 3);
        Assert.Equal(
            "INV2609-0001",
            DocumentNumberFormatter.FormatTemplate("{0}YYMM-{1}", "INV", 1, 4, d));
    }

    [Fact]
    public void Overflow_seq_digit_width()
    {
        Assert.Throws<DocumentNumberingOverflowException>(() =>
            DocumentNumberFormatter.FormatDateMode("INV", 10000, 4, "-", new DateTime(2026, 9, 1),
                DocumentNumberFormatter.DateMode.Monthly));
    }

    [Fact]
    public void Overflow_max_document_length()
    {
        Assert.Throws<DocumentNumberingOverflowException>(() =>
            DocumentNumberFormatter.EnsureFitsMaxLength(new string('X', 31), 30));
    }

    [Fact]
    public void Seq_less_than_one_invalid()
    {
        Assert.Throws<DocumentNumberingConfigurationException>(() =>
            DocumentNumberFormatter.FormatContinuous("INV", 0, 10));
    }
}
