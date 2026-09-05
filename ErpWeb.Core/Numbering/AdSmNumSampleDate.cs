namespace ErpWeb.Core.Numbering;

/// <summary>
/// Deterministic sample dates for admin collision checks and UI sample numbers.
/// Must stay identical between service and UI.
/// </summary>
public static class AdSmNumSampleDate
{
    public static readonly DateTime ContinuousDummy = new(2000, 1, 1);

    /// <summary>
    /// Period sample date from Year/Month business key.
    /// Year&gt;0 Month&gt;0 → that year/month day 1;
    /// Year&gt;0 Month=0 → that year Jan 1;
    /// Year=0 Month=0 (or continuous table) → ContinuousDummy.
    /// </summary>
    public static DateTime ForPeriod(short year, short month)
    {
        if (year > 0 && month > 0)
        {
            return new DateTime(year, month, 1);
        }

        if (year > 0 && month == 0)
        {
            return new DateTime(year, 1, 1);
        }

        return ContinuousDummy;
    }

    public static DocumentNumberFormatter.DateMode ModeForPeriod(short year, short month)
    {
        if (year == 0 && month == 0)
        {
            return DocumentNumberFormatter.DateMode.Continuous;
        }

        if (year > 0 && month == 0)
        {
            return DocumentNumberFormatter.DateMode.Yearly;
        }

        return DocumentNumberFormatter.DateMode.Monthly;
    }
}
