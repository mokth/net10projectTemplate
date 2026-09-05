using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;

namespace ErpWeb.Tests;

/// <summary>
/// SQLite invoice tests mock numbering — no UPDLOCK emulation.
/// Allocates INV{yy}{MM}-{seq:D4} with Prefix INV (table-driven; ignores customer InvoicePrefix).
/// </summary>
internal sealed class FakeDocumentNumberingService : IDocumentNumberingService
{
    private int _seq;

    public Task<DocumentNumberResult> NextAsync(
        AppDbContext db,
        string module,
        string extraPrefix,
        DateTime documentDate,
        DocumentNumberRequestMode requestMode,
        string currentDocNo,
        CancellationToken ct)
    {
        if (requestMode == DocumentNumberRequestMode.Edit
            && !string.Equals(currentDocNo, "AUTO", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(currentDocNo))
        {
            return Task.FromResult(new DocumentNumberResult(currentDocNo.Trim(), null));
        }

        var n = Interlocked.Increment(ref _seq);
        var doc = $"INV{documentDate:yy}{documentDate:MM}-{n:D4}";
        return Task.FromResult(new DocumentNumberResult(doc, "INV"));
    }
}
