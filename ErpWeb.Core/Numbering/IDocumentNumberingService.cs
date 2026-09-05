using ErpWeb.Model.Data;

namespace ErpWeb.Core.Numbering;

public interface IDocumentNumberingService
{
    /// <summary>
    /// Allocates the next document number on the caller's db/transaction.
    /// Must not create a context, begin/commit/rollback, or (on SQL Server) call SaveChanges.
    /// </summary>
    Task<DocumentNumberResult> NextAsync(
        AppDbContext db,
        string module,
        string extraPrefix,
        DateTime documentDate,
        DocumentNumberRequestMode requestMode,
        string currentDocNo,
        CancellationToken ct);
}
