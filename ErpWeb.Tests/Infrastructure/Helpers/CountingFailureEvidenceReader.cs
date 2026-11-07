using ErpWeb.Core.Inventory;

namespace ErpWeb.Tests.Infrastructure.Helpers;

/// <summary>
/// Wraps a real failure-evidence reader and counts lookups, so tests can prove the explanation query
/// runs only after a definitive refusal and never on a successful posting.
/// </summary>
internal sealed class CountingFailureEvidenceReader(IIvFailureEvidenceReader inner) : IIvFailureEvidenceReader
{
    public int Calls { get; private set; }

    public Task<IvFailureEvidence?> TryReadAsync(
        string companyCode,
        string branchCode,
        long eventId,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return inner.TryReadAsync(companyCode, branchCode, eventId, cancellationToken);
    }
}
