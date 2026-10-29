using ErpWeb.Core.Services;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// A bounded, read-only description of the transaction that a refusal was actually about.
/// </summary>
/// <remarks>
/// Every value here was read from one history row owned by the caller's company and branch. Nothing is
/// inferred from an error string, nothing is recalculated, and a value that could not be proven is left
/// null so the presenter omits it rather than guessing.
/// </remarks>
public sealed record IvFailureEvidence
{
    /// <summary>The document family of the affected transaction, or <c>Unknown</c> when unproven.</summary>
    public ErpDocumentKind DocumentKind { get; init; } = ErpDocumentKind.Unknown;

    /// <summary>The affected document/batch number, or null when the row is not a document.</summary>
    public string? DocumentNumber { get; init; }

    /// <summary>The affected transaction's own date, never the current business date.</summary>
    public DateTime? TransactionDate { get; init; }

    public string? ItemCode { get; init; }

    public string? ItemDescription { get; init; }

    public string? Warehouse { get; init; }

    public string? Location { get; init; }

    public string? LotNo { get; init; }

    /// <summary>
    /// True when the affected event is a stock-count generation (identified by a negative event id in
    /// the replay timeline). Such a row has no document number to name, so only the family is reported.
    /// </summary>
    public bool IsStockCountGeneration { get; init; }

    public bool HasDocument => DocumentKind != ErpDocumentKind.Unknown && !string.IsNullOrWhiteSpace(DocumentNumber);
}

/// <summary>
/// Read-only lookup of the transaction behind a replay event id. Implementations must be side-effect
/// free, bounded, tenant-scoped and must never throw for a missing, foreign, stale or unreadable row.
/// </summary>
public interface IIvFailureEvidenceReader
{
    /// <summary>
    /// Returns evidence for <paramref name="eventId"/>, or null when the row is missing, belongs to
    /// another company/branch, is a reversal, is stale, or cannot be read. Never throws.
    /// </summary>
    Task<IvFailureEvidence?> TryReadAsync(
        string companyCode,
        string branchCode,
        long eventId,
        CancellationToken cancellationToken = default);
}
