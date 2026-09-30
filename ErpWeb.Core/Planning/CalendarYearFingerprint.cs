using System.Security.Cryptography;
using System.Text;

namespace ErpWeb.Core.Planning;

public static class CalendarYearFingerprint
{
    public readonly record struct CalendarDayRow(
        DateTime Date,
        string? DateCd,
        string? ShiftGroupCd,
        string? OverrideToken);

    /// <summary>
    /// Company calendar fingerprint: CompCode|Year| + ordered yyyy-MM-dd;DateCd(W|O).
    /// Empty year (no rows) = SHA256("CompCode|Year|").
    /// </summary>
    public static string ComputeCompany(string companyCode, int year, IEnumerable<CompanyCalendarDayVm> days)
    {
        var comp = PlanningCodeNormalizer.NormalizeCode(companyCode);
        var sb = new StringBuilder();
        sb.Append(comp).Append('|').Append(year).Append('|');

        foreach (var d in days.OrderBy(x => x.Date))
        {
            var cd = NormalizeDateCd(d.DateCd);
            sb.Append(d.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(';').Append(cd).Append('\n');
        }

        return Hash(sb.ToString());
    }

    public static string EmptyCompany(string companyCode, int year) =>
        ComputeCompany(companyCode, year, Array.Empty<CompanyCalendarDayVm>());

    /// <summary>Machine calendar fingerprint (includes shift group).</summary>
    public static string Compute(IEnumerable<CalendarDayRow> rows)
    {
        var ordered = rows
            .OrderBy(r => r.Date)
            .ThenBy(r => r.ShiftGroupCd ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(ordered.Count).Append('|');
        foreach (var r in ordered)
        {
            sb.Append(r.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            sb.Append(NormalizeDateCd(r.DateCd)).Append(';');
            sb.Append(r.ShiftGroupCd ?? string.Empty).Append(';');
            sb.Append(r.OverrideToken ?? string.Empty).Append('\n');
        }

        return Hash(sb.ToString());
    }

    public static string NormalizeDateCd(string? dateCd) =>
        string.Equals(dateCd, "O", StringComparison.OrdinalIgnoreCase) ? "O" : "W";

    private static string Hash(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }
}
