using ErpWeb.Model.Entities.Quality;

namespace ErpWeb.Core.Quality;

internal static class QaInquiryQuerying
{
    public static IQueryable<QaCase> Filter(
        IQueryable<QaCase> rows,
        string company,
        string branch,
        QaInquiryQuery query,
        bool followUpReady)
    {
        rows = rows.Where(x => x.CompanyCode == company && x.BranchCode == branch);

        if (!string.IsNullOrWhiteSpace(query.Status)
            && !string.Equals(query.Status, "all", StringComparison.OrdinalIgnoreCase))
        {
            var status = query.Status.Trim().ToUpperInvariant();
            rows = rows.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceType)
            && !string.Equals(query.SourceType, "all", StringComparison.OrdinalIgnoreCase))
        {
            var source = query.SourceType.Trim().ToUpperInvariant();
            rows = rows.Where(x => x.SourceType == source);
        }

        if (query.DateFrom is not null)
        {
            var from = query.DateFrom.Value.Date;
            rows = rows.Where(x => x.CaseDate >= from);
        }

        if (query.DateTo is not null)
        {
            var to = query.DateTo.Value.Date.AddDays(1);
            rows = rows.Where(x => x.CaseDate < to);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var text = query.SearchText.Trim();
            rows = rows.Where(x =>
                x.CaseNo.Contains(text)
                || (x.SourceDocNo != null && x.SourceDocNo.Contains(text))
                || (x.SupplierName != null && x.SupplierName.Contains(text))
                || (x.SupplierCode != null && x.SupplierCode.Contains(text))
                || (x.WorkOrderNo != null && x.WorkOrderNo.Contains(text))
                || (x.PoNo != null && x.PoNo.Contains(text))
                || (x.DeliveryNote != null && x.DeliveryNote.Contains(text))
                || x.Lines.Any(l => l.ItemCode.Contains(text) || (l.LotNo != null && l.LotNo.Contains(text))));
        }

        if (!string.IsNullOrWhiteSpace(query.ItemCode))
        {
            var item = query.ItemCode.Trim();
            rows = rows.Where(x => x.Lines.Any(l => l.ItemCode == item));
        }

        if (!string.IsNullOrWhiteSpace(query.LotNo))
        {
            var lot = query.LotNo.Trim();
            rows = rows.Where(x => x.Lines.Any(l => l.LotNo == lot));
        }

        if (!string.IsNullOrWhiteSpace(query.PoNo))
        {
            var po = query.PoNo.Trim();
            rows = rows.Where(x => x.PoNo == po);
        }

        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
        {
            var wo = query.WorkOrderNo.Trim();
            rows = rows.Where(x => x.WorkOrderNo == wo);
        }

        if (!string.IsNullOrWhiteSpace(query.Supplier))
        {
            var supplier = query.Supplier.Trim();
            rows = rows.Where(x =>
                (x.SupplierCode != null && x.SupplierCode.Contains(supplier))
                || (x.SupplierName != null && x.SupplierName.Contains(supplier)));
        }

        if (!string.IsNullOrWhiteSpace(query.Inspector))
        {
            var inspector = query.Inspector.Trim();
            rows = rows.Where(x =>
                (x.InspectedBy != null && x.InspectedBy == inspector)
                || x.Inspections.Any(i => i.Inspector == inspector));
        }

        if (!string.IsNullOrWhiteSpace(query.Reviewer))
        {
            var reviewer = query.Reviewer.Trim();
            rows = rows.Where(x => x.ReviewedBy != null && x.ReviewedBy == reviewer);
        }

        if (followUpReady && !string.IsNullOrWhiteSpace(query.ActionOwner))
        {
            var owner = query.ActionOwner.Trim();
            rows = rows.Where(x => x.CorrectiveActions.Any(a => a.AssignedTo == owner));
        }

        if (followUpReady && query.OverdueOnly)
        {
            var today = DateTime.Today;
            rows = rows.Where(x => x.CorrectiveActions.Any(a =>
                (a.Status == QaActionStatuses.Open || a.Status == QaActionStatuses.InProgress)
                && a.DueDate != null
                && a.DueDate < today));
        }

        return rows;
    }

    public static decimal? Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.OrderBy(x => x).ToArray();
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[mid]
            : (ordered[mid - 1] + ordered[mid]) / 2m;
    }
}
