namespace ErpWeb.Core.Purchase;

/// <summary>
/// Best-effort physical-file cleanup after DB-authoritative attachment deletes.
/// </summary>
public interface IPoSupplierAttachmentFileCleanup
{
    Task TryDeletePhysicalFileAsync(
        string companyCode,
        string branchCode,
        string suppCode,
        string docId,
        string docName,
        string? docPath,
        string operation,
        CancellationToken cancellationToken = default);
}
