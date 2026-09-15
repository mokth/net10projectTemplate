using System.Globalization;

namespace ErpWeb.Core.Sales;

/// <summary>
/// The <c>SaLMW</c> date-window rules, kept pure so they can be unit-tested without a database.
/// Source of truth: docs/sales-master-plan.md §9.3.
///
/// This type is a validation helper, **not** the enforcement mechanism: the overlap query still
/// has to run inside the Serializable transaction in <c>SaSalesRefService.SaveLmwAsync</c> (D-9),
/// because a pure predicate cannot see a concurrent writer.
/// </summary>
public static class SaLmwRules
{
    /// <summary>Prefix of the overlap error message (§9.3).</summary>
    public const string OverlapMessagePrefix =
        "An LMW licence for this customer overlaps the system validity window ";

    /// <summary>
    /// Occupancy is the inclusive system window. Two windows overlap when neither ends before the
    /// other starts — adjacency (existing end + 1 day = new start) is therefore allowed.
    /// Only valid because the four date columns are NOT NULL (D-7).
    /// </summary>
    public static bool SystemWindowsOverlap(
        DateTime existingStart,
        DateTime existingEnd,
        DateTime candidateStart,
        DateTime candidateEnd) =>
        existingStart.Date <= candidateEnd.Date && existingEnd.Date >= candidateStart.Date;

    /// <summary>Message naming the conflicting licence, e.g. "… {LICENCE} (01/01/2026–31/12/2026)."</summary>
    public static string BuildOverlapMessage(string existingLicenseNo, DateTime existingStart, DateTime existingEnd) =>
        string.Concat(
            OverlapMessagePrefix,
            existingLicenseNo,
            " (",
            existingStart.ToString("d", CultureInfo.CurrentCulture),
            "–",
            existingEnd.ToString("d", CultureInfo.CurrentCulture),
            ").");

    /// <summary>
    /// Ordering and containment only. Licence-window overlap between two licences is deliberately
    /// **not** checked (D-8): two licences may legitimately run in parallel.
    /// </summary>
    public static void ValidateWindows(
        DateTime licenseStart,
        DateTime licenseEnd,
        DateTime systemStart,
        DateTime systemEnd,
        Dictionary<string, string> errors)
    {
        if (licenseStart.Date > licenseEnd.Date)
        {
            errors["LicenseEndDate"] = "Licence end date must be on or after the licence start date.";
        }

        if (systemStart.Date > systemEnd.Date)
        {
            errors["SystemEndDate"] = "System end date must be on or after the system start date.";
        }

        if (licenseStart.Date <= licenseEnd.Date
            && systemStart.Date <= systemEnd.Date
            && (licenseStart.Date > systemStart.Date || systemEnd.Date > licenseEnd.Date))
        {
            errors["SystemStartDate"] =
                "The system validity window must fall inside the licence validity window.";
        }
    }
}
