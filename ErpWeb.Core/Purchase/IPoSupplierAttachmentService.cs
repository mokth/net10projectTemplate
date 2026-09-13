using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public sealed class PoSupplierAttachmentRow
{
    public string DocName { get; init; } = string.Empty;
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
}

public interface IPoSupplierAttachmentService : IPoSupplierAttachmentFileCleanup
{
    Task<IvMasterOperationResult<IReadOnlyList<PoSupplierAttachmentRow>>> ListAsync(
        string suppCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoSupplierAttachmentRow>> UploadAsync(
        string suppCode,
        string originalFileName,
        string? contentType,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<(Stream Stream, string FileName, string ContentType)>> DownloadAsync(
        string suppCode,
        string docName,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteAsync(
        string suppCode,
        string docName,
        CancellationToken cancellationToken = default);
}
