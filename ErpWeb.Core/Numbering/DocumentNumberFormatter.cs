namespace ErpWeb.Core.Numbering;

/// <summary>Pure document-number formatting. No database.</summary>
public static class DocumentNumberFormatter
{
    public enum DateMode
    {
        Continuous,
        Yearly,
        Monthly
    }

    /// <summary>AdSmNum continuous: Prefix + Seq padded to TotLength - Prefix.Length.</summary>
    public static string FormatContinuous(string? prefix, long seq, short totLength)
    {
        var p = prefix ?? string.Empty;
        if (totLength <= p.Length)
        {
            throw new DocumentNumberingConfigurationException(
                "TotLength must be greater than Prefix length for continuous numbering.");
        }

        var digitWidth = totLength - p.Length;
        EnsureSeqFits(seq, digitWidth);
        return p + seq.ToString().PadLeft(digitWidth, '0');
    }

    /// <summary>
    /// AdSmNumDate mode formula (blank NumberingFormat).
    /// TotLength = Seq digit width only. Delimiter applied between date parts and Seq.
    /// </summary>
    public static string FormatDateMode(
        string? prefix,
        long seq,
        short totLength,
        string? delimiter,
        DateTime documentDate,
        DateMode mode)
    {
        EnsureSeqFits(seq, totLength);
        var p = prefix ?? string.Empty;
        var delim = (delimiter ?? string.Empty).Trim();
        var padded = seq.ToString().PadLeft(totLength, '0');

        return mode switch
        {
            DateMode.Continuous => string.IsNullOrEmpty(delim) ? p + padded : p + delim + padded,
            DateMode.Yearly => p
                + documentDate.ToString("yy")
                + (string.IsNullOrEmpty(delim) ? string.Empty : delim)
                + padded,
            DateMode.Monthly => p
                + documentDate.ToString("yy")
                + documentDate.ToString("MM")
                + (string.IsNullOrEmpty(delim) ? string.Empty : delim)
                + padded,
            _ => throw new DocumentNumberingConfigurationException("Unknown date numbering mode.")
        };
    }

    /// <summary>Non-blank NumberingFormat template. {1} required. Delimiter is not applied separately.</summary>
    public static string FormatTemplate(
        string format,
        string? prefix,
        long seq,
        short totLength,
        DateTime documentDate)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            throw new DocumentNumberingConfigurationException("NumberingFormat is blank.");
        }

        if (!format.Contains("{1}", StringComparison.Ordinal))
        {
            throw new DocumentNumberingConfigurationException("NumberingFormat must contain {1}.");
        }

        EnsureSeqFits(seq, totLength);
        var padded = seq.ToString().PadLeft(totLength, '0');
        var result = format;
        result = result.Replace("{0}", prefix ?? string.Empty, StringComparison.Ordinal);
        result = result.Replace("{1}", padded, StringComparison.Ordinal);
        result = result.Replace("YYYY", documentDate.ToString("yyyy"), StringComparison.Ordinal);
        result = result.Replace("YY", documentDate.ToString("yy"), StringComparison.Ordinal);
        result = result.Replace("MM", documentDate.ToString("MM"), StringComparison.Ordinal);
        result = result.Replace("DD", documentDate.ToString("dd"), StringComparison.Ordinal);

        if (result.Contains("{0}", StringComparison.Ordinal) || result.Contains("{1}", StringComparison.Ordinal))
        {
            throw new DocumentNumberingConfigurationException("NumberingFormat left unreplaced tokens.");
        }

        return result;
    }

    public static void EnsureFitsMaxLength(string documentNumber, int maxLength = 30)
    {
        if (documentNumber.Length > maxLength)
        {
            throw new DocumentNumberingOverflowException(
                $"Document number length {documentNumber.Length} exceeds maximum {maxLength}.");
        }
    }

    private static void EnsureSeqFits(long seq, int digitWidth)
    {
        if (digitWidth <= 0)
        {
            throw new DocumentNumberingConfigurationException("Sequence digit width must be positive.");
        }

        if (seq < 1)
        {
            throw new DocumentNumberingConfigurationException("Sequence must be at least 1.");
        }

        if (seq.ToString().Length > digitWidth)
        {
            throw new DocumentNumberingOverflowException(
                $"Sequence {seq} exceeds digit width {digitWidth}.");
        }
    }
}
