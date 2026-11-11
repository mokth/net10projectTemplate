using System.Globalization;
using System.Text;

namespace ErpWeb.Core.Quality;

internal static class QaInquiryCsv
{
    public static byte[] Build(QaInquiryPage page, QaInquiryQuery query, DateTime exportedAtUtc)
    {
        var builder = new StringBuilder();
        Append(builder, "Note", QaCaseMessages.InquiryRecordOnly);
        Append(builder, "ExportedAtUtc", exportedAtUtc.ToString("u", CultureInfo.InvariantCulture));
        Append(builder, "Filters", Snapshot(query));
        Append(builder, "OpenCases", page.OpenCaseCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, "PendingInspection", page.PendingInspectionCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, "PendingReview", page.PendingReviewCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, "RejectedDecisionQty", page.RejectedDecisionQty.ToString("0.####", CultureInfo.InvariantCulture));
        Append(builder, "OverdueActions", page.OverdueActionCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, "CompletedActions", page.CompletedActionCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, "AverageCloseDays", page.AverageCloseDays?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty);
        Append(builder, "MedianCloseDays", page.MedianCloseDays?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty);
        Append(builder, "ApprovedWithoutReject", page.ApprovedWithoutRejectCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, "ApprovedWithoutRejectNote", page.FirstPassYieldNote);
        builder.AppendLine();
        builder.AppendLine(string.Join(',',
        [
            "CaseNo", "CaseDate", "Status", "Source", "SourceDocNo", "Supplier", "WorkOrderNo",
            "Inspector", "Reviewer", "AcceptedQty", "RejectedQty", "PostedQty", "AgingDays",
            "OpenActions", "Destination"
        ]));
        foreach (var row in page.Rows)
        {
            builder.AppendLine(string.Join(',',
            [
                Escape(row.CaseNo),
                Escape(row.CaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Escape(QaLabels.Status(row.Status)),
                Escape(QaLabels.Source(row.SourceType)),
                Escape(row.SourceDocNo),
                Escape(row.SupplierName),
                Escape(row.WorkOrderNo),
                Escape(row.Inspector),
                Escape(row.Reviewer),
                row.AcceptedQty.ToString("0.####", CultureInfo.InvariantCulture),
                row.RejectedQty.ToString("0.####", CultureInfo.InvariantCulture),
                row.PostedQty.ToString("0.####", CultureInfo.InvariantCulture),
                row.AgingDays.ToString(CultureInfo.InvariantCulture),
                row.OpenActionCount.ToString(CultureInfo.InvariantCulture),
                Escape(row.DestinationSummary)
            ]));
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    public static string Snapshot(QaInquiryQuery query)
    {
        var parts = new List<string>();
        Add(parts, "status", query.Status);
        Add(parts, "source", query.SourceType);
        Add(parts, "from", query.DateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "to", query.DateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "search", query.SearchText);
        Add(parts, "item", query.ItemCode);
        Add(parts, "lot", query.LotNo);
        Add(parts, "po", query.PoNo);
        Add(parts, "wo", query.WorkOrderNo);
        Add(parts, "supplier", query.Supplier);
        Add(parts, "inspector", query.Inspector);
        Add(parts, "reviewer", query.Reviewer);
        Add(parts, "actionOwner", query.ActionOwner);
        if (query.OverdueOnly)
        {
            parts.Add("overdue=yes");
        }

        return parts.Count == 0 ? "all cases in this company/branch" : string.Join("; ", parts);
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"{name}={value.Trim()}");
        }
    }

    private static void Append(StringBuilder builder, string name, string value) =>
        builder.AppendLine(string.Join(',', [Escape(name), Escape(value)]));

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && "=+-@".Contains(text[0]))
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
