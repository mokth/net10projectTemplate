namespace ErpWeb.Core.Sales;

/// <summary>
/// Pure picker helpers for Add-from-SO / Add-from-DO. No UI, allocation, or document mutation.
/// SO identity is always <c>selectedSoNo + Line</c> — never inferred from <see cref="SaSoLineDto"/>.
/// </summary>
public static class SaDocPickerLines
{
    public static IReadOnlyList<SaSoLineDto> FilterRemainingSoLines(
        IEnumerable<SaSoLineDto>? remaining,
        string? selectedSoNo,
        IEnumerable<(string SoNo, int SoLine)>? existingDocumentKeys)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        var soNo = (selectedSoNo ?? string.Empty).Trim();
        if (soNo.Length == 0)
        {
            return [];
        }

        var existing = BuildKeySet(existingDocumentKeys?.Select(k => (k.SoNo, k.SoLine)));
        return remaining
            .Where(line => !existing.Contains(FormatKey(soNo, line.Line)))
            .ToList();
    }

    public static IReadOnlyList<SaDoBillableLineDto> FilterRemainingDoLines(
        IEnumerable<SaDoBillableLineDto>? remaining,
        IEnumerable<(string DoNo, int DoLine)>? existingDocumentKeys)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        var existing = BuildKeySet(existingDocumentKeys?.Select(k => (k.DoNo, k.DoLine)));
        return remaining
            .Where(line => !existing.Contains(FormatKey(line.DoNo, line.Line)))
            .ToList();
    }

    /// <summary>
    /// Shallow copy of current row references for grid selection. Does not clone DTOs.
    /// </summary>
    public static IReadOnlyList<T> SelectAllCurrent<T>(IReadOnlyList<T>? rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count == 0 ? [] : new List<T>(rows);
    }

    public static string FormatDoPickerKey(string? doNo, int line) =>
        FormatKey(doNo, line);

    private static HashSet<string> BuildKeySet(IEnumerable<(string DocNo, int Line)>? keys)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keys is null)
        {
            return set;
        }

        foreach (var (docNo, line) in keys)
        {
            if (string.IsNullOrWhiteSpace(docNo))
            {
                continue;
            }

            set.Add(FormatKey(docNo, line));
        }

        return set;
    }

    private static string FormatKey(string? docNo, int line) =>
        $"{(docNo ?? string.Empty).Trim()}|{line}";
}

/// <summary>
/// Thin grid row for invoice Add-from-DO when <c>Line</c> alone is not unique across DOs.
/// <see cref="PickerKey"/> is the DxGrid <c>KeyFieldName</c>.
/// </summary>
public sealed class SaDoBillablePickerRow
{
    public required SaDoBillableLineDto Source { get; init; }

    public string PickerKey => SaDocPickerLines.FormatDoPickerKey(Source.DoNo, Source.Line);

    public string DoNo => Source.DoNo;
    public short Line => Source.Line;
    public string SoNo => Source.SoNo;
    public short SoLine => Source.SoLine;
    public string ICode => Source.ICode;
    public string? IDesc => Source.IDesc;
    public decimal RemainingBillableQty => Source.RemainingBillableQty;
}
