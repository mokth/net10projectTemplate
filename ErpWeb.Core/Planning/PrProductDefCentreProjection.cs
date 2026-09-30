namespace ErpWeb.Core.Planning;

/// <summary>
/// Pure helpers for projecting / mutating work-centre groups over
/// <see cref="PrProductDefOperationVm"/> rows. Centre identity is
/// (WorkCentreCode, OutputItemCode); there is no persisted centre entity.
/// </summary>
public static class PrProductDefCentreProjection
{
    public readonly record struct CentreKey(string WorkCentreCode, string OutputItemCode)
    {
        public static CentreKey From(string? workCentreCode, string? outputItemCode) =>
            new(Normalize(workCentreCode), Normalize(outputItemCode));

        public static CentreKey From(PrProductDefOperationVm op) =>
            From(op.WorkCentreCode, op.OutputItemCode);

        public bool IsEmpty => WorkCentreCode.Length == 0 || OutputItemCode.Length == 0;

        public override string ToString() => $"{WorkCentreCode}|{OutputItemCode}";
    }

    public sealed class CentreRowVm
    {
        public string WorkCentreCode { get; set; } = string.Empty;
        public string? WorkCentreDescription { get; set; }
        public string OutputItemCode { get; set; } = string.Empty;
        public string? OutputItemDescription { get; set; }
        public string? Class { get; set; }
        public int CentralSequence { get; set; }
        public decimal OutputBaseQty { get; set; } = 1m;
        public string? OutputUom { get; set; }
        public bool HasSequenceConflict { get; set; }
        public bool IsPending { get; set; }
        public int ProcessCount { get; set; }

        public CentreKey Key => CentreKey.From(WorkCentreCode, OutputItemCode);
    }

    public sealed class CentreUpdateResult
    {
        public bool Succeeded { get; init; }
        public string? ErrorMessage { get; init; }
        public CentreKey NewKey { get; init; }

        public static CentreUpdateResult Ok(CentreKey newKey) =>
            new() { Succeeded = true, NewKey = newKey };

        public static CentreUpdateResult Fail(string message) =>
            new() { Succeeded = false, ErrorMessage = message };
    }

    public static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    public static bool KeysEqual(CentreKey a, CentreKey b) =>
        string.Equals(a.WorkCentreCode, b.WorkCentreCode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.OutputItemCode, b.OutputItemCode, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<CentreRowVm> BuildCentreRows(
        IEnumerable<PrProductDefOperationVm> operations,
        CentreRowVm? pending = null)
    {
        var rows = operations
            .GroupBy(CentreKey.From)
            .Where(g => !g.Key.IsEmpty)
            .Select(g =>
            {
                var list = g.ToList();
                var sequences = list.Select(x => x.CentralSequence).Distinct().ToList();
                var first = list
                    .OrderBy(x => x.CentralSequence)
                    .ThenBy(x => x.ProcessSequence)
                    .ThenBy(x => x.OperationCode, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new CentreRowVm
                {
                    WorkCentreCode = g.Key.WorkCentreCode,
                    WorkCentreDescription = first.WorkCentreDescription,
                    OutputItemCode = g.Key.OutputItemCode,
                    OutputItemDescription = null,
                    CentralSequence = sequences.Min(),
                    OutputBaseQty = first.OutputBaseQty,
                    OutputUom = first.OutputUom,
                    HasSequenceConflict = sequences.Count > 1,
                    IsPending = false,
                    ProcessCount = list.Count
                };
            })
            .OrderBy(x => x.CentralSequence)
            .ThenBy(x => x.WorkCentreCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.OutputItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pending is not null
            && !pending.Key.IsEmpty
            && rows.All(r => !KeysEqual(r.Key, pending.Key)))
        {
            pending.IsPending = true;
            pending.ProcessCount = 0;
            rows.Add(pending);
            rows = rows
                .OrderBy(x => x.CentralSequence)
                .ThenBy(x => x.WorkCentreCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.OutputItemCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return rows;
    }

    /// <summary>
    /// Updates every operation matching <paramref name="originalKey"/> to the edited centre values.
    /// Rejects when <paramref name="newKey"/> collides with a different existing centre group.
    /// </summary>
    public static CentreUpdateResult TryUpdateCentre(
        IList<PrProductDefOperationVm> operations,
        CentreKey originalKey,
        CentreRowVm edited)
    {
        if (originalKey.IsEmpty)
        {
            return CentreUpdateResult.Fail("Original work centre / center product is required.");
        }

        var newKey = CentreKey.From(edited.WorkCentreCode, edited.OutputItemCode);
        if (newKey.IsEmpty)
        {
            return CentreUpdateResult.Fail("Work centre and center product are required.");
        }

        if (edited.CentralSequence <= 0)
        {
            return CentreUpdateResult.Fail("Centre sequence must be greater than zero.");
        }

        if (edited.OutputBaseQty <= 0m)
        {
            return CentreUpdateResult.Fail("Pack size must be greater than zero.");
        }

        var matching = operations.Where(op => KeysEqual(CentreKey.From(op), originalKey)).ToList();
        if (matching.Count == 0)
        {
            return CentreUpdateResult.Fail("No processes found for the selected work centre.");
        }

        if (!KeysEqual(originalKey, newKey))
        {
            var collision = operations.Any(op =>
                KeysEqual(CentreKey.From(op), newKey)
                && !KeysEqual(CentreKey.From(op), originalKey));
            if (collision)
            {
                return CentreUpdateResult.Fail(
                    $"Centre {newKey.WorkCentreCode} / {newKey.OutputItemCode} already exists. Move or edit processes instead of merging centres.");
            }
        }

        foreach (var op in matching)
        {
            op.WorkCentreCode = newKey.WorkCentreCode;
            op.OutputItemCode = newKey.OutputItemCode;
            op.CentralSequence = edited.CentralSequence;
            op.OutputBaseQty = edited.OutputBaseQty;
            op.OutputUom = string.IsNullOrWhiteSpace(edited.OutputUom)
                ? op.OutputUom
                : edited.OutputUom.Trim();
            if (!string.IsNullOrWhiteSpace(edited.WorkCentreDescription))
            {
                op.WorkCentreDescription = edited.WorkCentreDescription;
            }
        }

        return CentreUpdateResult.Ok(newKey);
    }

    public static int DeleteCentre(IList<PrProductDefOperationVm> operations, CentreKey key)
    {
        if (key.IsEmpty || operations.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        for (var i = operations.Count - 1; i >= 0; i--)
        {
            if (!KeysEqual(CentreKey.From(operations[i]), key))
            {
                continue;
            }

            operations.RemoveAt(i);
            removed++;
        }

        return removed;
    }

    public static IReadOnlyList<PrProductDefOperationVm> FilterProcesses(
        IEnumerable<PrProductDefOperationVm> operations,
        CentreKey? centreKey)
    {
        if (centreKey is null || centreKey.Value.IsEmpty)
        {
            return [];
        }

        var key = centreKey.Value;
        return operations
            .Where(op => KeysEqual(CentreKey.From(op), key))
            .OrderBy(x => x.ProcessSequence)
            .ThenBy(x => x.Uid)
            .ThenBy(x => x.TempId, StringComparer.Ordinal)
            .ThenBy(x => x.OperationCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// After a centre selection change, returns whether the current process still belongs
    /// to the new centre (and thus whether machine/labour selection should be cleared).
    /// </summary>
    public static bool OperationBelongsToCentre(
        PrProductDefOperationVm? operation,
        CentreKey? centreKey)
    {
        if (operation is null || centreKey is null || centreKey.Value.IsEmpty)
        {
            return false;
        }

        return KeysEqual(CentreKey.From(operation), centreKey.Value);
    }
}
