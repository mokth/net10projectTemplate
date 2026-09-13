using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public sealed class PoOrderAttachmentRow
{
    public string DocName { get; init; } = string.Empty;
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
}

public interface IPoOrderAttachmentService
{
    Task MoveDraftToPoAsync(
        string company,
        string branch,
        string tempDocId,
        string poNo,
        CancellationToken cancellationToken = default);

    Task DiscardDraftAsync(string tempDocId, CancellationToken cancellationToken = default);

    Task DeletePhysicalForDocAsync(
        string company,
        string branch,
        string docId,
        IReadOnlyList<(string DocName, string? DocPath)> files,
        CancellationToken cancellationToken = default);

    Task CleanupExpiredDraftsAsync(CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyList<PoOrderAttachmentRow>>> ListAsync(
        string docId,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoOrderAttachmentRow>> UploadAsync(
        string docId,
        string originalFileName,
        string? contentType,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<(Stream Stream, string FileName, string ContentType)>> DownloadAsync(
        string docId,
        string docName,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteAsync(
        string docId,
        string docName,
        CancellationToken cancellationToken = default);
}
