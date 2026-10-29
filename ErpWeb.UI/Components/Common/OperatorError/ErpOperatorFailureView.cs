using ErpWeb.Core.Inventory;
using ErpWeb.Core.Services;

namespace ErpWeb.UI.Components.Common.OperatorError;

/// <summary>One proven fact about a refusal, already formatted by the presenter.</summary>
public sealed record ErpOperatorFailureFact(string Label, string Value);

/// <summary>One refused record, already in operator language.</summary>
public sealed record ErpOperatorFailureNote(
    string Title,
    string Explanation,
    string? NextStep,
    string? Reference,
    IReadOnlyList<ErpOperatorFailureFact> Facts);

/// <summary>
/// One failed record as a page already knows it: the operator-visible label (document number, batch
/// or line), the page's own message for the fallback, and the structured error when the service
/// boundary produced one.
/// </summary>
public sealed record ErpOperatorFailureRecord(
    string Label,
    string? Message,
    ErpOperatorError? Error);

/// <summary>
/// Operator-language view of refused documents, built only from the structured errors a service
/// boundary already produced. Presentation only: it reads results, changes nothing and returns
/// <c>null</c> when no record carried a structured error, so every page falls back to the message it
/// showed before this work stream existed.
/// </summary>
public sealed record ErpOperatorFailureView(
    string Headline,
    string? Reason,
    IReadOnlyList<ErpOperatorFailureNote> Notes)
{
    /// <summary>Facts shown per record. Enough to identify the transaction, never a wall of text.</summary>
    private const int MaxFacts = 6;

    /// <summary>Inventory posting aggregate (per-batch results).</summary>
    public static ErpOperatorFailureView? From(IvInventoryPostingResult? posting)
    {
        if (posting is null)
        {
            return null;
        }

        var coded = posting.Batches
            .Where(x => !x.Succeeded && x.OperatorError is not null)
            .Select(x => x.OperatorError!)
            .ToArray();
        if (coded.Length == 0)
        {
            return null;
        }

        var failed = posting.FailedCount;
        var succeeded = posting.SucceededCount;
        var headline = failed == 1
            ? coded[0].Title
            : succeeded > 0
                ? $"{failed} of the {failed + succeeded} selected records could not be completed."
                : $"None of the {failed} selected records could be completed.";

        var notes = posting.Batches
            .Where(x => !x.Succeeded)
            .Select(x => x.OperatorError is { } error
                ? Note(error)
                : Note($"Record {x.BatchNo}", x.ErrorMessage))
            .ToArray();

        return new ErpOperatorFailureView(headline, SharedReason(coded), notes);
    }

    /// <summary>
    /// Any page that knows its own failed records (one per document, delivery order, count, work
    /// order …). <paramref name="totalSelected"/> lets the headline say “2 of the 3” when the page
    /// knows the selection size.
    /// </summary>
    public static ErpOperatorFailureView? FromRecords(
        IEnumerable<ErpOperatorFailureRecord>? records,
        int? totalSelected = null)
    {
        if (records is null)
        {
            return null;
        }

        var all = records.ToArray();
        var coded = all
            .Where(x => x.Error is not null)
            .Select(x => x.Error!)
            .ToArray();
        if (coded.Length == 0)
        {
            return null;
        }

        var total = totalSelected ?? all.Length;
        var headline = all.Length == 1 && total <= 1
            ? coded[0].Title
            : all.Length < total
                ? $"{all.Length} of the {total} selected records could not be completed."
                : $"None of the {all.Length} selected records could be completed.";

        var notes = all
            .Select(x => x.Error is { } error
                ? Note(error, x.Label)
                : Note($"Record {x.Label}", x.Message))
            .ToArray();

        return new ErpOperatorFailureView(headline, SharedReason(coded), notes);
    }

    /// <summary>
    /// Single-record view for services that expose one operator error instead of a record list
    /// (for example a stock count or a work-order transfer).
    /// </summary>
    public static ErpOperatorFailureView? FromError(ErpOperatorError? error) =>
        error is null
            ? null
            : new ErpOperatorFailureView(error.Title, error.Explanation, [Note(error)]);

    private static string? SharedReason(IReadOnlyCollection<ErpOperatorError> coded)
    {
        var reasons = coded
            .Select(x => x.Explanation)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return reasons.Length == 1 ? reasons[0] : null;
    }

    private static ErpOperatorFailureNote Note(ErpOperatorError error, string? label = null)
    {
        // Producers already name the document inside the headline. Only prefix the page's own label
        // when it adds something the headline does not already say.
        var title = string.IsNullOrWhiteSpace(label)
            || error.Title.Contains(label, StringComparison.OrdinalIgnoreCase)
            ? error.Title
            : $"{label}: {error.Title}";
        return new ErpOperatorFailureNote(
            title,
            error.Explanation,
            string.IsNullOrWhiteSpace(error.NextStep) ? null : error.NextStep,
            string.IsNullOrWhiteSpace(error.ReferenceId) ? null : error.ReferenceId,
            error.Details
                .Where(d => !d.IsMonetary)
                .Take(MaxFacts)
                .Select(d => new ErpOperatorFailureFact(d.Label, d.Value))
                .ToArray());
    }

    private static ErpOperatorFailureNote Note(string title, string? message) =>
        new(title,
            string.IsNullOrWhiteSpace(message) ? "This record could not be processed." : message,
            null,
            null,
            []);
}
