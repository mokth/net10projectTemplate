namespace ErpWeb.Core.Quality;

public sealed class QaSourcePrefillQuery
{
    public long? LotId { get; init; }
    public long? OutputId { get; init; }
    public long? SourceHeaderId { get; init; }
    public string? PoNo { get; init; }
    public int? PoRelNo { get; init; }
}

public static class QaSourcePrefill
{
    public static bool HasIdentity(QaSourcePrefillQuery query) =>
        query.LotId is > 0
        || query.OutputId is > 0
        || query.SourceHeaderId is > 0
        || !string.IsNullOrWhiteSpace(query.PoNo);

    public static string? SearchText(QaSourcePrefillQuery query)
    {
        if (query.LotId is > 0)
        {
            return query.LotId.Value.ToString();
        }

        if (query.OutputId is > 0)
        {
            return query.OutputId.Value.ToString();
        }

        if (query.SourceHeaderId is > 0)
        {
            return query.SourceHeaderId.Value.ToString();
        }

        var poNo = (query.PoNo ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(poNo) ? null : poNo;
    }

    public static IReadOnlyList<QaSourceRow> Matching(
        IEnumerable<QaSourceRow> rows,
        QaSourcePrefillQuery query)
    {
        var list = rows as IList<QaSourceRow> ?? rows.ToList();
        if (query.LotId is > 0)
        {
            return list.Where(x => x.ProductionBalLotId == query.LotId || x.SourceHeaderId == query.LotId).ToList();
        }

        if (query.OutputId is > 0)
        {
            return list.Where(x => x.SourceHeaderId == query.OutputId).ToList();
        }

        if (query.SourceHeaderId is > 0)
        {
            var key = query.SourceHeaderId.Value.ToString();
            return list.Where(x =>
                    x.SourceHeaderId == query.SourceHeaderId
                    || string.Equals(x.SourceDocNo, key, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var poNo = (query.PoNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(poNo))
        {
            return [];
        }

        return list.Where(x =>
                string.Equals(x.PoNo, poNo, StringComparison.OrdinalIgnoreCase)
                && (query.PoRelNo is null || x.PoRelNo == query.PoRelNo))
            .ToList();
    }
}
