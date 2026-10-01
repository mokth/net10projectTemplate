namespace ErpWeb.Core.Planning;

/// <summary>
/// Shared Start/End normalization for preventive UI validation, overlap checks, and scheduler loading.
/// Overnight convention: when End time-of-day &lt;= Start time-of-day, End is next calendar day.
/// </summary>
public static class PreventiveWindowNormalizer
{
    public const double MaxDurationHours = 24;

    public readonly record struct NormalizedWindow(DateTime WindowStart, DateTime WindowEnd);

    public static bool TryNormalize(DateTime downDt, DateTime startTm, DateTime endTm, out NormalizedWindow window, out string? error)
    {
        window = default;
        error = null;

        if (startTm.Year < 1900 || endTm.Year < 1900)
        {
            error = "Start Time and End Time are required.";
            return false;
        }

        var day = downDt.Date;
        var start = day.Add(startTm.TimeOfDay);
        var end = day.Add(endTm.TimeOfDay);
        if (end <= start)
            end = end.AddDays(1);

        if (end <= start)
        {
            error = "Duration must be greater than zero.";
            return false;
        }

        var hours = (end - start).TotalHours;
        if (hours > MaxDurationHours)
        {
            error = $"Preventive window cannot exceed {MaxDurationHours} hours.";
            return false;
        }

        window = new NormalizedWindow(start, end);
        return true;
    }

    public static bool Overlaps(NormalizedWindow a, NormalizedWindow b) =>
        a.WindowStart < b.WindowEnd && a.WindowEnd > b.WindowStart;
}
